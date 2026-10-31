using System.Globalization;
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

    public ProcessConfig? Process { get; init; }
}

internal sealed record RangeConfig
{
    public string? Field { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    internal static readonly (Func<RangeConfig, bool> Invalid, string Message)[] Rules =
    [
        (range => string.IsNullOrWhiteSpace(range.Field), "{0}: field is empty."),
        (range => range.Min is null || range.Max is null, "{0}: min and max are required."),
        (range => !double.IsFinite(range.Min!.Value) || !double.IsFinite(range.Max!.Value), "{0}: min and max must be finite numbers."),
        (range => range.Min > range.Max, "{0}: min is greater than max."),
    ];
}

internal sealed record SvidWriteConfig
{
    public uint Svid { get; init; }

    public ValueConfig? Value { get; init; }

    public bool DryRunZero { get; init; }

    internal static readonly (Func<SvidWriteConfig, bool> Invalid, string Message)[] Rules =
    [
        (write => write.Svid == 0, "{0}: svid must be greater than 0."),
        (write => write.Value is null, "{0}: value is missing."),
        (write => write.Value!.SourceCount() != 1, "{0}: value needs exactly one of constant, param, ecid and addParam."),
        (write => write.Value!.Param is not null && string.IsNullOrWhiteSpace(write.Value.Param), "{0}: param is empty."),
        (write => write.Value!.AddParam is not null && string.IsNullOrWhiteSpace(write.Value.AddParam), "{0}: addParam is empty."),
        (write => write.Value!.Constant is { } constant && !double.IsFinite(constant), "{0}: constant must be a finite number."),
    ];
}

internal sealed record ValueConfig
{
    public double? Constant { get; init; }

    public string? Param { get; init; }

    public uint? Ecid { get; init; }

    public string? AddParam { get; init; }

    public bool Negate { get; init; }

    internal int SourceCount() =>
        (Constant is null ? 0 : 1) + (Param is null ? 0 : 1) + (Ecid is null ? 0 : 1) + (AddParam is null ? 0 : 1);
}

internal sealed record ProcessConfig
{
    public string? Count { get; init; }

    public int? MaxCount { get; init; }

    public string? Time { get; init; }

    public CycleConfig? Cycle { get; init; }

    public List<ProcessCheckConfig?>? Checks { get; init; } = [];

    public List<ProcessWriteConfig?>? Writes { get; init; } = [];

    internal static readonly (Func<ProcessConfig, bool> Invalid, string Message)[] Rules =
    [
        (process => string.IsNullOrWhiteSpace(process.Count) || string.IsNullOrWhiteSpace(process.Time), "{0}: count and time are required."),
        (process => process.MaxCount is null or < 1, "{0}: maxCount must be 1 or more."),
        (process => process.Checks is null || process.Writes is null, "{0}: checks and writes must not be null."),
    ];
}

internal sealed record CycleConfig
{
    public string? Control { get; init; }

    public long? Start { get; init; }

    public long? End { get; init; }

    public string? Count { get; init; }

    internal static readonly (Func<CycleConfig, bool> Invalid, string Message)[] Rules =
    [
        (
            cycle => string.IsNullOrWhiteSpace(cycle.Control) || string.IsNullOrWhiteSpace(cycle.Count) || cycle.Start is null || cycle.End is null || cycle.Start == cycle.End,
            "{0}: control, start, end and count are required and start must differ from end."),
    ];
}

internal sealed record ConditionConfig
{
    public string? Field { get; init; }

    [JsonPropertyName("equals")]
    public long? EqualsValue { get; init; }

    internal static readonly (Func<ConditionConfig, bool> Invalid, string Message)[] Rules =
    [
        (condition => string.IsNullOrWhiteSpace(condition.Field) || condition.EqualsValue is null, "{0}: field and equals are required."),
    ];
}

internal sealed record LookupConfig<T>
{
    public List<string>? Key { get; init; }

