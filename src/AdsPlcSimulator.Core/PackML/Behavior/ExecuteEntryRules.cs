using System.Globalization;
using AdsPlcSimulator.Core.PackML.ConfigParam;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.PackML.Behavior;

/// <summary>Result of the Execute entry check; Passed when MainErrorId is 0.</summary>
internal readonly record struct EntryCheck(uint MainErrorId, uint SourceErrorId, string Message)
{
    public static readonly EntryCheck Pass;

    public bool Passed => MainErrorId == 0;
}

/// <summary>The resolved Mode rules of one module.</summary>
internal sealed class ModuleRules
{
    private const uint ModeNotAllowedId = 2;
    private const uint InvalidModeSource = uint.MaxValue;

    private readonly SymbolRef _mode;
    private readonly Dictionary<long, ModeRule> _rules;
    private readonly Dictionary<string, ServiceRules> _services;

    private ModuleRules(SymbolRef mode, Dictionary<long, ModeRule> rules, Dictionary<string, ServiceRules> services)
    {
        _mode = mode;
        _rules = rules;
        _services = services;
    }

    /// <summary>Null when behavior is null. Services entries for paths that are not in boundRelativePaths are skipped (those services never run).</summary>
    /// <exception cref="ServiceHostException">A path of a bound service entry, or ModeSource, does not resolve.</exception>
    public static ModuleRules? Create(SymbolTable symbols, string modulePath, ModuleBehavior? behavior, IReadOnlyCollection<string> boundRelativePaths)
    {
        if (behavior is null)
            return null;

        SymbolRef mode = Resolve(symbols, $"{modulePath}.{behavior.ModeSource}");
        var rules = behavior.Modes.ToDictionary(
            rule => rule.Value,
            rule => rule with { Services = new HashSet<string>(rule.Services, StringComparer.OrdinalIgnoreCase) });
        var bound = new HashSet<string>(boundRelativePaths, StringComparer.OrdinalIgnoreCase);
        var services = new Dictionary<string, ServiceRules>(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, ServiceBehavior service) in behavior.Services.Where(entry => bound.Contains(entry.Key)))
            services[path] = ServiceRules.Create(symbols, modulePath, path, service);

        return new ModuleRules(mode, rules, services);
    }

    /// <summary>Checks the Mode text against the rules; dryRun is the DryRun of the matched rule (false when refused).</summary>
    public EntryCheck CheckMode(string relativeService, ProcessImage image, out bool dryRun)
    {
        dryRun = false;
        string text = (string)image.ReadValue(_mode);
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value) || !_rules.TryGetValue(value, out ModeRule? rule))
            return new EntryCheck(ModeNotAllowedId, InvalidModeSource, $"Not allowed in Mode '{text}'.");

        if (rule.Services.Contains(relativeService) != (rule.Kind == ModeRuleKind.AllowOnly))
            return new EntryCheck(ModeNotAllowedId, unchecked((uint)value), $"Not allowed in Mode {value} ({rule.Name}).");

        dryRun = rule.DryRun;
        return EntryCheck.Pass;
    }

    /// <summary>Null when the service has no entry in the table.</summary>
    public ServiceRules? For(string relativeService) => _services.GetValueOrDefault(relativeService);

    internal static SymbolRef Resolve(SymbolTable symbols, string path) =>
        symbols.TryResolve(path, out SymbolRef symbol, out string? error)
            ? symbol
            : throw new ServiceHostException($"Service behavior cannot resolve {path}: {error}");
}

/// <summary>The resolved behavior of one service.</summary>
internal sealed class ServiceRules
{
    private const uint ParamOutOfRangeId = 1;

    private readonly List<(SymbolRef Symbol, ParamRange Range)> _ranges;
    private readonly SymbolRef? _duration;
    private readonly string? _durationField;
    private readonly List<ResolvedWrite> _onEnter;
    private readonly List<ResolvedWrite> _beforeComplete;
    private readonly List<ResolvedWrite> _onLeave;

    private ServiceRules(
        List<(SymbolRef Symbol, ParamRange Range)> ranges, SymbolRef? duration, string? durationField,
        List<ResolvedWrite> onEnter, List<ResolvedWrite> beforeComplete, List<ResolvedWrite> onLeave)
    {
        _ranges = ranges;
        _duration = duration;
        _durationField = durationField;
        _onEnter = onEnter;
        _beforeComplete = beforeComplete;
        _onLeave = onLeave;
    }

