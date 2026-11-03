using AdsPlcSimulator.Core.PackML;

namespace AdsPlcSimulator.Integration;

// S30 to S33 of #18 (Arm1 rotate position and speed, Arm1 nozzle supply, Exhaust fan pressure).
internal static partial class Scenarios
{
    private const uint Arm1RotatePositionSvid = 3;
    private const uint Arm1RotateSpeedSvid = 5;
    private const uint Arm1Noz1FlowSvid = 9;
    private const uint ExhaustFan1PressureSvid = 59;
    private const uint Arm1RotateNormalSpeedEcid = 15;
    private const uint Arm1RotateJogSpeedEcid = 16;
    private const uint Arm1Noz1CenterEcid = 23;
    private const uint ExhaustFan1TargetEcid = 365;
    private const string Arm1Noz1Setpoint = Module1 + ".ServiceParam.Arm[1].Nozzle[1].ChemicalSupply.Setpoint";

    private static readonly Endpoint Arm1Noz1Center = Endpoint.Service(1, "Arm[1].Rotate.Noz1CenterPos");
    private static readonly Endpoint Arm1Noz1Supply = Endpoint.Service(1, "Arm[1].Nozzle[1].ChemicalSupply");
    private static readonly Endpoint Arm1JogNegative = Endpoint.Service(1, "Arm[1].Rotate.JogNegative");
    private static readonly Endpoint ExhaustFan1 = Endpoint.Service(1, "Exhaust.Fan[1].SupplyFlowPID");

    private static async Task<Checks> Arm1CenterAsync(PlcSession session)
    {
        var checks = new Checks();
        int normal = FindConfigParam(session, Arm1RotateNormalSpeedEcid);
        int center = FindConfigParam(session, Arm1Noz1CenterEcid);
        int position = FindVariable(session, Arm1RotatePositionSvid);
        int speed = FindVariable(session, Arm1RotateSpeedSvid);
        bool published = Math.Min(Math.Min(normal, center), Math.Min(position, speed)) > 0;
        checks.True($"ECID {Arm1RotateNormalSpeedEcid}, {Arm1Noz1CenterEcid} and SVID {Arm1RotatePositionSvid}, {Arm1RotateSpeedSvid} are published", published);
        if (!published)
            return checks;

        string normalBefore = session.Read<string>($"{ConfigParamEntry(normal)}.Value");
        string centerBefore = session.Read<string>($"{ConfigParamEntry(center)}.Value");
        try
        {
            checks.True($"SetConfig ECID {Arm1RotateNormalSpeedEcid} = 10", await SetConfigToAsync(session, Arm1RotateNormalSpeedEcid, "10"));
            checks.True($"SetConfig ECID {Arm1Noz1CenterEcid} = 45", await SetConfigToAsync(session, Arm1Noz1CenterEcid, "45"));
            await CheckCenterRunAsync(checks, session, VariableValue(position), VariableValue(speed));
        }
        finally
        {
            checks.True($"SetConfig ECID {Arm1RotateNormalSpeedEcid} back to '{normalBefore}'", await SetConfigToAsync(session, Arm1RotateNormalSpeedEcid, normalBefore));
            checks.True($"SetConfig ECID {Arm1Noz1CenterEcid} back to '{centerBefore}'", await SetConfigToAsync(session, Arm1Noz1CenterEcid, centerBefore));
        }

        return checks;
    }

    private static async Task CheckCenterRunAsync(Checks checks, PlcSession session, string position, string speed)
    {
        if (!await RequireIdleAsync(checks, session, Arm1Noz1Center))
            return;

        session.Send(Arm1Noz1Center, PackMLCommand.Start, session.NextRequestId());
        bool executing = await session.WaitForStateAsync(Arm1Noz1Center, PackMLState.Execute, Reaction);
        session.Release(Arm1Noz1Center);
        checks.True($"{Arm1Noz1Center.Name}: reaches Execute", executing, $"state={session.State(Arm1Noz1Center)}");
        checks.Equal($"{Arm1Noz1Center.Name}: SVID {Arm1RotateSpeedSvid} in Execute", "10", session.Read<string>(speed));

        bool completed = await session.WaitForStateAsync(Arm1Noz1Center, PackMLState.Complete, NaturalLate);
        checks.True($"{Arm1Noz1Center.Name}: Complete", completed, $"state={session.State(Arm1Noz1Center)}");
        checks.Equal($"{Arm1Noz1Center.Name}: SVID {Arm1RotatePositionSvid} after Complete", "45.0", session.Read<string>(position));
        checks.Equal($"{Arm1Noz1Center.Name}: SVID {Arm1RotateSpeedSvid} after Complete", "0", session.Read<string>(speed));
        await session.EnsureIdleAsync(Arm1Noz1Center);
    }