    public Dictionary<string, T>? Values { get; init; }
}

internal sealed record ProcessCheckConfig
{
    public string? Field { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    public string? MaxField { get; init; }

    public LookupConfig<double>? MaxBy { get; init; }

    public double? GreaterThan { get; init; }

    public ConditionConfig? When { get; init; }

    internal static readonly (Func<ProcessCheckConfig, bool> Invalid, string Message)[] Rules =
    [
        (check => string.IsNullOrWhiteSpace(check.Field), "{0}: field is empty."),
        (check => check.GreaterThan is not null && (check.Min is not null || check.BoundCount() > 0), "{0}: greaterThan cannot be used with min, max, maxField or maxBy."),
        (check => check.GreaterThan is null && check.Min is null, "{0}: min is required."),
        (check => check.GreaterThan is null && check.BoundCount() != 1, "{0}: exactly one of max, maxField and maxBy is required."),
        (check => !new[] { check.Min, check.Max, check.GreaterThan }.All(value => value is null || double.IsFinite(value.Value)), "{0}: min, max and greaterThan must be finite numbers."),
        (check => check.Min > check.Max, "{0}: min is greater than max."),
        (check => check.MaxField is { } field && field.Contains("[i]", StringComparison.Ordinal), "{0}: maxField must not contain [i]."),
    ];

    internal int BoundCount() => (Max is null ? 0 : 1) + (MaxField is null ? 0 : 1) + (MaxBy is null ? 0 : 1);
}

internal sealed record ProcessWriteConfig
{
    public uint? Svid { get; init; }

    public LookupConfig<uint>? SvidBy { get; init; }

    public ProcessValueConfig? Value { get; init; }

    public bool DryRunZero { get; init; }

    public bool Keep { get; init; }

    public ConditionConfig? When { get; init; }

    internal static readonly (Func<ProcessWriteConfig, bool> Invalid, string Message)[] Rules =
    [
        (write => (write.Svid is null) == (write.SvidBy is null), "{0}: exactly one of svid and svidBy is required."),
        (write => write.Svid == 0, "{0}: svid must be greater than 0."),
        (write => write.Value is null, "{0}: value is missing."),
    ];
}

internal sealed record ProcessValueConfig
{
    public string? Field { get; init; }

    public uint? Ecid { get; init; }

    public LookupConfig<uint>? EcidBy { get; init; }

    public ConditionConfig? NegateWhen { get; init; }

    internal static readonly (Func<ProcessValueConfig, bool> Invalid, string Message)[] Rules =
    [
        (value => (value.Field is null ? 0 : 1) + (value.Ecid is null ? 0 : 1) + (value.EcidBy is null ? 0 : 1) != 1, "{0}: exactly one of field, ecid and ecidBy is required."),
        (value => value.Field is not null && string.IsNullOrWhiteSpace(value.Field), "{0}: field is empty."),
    ];
}

internal static class ServiceBehaviorLoader
{
    public const string RelativePath = "config/service-behaviors.json";

    private const int SupportedSchemaVersion = 1;
    private const long MaxModeValue = 4294967294;
    private const double MaxExactInteger = 1L << 53;

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

    private static readonly (Func<ModeConfig, bool> Invalid, string Message)[] ModeRules =
    [
        (mode => mode.Value is null, "{0}: value is missing."),
        (mode => mode.Value is < 0 or > MaxModeValue, "{0}: value must be 0..4294967294."),
        (mode => string.IsNullOrWhiteSpace(mode.Name), "{0}: name is empty."),
        (mode => (mode.Allow is null) == (mode.Deny is null), "{0}: exactly one of allow and deny is required."),
    ];

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

            RequireUnique(values, mode.Value!.Value, path, $"{where}: value {mode.Value} appears more than once.");
            RequireUnique(names, mode.Name!, path, $"{where}: name '{mode.Name}' appears more than once.");
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
            RequireUnique(seen, name, path, $"{where}: '{name}' appears more than once.");
        }
    }

    private static void RequireUnique<T>(HashSet<T> seen, T item, string path, string message)
    {
        if (!seen.Add(item))
            throw ModuleConfigPublisher.Fail(path, message);
    }

