using System.Globalization;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.PackML.ConfigParam;

/// <summary>E_ConfigParamAccessError values that a batch check can return (FW E_ConfigParamAccessError).</summary>
internal enum ConfigParamError
{
    None = 0,
    EmptyBatch = 2,
    DuplicateId = 3,
    MetadataNotFound = 4,
    NotBound = 5,
    DataTypeMismatch = 6,
    InvalidValueFormat = 7,
    OutOfRange = 8,
}

internal readonly record struct ConfigParamCheck(ConfigParamError Error, uint ParamId)
{
    private static readonly Dictionary<ConfigParamError, string> Messages = new()
    {
        [ConfigParamError.EmptyBatch] = "Config Param batch is empty.",
        [ConfigParamError.DuplicateId] = "Config Param ID is duplicated in the batch.",
        [ConfigParamError.MetadataNotFound] = "Config Param metadata was not found.",
        [ConfigParamError.NotBound] = "Config Param is not bound to Persistent storage.",
        [ConfigParamError.DataTypeMismatch] = "Config Param dataType does not match metadata.",
        [ConfigParamError.InvalidValueFormat] = "Config Param value has an invalid strict format.",
        [ConfigParamError.OutOfRange] = "Config Param value is outside its type or metadata limits.",
    };

    public bool Passed => Error == ConfigParamError.None;

    // E_ConfigParamAccessError starts at 2 for EmptyBatch (1 is RegistryNotReady, which the simulator never reports), so 918 + 2 = 920.
    public uint MainErrorId => 918 + (uint)Error;

    public string Message => Messages[Error];
}

/// <summary>FB_SetConfigParamService for one service whose Param is T_SetConfigParamList.</summary>
internal sealed class ConfigParamService
{
    private static readonly (string Name, Func<PlcType, bool> Accepts)[] BatchShape =
    [
        ("Id", IsUdint), ("DataType", IsIntegerLike), ("Value", IsNarrowString),
    ];

    private static readonly (string Name, Func<PlcType, bool> Accepts)[] MetadataShape =
    [
        ("Id", IsUdint), ("DataType", IsIntegerLike), ("Value", IsNarrowString),
        ("IsBound", IsBool), ("LowerLimit", IsLreal), ("UpperLimit", IsLreal),
    ];

    private readonly StructArray _batch;
    private readonly StructArray _metadata;
    private readonly List<Item> _snapshot = [];

    private ConfigParamService(StructArray batch, StructArray metadata)
    {
        _batch = batch;
        _metadata = metadata;
    }

    /// <returns>null when serviceParam or configParamList does not have the shape below (#12 R6: the feature is off for this service).</returns>
    public static ConfigParamService? TryCreate(SymbolRef serviceParam, SymbolRef configParamList)
    {
        StructArray? batch = StructArray.TryCreate(serviceParam, BatchShape);
        StructArray? metadata = StructArray.TryCreate(configParamList, MetadataShape);
        return batch is null || metadata is null ? null : new ConfigParamService(batch, metadata);
    }

    /// <summary>Clears the snapshot, reads the batch, keeps the passing items as the snapshot, and checks it (M_ValidateConfigParamBatch). On failure the snapshot stays empty.</summary>
    public ConfigParamCheck Validate(ProcessImage image)
    {
        _snapshot.Clear();
        if (ReadId(image, _batch, 0) == 0)
            return new ConfigParamCheck(ConfigParamError.EmptyBatch, 0);

        Dictionary<uint, int> metadataById = IndexMetadata(image);
        var seen = new HashSet<uint>();
        var passed = new List<Item>();
        for (int index = 0; index < _batch.Count; index++)
        {
            uint id = ReadId(image, _batch, index);
            if (id == 0)
                break;

            ConfigParamError error = CheckItem(image, index, id, seen, metadataById, out Item item);
            if (error != ConfigParamError.None)
                return new ConfigParamCheck(error, id);
            passed.Add(item);
        }

        _snapshot.AddRange(passed);
        return new ConfigParamCheck(ConfigParamError.None, 0);
    }

    /// <summary>Writes each snapshot item to ModuleConfigParamList[n].Value, then clears the snapshot. Does nothing when the snapshot is empty.</summary>
    public void Apply(ProcessImage image)
    {
        foreach (Item item in _snapshot)
            image.WriteValue(_metadata.Symbol(item.MetadataIndex, "Value"), ConfigParamValue.Format(item.Value, item.DataType), WriteSource.Workflow);
        _snapshot.Clear();
    }

    /// <summary>Clears the snapshot (service reset).</summary>
    public void Clear() => _snapshot.Clear();

    private static bool IsUdint(PlcType type) => PlcLayout.IsKind(type, PrimitiveKind.UDInt);

    private static bool IsBool(PlcType type) => PlcLayout.IsKind(type, PrimitiveKind.Bool);

