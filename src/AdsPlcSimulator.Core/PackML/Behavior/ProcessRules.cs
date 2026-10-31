using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.PackML.Behavior;

internal enum CycleMark { None, Start, End }

/// <summary>The step plan copied when a run starts; index 0 is unused (steps are 1-based).</summary>
internal sealed record ProcessPlan(int Count, TimeSpan[] Times, CycleMark[] Marks, long[] CycleCounts);

internal sealed class ProcessRules
{
    private static readonly TimeSpan MinStepTime = TimeSpan.FromMilliseconds(1);

    private readonly ProcessBehavior _behavior;
    private readonly StepPath _count;
    private readonly StepPath _time;
    private readonly ResolvedCycle? _cycle;
    private readonly List<ResolvedCheck> _checks;
    private readonly List<ResolvedWrite> _writes;

    private ProcessRules(
        ProcessBehavior behavior, StepPath count, StepPath time, ResolvedCycle? cycle,
        List<ResolvedCheck> checks, List<ResolvedWrite> writes)
    {
        _behavior = behavior;
        _count = count;
        _time = time;
        _cycle = cycle;
        _checks = checks;
        _writes = writes;
    }

    // ByStep index 0 = step 1; one entry when the path has no [i].
    private sealed record StepPath(SymbolRef[] ByStep)
    {
        public SymbolRef At(int step) => ByStep[Math.Min(step, ByStep.Length) - 1];
    }

    private sealed record Choice<T>(StepPath[] Keys, IReadOnlyDictionary<string, T> Choices)
    {
        public bool TryPick(ProcessImage image, int step, [MaybeNullWhen(false)] out T value)
        {
            string key = string.Join(',', Keys.Select(path => Int(image, path, step).ToString(CultureInfo.InvariantCulture)));
            return Choices.TryGetValue(key, out value);
        }
    }

    private sealed record ResolvedCycle(ProcessCycle Cycle, StepPath Control, StepPath Count);

    private sealed record ResolvedCheck(ProcessCheck Check, StepPath Field, SymbolRef? MaxField, Choice<double>? MaxBy, StepPath? WhenField);

    private sealed record ResolvedWrite(
        ProcessWrite Write, Choice<(SymbolRef Target, bool IsReal)> Target, StepPath? ValueField, Choice<SymbolRef>? Ecid, StepPath? WhenField, StepPath? NegateField);

    private sealed class Resolver(SymbolTable symbols, string modulePath, string paramPath, int maxCount)
    {
        public StepPath Path(string path)
        {
            if (!path.Contains("[i]", StringComparison.Ordinal))
                return new StepPath([ModuleRules.Resolve(symbols, $"{paramPath}.{path}")]);

            return new StepPath(Enumerable.Range(1, maxCount)
                .Select(step => ModuleRules.Resolve(symbols, $"{paramPath}.{path.Replace("[i]", $"[{step}]", StringComparison.Ordinal)}"))
                .ToArray());
        }

        public SymbolRef Module(string path) => ModuleRules.Resolve(symbols, $"{modulePath}.{path}");

        public Choice<TOut> ChoiceOf<TIn, TOut>(KeyedChoice<TIn> source, Func<TIn, TOut> convert) =>
            new(source.KeyFields.Select(Path).ToArray(), source.Choices.ToDictionary(entry => entry.Key, entry => convert(entry.Value)));
    }

    /// <exception cref="ServiceHostException">A path does not resolve (same message as ModuleRules.Resolve).</exception>
    public static ProcessRules Create(SymbolTable symbols, string modulePath, string paramPath, ProcessBehavior behavior)
    {
        var resolver = new Resolver(symbols, modulePath, paramPath, behavior.MaxCount);
        ProcessCycle? cycle = behavior.Cycle;
        return new ProcessRules(
            behavior,
            resolver.Path(behavior.CountField),
            resolver.Path(behavior.TimeField),
            cycle is null ? null : new ResolvedCycle(cycle, resolver.Path(cycle.ControlField), resolver.Path(cycle.CountField)),
            behavior.Checks.Select(check => ResolveCheck(resolver, check)).ToList(),
            behavior.Writes.Select(write => ResolveWrite(resolver, write)).ToList());
    }

    private static ResolvedCheck ResolveCheck(Resolver resolver, ProcessCheck check) =>
        new(
            check,
            resolver.Path(check.Field),
            check.Max?.Field is { } field ? resolver.Path(field).At(1) : null,
            check.Max?.Lookup is { } lookup ? resolver.ChoiceOf(lookup, value => value) : null,
            check.When is null ? null : resolver.Path(check.When.Field));

