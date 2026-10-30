using System.Diagnostics;
using AdsPlcSimulator.Core.PackML;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;

namespace AdsPlcSimulator.Integration;

internal sealed record Scenario(string Id, string Title, Func<PlcSession, Task<Checks>> Run);

/// <summary>Scenarios S1 to S12 and S15 of .adf/scenarios/1.md; S13 and S14 are browser steps and are not here.</summary>
internal static class Scenarios
{
    private const string Gvl = "GVL_Module.Chamber_Control";
    private const string Module1 = Gvl + "[1]";
    private const string Svid = Module1 + ".ModuleVariableList[1].Value";
    private const string SetModeTarget = Module1 + ".ServiceParam.Chamber.SetMode.TargetMode";
    private const string InitialSwitchMode = Module1 + ".ServiceParam.Chamber.Initial.SwitchModeAfterFinish";

    private static readonly TimeSpan NaturalEarly = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan NaturalLate = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan HoldWindow = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan Reaction = TimeSpan.FromSeconds(2);

    private static readonly Endpoint ShutterOpen = Endpoint.Service(1, "Shutter.Open");
    private static readonly Endpoint ShutterClose = Endpoint.Service(1, "Shutter.Close");
    private static readonly Endpoint HolderMove = Endpoint.Service(1, "Holder.Rotate.MoveVelocityCW");
    private static readonly Endpoint UnboundArm = Endpoint.Service(1, "Arm[3].Rotate.NozzlePortPos");
    private static readonly Endpoint SetMode = Endpoint.Service(1, "Chamber.SetMode");
    private static readonly Endpoint Initial = Endpoint.Service(1, "Chamber.Initial");

    public static IReadOnlyList<Scenario> All { get; } =
    [
        new("S1", "Symbol size and index offset", SymbolLayoutAsync),
        new("S2", "Initial states and identity", InitialStatesAsync),
        new("S3", "Unbound slot ignores a command", UnboundSlotAsync),
        new("S4", "Module variable list", ModuleVariableAsync),
        new("S5", "Shutter.Open handshake", ShutterHandshakeAsync),
        new("S6", "MoveVelocityCW runs until Stop", HoldUntilStopAsync),
        new("S7", "Start while Execute is rejected", RejectedStartAsync),
        new("S8", "Abort, Clear, Reset chain", AbortChainAsync),
        new("S9", "Two services run in parallel", ParallelServicesAsync),
        new("S10", "SetMode TargetMode=2 writes SVID", session => SetModeAsync(session, 2, expectChange: true)),
        new("S11", "SetMode TargetMode=4 keeps SVID", session => SetModeAsync(session, 4, expectChange: false)),
        new("S12", "Initial switches mode to 3", InitialAsync),
        new("S15", "Module Stop ignores later service commands", ModuleStopAsync),
    ];

    // Expected sizes: tmc values listed in projects/Polar2Chamber/README.md. M1 and M6 share one layout.
    private static Task<Checks> SymbolLayoutAsync(PlcSession session)
    {
        var checks = new Checks();
        ISymbolLoader loader = SymbolLoaderFactory.Create(session.Client, SymbolLoaderSettings.Default);
        ISymbol? gvl = Find(loader, Gvl);
        ISymbol? module = Find(loader, Module1);
        ISymbol? open = Find(loader, Module1 + ".ServiceStatus.Shutter.Open");
        if (gvl is null || module is null || open is null)
        {
            checks.True("all three symbols are found", false, $"Chamber_Control={gvl is not null}, M1={module is not null}, Shutter.Open={open is not null}");
            return Task.FromResult(checks);
        }

        checks.Equal("Chamber_Control size", 12966720, gvl.ByteSize);
        checks.Equal("M1 size", 2161120, module.ByteSize);
        checks.Equal("Shutter.Open offset from M1", 1974172L, (long)((IProcessImageAddress)open).IndexOffset - ((IProcessImageAddress)module).IndexOffset);
        checks.Equal("Shutter.Open size", 11684, open.ByteSize);
        return Task.FromResult(checks);
    }

    private static ISymbol? Find(ISymbolLoader loader, string path) =>
        loader.Symbols.TryGetInstance(path, out ISymbol? symbol) ? symbol : null;

