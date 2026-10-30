using AdsPlcSimulator.Core.PackML;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.PackML;

public class ServiceAliveTests
{
    private const string Module = "GVL_Test.Modules[1]";
    private const string CtrlB = "GVL_Test.Modules[1].ServiceCtrl.B";
    private const string CtrlA = "GVL_Test.Modules[1].ServiceCtrl.A";
    private const string ErrorMessage = "Service Alive timeout expired.";

    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    private static HostFixture Create(string? declarations = null, AliveSettings? alive = null) =>
        new(declarations: declarations ?? HostFixture.AlarmDeclarations, alive: alive);

    private static HostFixture StartB(long aliveTimeoutMs = 500, long aliveIndex = 0, HostFixture? fixture = null)
    {
        fixture ??= Create();
        fixture.Write($"{CtrlB}.AliveTimeout", aliveTimeoutMs);
        fixture.Write($"{CtrlB}.AliveIndex", aliveIndex);
        fixture.ServiceCommand(1, "B", PackMLCommand.Start);
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "B"));
        return fixture;
    }

    private static long ErrorId(HostFixture fixture, int index) =>
        Convert.ToInt64(fixture.Read($"{Module}.ServiceStatus.B.ErrorList[{index}].MainErrorId"));

    private static long ErrorSource(HostFixture fixture, int index) =>
        Convert.ToInt64(fixture.Read($"{Module}.ServiceStatus.B.ErrorList[{index}].SourceErrorId"));

    [Fact]
    [Trait("AC", "6")]
    public void Timeout_in_the_scan_adds_940_and_aborts_in_the_same_scan()
    {
        HostFixture fixture = StartB();

        fixture.ScanAt(Seconds(0.499));
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "B"));
        Assert.Equal(0, ErrorId(fixture, 1));

        fixture.ScanAt(Seconds(0.5));

        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "B"));
        Assert.Equal(940, ErrorId(fixture, 1));
        Assert.Equal(500, ErrorSource(fixture, 1));
        Assert.Equal(ErrorMessage, fixture.Read($"{Module}.ServiceStatus.B.ErrorList[1].Message"));
        Assert.Equal(102L, Convert.ToInt64(fixture.Read($"{Module}.ServiceStatus.B.ErrorList[1].ServiceId")));
    }

    [Fact]
    [Trait("AC", "6")]
    public void Timeout_alarm_reaches_ModuleAlarmList()
    {
        HostFixture fixture = StartB();

        fixture.ScanAt(Seconds(0.5));
        fixture.ScanAt(Seconds(0.51));

        Assert.Equal(940L, Convert.ToInt64(fixture.Read($"{Module}.ModuleAlarmList[1].MainErrorId")));
        Assert.Equal(102L, Convert.ToInt64(fixture.Read($"{Module}.ModuleAlarmList[1].ServiceId")));
    }

    [Fact]
    [Trait("AC", "5")]
    public void Index_changed_every_400_ms_keeps_the_service_in_Execute()
    {
        HostFixture fixture = StartB();

        for (int step = 1; step <= 12; step++)
        {
            fixture.Write($"{CtrlB}.AliveIndex", (long)step);
            fixture.ScanAt(Seconds(0.4 * step));
        }

        fixture.ScanAt(Seconds(5));
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "B"));
        Assert.Equal(0, ErrorId(fixture, 1));
    }

    [Fact]
    [Trait("AC", "5")]
    public void Zero_AliveTimeout_does_not_monitor()
    {
        HostFixture fixture = StartB(aliveTimeoutMs: 0);

        fixture.ScanAt(Seconds(10));

        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "B"));
    }

    [Fact]
    [Trait("AC", "5")]
    public void NaturalCompletion_service_is_not_monitored()
    {
        HostFixture fixture = Create();
        fixture.Write($"{CtrlA}.AliveTimeout", 500L);

        fixture.StartServiceA();
        fixture.ScanAt(Seconds(1));
        fixture.ScanAt(Seconds(4.9));

        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));
        Assert.Equal(0L, Convert.ToInt64(fixture.Read($"{Module}.ServiceStatus.A.ErrorList[1].MainErrorId")));
    }

    [Theory]
    [InlineData(50, 0.1, 100)]
    [InlineData(20000, 10, 10000)]
    [Trait("AC", "5")]
    public void AliveTimeout_is_limited_to_the_configured_range(long aliveTimeoutMs, double firesAt, long expectedSource)
    {
        HostFixture fixture = StartB(aliveTimeoutMs);

        fixture.ScanAt(Seconds(firesAt) - TimeSpan.FromMilliseconds(1));
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "B"));

        fixture.ScanAt(Seconds(firesAt));
        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "B"));
        Assert.Equal(expectedSource, ErrorSource(fixture, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5000)]
    [Trait("AC", "5")]
    public void AliveTimeout_changed_during_Execute_does_not_change_the_monitored_value(long changedTo)
    {
        HostFixture fixture = StartB();
        fixture.Write($"{CtrlB}.AliveTimeout", changedTo);

        fixture.ScanAt(Seconds(0.499));
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "B"));

        fixture.ScanAt(Seconds(0.5));
        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "B"));
        Assert.Equal(500, ErrorSource(fixture, 1));
    }

    [Fact]
    [Trait("AC", "6")]
    public void One_940_per_Execute_and_a_new_Execute_is_monitored_again()
    {
        HostFixture fixture = StartB();
        fixture.ScanAt(Seconds(0.5));
        fixture.ScanAt(Seconds(5));
        Assert.Equal(940, ErrorId(fixture, 1));
        Assert.Equal(0, ErrorId(fixture, 2));

        fixture.ServiceCommand(1, "B", PackMLCommand.Clear, 2);
        Assert.Equal(0, ErrorId(fixture, 1));
        fixture.ServiceCommand(1, "B", PackMLCommand.Reset, 3);
        fixture.ServiceCommand(1, "B", PackMLCommand.Start, 4);
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "B"));

        fixture.ScanAt(Seconds(5.499));
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "B"));
        fixture.ScanAt(Seconds(5.5));
        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "B"));
        Assert.Equal(940, ErrorId(fixture, 1));
    }

    [Fact]
    [Trait("AC", "6")]
    public void Client_holding_a_mode_request_does_not_stop_the_Abort()
    {
        HostFixture fixture = StartB();
        fixture.Write($"{CtrlB}.PackMLIn.bModeChangeRequest", true);

        fixture.ScanAt(Seconds(0.5));
        fixture.ScanAt(Seconds(0.51));

        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "B"));
    }

    [Theory]
    [InlineData("ErrorListHasIt")]
    [InlineData("ErrorListIsFull")]
    [InlineData("NoErrorList")]
    [Trait("AC", "6")]
    public void Error_that_cannot_be_added_still_aborts(string situation)
    {
        HostFixture fixture = Create(declarations: situation == "NoErrorList" ? HostFixture.Declarations : null);
        if (situation == "ErrorListHasIt")
            fixture.Host.AddServiceError(CtrlB, 940, 1, "already");
        if (situation == "ErrorListIsFull")
        {
            for (uint id = 1; id <= 10; id++)
                fixture.Host.AddServiceError(CtrlB, id, 0, "fill");
        }

        StartB(fixture: fixture);
        fixture.ScanAt(Seconds(0.5));

        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "B"));
        if (situation == "ErrorListHasIt")
            Assert.Equal(1, ErrorSource(fixture, 1));
        if (situation == "ErrorListIsFull")
            Assert.Equal(10, ErrorId(fixture, 10));
    }

    [Fact]
    [Trait("AC", "8")]
    public void Module_Hold_reads_the_index_as_zero_and_the_service_still_times_out()
    {
        HostFixture fixture = StartB(aliveIndex: 5);
        fixture.ScanAt(Seconds(0.3));

        fixture.ModuleCommand(1, PackMLCommand.Hold);
        Assert.Equal(PackMLState.Held, fixture.ModuleState(1));
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "B"));

        fixture.ScanAt(Seconds(0.799));
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "B"));

        fixture.ScanAt(Seconds(0.8));
        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "B"));
        Assert.Equal(940, ErrorId(fixture, 1));
        Assert.Equal(500, ErrorSource(fixture, 1));
    }

    [Fact]
    [Trait("AC", "7")]
    public void Module_Stop_leaves_Execute_in_the_same_scan_without_a_940()
    {
        HostFixture fixture = StartB();
        fixture.ScanAt(Seconds(0.2));

        fixture.SendToModule(1, PackMLCommand.Stop);
        fixture.ScanAt(Seconds(0.3));
        fixture.ReleaseModule(1);
        fixture.ScanAt(Seconds(5));

        Assert.Equal(PackMLState.Stopped, fixture.ServiceState(1, "B"));
        Assert.Equal(0, ErrorId(fixture, 1));
    }

    [Fact]
    [Trait("AC", "9")]
    public void Service_taken_over_by_a_workflow_is_monitored_too()
    {
        HostFixture fixture = Create();
        fixture.SetOverrideToken(1, "B", new object());
        StartB(fixture: fixture);

        fixture.ScanAt(Seconds(0.5));

        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "B"));
        Assert.Equal(940, ErrorId(fixture, 1));
    }

    [Fact]
    [Trait("AC", "6")]
    public void Initialize_clears_a_pending_Abort()
    {
        HostFixture fixture = StartB();
        fixture.Host.Abort(CtrlB, null);
        fixture.ScanAt(Seconds(0.5));
        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "B"));

        fixture.Host.Initialize(fixture.Image);
        fixture.ScanAt(Seconds(0.6));

        Assert.Equal(PackMLState.Idle, fixture.ServiceState(1, "B"));
    }

    [Fact]
    [Trait("AC", "6")]
    public void Timeout_and_a_client_Stop_in_one_scan_abort_first_and_Stop_is_rejected_next()
    {
        HostFixture fixture = StartB();
        fixture.SendToService(1, "B", PackMLCommand.Stop, 7);

        fixture.ScanAt(Seconds(0.5));
        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "B"));

        fixture.ScanAt(Seconds(0.51));
        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "B"));
        Assert.Equal(true, fixture.ServiceStatus(1, "B", "PackMLOut.bCommandRejected"));
        Assert.NotEqual(0L, Convert.ToInt64(fixture.ServiceStatus(1, "B", "PackMLOut.eStateErrorID")));
    }

    [Fact]
    [Trait("AC", "7")]
    public void Service_stopped_by_the_client_is_not_aborted_later()
    {
        HostFixture fixture = StartB();
        fixture.ServiceCommand(1, "B", PackMLCommand.Stop, 2);
        Assert.Equal(PackMLState.Stopped, fixture.ServiceState(1, "B"));

        fixture.ScanAt(Seconds(10));

        Assert.Equal(PackMLState.Stopped, fixture.ServiceState(1, "B"));
        Assert.Equal(0, ErrorId(fixture, 1));
    }

    [Fact]
    [Trait("AC", "6")]
    public void Pending_Abort_is_kept_after_the_service_leaves_Execute_until_it_is_queued()
    {
        HostFixture fixture = StartB();
        fixture.Host.Abort(CtrlB, null);

        fixture.ScanAt(Seconds(0.5));
        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "B"));
        Assert.Equal(940, ErrorId(fixture, 1));
        Assert.Equal(false, fixture.ServiceStatus(1, "B", "PackMLOut.bCommandRejected"));

        fixture.ScanAt(Seconds(0.51));

        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "B"));
        Assert.Equal(true, fixture.ServiceStatus(1, "B", "PackMLOut.bCommandRejected"));
    }

    [Theory]
    [InlineData("AliveIndex : UDINT;", "")]
    [InlineData("AliveTimeout : TIME;", "")]
    [InlineData("AliveIndex : UDINT;", "AliveIndex : INT;")]
    [InlineData("AliveTimeout : TIME;", "AliveTimeout : DINT;")]
    [Trait("AC", "5")]
    public void Service_with_a_missing_or_wrongly_typed_alive_member_is_not_monitored(string original, string replaced)
    {
        HostFixture fixture = Create(HostFixture.AlarmDeclarations.Replace(original, replaced));

        if (fixture.Symbols.TryResolve($"{CtrlB}.AliveTimeout", out _, out _))
            fixture.Write($"{CtrlB}.AliveTimeout", 500L);
        fixture.ServiceCommand(1, "B", PackMLCommand.Start);
        fixture.ScanAt(Seconds(10));

        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "B"));
        Assert.Equal(0, ErrorId(fixture, 1));
    }

    [Fact]
    [Trait("AC", "5")]
    public void Service_without_the_alive_members_runs_and_leaves_Execute_normally()
    {
        string declarations = HostFixture.Declarations.Replace("AliveIndex : UDINT;", "").Replace("AliveTimeout : TIME;", "");
        HostFixture fixture = new(declarations: declarations);

        fixture.StartServiceA();
        fixture.ScanAt(Seconds(1));
        fixture.ServiceCommand(1, "A", PackMLCommand.Stop, 2);

        Assert.Equal(PackMLState.Stopped, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "6")]
    public void Mode_request_held_by_the_client_is_not_processed_in_the_scan_of_the_timeout_Abort()
    {
        HostFixture fixture = StartB();
        fixture.Write($"{CtrlB}.PackMLIn.eMode", 99L);
        fixture.Write($"{CtrlB}.PackMLIn.bModeChangeRequest", true);

        fixture.ScanAt(Seconds(0.5));

        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "B"));
        Assert.Equal(false, fixture.ServiceStatus(1, "B", "PackMLOut.bModeChangeRejected"));
    }
}
