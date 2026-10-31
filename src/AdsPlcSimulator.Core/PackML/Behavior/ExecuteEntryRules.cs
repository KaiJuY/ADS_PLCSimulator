using System.Globalization;
using AdsPlcSimulator.Core.PackML.ConfigParam;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.PackML.Behavior;

internal readonly record struct EntryCheck(uint MainErrorId, uint SourceErrorId, string Message)
{
    public static readonly EntryCheck Pass;

    public bool Passed => MainErrorId == 0;
}

internal class ModuleRules
{
    public static readonly ModuleRules None = new NoModuleRules();

    private const uint ModeNotAllowedId = 2;
    private const uint InvalidModeSource = uint.MaxValue;

    private readonly SymbolRef _mode;
    private readonly Dictionary<long, ModeRule> _rules;
    private readonly Dictionary<string, ServiceRules> _services;

    private ModuleRules()
    {
        _rules = [];
        _services = [];
    }

    private ModuleRules(SymbolRef mode, Dictionary<long, ModeRule> rules, Dictionary<string, ServiceRules> services)
    {
        _mode = mode;
        _rules = rules;
        _services = services;
    }

    /// <summary>Returns ModuleRules.None when behavior is null. Services entries for paths that are not in boundRelativePaths are skipped (those services never run).</summary>
    /// <exception cref="ServiceHostException">A path of a bound service entry, or ModeSource, does not resolve.</exception>
    public static ModuleRules Create(SymbolTable symbols, string modulePath, ModuleBehavior? behavior, IReadOnlyCollection<string> boundRelativePaths)
    {
        if (behavior is null)
            return None;

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
    public virtual EntryCheck CheckMode(string relativeService, ProcessImage image, out bool dryRun)
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

    public ServiceRules For(string relativeService) => _services.GetValueOrDefault(relativeService) ?? ServiceRules.None;

    internal static SymbolRef Resolve(SymbolTable symbols, string path) =>
        symbols.TryResolve(path, out SymbolRef symbol, out string? error)
            ? symbol
            : throw new ServiceHostException($"Service behavior cannot resolve {path}: {error}");

    private sealed class NoModuleRules : ModuleRules
    {
        public override EntryCheck CheckMode(string relativeService, ProcessImage image, out bool dryRun)
        {
            dryRun = false;
            return EntryCheck.Pass;
        }
    }
}

internal sealed class ServiceRules
{
    public static readonly ServiceRules None = new();

    private const uint ParamOutOfRangeId = 1;

    private readonly List<ResolvedRange> _ranges;
    private readonly SymbolRef? _duration;
    private readonly string? _durationField;
    private readonly List<ResolvedWrite> _onEnter;
    private readonly List<ResolvedWrite> _beforeComplete;
    private readonly List<ResolvedWrite> _onLeave;

    // Stryker disable once Block: ServiceRules.None is a static instance created once per test process, so the per-test mutant switch of Stryker cannot reach this constructor
    private ServiceRules()
    {
        _ranges = [];
        _onEnter = [];
        _beforeComplete = [];
        _onLeave = [];
    }

