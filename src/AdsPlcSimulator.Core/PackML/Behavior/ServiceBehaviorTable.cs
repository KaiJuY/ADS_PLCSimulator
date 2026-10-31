namespace AdsPlcSimulator.Core.PackML.Behavior;

/// <summary>Behaviors per module path ("GVL_Module.Chamber_Control[1]"); built once per SimulatorSession (= once per Run) by the Host.</summary>
public sealed class ServiceBehaviorTable(IReadOnlyDictionary<string, ModuleBehavior> byModulePath)
{
    public static readonly ServiceBehaviorTable Empty = new(new Dictionary<string, ModuleBehavior>());

    private readonly Dictionary<string, ModuleBehavior> _byModulePath =
        new(byModulePath, StringComparer.OrdinalIgnoreCase);

    /// <summary>Null when the module has none; the path match is case-insensitive.</summary>
    public ModuleBehavior? For(string modulePath) =>
        _byModulePath.TryGetValue(modulePath, out ModuleBehavior? behavior) ? behavior : null;
}

/// <param name="ModeSource">Mode text, relative to the module, e.g. "ModuleVariableList[1].Value".</param>
/// <param name="Services">Key: service path relative to ServiceCtrl, e.g. "Holder.Rotate.MoveVelocityCW".</param>
public sealed record ModuleBehavior(string ModeSource, IReadOnlyList<ModeRule> Modes, IReadOnlyDictionary<string, ServiceBehavior> Services);

/// <param name="Services">Relative service paths. AllowOnly: only these may start; DenyListed: all except these.</param>
public sealed record ModeRule(long Value, string Name, bool DryRun, ModeRuleKind Kind, IReadOnlySet<string> Services);

public enum ModeRuleKind { AllowOnly, DenyListed }

/// <param name="DurationField">TIME field relative to ServiceParam.&lt;service&gt;, e.g. "TestTime"; null = defaultDelayMs.</param>
public sealed record ServiceBehavior(
    IReadOnlyList<ParamRange> Ranges,
    string? DurationField,
    IReadOnlyList<SvidWrite> OnEnter,
    IReadOnlyList<SvidWrite> BeforeComplete,
    IReadOnlyList<SvidWrite> OnLeave);

/// <param name="Field">Integer field relative to ServiceParam.&lt;service&gt;, e.g. "ArmIndex".</param>
public sealed record ParamRange(string Field, long Min, long Max);

/// <param name="Target">SVID Value relative to the module, e.g. "ModuleVariableList[53].Value".</param>
/// <param name="IsReal">The SVID is REAL (text like "2.0"); false = DINT (integer text).</param>
/// <param name="DryRunZero">Write 0 when the Mode at Execute entry has DryRun.</param>
public sealed record SvidWrite(string Target, bool IsReal, SvidValue Value, bool DryRunZero);

/// <param name="Source">Param: field relative to ServiceParam.&lt;service&gt;; Ecid: ECID Value relative to the module, e.g. "ModuleConfigParamList[318].Value"; Constant: null.</param>
public sealed record SvidValue(SvidValueKind Kind, double Constant, string? Source, bool Negate);

public enum SvidValueKind { Constant, Param, Ecid }