    // Identity: projects/Polar2Chamber/plc/ServiceIdentity/F_InitializePolarIIChamberServiceStatus.TcPOU line 178 (ShutterOpen, NaturalCompletion); 3301 from E_PolarIIChamberServiceId.
    private static Task<Checks> InitialStatesAsync(PlcSession session)
    {
        var checks = new Checks();
        checks.Equal("M1 module state", PackMLState.Execute, session.State(Endpoint.Module(1)));
        checks.Equal("M6 module state", PackMLState.Execute, session.State(Endpoint.Module(6)));
        checks.Equal("Shutter.Open state", PackMLState.Idle, session.State(ShutterOpen));
        checks.Equal("Shutter.Open ServiceId", 3301u, session.Read<uint>(ShutterOpen.Status + ".ServiceId"));
        checks.Equal("Shutter.Open CompletionBehavior", (uint)CompletionBehavior.NaturalCompletion, session.Read<uint>(ShutterOpen.Status + ".CompletionBehavior"));
        return Task.FromResult(checks);
    }

    private static async Task<Checks> UnboundSlotAsync(PlcSession session)
    {
        var checks = new Checks();
        uint responseBefore = session.ResponseId(UnboundArm);
        PackMLState stateBefore = session.State(UnboundArm);

        session.Send(UnboundArm, PackMLCommand.Start, 77);
        await session.ObserveNegativeAsync();
        session.Release(UnboundArm);

        checks.Equal("ResponseId", responseBefore, session.ResponseId(UnboundArm));
        checks.Equal("state", stateBefore, session.State(UnboundArm));
        return checks;
    }

    // Expected values: projects/Polar2Chamber/config/module-config.json (63 SVIDs for slot 1) and Polar2_ModuleModeInit.flow.json (Value '1'). They hold on a fresh Host only; S10, S12 change SVID 1 of M1.
    private static Task<Checks> ModuleVariableAsync(PlcSession session)
    {
        var checks = new Checks();
        string variable = $"{Gvl}[1].ModuleVariableList[1]";
        checks.Equal("M1 Id", 1u, session.Read<uint>(variable + ".Id"));
        checks.Equal("M1 Name", "Module Mode", session.Read<string>(variable + ".Name"));
        checks.Equal("M1 DataType (Integer_32bit)", (short)1, session.Read<short>(variable + ".DataType"));
        checks.Equal("M1 Unit", "", session.Read<string>(variable + ".Unit"));
        checks.Equal("M1 Value", "1", session.Read<string>(variable + ".Value"));
        checks.Equal("M1 PublishedCount", (ushort)63, session.Read<ushort>($"{Gvl}[1].ModuleStatus.Variable.PublishedCount"));

        checks.Equal("M6 Id", 0u, session.Read<uint>($"{Gvl}[6].ModuleVariableList[1].Id"));
        checks.Equal("M6 PublishedCount", (ushort)0, session.Read<ushort>($"{Gvl}[6].ModuleStatus.Variable.PublishedCount"));
        return Task.FromResult(checks);
    }

    private static async Task<Checks> ShutterHandshakeAsync(PlcSession session)
    {
        var checks = new Checks();
        if (!await RequireIdleAsync(checks, session, ShutterOpen))
            return checks;

        uint requestId = session.NextRequestId();
        var elapsed = Stopwatch.StartNew();
        session.Send(ShutterOpen, PackMLCommand.Start, requestId);
        bool answered = await session.WaitForResponseAsync(ShutterOpen, requestId);
        checks.True("ResponseId echoes RequestId", answered, $"ResponseId={session.ResponseId(ShutterOpen)}");
        if (!answered)
            return checks;

        checks.Equal("bCommandAccepted", true, session.Accepted(ShutterOpen));
        checks.Equal("state", PackMLState.Execute, session.State(ShutterOpen));
        checks.Equal("previous state", PackMLState.Starting, session.PreviousState(ShutterOpen));
        await CheckNaturalCompletionAsync(checks, session, ShutterOpen, elapsed);

        session.Release(ShutterOpen);
        bool cleared = await session.WaitForAsync(
            () => !session.Accepted(ShutterOpen) && !session.Rejected(ShutterOpen) && session.StateErrorId(ShutterOpen) == 0,
            Reaction);
        checks.True("Accepted, Rejected and ErrorID clear after request falls", cleared);

        await session.EnsureIdleAsync(ShutterOpen);
        return checks;
    }

