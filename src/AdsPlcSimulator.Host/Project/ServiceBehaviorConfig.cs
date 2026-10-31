using System.Text.Json;
using System.Text.Json.Serialization;
using AdsPlcSimulator.Core.PackML.Behavior;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Host.Project;

internal sealed record ServiceBehaviorFile
{
    public int SchemaVersion { get; init; }

    public uint ModeSvid { get; init; }

    public List<ModeConfig?>? Modes { get; init; }

    public Dictionary<string, ServiceBehaviorConfig?>? Services { get; init; } = [];
}

internal sealed record ModeConfig
{
    public long? Value { get; init; }

    public string? Name { get; init; }

    public List<string>? Allow { get; init; }

    public List<string>? Deny { get; init; }

    public bool DryRun { get; init; }
}

internal sealed record ServiceBehaviorConfig
{
    public List<RangeConfig?>? Ranges { get; init; } = [];

    public string? Duration { get; init; }

    public List<SvidWriteConfig?>? OnEnter { get; init; } = [];

    public List<SvidWriteConfig?>? BeforeComplete { get; init; } = [];

    public List<SvidWriteConfig?>? OnLeave { get; init; } = [];
}

internal sealed record RangeConfig
{
    public string? Field { get; init; }

    public long? Min { get; init; }

    public long? Max { get; init; }
}

internal sealed record SvidWriteConfig
{
    public uint Svid { get; init; }

    public ValueConfig? Value { get; init; }

    public bool DryRunZero { get; init; }
}

internal sealed record ValueConfig
{
    public double? Constant { get; init; }

    public string? Param { get; init; }

    public uint? Ecid { get; init; }

    public bool Negate { get; init; }
}

/// <summary>Reads config/service-behaviors.json (the simulator's own format, not the real PLC's) into the table the service host uses.</summary>
internal static class ServiceBehaviorLoader
{
    public const string RelativePath = "config/service-behaviors.json";