    private static void ValidateServices(Dictionary<string, ServiceBehaviorConfig?> services, string path)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, ServiceBehaviorConfig? service) in services)
        {
            string where = $"services.{key}";
            RequireUnique(keys, key, path, $"{where} appears more than once (names are not case-sensitive).");
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

        ValidateProcess(service, where, path);
        ValidateEach(service.Ranges, $"{where}.ranges", RangeConfig.Rules, path);
        ValidateEach(service.OnEnter, $"{where}.onEnter", SvidWriteConfig.Rules, path);
        ValidateEach(service.BeforeComplete, $"{where}.beforeComplete", SvidWriteConfig.Rules, path);
        ValidateEach(service.OnLeave, $"{where}.onLeave", SvidWriteConfig.Rules, path);
    }

    private static void ValidateEach<T>(List<T?> items, string where, (Func<T, bool> Invalid, string Message)[] rules, string path, Action<T, string>? validateMore = null)
        where T : class
    {
        for (int index = 0; index < items.Count; index++)
        {
            string location = $"{where}[{index + 1}]";
            T item = items[index] ?? throw ModuleConfigPublisher.Fail(path, $"{location} is null.");
            ApplyRules(item, location, rules, path);
            validateMore?.Invoke(item, location);
        }
    }

    private static void ApplyRules<T>(T item, string location, (Func<T, bool> Invalid, string Message)[] rules, string path)
    {
        foreach ((Func<T, bool> invalid, string message) in rules)
        {
            if (invalid(item))
                throw ModuleConfigPublisher.Fail(path, string.Format(message, location));
        }
    }

    private static void ValidateProcess(ServiceBehaviorConfig service, string where, string path)
    {
        if (service.Process is not { } process)
            return;
        if (service.Duration is not null)
            throw ModuleConfigPublisher.Fail(path, $"{where}: process and duration cannot both be set.");

        string location = $"{where}.process";
        ApplyRules(process, location, ProcessConfig.Rules, path);
        if (process.Cycle is { } cycle)
            ApplyRules(cycle, $"{location}.cycle", CycleConfig.Rules, path);

        ValidateEach(process.Checks!, $"{location}.checks", ProcessCheckConfig.Rules, path, (check, at) =>
        {
            ValidateLookup(check.MaxBy, $"{at}.maxBy", path);
            ValidateCondition(check.When, $"{at}.when", path);
        });
        ValidateEach(process.Writes!, $"{location}.writes", ProcessWriteConfig.Rules, path, (write, at) =>
        {
            ApplyRules(write.Value!, $"{at}.value", ProcessValueConfig.Rules, path);
            ValidateLookup(write.SvidBy, $"{at}.svidBy", path);
            ValidateLookup(write.Value!.EcidBy, $"{at}.value.ecidBy", path);
            ValidateCondition(write.When, $"{at}.when", path);
            ValidateCondition(write.Value.NegateWhen, $"{at}.value.negateWhen", path);
        });
    }

    private static void ValidateCondition(ConditionConfig? condition, string where, string path)
    {
        if (condition is not null)
            ApplyRules(condition, where, ConditionConfig.Rules, path);
    }

    private static void ValidateLookup<T>(LookupConfig<T>? lookup, string where, string path)
    {
        if (lookup is null)
            return;
        if (lookup.Key is not { Count: > 0 } key || key.Any(string.IsNullOrWhiteSpace))
            throw ModuleConfigPublisher.Fail(path, $"{where}: key is empty or has an empty field.");
        if (lookup.Values is not { Count: > 0 } values)
            throw ModuleConfigPublisher.Fail(path, $"{where}: values is empty.");

        foreach (string entry in values.Keys.Where(entry => !IsKeyOf(entry, key.Count)))
            throw ModuleConfigPublisher.Fail(path, $"{where}: '{entry}' is not {key.Count} integers separated by ','.");
    }

    private static bool IsKeyOf(string entry, int count)
    {
        string[] parts = entry.Split(',');
        return parts.Length == count && parts.All(part => long.TryParse(part, out long number) && number.ToString(CultureInfo.InvariantCulture) == part);
    }

    private sealed class ModuleBuilder(ServiceBehaviorFile file, string path, SymbolTable symbols, ModuleConfigEntry module)
    {
        private const string ParamRoot = "ServiceParam";

        private sealed record SvidList(string Name, List<DescriptorConfig?> Published, string Kind);

        public string ModulePath { get; } = $"{ModuleConfigPublisher.ModulesRoot}[{module.Slot}]";

        public ModuleBehavior Build()
        {
            (string modeSource, _) = Svid(file.ModeSvid, "modeSvid", Svids, AnyType);
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
                BuildWrites(service.OnLeave!, param, $"{where}.onLeave"),
                service.Process is null ? null : BuildProcess(service.Process, param, $"{where}.process"));
        }

        private ProcessBehavior BuildProcess(ProcessConfig process, string param, string where)
        {
            int max = process.MaxCount!.Value;
            RequireFirstAndLastStep(param, process.Count!, max, where, "an integer or enum", IsIntegerOrEnum);
            RequireFirstAndLastStep(param, process.Time!, max, where, "a TIME", IsTime);
            return new ProcessBehavior(
                process.Count!,
                max,
                process.Time!,
                process.Cycle is null ? null : BuildCycle(process.Cycle, param, max, $"{where}.cycle"),
                process.Checks!.Select((check, index) => BuildCheck(check!, param, max, $"{where}.checks[{index + 1}]")).ToList(),
                process.Writes!.Select((write, index) => BuildProcessWrite(write!, param, max, $"{where}.writes[{index + 1}]")).ToList());
        }

        private ProcessCycle BuildCycle(CycleConfig cycle, string param, int max, string where)
        {
            RequireFirstAndLastStep(param, cycle.Control!, max, where, "an integer or enum", IsIntegerOrEnum);
            RequireFirstAndLastStep(param, cycle.Count!, max, where, "an integer or enum", IsIntegerOrEnum);
            return new ProcessCycle(cycle.Control!, cycle.Start!.Value, cycle.End!.Value, cycle.Count!);
        }

        private ProcessCheck BuildCheck(ProcessCheckConfig check, string param, int max, string where)
        {
            RequireFirstAndLastStep(param, check.Field!, max, where, "a number, enum or TIME", IsNumberEnumOrTime);
            ProcessCondition? when = BuildCondition(check.When, param, max, $"{where}.when");
            if (check.GreaterThan is { } greaterThan)
                return new ProcessCheck(check.Field!, ProcessCheckKind.GreaterThan, greaterThan, null, when);

            return new ProcessCheck(check.Field!, ProcessCheckKind.Range, check.Min!.Value, BuildBound(check, param, max, where), when);
        }

        private ProcessBound BuildBound(ProcessCheckConfig check, string param, int max, string where)
        {
            if (check.MaxField is { } field)
            {
                RequireField($"{param}.{field}", where, "a number", IsNumber);
                return new ProcessBound(null, field, null);
            }

            if (check.MaxBy is { } lookup)
                return new ProcessBound(null, null, BuildLookup(lookup, param, max, $"{where}.maxBy", bound => bound));

            return new ProcessBound(check.Max, null, null);
        }

        private ProcessWrite BuildProcessWrite(ProcessWriteConfig write, string param, int max, string where)
        {
            KeyedChoice<SvidTarget> target = write.Svid is { } svid
                ? Single(SvidTargetOf(svid, where))
                : BuildLookup(write.SvidBy!, param, max, $"{where}.svidBy", id => SvidTargetOf(id, where));
            return new ProcessWrite(
                target,
                BuildProcessValue(write.Value!, param, max, $"{where}.value"),
                write.DryRunZero,
                write.Keep,
                BuildCondition(write.When, param, max, $"{where}.when"));
        }

        private ProcessValue BuildProcessValue(ProcessValueConfig value, string param, int max, string where)
        {
            ProcessCondition? negateWhen = BuildCondition(value.NegateWhen, param, max, $"{where}.negateWhen");
            if (value.Field is { } field)
            {
                RequireFirstAndLastStep(param, field, max, where, "a number or enum", IsNumberOrEnum);
                return new ProcessValue(ProcessValueKind.Field, field, null, negateWhen);
            }

            KeyedChoice<string> ecid = value.Ecid is { } id
                ? Single(Svid(id, where, Ecids, IsDintOrReal).Target)
                : BuildLookup(value.EcidBy!, param, max, $"{where}.ecidBy", ecidId => Svid(ecidId, where, Ecids, IsDintOrReal).Target);
            return new ProcessValue(ProcessValueKind.Ecid, null, ecid, negateWhen);
        }

        private ProcessCondition? BuildCondition(ConditionConfig? condition, string param, int max, string where)
        {
            if (condition is null)
                return null;

            RequireFirstAndLastStep(param, condition.Field!, max, where, "an integer or enum", IsIntegerOrEnum);
            return new ProcessCondition(condition.Field!, condition.EqualsValue!.Value);
        }

        private KeyedChoice<TOut> BuildLookup<TIn, TOut>(LookupConfig<TIn> lookup, string param, int max, string where, Func<TIn, TOut> convert)
        {
            foreach (string field in lookup.Key!)
                RequireFirstAndLastStep(param, field, max, where, "an integer or enum", IsIntegerOrEnum);

            return new KeyedChoice<TOut>(lookup.Key!, lookup.Values!.ToDictionary(entry => entry.Key, entry => convert(entry.Value)));
        }

        private static KeyedChoice<T> Single<T>(T value) => new([], new Dictionary<string, T> { [""] = value });

        private SvidTarget SvidTargetOf(uint id, string where)
        {
            (string target, string dataType) = Svid(id, where, Svids, IsDintOrReal);
            return new SvidTarget(target, dataType == "REAL");
        }

        private void RequireFirstAndLastStep(string param, string path, int max, string where, string expected, Func<PlcType, bool> accepts)
        {
            RequireField($"{param}.{path.Replace("[i]", "[1]", StringComparison.Ordinal)}", where, expected, accepts);
            RequireField($"{param}.{path.Replace("[i]", $"[{max}]", StringComparison.Ordinal)}", where, expected, accepts);
        }

        private ParamRange BuildRange(RangeConfig range, string param, string where)
        {
            SymbolRef field = RequireField($"{param}.{range.Field}", where, "an integer or REAL", IsNumber);
            double min = range.Min!.Value, max = range.Max!.Value;
            if (IsInteger(field.Type.Unaliased) && !(IsExactInteger(min) && IsExactInteger(max)))
                throw Fail(where, $"{param}.{range.Field} is {field.Type.Name}, so min and max must be integers from -{MaxExactInteger:F0} to {MaxExactInteger:F0}.");
            return new ParamRange(range.Field!, min, max);
        }

        private List<SvidWrite> BuildWrites(List<SvidWriteConfig?> writes, string param, string where) =>
            writes.Select((write, index) => BuildWrite(write!, param, $"{where}[{index + 1}]")).ToList();

        private SvidWrite BuildWrite(SvidWriteConfig write, string param, string where)
        {
            (string target, string dataType) = Svid(write.Svid, where, Svids, IsDintOrReal);
            bool isReal = dataType == "REAL";
            ValueConfig value = write.Value!;
            return new SvidWrite(target, isReal, BuildValue(value, param, where, isReal), write.DryRunZero);
        }

        private SvidValue BuildValue(ValueConfig value, string param, string where, bool targetIsReal)
        {
            if (value.Ecid is { } ecid)
            {
                (string source, _) = Svid(ecid, where, Ecids, IsDintOrReal);
                return new SvidValue(SvidValueKind.Ecid, 0, source, value.Negate);
            }

            if (value.Param is { } field)
            {
                RequireField($"{param}.{field}", where, "an integer or REAL", IsNumber);
                return new SvidValue(SvidValueKind.Param, 0, field, value.Negate);
            }

            if (value.AddParam is { } addField)
            {
                RequireField($"{param}.{addField}", where, "an integer or REAL", IsNumber);
                return new SvidValue(SvidValueKind.AddParam, 0, addField, value.Negate);
            }

            double constant = value.Constant!.Value;
            if (!targetIsReal && !IsDint(constant))
                throw Fail(where, $"constant {constant} does not fit a DINT SVID (an integer from -2147483648 to 2147483647).");
            return new SvidValue(SvidValueKind.Constant, constant, null, value.Negate);
        }

        // The SVID or ECID is found by id in module-config.json; its place in the list is the place in the array (ModuleConfigPublisher.AddList).
        private (string Target, string DataType) Svid(uint id, string where, SvidList source, Func<string, bool> acceptsType)
        {
            int position = source.Published.FindIndex(item => item!.Id == id);
            if (position < 0)
                throw Fail(where, $"{source.Kind} {id} is not in module-config.json.");

            string listPath = $"{ModulePath}.{source.Name}";
            if (ResolveSymbol(listPath, where).Type.Unaliased is not ArrayType { Dimensions.Count: 1 } array)
                throw Fail(where, $"{listPath} is not a one-dimensional array.");

            string dataType = source.Published[position]!.DataType!;
            if (!acceptsType(dataType))
                throw Fail(where, $"{source.Kind} {id} is {dataType}, not DINT or REAL.");
            return ($"{source.Name}[{array.Dimensions[0].LowerBound + position}].Value", dataType);
        }

        private SymbolRef ResolveSymbol(string full, string where) =>
            symbols.TryResolve(full, out SymbolRef symbol, out string? error)
                ? symbol
                : throw Fail(where, $"cannot resolve {full}: {error}");

        private void RequireService(string service, string where)
        {
            string full = $"{ModulePath}.ServiceCtrl.{service}";
            SymbolRef symbol = ResolveSymbol(full, where);
            if (symbol.Type.Unaliased.Name != "ST_ServiceCtrl")
                throw Fail(where, $"{full} is {symbol.Type.Name}, not ST_ServiceCtrl.");
        }

        private SymbolRef RequireField(string relative, string where, string expected, Func<PlcType, bool> accepts)
        {
            SymbolRef symbol = ResolveSymbol($"{ModulePath}.{relative}", where);
            if (!accepts(symbol.Type.Unaliased))
                throw Fail(where, $"{relative} is {symbol.Type.Name}, not {expected}.");
            return symbol;
        }

        private static bool IsExactInteger(double value) => double.IsInteger(value) && Math.Abs(value) <= MaxExactInteger;

        private static bool IsInteger(PlcType type) => type is PrimitiveType primitive && IntegerKinds.Contains(primitive.Kind);

        private static bool IsNumber(PlcType type) => type is PrimitiveType primitive && NumberKinds.Contains(primitive.Kind);

        private static bool IsIntegerOrEnum(PlcType type) => IsInteger(type) || type is EnumType enumType && IsInteger(enumType.BaseType);

        private static bool IsNumberOrEnum(PlcType type) => IsNumber(type) || type is EnumType enumType && IsNumber(enumType.BaseType);

        private static bool IsTime(PlcType type) => type is PrimitiveType { Kind: PrimitiveKind.Time };

        private static bool IsNumberEnumOrTime(PlcType type) => IsNumberOrEnum(type) || IsTime(type);

        private static bool IsDint(double value) => double.IsInteger(value) && value is >= int.MinValue and <= int.MaxValue;

        private static bool IsDintOrReal(string dataType) => dataType is "DINT" or "REAL";

        private static bool AnyType(string dataType) => true;

        private SvidList Svids => new("ModuleVariableList", module.VariableList!, "SVID");

        private SvidList Ecids => new("ModuleConfigParamList", module.ConfigParamList!, "ECID");

        private ModuleConfigException Fail(string where, string reason) =>
            ModuleConfigPublisher.Fail(path, $"slot {module.Slot}: {where}: {reason}");
    }
}