    private static async Task CheckNaturalCompletionAsync(Checks checks, PlcSession session, Endpoint service, Stopwatch elapsed)
    {
        bool leftEarly = await session.WaitForAsync(() => session.State(service) != PackMLState.Execute, Remaining(NaturalEarly, elapsed));
        checks.Equal("left Execute before 4 s", false, leftEarly);

        bool completed = await session.WaitForStateAsync(service, PackMLState.Complete, Remaining(NaturalLate, elapsed));
        checks.True("Complete before 7 s", completed, $"after {elapsed.Elapsed.TotalSeconds:F1} s");
    }

    private static TimeSpan Remaining(TimeSpan limit, Stopwatch elapsed) =>
        limit > elapsed.Elapsed ? limit - elapsed.Elapsed : TimeSpan.Zero;

    private static async Task<bool> RequireIdleAsync(Checks checks, PlcSession session, Endpoint service)
    {
        bool idle = await session.EnsureIdleAsync(service);
        if (!idle)
            checks.True($"{service.Name} starts from Idle", false, $"state={session.State(service)}");
        return idle;
    }

    private static async Task<Checks> HoldUntilStopAsync(PlcSession session)
    {
        var checks = new Checks();
        if (!await RequireIdleAsync(checks, session, HolderMove))
            return checks;

        session.Send(HolderMove, PackMLCommand.Start, session.NextRequestId());
        bool executing = await session.WaitForStateAsync(HolderMove, PackMLState.Execute, Reaction);
        checks.True("reaches Execute", executing);

        bool leftExecute = await session.WaitForAsync(() => session.State(HolderMove) != PackMLState.Execute, HoldWindow);
        checks.Equal("left Execute during the 6 s hold", false, leftExecute);
        session.Release(HolderMove);

        bool stopped = await session.RunSettledAsync(HolderMove, PackMLCommand.Stop, PackMLState.Stopped);
        checks.True("Stopped after Stop", stopped);

        await session.EnsureIdleAsync(HolderMove);
        return checks;
    }

    private static async Task<Checks> RejectedStartAsync(PlcSession session)
    {
        var checks = new Checks();
        if (!await RequireIdleAsync(checks, session, HolderMove))
            return checks;

        session.Send(HolderMove, PackMLCommand.Start, session.NextRequestId());
        bool executing = await session.WaitForStateAsync(HolderMove, PackMLState.Execute, Reaction);
        session.Release(HolderMove);
        checks.True("reaches Execute", executing);
        if (!executing)
            return checks;

        uint secondId = session.NextRequestId();
        session.Send(HolderMove, PackMLCommand.Start, secondId);
        bool answered = await session.WaitForResponseAsync(HolderMove, secondId);
        checks.True("ResponseId echoes the second RequestId", answered, $"ResponseId={session.ResponseId(HolderMove)}");

        uint errorId = session.StateErrorId(HolderMove);
        checks.Equal("bCommandRejected", true, session.Rejected(HolderMove));
        checks.True("ErrorID in 1002..1010", errorId is >= 1002 and <= 1010, $"ErrorID={errorId}");
        checks.Equal("state", PackMLState.Execute, session.State(HolderMove));
        session.Release(HolderMove);

        await session.EnsureIdleAsync(HolderMove);
        return checks;
    }

    private static async Task<Checks> AbortChainAsync(PlcSession session)
    {
        var checks = new Checks();
        if (!await RequireIdleAsync(checks, session, ShutterOpen))
            return checks;

        (PackMLCommand Command, PackMLState Target, PackMLState Transition)[] steps =
        [
            (PackMLCommand.Abort, PackMLState.Aborted, PackMLState.Aborting),
            (PackMLCommand.Clear, PackMLState.Stopped, PackMLState.Clearing),
            (PackMLCommand.Reset, PackMLState.Idle, PackMLState.Resetting),
        ];
        foreach (var (command, target, transition) in steps)
        {
            bool reached = await session.RunSettledAsync(ShutterOpen, command, target);
            checks.True($"{command} reaches {target}", reached, $"state={session.State(ShutterOpen)}");
            checks.Equal($"previous state after {command}", transition, session.PreviousState(ShutterOpen));
        }
        return checks;
    }

