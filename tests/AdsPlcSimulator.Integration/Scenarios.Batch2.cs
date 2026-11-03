using AdsPlcSimulator.Core.PackML;

namespace AdsPlcSimulator.Integration;

// S26 to S29 of #17 (OutsideRing speed, Holder MoveModulo, Cup1 lift height).
internal static partial class Scenarios
{
    private static async Task<Checks> OutsideRingSpeedAsync(PlcSession session)
    {
        var checks = new Checks();
        int ecid = FindConfigParam(session, OutsideRingSpeedEcid);
        int svid = FindVariable(session, OutsideRingSpeedSvid);
        checks.True($"ECID {OutsideRingSpeedEcid} and SVID {OutsideRingSpeedSvid} are published", ecid > 0 && svid > 0);
        if (ecid == 0 || svid == 0)
            return checks;

        string speed = $"{Module1}.ModuleVariableList[{svid}].Value";
        string before = session.Read<string>($"{ConfigParamEntry(ecid)}.Value");
        try
        {
            checks.True($"SetConfig ECID {OutsideRingSpeedEcid} = 20", await SetConfigToAsync(session, OutsideRingSpeedEcid, "20"));
            await CheckSpeedAsync(checks, session, OutsideRingCw, OutsideRingSpeedSvid, speed, "20");
            await CheckSpeedAsync(checks, session, OutsideRingCcw, OutsideRingSpeedSvid, speed, "-20");
        }
        finally
        {
            checks.True($"SetConfig ECID {OutsideRingSpeedEcid} back to '{before}'", await SetConfigToAsync(session, OutsideRingSpeedEcid, before));
        }

        return checks;
    }

    private static async Task<Checks> MoveModuloAsync(PlcSession session)
    {
        var checks = new Checks();
        int ecid = FindConfigParam(session, ConfigEcid);
        int svid = FindVariable(session, HolderSpeedSvid);
        checks.True($"ECID {ConfigEcid} and SVID {HolderSpeedSvid} are published", ecid > 0 && svid > 0);
        if (ecid == 0 || svid == 0)
            return checks;

        string speed = $"{Module1}.ModuleVariableList[{svid}].Value";
        double before = session.Read<double>(MoveModuloPosition);
        string speedBefore = session.Read<string>($"{ConfigParamEntry(ecid)}.Value");
        try
        {
            // ECID 3001 starts at "0", which SVID 53 already shows; 25 proves the write.
            checks.True($"SetConfig ECID {ConfigEcid} = 25", await SetConfigToAsync(session, ConfigEcid, "25"));
            session.Write(MoveModuloPosition, 400.0);
            await CheckRefusedAsync(checks, session, MoveModulo, ParameterOutOfRange, 400, "Position out of range (0~360).");
            session.Write(MoveModuloPosition, 90.0);
            await CheckModuloRunAsync(checks, session, speed, "25");
        }
        finally
        {
            session.Write(MoveModuloPosition, before);
            checks.True($"SetConfig ECID {ConfigEcid} back to '{speedBefore}'", await SetConfigToAsync(session, ConfigEcid, speedBefore));
        }

        return checks;
    }

    private static async Task CheckModuloRunAsync(Checks checks, PlcSession session, string speed, string ecidValue)
    {
        if (!await RequireIdleAsync(checks, session, MoveModulo))
            return;

        session.Send(MoveModulo, PackMLCommand.Start, session.NextRequestId());
        bool executing = await session.WaitForStateAsync(MoveModulo, PackMLState.Execute, Reaction);
        session.Release(MoveModulo);
        checks.True($"{MoveModulo.Name}: reaches Execute", executing, $"state={session.State(MoveModulo)}");
        checks.Equal($"{MoveModulo.Name}: SVID {HolderSpeedSvid} in Execute", ecidValue, session.Read<string>(speed));

        bool completed = await session.WaitForStateAsync(MoveModulo, PackMLState.Complete, NaturalLate);
        checks.True($"{MoveModulo.Name}: Complete", completed, $"state={session.State(MoveModulo)}");
        checks.Equal($"{MoveModulo.Name}: SVID {HolderSpeedSvid} after Complete", "0", session.Read<string>(speed));
        await session.EnsureIdleAsync(MoveModulo);
    }