    private static ResolvedWrite ResolveWrite(Resolver resolver, ProcessWrite write)
    {
        ProcessValue value = write.Value;
        return new ResolvedWrite(
            write,
            resolver.ChoiceOf(write.Target, target => (resolver.Module(target.Target), target.IsReal)),
            value.Field is null ? null : resolver.Path(value.Field),
            value.Ecid is null ? null : resolver.ChoiceOf(value.Ecid, resolver.Module),
            write.When is null ? null : resolver.Path(write.When.Field),
            value.NegateWhen is null ? null : resolver.Path(value.NegateWhen.Field));
    }

    private static long Int(ProcessImage image, StepPath path, int step) => PlcValue.ToInt64(image.ReadValue(path.At(step)));

    private static double Number(ProcessImage image, StepPath path, int step) => PlcValue.ToDouble(image.ReadValue(path.At(step)));

    private static bool Holds(ProcessCondition? condition, StepPath? field, ProcessImage image, int step) =>
        condition is null || Int(image, field!, step) == condition.Value;

    private static string StepText(string path, int step) => path.Replace("[i]", $"[{step}]", StringComparison.Ordinal);

    public EntryCheck Check(ProcessImage image)
    {
        long count = Int(image, _count, 1);
        if (count < 1 || count > _behavior.MaxCount)
            return new EntryCheck(ServiceRules.ParamOutOfRangeId, unchecked((uint)count), $"{_behavior.CountField} out of range (1~{_behavior.MaxCount}).");

        for (int step = 1; step <= count; step++)
        {
            foreach (ResolvedCheck check in _checks)
            {
                EntryCheck result = CheckOne(check, image, step);
                if (!result.Passed)
                    return result;
            }
        }
        return CheckPairing(image, (int)count);
    }

    private EntryCheck CheckOne(ResolvedCheck check, ProcessImage image, int step)
    {
        if (!Holds(check.Check.When, check.WhenField, image, step))
            return EntryCheck.Pass;

        double value = Number(image, check.Field, step);
        string path = StepText(check.Check.Field, step);
        return check.Check.Kind == ProcessCheckKind.GreaterThan
            ? CheckGreater(check.Check.Min, value, path)
            : CheckRange(check, image, step, value, path);
    }

    private static EntryCheck CheckGreater(double min, double value, string path) =>
        value > min
            ? EntryCheck.Pass
            : new EntryCheck(ServiceRules.ParamOutOfRangeId, ServiceRules.SourceErrorId(value), $"{path} must be greater than {ServiceRules.Text(min)}.");

    private static EntryCheck CheckRange(ResolvedCheck check, ProcessImage image, int step, double value, string path)
    {
        double min = check.Check.Min;
        double max = UpperBound(check, image, step);
        return value >= min && value <= max
            ? EntryCheck.Pass
            : new EntryCheck(ServiceRules.ParamOutOfRangeId, ServiceRules.SourceErrorId(value), $"{path} out of range ({ServiceRules.Text(min)}~{ServiceRules.Text(max)}).");
    }

    private static double UpperBound(ResolvedCheck check, ProcessImage image, int step)
    {
        if (check.MaxField is { } field)
            return PlcValue.ToDouble(image.ReadValue(field));
        if (check.MaxBy is null)
            return check.Check.Max!.Constant!.Value;

        return check.MaxBy.TryPick(image, step, out double max) ? max : check.Check.Min - 1;
    }

    private EntryCheck CheckPairing(ProcessImage image, int count)
    {
        if (_cycle is null)
            return EntryCheck.Pass;

        var open = new Stack<int>();
        for (int step = 1; step <= count; step++)
        {
            long control = Int(image, _cycle.Control, step);
            if (control == _cycle.Cycle.StartValue)
                open.Push(step);
            else if (control == _cycle.Cycle.EndValue && !open.TryPop(out _))
                return PairingError(step, "End without Start");
        }
        return open.TryPeek(out int innermost) ? PairingError(innermost, "Start without End") : EntryCheck.Pass;
    }

    private EntryCheck PairingError(int step, string problem) =>
        new(ServiceRules.ParamOutOfRangeId, (uint)step, $"{StepText(_cycle!.Cycle.ControlField, step)} {problem}.");

    public ProcessRun Start(ProcessImage image, TimeSpan now, bool dryRun)
    {
        int count = (int)Int(image, _count, 1);
        var plan = new ProcessPlan(count, new TimeSpan[count + 1], new CycleMark[count + 1], new long[count + 1]);
        for (int step = 1; step <= count; step++)
        {
            plan.Times[step] = TimeSpan.FromMilliseconds(Math.Max(MinStepTime.TotalMilliseconds, Number(image, _time, step)));
            if (_cycle is null)
                continue;

            plan.Marks[step] = MarkOf(_cycle.Cycle, Int(image, _cycle.Control, step));
            plan.CycleCounts[step] = Int(image, _cycle.Count, step);
        }

        var run = new ProcessRun(this, plan, dryRun, now);
        run.Begin(image);
        return run;
    }

