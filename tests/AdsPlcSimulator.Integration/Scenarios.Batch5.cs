using AdsPlcSimulator.Core.PackML;

namespace AdsPlcSimulator.Integration;

// S38 and S39 of #23: a positioning service interrupted by Hold/Unhold or Abort, for the Simulation page's sim commands.
internal static partial class Scenarios
{
    private static readonly TimeSpan MidMove = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HeldFor = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FullMove = TimeSpan.FromSeconds(8);

    private static async Task<Checks> Cup1TopHoldAsync(PlcSession session)
    {
        var checks = new Checks();
        if (!await StartMidMoveAsync(checks, session, Cup1Top))
            return checks;

        checks.True($"{Cup1Top.Name}: Hold reaches Held", await session.RunSettledAsync(Cup1Top, PackMLCommand.Hold, PackMLState.Held), $"state={session.State(Cup1Top)}");
        await Task.Delay(HeldFor);
        checks.True($"{Cup1Top.Name}: Unhold reaches Execute", await session.RunSettledAsync(Cup1Top, PackMLCommand.Unhold, PackMLState.Execute), $"state={session.State(Cup1Top)}");
        checks.True($"{Cup1Top.Name}: reaches Complete after Unhold", await session.WaitForStateAsync(Cup1Top, PackMLState.Complete, FullMove), $"state={session.State(Cup1Top)}");
        await session.EnsureIdleAsync(Cup1Top);
        return checks;
    }

    private static async Task<Checks> Cup1TopAbortAsync(PlcSession session)
    {
        var checks = new Checks();
        if (!await StartMidMoveAsync(checks, session, Cup1Top))
            return checks;

        checks.True($"{Cup1Top.Name}: Abort reaches Aborted", await session.RunSettledAsync(Cup1Top, PackMLCommand.Abort, PackMLState.Aborted), $"state={session.State(Cup1Top)}");
        await session.EnsureIdleAsync(Cup1Top);
        return checks;
    }

    private static async Task<bool> StartMidMoveAsync(Checks checks, PlcSession session, Endpoint service)
    {
        if (!await RequireIdleAsync(checks, session, service))
            return false;

        session.Send(service, PackMLCommand.Start, session.NextRequestId());
        bool executing = await session.WaitForStateAsync(service, PackMLState.Execute, Reaction);
        session.Release(service);
        checks.True($"{service.Name}: reaches Execute", executing, $"state={session.State(service)}");
        if (executing)
            await Task.Delay(MidMove);
        return executing;
    }
}