    private static async Task<Checks> Arm1SupplyAsync(PlcSession session)
    {
        var checks = new Checks();
        int entry = FindVariable(session, Arm1Noz1FlowSvid);
        checks.True($"SVID {Arm1Noz1FlowSvid} is published", entry > 0);
        if (entry == 0)
            return checks;

        ushort mode = ushort.TryParse(session.Read<string>(Svid), out ushort current) ? current : (ushort)1;
        int setpoint = session.Read<int>(Arm1Noz1Setpoint);
        try
        {
            session.Write(Arm1Noz1Setpoint, 12);
            await CheckSpeedAsync(checks, session, Arm1Noz1Supply, Arm1Noz1FlowSvid, VariableValue(entry), "12");
            checks.True("Mode 2 reached", await SwitchModeAsync(session, 2));
            await CheckSpeedAsync(checks, session, Arm1Noz1Supply, Arm1Noz1FlowSvid, VariableValue(entry), "0");
        }
        finally
        {
            session.Write(Arm1Noz1Setpoint, setpoint);
            checks.True($"SetMode back to {mode}", await SwitchModeAsync(session, mode));
        }

        return checks;
    }

    private static async Task<Checks> Arm1JogAsync(PlcSession session)
    {
        var checks = new Checks();
        int jog = FindConfigParam(session, Arm1RotateJogSpeedEcid);
        int entry = FindVariable(session, Arm1RotateSpeedSvid);
        checks.True($"ECID {Arm1RotateJogSpeedEcid} and SVID {Arm1RotateSpeedSvid} are published", jog > 0 && entry > 0);
        if (jog == 0 || entry == 0)
            return checks;

        string before = session.Read<string>($"{ConfigParamEntry(jog)}.Value");
        try
        {
            checks.True($"SetConfig ECID {Arm1RotateJogSpeedEcid} = 6", await SetConfigToAsync(session, Arm1RotateJogSpeedEcid, "6"));
            await CheckSpeedAsync(checks, session, Arm1JogNegative, Arm1RotateSpeedSvid, VariableValue(entry), "-6");
        }
        finally
        {
            checks.True($"SetConfig ECID {Arm1RotateJogSpeedEcid} back to '{before}'", await SetConfigToAsync(session, Arm1RotateJogSpeedEcid, before));
        }

        return checks;
    }

    private static async Task<Checks> ExhaustFanAsync(PlcSession session)
    {
        var checks = new Checks();
        int target = FindConfigParam(session, ExhaustFan1TargetEcid);
        int entry = FindVariable(session, ExhaustFan1PressureSvid);
        checks.True($"ECID {ExhaustFan1TargetEcid} and SVID {ExhaustFan1PressureSvid} are published", target > 0 && entry > 0);
        if (target == 0 || entry == 0)
            return checks;

        string before = session.Read<string>($"{ConfigParamEntry(target)}.Value");
        try
        {
            checks.True($"SetConfig ECID {ExhaustFan1TargetEcid} = 50", await SetConfigToAsync(session, ExhaustFan1TargetEcid, "50"));
            await CheckSpeedAsync(checks, session, ExhaustFan1, ExhaustFan1PressureSvid, VariableValue(entry), "50.0", "0.0");
        }
        finally
        {
            checks.True($"SetConfig ECID {ExhaustFan1TargetEcid} back to '{before}'", await SetConfigToAsync(session, ExhaustFan1TargetEcid, before));
        }

        return checks;
    }
}