    private static CycleMark MarkOf(ProcessCycle cycle, long control)
    {
        if (control == cycle.StartValue)
            return CycleMark.Start;
        return control == cycle.EndValue ? CycleMark.End : CycleMark.None;
    }

    internal void WriteStep(ProcessImage image, int step, bool dryRun, List<(SymbolRef Target, bool IsReal)> written)
    {
        foreach (ResolvedWrite write in _writes)
        {
            if (!Holds(write.Write.When, write.WhenField, image, step))
                continue;
            if (!write.Target.TryPick(image, step, out (SymbolRef Target, bool IsReal) target))
                continue;

            double number = ValueOf(write, image, step, dryRun);
            image.WriteValue(target.Target, ServiceRules.Format(target.IsReal, number), WriteSource.Workflow);
            if (!write.Write.Keep)
                written.Add(target);
        }
    }

    private static double ValueOf(ResolvedWrite write, ProcessImage image, int step, bool dryRun)
    {
        if (write.Write.DryRunZero && dryRun)
            return 0;

        double number = write.ValueField is { } field ? Number(image, field, step) : EcidNumber(write, image, step);
        bool negate = write.NegateField is not null && Holds(write.Write.Value.NegateWhen, write.NegateField, image, step);
        return negate && number != 0 ? -number : number;
    }

    private static double EcidNumber(ResolvedWrite write, ProcessImage image, int step) =>
        write.Ecid!.TryPick(image, step, out SymbolRef ecid) ? ServiceRules.ParseNumber((string)image.ReadValue(ecid)) : 0;
}

internal sealed class ProcessRun
{
    private readonly ProcessRules _rules;
    private readonly ProcessPlan _plan;
    private readonly bool _dryRun;
    private readonly List<(int StartStep, long Remaining)> _cycles = [];
    private readonly List<(SymbolRef Target, bool IsReal)> _written = [];
    private int _step = 1;
    private TimeSpan _stepStarted;
    private bool _finished;

    internal ProcessRun(ProcessRules rules, ProcessPlan plan, bool dryRun, TimeSpan now)
    {
        _rules = rules;
        _plan = plan;
        _dryRun = dryRun;
        _stepStarted = now;
    }

    internal void Begin(ProcessImage image) => EnterStep(image, true);

    /// <summary>Moves past every step whose time has passed (several in one scan when they are short). True when the last step has ended; the non-Keep SVIDs of the last step are then written 0.</summary>
    public bool Advance(ProcessImage image, TimeSpan now)
    {
        while (!_finished && now - _stepStarted >= _plan.Times[_step])
        {
            _stepStarted += _plan.Times[_step];
            GoNext(image);
        }
        return _finished;
    }

    /// <summary>Writes 0 to the non-Keep SVIDs of the current step (nothing after the run has finished).</summary>
    public void Leave(ProcessImage image) => ZeroWritten(image);

    /// <summary>Unhold or Unsuspend: the current step starts again at now and its SVIDs are written again.</summary>
    public void Resume(ProcessImage image, TimeSpan now)
    {
        if (_finished)
            return;

        _stepStarted = now;
        EnterStep(image, false);
    }

    private void GoNext(ProcessImage image)
    {
        int next = NextStep();
        if (next > _plan.Count)
        {
            ZeroWritten(image);
            _finished = true;
            return;
        }

        _step = next;
        EnterStep(image, true);
    }

    private int NextStep()
    {
        if (_plan.Marks[_step] != CycleMark.End || _cycles.Count == 0)
            return _step + 1;

        (int startStep, long remaining) = _cycles[^1];
        if (remaining > 1)
        {
            _cycles[^1] = (startStep, remaining - 1);
            return startStep;
        }

        _cycles.RemoveAt(_cycles.Count - 1);
        return _step + 1;
    }

    private void EnterStep(ProcessImage image, bool openCycle)
    {
        ZeroWritten(image);
        bool starts = _plan.Marks[_step] == CycleMark.Start && (_cycles.Count == 0 || _cycles[^1].StartStep != _step);
        if (openCycle && starts)
            _cycles.Add((_step, _plan.CycleCounts[_step]));

        _rules.WriteStep(image, _step, _dryRun, _written);
    }

    private void ZeroWritten(ProcessImage image)
    {
        foreach ((SymbolRef target, bool isReal) in _written)
            image.WriteValue(target, ServiceRules.Format(isReal, 0), WriteSource.Workflow);
        _written.Clear();
    }
}
