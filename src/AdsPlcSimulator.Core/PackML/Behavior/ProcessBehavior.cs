namespace AdsPlcSimulator.Core.PackML.Behavior;

/// <param name="CountField">Step count field relative to ServiceParam.&lt;service&gt;, e.g. "ProcessStepCount".</param>
/// <param name="MaxCount">The count must be 1..MaxCount; step paths are resolved for i = 1..MaxCount.</param>
/// <param name="TimeField">TIME step field, e.g. "ProcessSteps[i].ProcessTime".</param>
/// <param name="Cycle">Null: no cycles.</param>
public sealed record ProcessBehavior(
    string CountField, int MaxCount, string TimeField, ProcessCycle? Cycle,
    IReadOnlyList<ProcessCheck> Checks, IReadOnlyList<ProcessWrite> Writes);

/// <param name="ControlField">e.g. "ProcessSteps[i].CycleControl"; StartValue opens a cycle, EndValue closes it, other values do nothing.</param>
/// <param name="CountField">e.g. "ProcessSteps[i].CycleCount"; read on the Start step.</param>
public sealed record ProcessCycle(string ControlField, long StartValue, long EndValue, string CountField);

/// <summary>True when the integer value of Field equals Value.</summary>
public sealed record ProcessCondition(string Field, long Value);

public enum ProcessCheckKind { Range, GreaterThan }

/// <param name="Max">Range: the upper bound (exactly one of Max.Constant, Max.Field, Max.Lookup). GreaterThan: null.</param>
/// <param name="Min">Range: the lower bound. GreaterThan: the value must be greater than Min.</param>
/// <param name="When">Null: always checked.</param>
public sealed record ProcessCheck(string Field, ProcessCheckKind Kind, double Min, ProcessBound? Max, ProcessCondition? When);

/// <param name="Field">A path without [i], e.g. "RecipeCount".</param>
public sealed record ProcessBound(double? Constant, string? Field, KeyedChoice<double>? Lookup);

/// <summary>Picks a value by the integer values of KeyFields joined with "," (e.g. "1,3"); no key fields: the single entry under "".</summary>
public sealed record KeyedChoice<T>(IReadOnlyList<string> KeyFields, IReadOnlyDictionary<string, T> Choices);

/// <param name="Target">SVID Value paths with their type; one entry under "" for a fixed SVID.</param>
/// <param name="Keep">False: written 0 when the step ends, the run finishes or Execute is left. True: never written 0.</param>
public sealed record ProcessWrite(KeyedChoice<SvidTarget> Target, ProcessValue Value, bool DryRunZero, bool Keep, ProcessCondition? When);

/// <param name="Target">e.g. "ModuleVariableList[53].Value".</param>
public sealed record SvidTarget(string Target, bool IsReal);

public enum ProcessValueKind { Field, Ecid }

/// <param name="Field">Field kind: a step path, e.g. "ProcessSteps[i].RotateSpeed".</param>
/// <param name="Ecid">Ecid kind: ECID Value paths, e.g. "ModuleConfigParamList[276].Value".</param>
/// <param name="NegateWhen">Null: never negated.</param>
public sealed record ProcessValue(ProcessValueKind Kind, string? Field, KeyedChoice<string>? Ecid, ProcessCondition? NegateWhen);
