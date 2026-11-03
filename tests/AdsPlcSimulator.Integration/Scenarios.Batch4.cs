using System.Diagnostics;
using AdsPlcSimulator.Core.PackML;

namespace AdsPlcSimulator.Integration;

// S34 to S37 of #19 (SingleProcess steps, step check, cycles, CupChamberClean).
internal static partial class Scenarios
{
    private const string SingleProcessParam = Module1 + ".ServiceParam.Chamber.SingleProcess";
    private const string CupCleanParam = Module1 + ".ServiceParam.Chamber.CupChamberClean";
    private const uint FixNozzle1FlowSvid = 54;
    private const uint Cup2MasterSvid = 47;
    private const uint Cup2SlaveSvid = 48;
    private const uint Cup2TopEcid = 2107;
    private const ushort AutoMode = 3;
    private const ushort Clockwise = 1;
    private const ushort CounterClockwise = 2;
    private const ushort CycleNone = 0;
    private const ushort CycleStartControl = 1;
    private const ushort CycleEndControl = 2;

    private static readonly TimeSpan SampleTolerance = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan StepTime = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CycleStepTime = TimeSpan.FromSeconds(0.5);

    private static readonly Endpoint SingleProcess = Endpoint.Service(1, "Chamber.SingleProcess");
    private static readonly Endpoint CupClean = Endpoint.Service(1, "Chamber.CupChamberClean");

    private static async Task<Checks> SingleProcessStepsAsync(PlcSession session)
    {
        var checks = new Checks();
        int speed = FindVariable(session, HolderSpeedSvid);
        int arm = FindVariable(session, Arm1Noz1FlowSvid);
        int fixedNozzle = FindVariable(session, FixNozzle1FlowSvid);
        bool published = Math.Min(speed, Math.Min(arm, fixedNozzle)) > 0;
        checks.True($"SVID {HolderSpeedSvid}, {Arm1Noz1FlowSvid} and {FixNozzle1FlowSvid} are published", published);
        if (!published)
            return checks;

        ushort mode = ReadMode(session);
        var saved = new List<(string Path, object Value)>();
        try
        {
            checks.True("SetMode 3 reaches Complete", await SetModeToAsync(session, AutoMode));
            Set(session, saved, $"{SingleProcessParam}.RecipeCount", (ushort)1);
            Set(session, saved, $"{SingleProcessParam}.ProcessStepCount", (ushort)2);
            SetStep(session, saved, SingleProcessParam, 1, 1, 10, Clockwise, 100, StepTime, 0, CycleNone, 0);
            SetStep(session, saved, SingleProcessParam, 2, 1, 0, CounterClockwise, 50, StepTime, 5, CycleNone, 0);
            await CheckStepsRunAsync(checks, session, VariableValue(speed), VariableValue(arm), VariableValue(fixedNozzle));
        }
        finally
        {
            Restore(session, saved);
            checks.True($"SetMode back to {mode}", await SetModeToAsync(session, mode));
        }

        return checks;
    }

    private static async Task CheckStepsRunAsync(Checks checks, PlcSession session, string speed, string arm, string fixedNozzle)
    {
        if (!await RequireIdleAsync(checks, session, SingleProcess))
            return;

        session.Send(SingleProcess, PackMLCommand.Start, session.NextRequestId());
        bool executing = await session.WaitForStateAsync(SingleProcess, PackMLState.Execute, Reaction);
        session.Release(SingleProcess);
        var watch = Stopwatch.StartNew();
        checks.True($"{SingleProcess.Name}: reaches Execute", executing, $"state={session.State(SingleProcess)}");

        await SampleAtAsync(checks, watch, TimeSpan.FromSeconds(0.5));
        checks.Equal("step 1: speed", "100", session.Read<string>(speed));
        checks.Equal("step 1: Arm1 nozzle 1 flow", "10", session.Read<string>(arm));
        checks.Equal("step 1: FixNozzle1 flow", "0", session.Read<string>(fixedNozzle));

        await SampleAtAsync(checks, watch, TimeSpan.FromSeconds(1.5));
        checks.Equal("step 2: speed", "-50", session.Read<string>(speed));
        checks.Equal("step 2: Arm1 nozzle 1 flow", "0", session.Read<string>(arm));
        checks.Equal("step 2: FixNozzle1 flow", "5", session.Read<string>(fixedNozzle));

        bool completed = await session.WaitForStateAsync(SingleProcess, PackMLState.Complete, NaturalLate);
        checks.True($"{SingleProcess.Name}: Complete", completed, $"state={session.State(SingleProcess)}");
        checks.Equal("after Complete: speed", "0", session.Read<string>(speed));
        checks.Equal("after Complete: Arm1 nozzle 1 flow", "0", session.Read<string>(arm));
        checks.Equal("after Complete: FixNozzle1 flow", "0", session.Read<string>(fixedNozzle));
        await session.EnsureIdleAsync(SingleProcess);
    }

