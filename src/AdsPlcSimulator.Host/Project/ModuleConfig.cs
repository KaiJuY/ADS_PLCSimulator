using System.Text.Json;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Host.Project;

public sealed class ModuleConfigException(string message) : Exception(message);

// init properties with defaults: a JSON file without the field gets the default instead of null.
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
}

internal sealed record DescriptorConfig
{
    public uint Id { get; init; }

    public string? Name { get; init; }

    public string? Unit { get; init; }

    public string? DataType { get; init; }
}

/// <summary>Publishes module identity, SVIDs and ECIDs from a project's config/module-config.json into the process image.</summary>
internal static class ModuleConfigPublisher
{
    public const string RelativePath = "config/module-config.json";
    internal const string ModulesRoot = "GVL_Module.Chamber_Control";

    private const int SupportedSchemaVersion = 4;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Config data type -> (member name of E_DescriptorType, initial Value text).
    private static readonly Dictionary<string, (string Descriptor, string Value)> DataTypes = new(StringComparer.Ordinal)
    {
        ["DINT"] = ("Integer_32bit", "0"),
        ["REAL"] = ("Float_32bit", "0"),
        ["BOOL"] = ("Boolean", "FALSE"),
        ["STRING"] = ("String_80byte", ""),
    };

    /// <returns>The config path when published; null when the project has no config file.</returns>
    public static string? PublishIfPresent(string projectDirectory, SymbolTable symbols, ProcessImage image)
    {
        string path = Path.Combine(projectDirectory, "config", "module-config.json");
        if (!File.Exists(path))
            return null;

        Publish(Parse(File.ReadAllText(path), path), path, symbols, image);
        return path;
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

    internal static void Publish(ModuleConfigFile config, string path, SymbolTable symbols, ProcessImage image)
    {
        var plan = new List<(SymbolRef Symbol, object Value)>();
        var planner = new PublishPlanner(path, symbols, plan);
        foreach (ModuleConfigEntry module in config.Modules!.Where(module => module!.Enabled).Select(module => module!))
            planner.AddModule(module);

        foreach ((SymbolRef symbol, object value) in plan)
            image.WriteValue(symbol, value, WriteSource.Workflow);
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
    }

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
        if (!IsAscii(item.Name) || !IsAscii(item.Unit))
            throw Fail(path, $"{where}: name or unit of id {item.Id} contains non-ASCII characters.");
        if (!DataTypes.ContainsKey(item.DataType))
            throw Fail(path, $"{where}: dataType '{item.DataType}' of id {item.Id} is not one of DINT, REAL, BOOL, STRING.");
    }

    private static bool IsAscii(string text) => text.All(character => character < 128);

    /// <summary>Phase one of the publish: resolves every symbol and checks every limit, so nothing is written when a check fails.</summary>
    private sealed class PublishPlanner(string path, SymbolTable symbols, List<(SymbolRef Symbol, object Value)> plan)
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
        }

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
            (string descriptor, string value) = DataTypes[item.DataType!];
            CheckLength($"{itemPath}.Name", item.Name!, "name");
            CheckLength($"{itemPath}.Unit", item.Unit!, "unit");
            Add($"{itemPath}.Id", (long)item.Id);
            Add($"{itemPath}.Name", item.Name!);
            Add($"{itemPath}.DataType", descriptor);
            Add($"{itemPath}.Unit", item.Unit!);
            Add($"{itemPath}.Value", value);
            if (!isConfigParam)
                return;

            Add($"{itemPath}.IsBound", true);
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