    private static bool IsLreal(PlcType type) => PlcLayout.IsKind(type, PrimitiveKind.LReal);

    private static bool IsNarrowString(PlcType type) => type.Unaliased is StringType { IsWide: false };

    private static bool IsIntegerLike(PlcType type) =>
        type.Unaliased is EnumType || type.Unaliased is PrimitiveType { IsFloatingPoint: false, Kind: not PrimitiveKind.Bool };

    private static uint ReadId(ProcessImage image, StructArray array, int index) =>
        (uint)PlcValue.ToInt64(image.ReadValue(array.Symbol(index, "Id")));

    private static double ParseNumber(string value) =>
        double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private ConfigParamError CheckItem(
        ProcessImage image, int index, uint id, HashSet<uint> seen, Dictionary<uint, int> metadataById, out Item item)
    {
        item = default;
        if (!seen.Add(id))
            return ConfigParamError.DuplicateId;
        if (!metadataById.TryGetValue(id, out int metadataIndex))
            return ConfigParamError.MetadataNotFound;
        if (!PlcValue.ToBoolean(image.ReadValue(_metadata.Symbol(metadataIndex, "IsBound"))))
            return ConfigParamError.NotBound;

        int dataType = (int)PlcValue.ToInt64(image.ReadValue(_batch.Symbol(index, "DataType")));
        long descriptorType = PlcValue.ToInt64(image.ReadValue(_metadata.Symbol(metadataIndex, "DataType")));
        if (ConfigParamValue.ToConfigDataType(descriptorType) != dataType)
            return ConfigParamError.DataTypeMismatch;

        string value = (string)image.ReadValue(_batch.Symbol(index, "Value"));
        item = new Item(dataType, value, metadataIndex);
        return CheckValue(image, item);
    }

    private ConfigParamError CheckValue(ProcessImage image, Item item)
    {
        if (!ConfigParamValue.IsStrict(item.Value, item.DataType))
            return ConfigParamError.InvalidValueFormat;
        if (!ConfigParamValue.IsNumeric(item.DataType))
            return ConfigParamError.None;

        double number = ParseNumber(item.Value);
        if (!double.IsFinite(number))
            return ConfigParamError.InvalidValueFormat;
        if (!ConfigParamValue.IsInTypeRange(number, item.DataType))
            return ConfigParamError.OutOfRange;

        double lower = PlcValue.ToDouble(image.ReadValue(_metadata.Symbol(item.MetadataIndex, "LowerLimit")));
        double upper = PlcValue.ToDouble(image.ReadValue(_metadata.Symbol(item.MetadataIndex, "UpperLimit")));
        return number < lower || number > upper ? ConfigParamError.OutOfRange : ConfigParamError.None;
    }

    // The first element wins for a repeated Id, like M_FindConfigParamMetadata; Id 0 is an empty slot.
    private Dictionary<uint, int> IndexMetadata(ProcessImage image)
    {
        var byId = new Dictionary<uint, int>();
        for (int index = 0; index < _metadata.Count; index++)
        {
            uint id = ReadId(image, _metadata, index);
            if (id != 0)
                byId.TryAdd(id, index);
        }

        return byId;
    }

    private readonly record struct Item(int DataType, string Value, int MetadataIndex);

    /// <summary>A one-dimensional array of structs; members are located once, elements are addressed by position.</summary>
    private sealed class StructArray
    {
        private readonly string _path;
        private readonly int _origin;
        private readonly int _lower;
        private readonly int _stride;
        private readonly Dictionary<string, StructMember> _members;

        private StructArray(SymbolRef array, ArrayDimension bounds, int stride, Dictionary<string, StructMember> members)
        {
            _path = array.Path;
            _origin = array.Offset;
            _lower = bounds.LowerBound;
            Count = bounds.Length;
            _stride = stride;
            _members = members;
        }

        public int Count { get; }

        public static StructArray? TryCreate(SymbolRef array, (string Name, Func<PlcType, bool> Accepts)[] shape)
        {
            if (array.Type.Unaliased is not ArrayType { Dimensions.Count: 1 } arrayType || arrayType.ElementType.Unaliased is not StructType element)
                return null;

            var members = new Dictionary<string, StructMember>(shape.Length);
            foreach ((string name, Func<PlcType, bool> accepts) in shape)
            {
                StructMember? member = PlcLayout.Find(element, name);
                if (member is null || !accepts(member.Type))
                    return null;
                members[name] = member;
            }

            return new StructArray(array, arrayType.Dimensions[0], element.Size, members);
        }

        /// <param name="index">Zero-based position in the array.</param>
        public SymbolRef Symbol(int index, string member)
        {
            StructMember field = _members[member];
            return new SymbolRef($"{_path}[{_lower + index}].{member}", field.Type, _origin + index * _stride + field.Offset);
        }
    }
}