    private static async Task<Checks> SingleProcessCheckAsync(PlcSession session)
    {
        var checks = new Checks();
        ushort mode = ReadMode(session);
        var saved = new List<(string Path, object Value)>();
        try
        {
            checks.True("SetMode 3 reaches Complete", await SetModeToAsync(session, AutoMode));
            Set(session, saved, $"{SingleProcessParam}.RecipeCount", (ushort)1);
            Set(session, saved, $"{SingleProcessParam}.ProcessStepCount", (ushort)1);
            SetStep(session, saved, SingleProcessParam, 1, 1, 10, Clockwise, 100, StepTime, 0, CycleNone, 0);
            Set(session, saved, $"{SingleProcessParam}.ProcessSteps[1].ArmIndex", (ushort)5);
            await CheckRefusedAsync(checks, session, SingleProcess, ParameterOutOfRange, 5, "ProcessSteps[1].ArmIndex out of range (1~4).");
        }
        finally
        {
            Restore(session, saved);
            checks.True($"SetMode back to {mode}", await SetModeToAsync(session, mode));
        }

        return checks;
    }

    private static async Task<Checks> SingleProcessCycleAsync(PlcSession session)
    {
        var checks = new Checks();
        ushort mode = ReadMode(session);
        var saved = new List<(string Path, object Value)>();
        try
        {
            checks.True("SetMode 3 reaches Complete", await SetModeToAsync(session, AutoMode));
            Set(session, saved, $"{SingleProcessParam}.RecipeCount", (ushort)1);
            Set(session, saved, $"{SingleProcessParam}.ProcessStepCount", (ushort)3);
            SetStep(session, saved, SingleProcessParam, 1, 1, 0, Clockwise, 100, CycleStepTime, 0, CycleNone, 0);
            SetStep(session, saved, SingleProcessParam, 2, 1, 0, Clockwise, 100, CycleStepTime, 0, CycleStartControl, 2);
            SetStep(session, saved, SingleProcessParam, 3, 1, 0, Clockwise, 100, CycleStepTime, 0, CycleEndControl, 0);
            await CheckCycleRunAsync(checks, session);
        }
        finally
        {
            Restore(session, saved);
            checks.True($"SetMode back to {mode}", await SetModeToAsync(session, mode));
        }

        return checks;
    }

    private static async Task CheckCycleRunAsync(Checks checks, PlcSession session)
    {
        if (!await RequireIdleAsync(checks, session, SingleProcess))
            return;

        session.Send(SingleProcess, PackMLCommand.Start, session.NextRequestId());
        bool executing = await session.WaitForStateAsync(SingleProcess, PackMLState.Execute, Reaction);
        session.Release(SingleProcess);
        var watch = Stopwatch.StartNew();
        checks.True($"{SingleProcess.Name}: reaches Execute", executing, $"state={session.State(SingleProcess)}");

        await SampleAtAsync(checks, watch, TimeSpan.FromSeconds(2.2));
        checks.True($"{SingleProcess.Name}: still Execute at 2.2 s", session.State(SingleProcess) == PackMLState.Execute, $"state={session.State(SingleProcess)}");

        bool completed = await session.WaitForStateAsync(SingleProcess, PackMLState.Complete, TimeSpan.FromSeconds(3.5) - watch.Elapsed);
        checks.True($"{SingleProcess.Name}: Complete within 3.5 s", completed, $"state={session.State(SingleProcess)}");
        await session.EnsureIdleAsync(SingleProcess);
    }