    private static async Task<Checks> ParallelServicesAsync(PlcSession session)
    {
        var checks = new Checks();
        if (!await RequireIdleAsync(checks, session, ShutterOpen) || !await RequireIdleAsync(checks, session, ShutterClose))
            return checks;

        session.Send(ShutterOpen, PackMLCommand.Start, session.NextRequestId());
        bool openRunning = await session.WaitForStateAsync(ShutterOpen, PackMLState.Execute, Reaction);
        checks.True("Shutter.Open reaches Execute", openRunning);

        uint closeId = session.NextRequestId();
        session.Send(ShutterClose, PackMLCommand.Start, closeId);
        bool answered = await session.WaitForResponseAsync(ShutterClose, closeId);
        checks.True("Shutter.Close answers while Shutter.Open runs", answered, $"ResponseId={session.ResponseId(ShutterClose)}");
        checks.Equal("Shutter.Open still Execute", PackMLState.Execute, session.State(ShutterOpen));

        bool openDone = await session.WaitForStateAsync(ShutterOpen, PackMLState.Complete, NaturalLate);
        bool closeDone = await session.WaitForStateAsync(ShutterClose, PackMLState.Complete, NaturalLate);
        session.Release(ShutterOpen);
        session.Release(ShutterClose);
        checks.True("both reach Complete", openDone && closeDone);

        await session.EnsureIdleAsync(ShutterOpen);
        await session.EnsureIdleAsync(ShutterClose);
        return checks;
    }

    // The SetMode workflow writes SVID only for TargetMode 0..3 (Polar2_Chamber_SetMode.flow.json); with 4 the value stays.
    private static async Task<Checks> SetModeAsync(PlcSession session, ushort targetMode, bool expectChange)
    {
        var checks = new Checks();
        if (!await RequireIdleAsync(checks, session, SetMode))
            return checks;

        string before = session.Read<string>(Svid);
        session.Write(SetModeTarget, targetMode);
        session.Send(SetMode, PackMLCommand.Start, session.NextRequestId());
        bool completed = await session.WaitForStateAsync(SetMode, PackMLState.Complete, NaturalLate);
        session.Release(SetMode);
        checks.True("SetMode reaches Complete", completed, $"state={session.State(SetMode)}");

        string expected = expectChange ? targetMode.ToString() : before;
        checks.Equal("SVID Value", expected, session.Read<string>(Svid));

        await session.EnsureIdleAsync(SetMode);
        return checks;
    }

    private static async Task<Checks> InitialAsync(PlcSession session)
    {
        var checks = new Checks();
        if (!await RequireIdleAsync(checks, session, Initial))
            return checks;

        session.Write(InitialSwitchMode, (ushort)2);
        var elapsed = Stopwatch.StartNew();
        session.Send(Initial, PackMLCommand.Start, session.NextRequestId());
        bool started = await session.WaitForStateAsync(Initial, PackMLState.Execute, Reaction);
        checks.True("reaches Execute", started);
        await CheckNaturalCompletionAsync(checks, session, Initial, elapsed);
        session.Release(Initial);
        checks.Equal("SVID Value", "3", session.Read<string>(Svid));

        await session.EnsureIdleAsync(Initial);
        return checks;
    }

    private static async Task<Checks> ModuleStopAsync(PlcSession session)
    {
        var checks = new Checks();
        Endpoint module = Endpoint.Module(1);
        bool stopped = await session.RunSettledAsync(module, PackMLCommand.Stop, PackMLState.Stopped);
        checks.True("module reaches Stopped", stopped, $"state={session.State(module)}");

        uint responseBefore = session.ResponseId(ShutterOpen);
        session.Send(ShutterOpen, PackMLCommand.Start, session.NextRequestId());
        await session.ObserveNegativeAsync();
        session.Release(ShutterOpen);
        checks.Equal("Shutter.Open ResponseId after Start", responseBefore, session.ResponseId(ShutterOpen));
        return checks;
    }
}
