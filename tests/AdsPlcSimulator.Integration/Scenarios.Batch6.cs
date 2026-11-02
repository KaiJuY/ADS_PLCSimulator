using AdsPlcSimulator.Core.PackML;

namespace AdsPlcSimulator.Integration;

// S40 to S43 of #24: services that the Simulation page animates, started the way a client does.
internal static partial class Scenarios
{
    private static readonly TimeSpan JogRun = TimeSpan.FromSeconds(1);

    private static async Task<Checks> Arm1JogPositiveAsync(PlcSession session)
    {
        var checks = new Checks();
        if (!await RequireIdleAsync(checks, session, Arm1JogPositive))
            return checks;

        session.Send(Arm1JogPositive, PackMLCommand.Start, session.NextRequestId());
        bool executing = await session.WaitForStateAsync(Arm1JogPositive, PackMLState.Execute, Reaction);
        session.Release(Arm1JogPositive);
        checks.True($"{Arm1JogPositive.Name}: reaches Execute", executing, $"state={session.State(Arm1JogPositive)}");
        if (executing)
        {
            await Task.Delay(JogRun);
            checks.True($"{Arm1JogPositive.Name}: Stop reaches Stopped", await session.RunSettledAsync(Arm1JogPositive, PackMLCommand.Stop, PackMLState.Stopped), $"state={session.State(Arm1JogPositive)}");
        }

        checks.True($"{Arm1JogPositive.Name}: returns to Idle", await session.EnsureIdleAsync(Arm1JogPositive), $"state={session.State(Arm1JogPositive)}");
        return checks;
    }

    private static Task<Checks> ShutterCycleAsync(PlcSession session) => CompleteEachAsync(session, ShutterOpen, ShutterClose);

    private static Task<Checks> SideVentDamperAsync(PlcSession session) => CompleteEachAsync(session, SideVent1Open, DamperSex);

    private static Task<Checks> GripperOprAsync(PlcSession session) => CompleteEachAsync(session, GripperClose, HolderOpr);

    private static async Task<Checks> CompleteEachAsync(PlcSession session, params Endpoint[] services)
    {
        var checks = new Checks();
        foreach (Endpoint service in services)
        {
            if (!await RequireIdleAsync(checks, session, service))
                return checks;

            checks.True($"{service.Name}: reaches Complete", await RunToCompleteAsync(session, service), $"state={session.State(service)}");
            checks.True($"{service.Name}: returns to Idle", await session.EnsureIdleAsync(service), $"state={session.State(service)}");
        }

        return checks;
    }
}