    private static async Task<Checks> Cup1TopAsync(PlcSession session)
    {
        var checks = new Checks();
        int ecid = FindConfigParam(session, Cup1TopEcid);
        int master = FindVariable(session, Cup1MasterSvid);
        int slave = FindVariable(session, Cup1SlaveSvid);
        checks.True($"ECID {Cup1TopEcid} and SVID {Cup1MasterSvid}, {Cup1SlaveSvid} are published", ecid > 0 && master > 0 && slave > 0);
        if (ecid == 0 || master == 0 || slave == 0)
            return checks;

        string before = session.Read<string>($"{ConfigParamEntry(ecid)}.Value");
        try
        {
            checks.True($"SetConfig ECID {Cup1TopEcid} = 120", await SetConfigToAsync(session, Cup1TopEcid, "120"));
            await session.EnsureIdleAsync(Cup1Top);
            checks.True($"{Cup1Top.Name}: reaches Complete", await RunToCompleteAsync(session, Cup1Top), $"state={session.State(Cup1Top)}");
            checks.Equal($"SVID {Cup1MasterSvid} after Complete", "120.0", session.Read<string>(VariableValue(master)));
            checks.Equal($"SVID {Cup1SlaveSvid} after Complete", "120.0", session.Read<string>(VariableValue(slave)));
            await session.EnsureIdleAsync(Cup1Top);
        }
        finally
        {
            checks.True($"SetConfig ECID {Cup1TopEcid} back to '{before}'", await SetConfigToAsync(session, Cup1TopEcid, before));
        }

        return checks;
    }

    private static async Task<Checks> Cup1JogAsync(PlcSession session)
    {
        var checks = new Checks();
        int master = FindVariable(session, Cup1MasterSvid);
        int slave = FindVariable(session, Cup1SlaveSvid);
        checks.True($"SVID {Cup1MasterSvid}, {Cup1SlaveSvid} are published", master > 0 && slave > 0);
        if (master == 0 || slave == 0 || !await RequireIdleAsync(checks, session, Cup1Jog))
            return checks;

        double masterBefore = ReadHeight(session, master);
        double slaveBefore = ReadHeight(session, slave);
        double positionBefore = session.Read<double>(Cup1JogPosition);
        try
        {
            session.Write(Cup1JogPosition, JogStep);
            checks.True($"{Cup1Jog.Name}: reaches Complete", await RunToCompleteAsync(session, Cup1Jog), $"state={session.State(Cup1Jog)}");
            checks.True($"SVID {Cup1MasterSvid} grew by {JogStep}", Math.Abs(ReadHeight(session, master) - masterBefore - JogStep) < 0.001);
            checks.True($"SVID {Cup1SlaveSvid} grew by {JogStep}", Math.Abs(ReadHeight(session, slave) - slaveBefore - JogStep) < 0.001);
            await session.EnsureIdleAsync(Cup1Jog);
        }
        finally
        {
            session.Write(Cup1JogPosition, positionBefore);
        }

        return checks;
    }

    private static string VariableValue(int entry) => $"{Module1}.ModuleVariableList[{entry}].Value";

    private static double ReadHeight(PlcSession session, int entry) =>
        double.Parse(session.Read<string>(VariableValue(entry)), System.Globalization.CultureInfo.InvariantCulture);

    // The caller has made the service Idle and reads the SVIDs before calling EnsureIdleAsync.
    private static async Task<bool> RunToCompleteAsync(PlcSession session, Endpoint service)
    {
        session.Send(service, PackMLCommand.Start, session.NextRequestId());
        bool completed = await session.WaitForStateAsync(service, PackMLState.Complete, NaturalLate);
        session.Release(service);
        return completed;
    }
}
