using System.Text.Json;
using AdsPlcSimulator.Core.PackML.Alarm;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Host.Project;

public sealed class ModuleConfigException(string message) : Exception(message);

internal sealed record ModuleConfigFile
{
    public int SchemaVersion { get; init; }

    public List<ModuleConfigEntry?>? Modules { get; init; }
}

internal sealed record ModuleConfigEntry
{
    public bool Enabled { get; init; }

    public string? ModuleType { get; init; }

    public int Slot { get; init; }

    public uint Id { get; init; }

    public List<DescriptorConfig?>? VariableList { get; init; } = [];

    public List<DescriptorConfig?>? ConfigParamList { get; init; } = [];

    public List<AlarmConfig?>? AlarmList { get; init; } = [];
}

internal sealed record AlarmConfig
{
    public string? Source { get; init; }

    public string? DataType { get; init; }

    public AlarmConditionConfig? Condition { get; init; }

    public uint MainErrorId { get; init; }

    public uint SourceErrorId { get; init; }

    public string? Message { get; init; }

    public string? Severity { get; init; }
}

internal sealed record AlarmConditionConfig
{
    public string? Operator { get; init; }

    public double Value { get; init; }

    public double Tolerance { get; init; }
}

internal sealed record ModuleConfigResult(string Path, ConfiguredAlarmTable ConfiguredAlarms);

internal sealed record DescriptorConfig
{
    public uint Id { get; init; }

    public string? Name { get; init; }

    public string? Unit { get; init; }

    public string? DataType { get; init; }
}

internal static class ModuleConfigPublisher
{
    public const string RelativePath = "config/module-config.json";
    internal const string ModulesRoot = "GVL_Module.Chamber_Control";

    private const int SupportedSchemaVersion = 4;
    private const int MaxConfiguredAlarms = 30;
    private const uint ReservedErrorIdStart = 0xFFFF0000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, (string EnumMember, string InitialValue)> DataTypes = new(StringComparer.Ordinal)
    {
        ["DINT"] = ("Integer_32bit", "0"),
        ["REAL"] = ("Float_32bit", "0"),
        ["BOOL"] = ("Boolean", "FALSE"),
        ["STRING"] = ("String_80byte", ""),
    };