    private const int SupportedSchemaVersion = 1;
    private const long MaxModeValue = 4294967294;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
    };

    private static readonly PrimitiveKind[] IntegerKinds =
    [
        PrimitiveKind.SInt, PrimitiveKind.USInt, PrimitiveKind.Int, PrimitiveKind.UInt, PrimitiveKind.DInt,
        PrimitiveKind.UDInt, PrimitiveKind.LInt, PrimitiveKind.Byte, PrimitiveKind.Word, PrimitiveKind.DWord,
    ];

    private static readonly PrimitiveKind[] NumberKinds = [.. IntegerKinds, PrimitiveKind.Real, PrimitiveKind.LReal];

    /// <summary>Checked in order; {0} is the location of the mode.</summary>
    private static readonly (Func<ModeConfig, bool> Invalid, string Message)[] ModeRules =
    [
        (mode => mode.Value is null, "{0}: value is missing."),
        (mode => mode.Value is < 0 or > MaxModeValue, "{0}: value must be 0..4294967294."),
        (mode => string.IsNullOrWhiteSpace(mode.Name), "{0}: name is empty."),
        (mode => (mode.Allow is null) == (mode.Deny is null), "{0}: exactly one of allow and deny is required."),
    ];

    /// <summary>Checked in order; {0} is the location of the range.</summary>
    private static readonly (Func<RangeConfig, bool> Invalid, string Message)[] RangeRules =
    [
        (range => string.IsNullOrWhiteSpace(range.Field), "{0}: field is empty."),
        (range => range.Min is null || range.Max is null, "{0}: min and max are required."),
        (range => range.Min > range.Max, "{0}: min is greater than max."),
    ];

    /// <summary>Checked in order; {0} is the location of the SVID write.</summary>
    private static readonly (Func<SvidWriteConfig, bool> Invalid, string Message)[] WriteRules =
    [
        (write => write.Svid == 0, "{0}: svid must be greater than 0."),
        (write => write.Value is null, "{0}: value is missing."),
        (write => ValueSourceCount(write.Value!) != 1, "{0}: value needs exactly one of constant, param and ecid."),
        (write => write.Value!.Param is not null && string.IsNullOrWhiteSpace(write.Value.Param), "{0}: param is empty."),
        (write => write.Value!.Constant is { } constant && !double.IsFinite(constant), "{0}: constant must be a finite number."),
    ];

    /// <summary>Null when the file does not exist.</summary>
    /// <exception cref="ModuleConfigException">The file is invalid, or module-config.json is missing (moduleConfig is null).</exception>
    public static ServiceBehaviorTable? LoadIfPresent(string projectDirectory, SymbolTable symbols, ModuleConfigFile? moduleConfig)
    {
        string path = Path.Combine(projectDirectory, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            return null;
        if (moduleConfig is null)
            throw ModuleConfigPublisher.Fail(path, "needs config/module-config.json to find SVID and ECID ids.");

        return Build(Parse(File.ReadAllText(path), path), path, symbols, moduleConfig);
    }

    internal static ServiceBehaviorFile Parse(string json, string path)
    {
        ServiceBehaviorFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ServiceBehaviorFile>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw ModuleConfigPublisher.Fail(path, $"invalid JSON: {exception.Message}");
        }

        if (file is null)
            throw ModuleConfigPublisher.Fail(path, "the file is empty.");
        if (file.SchemaVersion != SupportedSchemaVersion)
            throw ModuleConfigPublisher.Fail(path, $"schemaVersion must be {SupportedSchemaVersion}, got {file.SchemaVersion}.");
        if (file.ModeSvid == 0)
            throw ModuleConfigPublisher.Fail(path, "modeSvid must be greater than 0.");
        if (file.Modes is not { Count: > 0 })
            throw ModuleConfigPublisher.Fail(path, "modes is missing, null or empty.");
        if (file.Services is null)
            throw ModuleConfigPublisher.Fail(path, "services is null.");

        ValidateModes(file.Modes, path);
        ValidateServices(file.Services, path);
        return file;
    }

    internal static ServiceBehaviorTable Build(ServiceBehaviorFile file, string path, SymbolTable symbols, ModuleConfigFile moduleConfig)
    {
        var byModule = new Dictionary<string, ModuleBehavior>();
        foreach (ModuleConfigEntry module in moduleConfig.Modules!.Where(module => module!.Enabled).Select(module => module!))
        {
            var builder = new ModuleBuilder(file, path, symbols, module);
            byModule[builder.ModulePath] = builder.Build();
        }

        return new ServiceBehaviorTable(byModule);
    }

    private static int ValueSourceCount(ValueConfig value) =>
        (value.Constant is null ? 0 : 1) + (value.Param is null ? 0 : 1) + (value.Ecid is null ? 0 : 1);

    private static void ValidateModes(List<ModeConfig?> modes, string path)
    {
        var values = new HashSet<long>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < modes.Count; index++)
        {
            string where = $"modes[{index + 1}]";
            ModeConfig mode = modes[index] ?? throw ModuleConfigPublisher.Fail(path, $"{where} is null.");
            foreach ((Func<ModeConfig, bool> invalid, string message) in ModeRules)
            {
                if (invalid(mode))
                    throw ModuleConfigPublisher.Fail(path, string.Format(message, where));
            }

            if (!values.Add(mode.Value!.Value))
                throw ModuleConfigPublisher.Fail(path, $"{where}: value {mode.Value} appears more than once.");
            if (!names.Add(mode.Name!))
                throw ModuleConfigPublisher.Fail(path, $"{where}: name '{mode.Name}' appears more than once.");
            ValidateServiceNames(mode.Allow ?? mode.Deny!, mode.Allow is null ? $"{where}.deny" : $"{where}.allow", path);
        }
    }

    private static void ValidateServiceNames(List<string> names, string where, string path)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw ModuleConfigPublisher.Fail(path, $"{where} contains an empty service name.");
            if (!seen.Add(name))
                throw ModuleConfigPublisher.Fail(path, $"{where}: '{name}' appears more than once.");
        }
    }

    private static void ValidateServices(Dictionary<string, ServiceBehaviorConfig?> services, string path)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, ServiceBehaviorConfig? service) in services)
        {
            string where = $"services.{key}";
            if (!keys.Add(key))
                throw ModuleConfigPublisher.Fail(path, $"{where} appears more than once (names are not case-sensitive).");
            if (service is null)
                throw ModuleConfigPublisher.Fail(path, $"{where} is null.");

            ValidateService(service, where, path);
        }
    }

    private static void ValidateService(ServiceBehaviorConfig service, string where, string path)
    {
        if (service.Ranges is null || service.OnEnter is null || service.BeforeComplete is null || service.OnLeave is null)
            throw ModuleConfigPublisher.Fail(path, $"{where}: ranges, onEnter, beforeComplete and onLeave must not be null.");
        if (service.Duration is not null && string.IsNullOrWhiteSpace(service.Duration))
            throw ModuleConfigPublisher.Fail(path, $"{where}: duration is empty.");

        ValidateEach(service.Ranges, $"{where}.ranges", RangeRules, path);
        ValidateEach(service.OnEnter, $"{where}.onEnter", WriteRules, path);
        ValidateEach(service.BeforeComplete, $"{where}.beforeComplete", WriteRules, path);
        ValidateEach(service.OnLeave, $"{where}.onLeave", WriteRules, path);
    }

    private static void ValidateEach<T>(List<T?> items, string where, (Func<T, bool> Invalid, string Message)[] rules, string path)
        where T : class
    {
        for (int index = 0; index < items.Count; index++)
        {
            string location = $"{where}[{index + 1}]";
            T item = items[index] ?? throw ModuleConfigPublisher.Fail(path, $"{location} is null.");
            foreach ((Func<T, bool> invalid, string message) in rules)
            {
                if (invalid(item))
                    throw ModuleConfigPublisher.Fail(path, string.Format(message, location));
            }
        }
    }

    private sealed class ModuleBuilder(ServiceBehaviorFile file, string path, SymbolTable symbols, ModuleConfigEntry module)
    {
        private const string ParamRoot = "ServiceParam";

        public string ModulePath { get; } = $"{ModuleConfigPublisher.ModulesRoot}[{module.Slot}]";

        public ModuleBehavior Build()
        {
            (string modeSource, _) = Svid(file.ModeSvid, "modeSvid", isEcid: false, anyType: true);
            var modes = new List<ModeRule>();
            for (int index = 0; index < file.Modes!.Count; index++)
                modes.Add(BuildMode(file.Modes[index]!, $"modes[{index + 1}]"));

            var services = new Dictionary<string, ServiceBehavior>();
            foreach ((string key, ServiceBehaviorConfig? service) in file.Services!)
                services[key] = BuildService(key, service!);

            return new ModuleBehavior(modeSource, modes, services);
        }

        private ModeRule BuildMode(ModeConfig mode, string where)
        {
            List<string> names = mode.Allow ?? mode.Deny!;
            foreach (string name in names)
                RequireService(name, where);

            ModeRuleKind kind = mode.Allow is null ? ModeRuleKind.DenyListed : ModeRuleKind.AllowOnly;
            return new ModeRule(mode.Value!.Value, mode.Name!, mode.DryRun, kind, new HashSet<string>(names, StringComparer.OrdinalIgnoreCase));
        }

        private ServiceBehavior BuildService(string key, ServiceBehaviorConfig service)
        {
            string where = $"services.{key}";
            RequireService(key, where);
            string param = $"{ParamRoot}.{key}";
            var ranges = service.Ranges!.Select(range => BuildRange(range!, param, where)).ToList();
            if (service.Duration is not null)
                RequireField($"{param}.{service.Duration}", where, "a TIME", type => type is PrimitiveType { Kind: PrimitiveKind.Time });

            return new ServiceBehavior(
                ranges,
                service.Duration,
                BuildWrites(service.OnEnter!, param, $"{where}.onEnter"),
                BuildWrites(service.BeforeComplete!, param, $"{where}.beforeComplete"),
                BuildWrites(service.OnLeave!, param, $"{where}.onLeave"));
        }

        private ParamRange BuildRange(RangeConfig range, string param, string where)
        {
            RequireField($"{param}.{range.Field}", where, "an integer", IsInteger);
            return new ParamRange(range.Field!, range.Min!.Value, range.Max!.Value);
        }

        private List<SvidWrite> BuildWrites(List<SvidWriteConfig?> writes, string param, string where) =>
            writes.Select((write, index) => BuildWrite(write!, param, $"{where}[{index + 1}]")).ToList();

        private SvidWrite BuildWrite(SvidWriteConfig write, string param, string where)
        {
            (string target, string dataType) = Svid(write.Svid, where, isEcid: false);
            bool isReal = dataType == "REAL";
            ValueConfig value = write.Value!;
            return new SvidWrite(target, isReal, BuildValue(value, param, where, isReal), write.DryRunZero);
        }

        private SvidValue BuildValue(ValueConfig value, string param, string where, bool targetIsReal)
        {
            if (value.Ecid is { } ecid)
            {
                (string source, _) = Svid(ecid, where, isEcid: true);
                return new SvidValue(SvidValueKind.Ecid, 0, source, value.Negate);
            }

            if (value.Param is { } field)
            {
                RequireField($"{param}.{field}", where, "an integer or REAL", type => type is PrimitiveType primitive && NumberKinds.Contains(primitive.Kind));
                return new SvidValue(SvidValueKind.Param, 0, field, value.Negate);
            }

            double constant = value.Constant!.Value;
            if (!targetIsReal && (constant != Math.Floor(constant) || constant is < int.MinValue or > int.MaxValue))
                throw Fail(where, $"constant {constant} does not fit a DINT SVID (an integer from -2147483648 to 2147483647).");
            return new SvidValue(SvidValueKind.Constant, constant, null, value.Negate);
        }

        // The SVID or ECID is found by id in module-config.json; its place in the list is the place in the array (ModuleConfigPublisher.AddList).
        private (string Target, string DataType) Svid(uint id, string where, bool isEcid, bool anyType = false)
        {
            (string list, List<DescriptorConfig?> published, string kind) = isEcid
                ? ("ModuleConfigParamList", module.ConfigParamList!, "ECID")
                : ("ModuleVariableList", module.VariableList!, "SVID");
            int position = published.FindIndex(item => item!.Id == id);
            if (position < 0)
                throw Fail(where, $"{kind} {id} is not in module-config.json.");

            string listPath = $"{ModulePath}.{list}";
            if (!symbols.TryResolve(listPath, out SymbolRef symbol, out string? error))
                throw Fail(where, $"cannot resolve {listPath}: {error}");
            if (symbol.Type.Unaliased is not ArrayType { Dimensions.Count: 1 } array)
                throw Fail(where, $"{listPath} is not a one-dimensional array.");

            string dataType = published[position]!.DataType!;
            if (!anyType && dataType is not ("DINT" or "REAL"))
                throw Fail(where, $"{kind} {id} is {dataType}, not DINT or REAL.");
            return ($"{list}[{array.Dimensions[0].LowerBound + position}].Value", dataType);
        }

        private void RequireService(string service, string where)
        {
            string full = $"{ModulePath}.ServiceCtrl.{service}";
            if (!symbols.TryResolve(full, out SymbolRef symbol, out string? error))
                throw Fail(where, $"cannot resolve {full}: {error}");
            if (symbol.Type.Unaliased.Name != "ST_ServiceCtrl")
                throw Fail(where, $"{full} is {symbol.Type.Name}, not ST_ServiceCtrl.");
        }

        private void RequireField(string relative, string where, string expected, Func<PlcType, bool> accepts)
        {
            string full = $"{ModulePath}.{relative}";
            if (!symbols.TryResolve(full, out SymbolRef symbol, out string? error))
                throw Fail(where, $"cannot resolve {full}: {error}");
            if (!accepts(symbol.Type.Unaliased))
                throw Fail(where, $"{relative} is {symbol.Type.Name}, not {expected}.");
        }

        private static bool IsInteger(PlcType type) => type is PrimitiveType primitive && IntegerKinds.Contains(primitive.Kind);

        private ModuleConfigException Fail(string where, string reason) =>
            ModuleConfigPublisher.Fail(path, $"slot {module.Slot}: {where}: {reason}");
    }
}