    private ServiceRules(
        List<ResolvedRange> ranges, SymbolRef? duration, string? durationField,
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

    // Min and Max are the bounds as the field type holds them: a REAL field compares against float bounds.
    private sealed record ResolvedRange(SymbolRef Symbol, ParamRange Range, bool IsInteger, double Min, double Max);

    public static ServiceRules Create(SymbolTable symbols, string modulePath, string relativeService, ServiceBehavior behavior)
    {
        string param = $"{modulePath}.ServiceParam.{relativeService}";
        List<ResolvedWrite> ResolveAll(IReadOnlyList<SvidWrite> writes) =>
            writes.Select(write => ResolveWrite(symbols, modulePath, param, write)).ToList();

        return new ServiceRules(
            behavior.Ranges.Select(range => ResolveRange(symbols, param, range)).ToList(),
            behavior.DurationField is null ? null : ModuleRules.Resolve(symbols, $"{param}.{behavior.DurationField}"),
            behavior.DurationField,
            ResolveAll(behavior.OnEnter),
            ResolveAll(behavior.BeforeComplete),
            ResolveAll(behavior.OnLeave));
    }

    public EntryCheck CheckParams(ProcessImage image)
    {
        foreach (ResolvedRange range in _ranges)
        {
            EntryCheck check = range.IsInteger ? CheckInteger(range, image) : CheckReal(range, image);
            if (!check.Passed)
                return check;
        }

        // TIME is an unsigned millisecond count, so "greater than 0" means "not 0".
        if (_duration is { } duration && PlcValue.ToInt64(image.ReadValue(duration)) == 0)
            return new EntryCheck(ParamOutOfRangeId, 0, $"{_durationField} must be greater than 0.");
        return EntryCheck.Pass;
    }

    // The Host allows only integers within +-2^53 as bounds of an integer field, so the long casts are exact.
    private static EntryCheck CheckInteger(ResolvedRange range, ProcessImage image)
    {
        long value = PlcValue.ToInt64(image.ReadValue(range.Symbol));
        long min = (long)range.Min, max = (long)range.Max;
        return value < min || value > max
            ? new EntryCheck(ParamOutOfRangeId, unchecked((uint)value), $"{range.Range.Field} out of range ({min}~{max}).")
            : EntryCheck.Pass;
    }

    // NaN fails both comparisons, so it is out of range.
    private static EntryCheck CheckReal(ResolvedRange range, ProcessImage image)
    {
        double value = PlcValue.ToDouble(image.ReadValue(range.Symbol));
        return value >= range.Min && value <= range.Max
            ? EntryCheck.Pass
            : new EntryCheck(ParamOutOfRangeId, SourceErrorId(value), $"{range.Range.Field} out of range ({Text(range.Range.Min)}~{Text(range.Range.Max)}).");
    }

    // A value that is not finite has no integer part; 4294967295 is the same "cannot be shown" id as an invalid Mode text.
    private static uint SourceErrorId(double value) =>
        double.IsFinite(value) ? unchecked((uint)(long)Math.Truncate(value)) : uint.MaxValue;

    private static string Text(double bound) => bound.ToString(CultureInfo.InvariantCulture);

    private static ResolvedRange ResolveRange(SymbolTable symbols, string param, ParamRange range)
    {
        SymbolRef symbol = ModuleRules.Resolve(symbols, $"{param}.{range.Field}");
        PrimitiveKind? kind = (symbol.Type.Unaliased as PrimitiveType)?.Kind;
        return kind switch
        {
            PrimitiveKind.Real => new ResolvedRange(symbol, range, false, (float)range.Min, (float)range.Max),
            PrimitiveKind.LReal => new ResolvedRange(symbol, range, false, range.Min, range.Max),
            _ => new ResolvedRange(symbol, range, true, range.Min, range.Max),
        };
    }

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
            SvidValueKind.Param or SvidValueKind.AddParam => $"{param}.{write.Value.Source}",
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
        if (value.Kind == SvidValueKind.AddParam)
            return ParseNumber((string)image.ReadValue(write.Target)) + Step(write, image);   // SVID Value is STRING(80); Negate is applied inside Step only

        double number = value.Kind switch
        {
            SvidValueKind.Param => PlcValue.ToDouble(image.ReadValue(write.Source!.Value)),
            SvidValueKind.Ecid => ParseNumber((string)image.ReadValue(write.Source!.Value)),
            _ => value.Constant,
        };
        return value.Negate && number != 0 ? -number : number;
    }

    // The field value to add; a NaN or infinite field adds 0.
    private static double Step(ResolvedWrite write, ProcessImage image)
    {
        double field = PlcValue.ToDouble(image.ReadValue(write.Source!.Value));
        if (!double.IsFinite(field))
            return 0;
        return write.Write.Value.Negate ? -field : field;
    }

    private static double ParseNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : 0;

    private static string Format(ResolvedWrite write, double number)
    {
        if (write.Write.IsReal)
            return ConfigParamValue.FormatFloat32(number);

        double rounded = Math.Clamp(Math.Round(number, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);
        return ((long)rounded).ToString(CultureInfo.InvariantCulture);
    }
}