    public static ModuleConfigResult? PublishIfPresent(string projectDirectory, SymbolTable symbols, ProcessImage image)
    {
        string path = Path.Combine(projectDirectory, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            return null;

        ConfiguredAlarmTable alarms = Publish(Parse(File.ReadAllText(path), path), path, symbols, image);
        return new ModuleConfigResult(path, alarms);
    }

    internal static ModuleConfigFile Parse(string json, string path)
    {
        ModuleConfigFile? config;
        try
        {
            config = JsonSerializer.Deserialize<ModuleConfigFile>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw Fail(path, $"invalid JSON: {exception.Message}");
        }

        if (config is null)
            throw Fail(path, "the file is empty.");
        if (config.SchemaVersion != SupportedSchemaVersion)
            throw Fail(path, $"schemaVersion must be {SupportedSchemaVersion}, got {config.SchemaVersion}.");
        if (config.Modules is null)
            throw Fail(path, "modules is missing or null.");

        var slots = new HashSet<int>();
        foreach (ModuleConfigEntry? module in config.Modules)
            ValidateModule(module, slots, path);
        return config;
    }

    internal static ConfiguredAlarmTable Publish(ModuleConfigFile config, string path, SymbolTable symbols, ProcessImage image)
    {
        var plan = new List<(SymbolRef Symbol, object Value)>();
        var alarms = new Dictionary<string, IReadOnlyList<ConfiguredAlarm>>();
        var planner = new PublishPlanner(path, symbols, plan, alarms);
        foreach (ModuleConfigEntry module in config.Modules!.Where(module => module!.Enabled).Select(module => module!))
            planner.AddModule(module);

        foreach ((SymbolRef symbol, object value) in plan)
            image.WriteValue(symbol, value, WriteSource.Workflow);
        return new ConfiguredAlarmTable(alarms);
    }

    private static ModuleConfigException Fail(string path, string reason) => new($"{path}: {reason}");

    private static void ValidateModule(ModuleConfigEntry? module, HashSet<int> slots, string path)
    {
        if (module is null)
            throw Fail(path, "modules contains a null element.");
        if (!slots.Add(module.Slot))
            throw Fail(path, $"slot {module.Slot} appears more than once.");
        if (module.ModuleType is not null && !IsAscii(module.ModuleType))
            throw Fail(path, $"slot {module.Slot}: moduleType contains non-ASCII characters.");

        ValidateList(module.VariableList, "variableList", module.Slot, path);
        ValidateList(module.ConfigParamList, "configParamList", module.Slot, path);
        ValidateAlarms(module.AlarmList, module.Slot, path);
    }

    private static void ValidateAlarms(List<AlarmConfig?>? list, int slot, string path)
    {
        if (list is null)
            throw Fail(path, $"slot {slot}: alarmList is null.");

        string where = $"slot {slot} alarmList";
        if (list.Contains(null))
            throw Fail(path, $"{where} contains a null element.");
        if (list.Count > MaxConfiguredAlarms)
            throw Fail(path, $"{where} has {list.Count} entries, the limit is {MaxConfiguredAlarms}.");

        var ids = new HashSet<uint>();
        foreach (AlarmConfig? alarm in list)
            ValidateAlarm(alarm!, ids, where, path);
    }

    private static void ValidateAlarm(AlarmConfig alarm, HashSet<uint> ids, string where, string path)
    {
        string id = $"mainErrorId {alarm.MainErrorId}";
        if (alarm.Condition is null)
            throw Fail(path, $"{where}: {id} has no condition.");
        if (string.IsNullOrWhiteSpace(alarm.Source))
            throw Fail(path, $"{where}: {id} has an empty source.");
        if (AlarmCondition.ParseDataType(alarm.DataType) is null)
            throw Fail(path, $"{where}: {id}: dataType '{alarm.DataType}' is not supported.");
        if (AlarmCondition.ParseOperator(alarm.Condition.Operator) is null)
            throw Fail(path, $"{where}: {id}: operator '{alarm.Condition.Operator}' is not one of gt, lt, ge, le, eq.");
        if (ParseSeverity(alarm.Severity) is null)
            throw Fail(path, $"{where}: {id}: severity '{alarm.Severity}' is not Alarm or Warning.");
        if (alarm.MainErrorId is 0 or >= ReservedErrorIdStart)
            throw Fail(path, $"{where}: {id} is outside 1..{ReservedErrorIdStart - 1}.");
        if (!ids.Add(alarm.MainErrorId))
            throw Fail(path, $"{where}: {id} appears more than once.");
    }

    private static AlarmSeverity? ParseSeverity(string? text) => text switch
    {
        null or "Alarm" => AlarmSeverity.Alarm,
        "Warning" => AlarmSeverity.Warning,
        _ => null,
    };

    private static void ValidateList(List<DescriptorConfig?>? list, string listName, int slot, string path)
    {
        if (list is null)
            throw Fail(path, $"slot {slot}: {listName} is null.");

        var ids = new HashSet<uint>();
        foreach (DescriptorConfig? item in list)
        {
            if (item is null)
                throw Fail(path, $"slot {slot}: {listName} contains a null element.");
            ValidateDescriptor(item, $"slot {slot} {listName}", path);
            if (item.Id == 0)
                throw Fail(path, $"slot {slot} {listName}: id of '{item.Name}' must not be 0.");
            if (!ids.Add(item.Id))
                throw Fail(path, $"slot {slot} {listName}: id {item.Id} appears more than once.");
        }
    }

    private static void ValidateDescriptor(DescriptorConfig item, string where, string path)
    {
        if (item.Name is null || item.Unit is null || item.DataType is null)
            throw Fail(path, $"{where}: id {item.Id} needs name, unit and dataType.");
        if (string.IsNullOrWhiteSpace(item.Name))
            throw Fail(path, $"{where}: id {item.Id} has an empty name.");
        if (!IsAscii(item.Name) || !IsAscii(item.Unit))
            throw Fail(path, $"{where}: name or unit of id {item.Id} contains non-ASCII characters.");
        if (!DataTypes.ContainsKey(item.DataType))
            throw Fail(path, $"{where}: dataType '{item.DataType}' of id {item.Id} is not one of DINT, REAL, BOOL, STRING.");
    }

    private static bool IsAscii(string text) => text.All(character => character < 128);

    private sealed class PublishPlanner(
        string path, SymbolTable symbols, List<(SymbolRef Symbol, object Value)> plan, Dictionary<string, IReadOnlyList<ConfiguredAlarm>> alarms)
    {
        public void AddModule(ModuleConfigEntry module)
        {
            ArrayDimension slots = ModuleSlots();
            if (module.Slot < slots.LowerBound || module.Slot > slots.UpperBound)
                throw Fail(path, $"slot {module.Slot} is outside [{slots}] of {ModulesRoot}.");

            string root = $"{ModulesRoot}[{module.Slot}]";
            Add($"{root}.ModuleStatus.ModuleId", (long)module.Id);
            Add($"{root}.ModuleStatus.Enabled", true);
            string moduleType = module.ModuleType ?? "";
            CheckLength($"{root}.ModuleStatus.ModuleType", moduleType, "moduleType");
            Add($"{root}.ModuleStatus.ModuleType", moduleType);

            AddList($"{root}.ModuleVariableList", module.VariableList!, isConfigParam: false);
            Add($"{root}.ModuleStatus.Variable.PublishedCount", (long)module.VariableList!.Count);
            AddList($"{root}.ModuleConfigParamList", module.ConfigParamList!, isConfigParam: true);
            AddAlarms(root, module.AlarmList!);
        }

        private void AddAlarms(string root, List<AlarmConfig?> items)
        {
            if (items.Count == 0)
                return;

            string alarmItem = $"{root}.ModuleAlarmList[{AlarmListLowerBound(root)}]";
            var configured = new List<ConfiguredAlarm>();
            foreach (AlarmConfig? item in items)
                configured.Add(ToConfiguredAlarm(root, alarmItem, item!));
            alarms[root] = configured;
        }

        private int AlarmListLowerBound(string root) =>
            Unwrap(Resolve($"{root}.ModuleAlarmList").Type) is ArrayType { Dimensions.Count: 1 } array
                ? array.Dimensions[0].LowerBound
                : throw Fail(path, $"{root}.ModuleAlarmList is not a one-dimensional array.");

        private ConfiguredAlarm ToConfiguredAlarm(string root, string alarmItem, AlarmConfig item)
        {
            string fullPath = $"{root}.{item.Source}";
            AlarmSourceType sourceType = AlarmCondition.ParseDataType(item.DataType)!.Value;
            PlcType symbolType = Resolve(fullPath).Type;
            if (!AlarmCondition.Matches(sourceType, symbolType))
                throw Fail(path, $"{fullPath} is {symbolType.Name}, not {item.DataType}.");

            string message = item.Message ?? "";
            int messageLimit = StringLength($"{alarmItem}.Message");
            if (message.Length > messageLimit)
                throw Fail(path, $"mainErrorId {item.MainErrorId}: message is {message.Length} characters, the limit is {messageLimit}.");
            int pathLimit = StringLength($"{alarmItem}.Path");
            if (fullPath.Length > pathLimit)
                throw Fail(path, $"mainErrorId {item.MainErrorId}: source path is {fullPath.Length} characters, the limit is {pathLimit}.");

            AlarmConditionConfig condition = item.Condition!;
            return new ConfiguredAlarm(
                item.Source!, sourceType, AlarmCondition.ParseOperator(condition.Operator)!.Value, condition.Value, condition.Tolerance,
                item.MainErrorId, item.SourceErrorId, message, ParseSeverity(item.Severity)!.Value);
        }

        private int StringLength(string symbolPath) =>
            Unwrap(Resolve(symbolPath).Type) is StringType type
                ? type.Length
                : throw Fail(path, $"{symbolPath} is not a STRING.");

        private ArrayDimension ModuleSlots()
        {
            if (Unwrap(Resolve(ModulesRoot).Type) is not ArrayType { Dimensions.Count: 1 } array)
                throw Fail(path, $"{ModulesRoot} is not a one-dimensional array.");
            return array.Dimensions[0];
        }

        private void AddList(string listPath, List<DescriptorConfig?> items, bool isConfigParam)
        {
            if (Unwrap(Resolve(listPath).Type) is not ArrayType { Dimensions.Count: 1 } array)
                throw Fail(path, $"{listPath} is not a one-dimensional array.");
            if (items.Count > array.ElementCount)
                throw Fail(path, $"{listPath} holds {array.ElementCount} entries, the config has {items.Count}.");

            int index = array.Dimensions[0].LowerBound;
            foreach (DescriptorConfig? item in items)
                AddDescriptor($"{listPath}[{index++}]", item!, isConfigParam);
        }

        private void AddDescriptor(string itemPath, DescriptorConfig item, bool isConfigParam)
        {
            (string enumMember, string initialValue) = DataTypes[item.DataType!];
            CheckLength($"{itemPath}.Name", item.Name!, "name");
            CheckLength($"{itemPath}.Unit", item.Unit!, "unit");
            Add($"{itemPath}.Id", (long)item.Id);
            Add($"{itemPath}.Name", item.Name!);
            Add($"{itemPath}.DataType", enumMember);
            Add($"{itemPath}.Unit", item.Unit!);
            Add($"{itemPath}.Value", initialValue);
            if (!isConfigParam)
                return;

            Add($"{itemPath}.IsBound", true);
            // Stryker disable once Statement: the process image starts zeroed, so FALSE is already there; the write documents the published value
            Add($"{itemPath}.HasLimits", false);
        }

        private void CheckLength(string symbolPath, string text, string what)
        {
            if (Unwrap(Resolve(symbolPath).Type) is StringType type && text.Length > type.Length)
                throw Fail(path, $"{what} '{text}' is {text.Length} characters, {symbolPath} holds {type.Length}.");
        }

        private void Add(string symbolPath, object value) => plan.Add((Resolve(symbolPath), value));

        private SymbolRef Resolve(string symbolPath) =>
            symbols.TryResolve(symbolPath, out SymbolRef symbol, out string? error) ? symbol : throw Fail(path, $"cannot resolve {symbolPath}: {error}");

        private static PlcType Unwrap(PlcType type)
        {
            while (type is AliasType alias)
                type = alias.Target;
            return type;
        }
    }
}