    private sealed record ResolvedWrite(SymbolRef Target, SvidWrite Write, SymbolRef? Source);

    public static ServiceRules Create(SymbolTable symbols, string modulePath, string relativeService, ServiceBehavior behavior)
    {
        string param = $"{modulePath}.ServiceParam.{relativeService}";
        List<ResolvedWrite> ResolveAll(IReadOnlyList<SvidWrite> writes) =>
            writes.Select(write => ResolveWrite(symbols, modulePath, param, write)).ToList();

        return new ServiceRules(
            behavior.Ranges.Select(range => (ModuleRules.Resolve(symbols, $"{param}.{range.Field}"), range)).ToList(),
            behavior.DurationField is null ? null : ModuleRules.Resolve(symbols, $"{param}.{behavior.DurationField}"),
            behavior.DurationField,
            ResolveAll(behavior.OnEnter),
            ResolveAll(behavior.BeforeComplete),
            ResolveAll(behavior.OnLeave));
    }

    /// <summary>Ranges in table order, then DurationField &gt; 0.</summary>
    public EntryCheck CheckParams(ProcessImage image)
    {
        foreach ((SymbolRef symbol, ParamRange range) in _ranges)
        {
            long value = PlcValue.ToInt64(image.ReadValue(symbol));
            if (value < range.Min || value > range.Max)
                return new EntryCheck(ParamOutOfRangeId, unchecked((uint)value), $"{range.Field} out of range ({range.Min}~{range.Max}).");
        }

        // TIME is an unsigned millisecond count, so "greater than 0" means "not 0".
        if (_duration is { } duration && PlcValue.ToInt64(image.ReadValue(duration)) == 0)
            return new EntryCheck(ParamOutOfRangeId, 0, $"{_durationField} must be greater than 0.");
        return EntryCheck.Pass;
    }

    /// <summary>Null when DurationField is null.</summary>
    public TimeSpan? ReadDuration(ProcessImage image) =>
        _duration is { } duration ? TimeSpan.FromMilliseconds(PlcValue.ToInt64(image.ReadValue(duration))) : null;

    public void WriteOnEnter(ProcessImage image, bool dryRun) => WriteAll(_onEnter, image, dryRun);

    public void WriteBeforeComplete(ProcessImage image, bool dryRun) => WriteAll(_beforeComplete, image, dryRun);

    public void WriteOnLeave(ProcessImage image, bool dryRun) => WriteAll(_onLeave, image, dryRun);

    private static ResolvedWrite ResolveWrite(SymbolTable symbols, string modulePath, string param, SvidWrite write)
    {
        SymbolRef target = ModuleRules.Resolve(symbols, $"{modulePath}.{write.Target}");
        string? source = write.Value.Kind switch
        {
            SvidValueKind.Param => $"{param}.{write.Value.Source}",
            SvidValueKind.Ecid => $"{modulePath}.{write.Value.Source}",
            _ => null,
        };
        return new ResolvedWrite(target, write, source is null ? null : ModuleRules.Resolve(symbols, source));
    }

    private static void WriteAll(List<ResolvedWrite> writes, ProcessImage image, bool dryRun)
    {
        foreach (ResolvedWrite write in writes)
            image.WriteValue(write.Target, Format(write, Number(write, image, dryRun)), WriteSource.Workflow);
    }

    private static double Number(ResolvedWrite write, ProcessImage image, bool dryRun)
    {
        if (write.Write.DryRunZero && dryRun)
            return 0;

        SvidValue value = write.Write.Value;
        double number = value.Kind switch
        {
            SvidValueKind.Param => PlcValue.ToDouble(image.ReadValue(write.Source!.Value)),
            SvidValueKind.Ecid => ParseEcid((string)image.ReadValue(write.Source!.Value)),
            _ => value.Constant,
        };
        return value.Negate && number != 0 ? -number : number;
    }

    private static double ParseEcid(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : 0;

    private static string Format(ResolvedWrite write, double number)
    {
        if (write.Write.IsReal)
            return ConfigParamValue.FormatFloat32(number);

        double rounded = Math.Clamp(Math.Round(number, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);
        return ((long)rounded).ToString(CultureInfo.InvariantCulture);
    }
}