    private static async Task<Checks> CupCleanAsync(PlcSession session)
    {
        var checks = new Checks();
        int master = FindVariable(session, Cup2MasterSvid);
        int slave = FindVariable(session, Cup2SlaveSvid);
        int ecid = FindConfigParam(session, Cup2TopEcid);
        checks.True($"SVID {Cup2MasterSvid}, {Cup2SlaveSvid} and ECID {Cup2TopEcid} are published", Math.Min(master, Math.Min(slave, ecid)) > 0);
        if (master == 0 || slave == 0 || ecid == 0)
            return checks;

        string before = session.Read<string>($"{ConfigParamEntry(ecid)}.Value");
        var saved = new List<(string Path, object Value)>();
        try
        {
            checks.True($"SetConfig ECID {Cup2TopEcid} = 80", await SetConfigToAsync(session, Cup2TopEcid, "80"));
            Set(session, saved, $"{CupCleanParam}.RecipeCount", (ushort)1);
            Set(session, saved, $"{CupCleanParam}.ProcessStepCount", (ushort)1);
            SetStep(session, saved, CupCleanParam, 1, 1, 10, Clockwise, 100, StepTime, 0, CycleNone, 0);
            Set(session, saved, $"{CupCleanParam}.CleanSettings[1].TargetCupIndex", (ushort)2);
            Set(session, saved, $"{CupCleanParam}.CleanSettings[1].CleanType", (ushort)1);
            await CheckCleanRunAsync(checks, session, VariableValue(master), VariableValue(slave));
        }
        finally
        {
            Restore(session, saved);
            checks.True($"SetConfig ECID {Cup2TopEcid} back to '{before}'", await SetConfigToAsync(session, Cup2TopEcid, before));
        }

        return checks;
    }

    private static async Task CheckCleanRunAsync(Checks checks, PlcSession session, string master, string slave)
    {
        if (!await RequireIdleAsync(checks, session, CupClean))
            return;

        session.Send(CupClean, PackMLCommand.Start, session.NextRequestId());
        bool executing = await session.WaitForStateAsync(CupClean, PackMLState.Execute, Reaction);
        session.Release(CupClean);
        checks.True($"{CupClean.Name}: reaches Execute", executing, $"state={session.State(CupClean)}");
        checks.Equal($"{CupClean.Name}: SVID {Cup2MasterSvid} in Execute", "80.0", session.Read<string>(master));
        checks.Equal($"{CupClean.Name}: SVID {Cup2SlaveSvid} in Execute", "80.0", session.Read<string>(slave));

        bool completed = await session.WaitForStateAsync(CupClean, PackMLState.Complete, NaturalLate);
        checks.True($"{CupClean.Name}: Complete", completed, $"state={session.State(CupClean)}");
        await session.EnsureIdleAsync(CupClean);
    }

    private static ushort ReadMode(PlcSession session) =>
        ushort.TryParse(session.Read<string>(Svid), out ushort current) ? current : (ushort)1;

    private static async Task SampleAtAsync(Checks checks, Stopwatch watch, TimeSpan at)
    {
        TimeSpan left = at - watch.Elapsed;
        if (left > TimeSpan.Zero)
            await Task.Delay(left);

        TimeSpan late = watch.Elapsed - at;
        checks.True($"sample at {at.TotalSeconds:0.0} s is taken within {SampleTolerance.TotalMilliseconds:0} ms", late <= SampleTolerance, $"late by {late.TotalMilliseconds:0} ms");
    }

    private static void Set<T>(PlcSession session, List<(string Path, object Value)> saved, string path, T value) where T : notnull
    {
        saved.Add((path, session.Read<T>(path)));
        session.Write(path, value);
    }

    private static void Restore(PlcSession session, List<(string Path, object Value)> saved)
    {
        for (int k = saved.Count - 1; k >= 0; k--)
            session.Write(saved[k].Path, saved[k].Value);
    }

    private static void SetStep(
        PlcSession session, List<(string Path, object Value)> saved, string param, int step,
        ushort armNozzle, int armSetpoint, ushort direction, double speed, TimeSpan time, int fixed1, ushort control, ushort count)
    {
        string at = $"{param}.ProcessSteps[{step}]";
        Set(session, saved, $"{at}.ArmIndex", (ushort)1);
        Set(session, saved, $"{at}.RecipeIndex", (ushort)1);
        Set(session, saved, $"{at}.ArmNozzleIndex", armNozzle);
        Set(session, saved, $"{at}.ArmNozzleSetpoint", armSetpoint);
        Set(session, saved, $"{at}.ProcessTime", time);
        Set(session, saved, $"{at}.RotateDirection", direction);
        Set(session, saved, $"{at}.RotateSpeed", speed);
        Set(session, saved, $"{at}.FixedNozzleSetpoint[1]", fixed1);
        Set(session, saved, $"{at}.CupDrainIndex", (ushort)1);
        Set(session, saved, $"{at}.CycleControl", control);
        Set(session, saved, $"{at}.CycleCount", count);
    }
}
