using AdsPlcSimulator.Core.PackML;
using AdsPlcSimulator.Core.PackML.Alarm;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.PackML;

public class ServiceHostAlarmTests
{
    private const string Module = "GVL_Test.Modules[1]";
    private const string ServiceA = "GVL_Test.Modules[1].ServiceCtrl.A";
    private static readonly DateTimeOffset Clock = new(2026, 1, 2, 3, 4, 5, 6, TimeSpan.Zero);

    private static HostFixture NewFixture(ConfiguredAlarmTable? table = null, bool initialize = true, string? declarations = null) =>
        new(initialize: initialize, declarations: declarations ?? HostFixture.AlarmDeclarations, configuredAlarms: table, utcNow: () => Clock);

    private static ConfiguredAlarmTable Table(params ConfiguredAlarm[] alarms) =>
        new(new Dictionary<string, IReadOnlyList<ConfiguredAlarm>> { [Module] = alarms });

    private static ConfiguredAlarm Pressure(uint mainErrorId = 101) =>
        new("Sources.Pressure", AlarmSourceType.Real, AlarmOperator.GreaterThan, 80, 0, mainErrorId, 5, "pressure high", AlarmSeverity.Warning);

    private static long Num(HostFixture fixture, string path) => Convert.ToInt64(fixture.Read(path));

    private static long ListNum(HostFixture fixture, int index, string field) => Num(fixture, $"{Module}.ModuleAlarmList[{index}].{field}");

    private static long ErrorNum(HostFixture fixture, int index, string field) => Num(fixture, $"{Module}.ServiceStatus.A.ErrorList[{index}].{field}");

    private static long AlarmStatus(HostFixture fixture, string field) => Num(fixture, $"{Module}.ModuleStatus.Alarm.{field}");

    private static string ListText(HostFixture fixture, int index, string field) => (string)fixture.Read($"{Module}.ModuleAlarmList[{index}].{field}");

    private static void SendAck(HostFixture fixture, uint requestId, AlarmAckCommand command, uint mainErrorId = 0, uint serviceId = 0)
    {
        string path = $"{Module}.ModuleCtrl.AlarmAckRequest";
        fixture.Write($"{path}.RequestId", (long)requestId);
        fixture.Write($"{path}.Command", (long)command);
        fixture.Write($"{path}.MainErrorId", (long)mainErrorId);
        fixture.Write($"{path}.ServiceId", (long)serviceId);
        fixture.Write($"{path}.bChangeRequest", true);
    }

    private static void ReleaseAck(HostFixture fixture) => fixture.Write($"{Module}.ModuleCtrl.AlarmAckRequest.bChangeRequest", false);

    private static void AbortServiceA(HostFixture fixture)
    {
        fixture.ServiceCommand(1, "A", PackMLCommand.Abort, 10);
        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "1")]
    public void AddServiceError_fills_ErrorList_and_the_next_scan_publishes_it_in_ModuleAlarmList()
    {
        HostFixture fixture = NewFixture();
        fixture.Write($"{Module}.ModuleStatus.ModuleId", 3L);

        var result = fixture.Host.AddServiceError(ServiceA, 7, 9, "boom");

        Assert.Equal(new ServiceHost.ServiceErrorResult(true, ""), result);
        Assert.Equal(3, ErrorNum(fixture, 1, "ModuleId"));
        Assert.Equal(101, ErrorNum(fixture, 1, "ServiceId"));
        Assert.Equal(7, ErrorNum(fixture, 1, "MainErrorId"));
        Assert.Equal(9, ErrorNum(fixture, 1, "SourceErrorId"));
        Assert.Equal(1, ErrorNum(fixture, 1, "Severity"));
        Assert.Equal(1, ErrorNum(fixture, 1, "Active"));
        Assert.Equal(1, ErrorNum(fixture, 1, "OccurrenceCount"));
        Assert.Equal("boom", fixture.Read($"{Module}.ServiceStatus.A.ErrorList[1].Message"));
        Assert.Equal("20260102030405006", fixture.Read($"{Module}.ServiceStatus.A.ErrorList[1].DateTime"));
        Assert.Equal($"{Module}.ServiceStatus.A", fixture.Read($"{Module}.ServiceStatus.A.ErrorList[1].Path"));
        Assert.Equal(0, ListNum(fixture, 1, "MainErrorId"));

        fixture.Scan();

        foreach (string field in new[] { "ModuleId", "ServiceId", "MainErrorId", "SourceErrorId", "Severity", "Active", "Acknowledged", "OccurrenceCount" })
            Assert.Equal(ErrorNum(fixture, 1, field), field == "Acknowledged" ? 0 : ListNum(fixture, 1, field));
        Assert.Equal("boom", ListText(fixture, 1, "Message"));
        Assert.Equal("20260102030405006", ListText(fixture, 1, "DateTime"));
        Assert.Equal(1, AlarmStatus(fixture, "ServiceLatchedCount"));
    }

    [Fact]
    [Trait("AC", "6")]
    public void A_duplicate_or_eleventh_error_is_refused_and_the_image_stays_the_same()
    {
        HostFixture fixture = NewFixture();
        fixture.Host.AddServiceError(ServiceA, 7, 0, "first");

        var duplicate = fixture.Host.AddServiceError(ServiceA, 7, 1, "again");

        Assert.False(duplicate.Added);
        Assert.Contains("duplicate", duplicate.Reason);
        for (uint id = 8; id <= 16; id++)
            Assert.True(fixture.Host.AddServiceError(ServiceA, id, 0, "x").Added);
        byte[] before = fixture.Image.Read(0, fixture.Image.Size);

        var full = fixture.Host.AddServiceError(ServiceA, 99, 0, "x");

        Assert.False(full.Added);
        Assert.Contains("full", full.Reason);
        Assert.Equal(before, fixture.Image.Read(0, fixture.Image.Size));
    }

    [Fact]
    [Trait("AC", "6")]
    public void A_message_of_81_characters_is_cut_to_80()
    {
        HostFixture fixture = NewFixture();
        string message = new('m', 81);

        fixture.Host.AddServiceError(ServiceA, 7, 0, message);

        Assert.Equal(message[..80], fixture.Read($"{Module}.ServiceStatus.A.ErrorList[1].Message"));
    }

    [Fact]
    [Trait("AC", "7")]
    public void Clearing_empties_ErrorList_and_the_alarm_becomes_inactive_then_Ack_removes_it()
    {
        HostFixture fixture = NewFixture();
        fixture.Host.AddServiceError(ServiceA, 7, 0, "boom");
        fixture.Scan();
        AbortServiceA(fixture);
        Assert.Equal(7, ErrorNum(fixture, 1, "MainErrorId"));

        fixture.ServiceCommand(1, "A", PackMLCommand.Clear, 11);

        Assert.Equal(0, ErrorNum(fixture, 1, "MainErrorId"));
        Assert.Equal(7, ListNum(fixture, 1, "MainErrorId"));
        Assert.Equal(0, ListNum(fixture, 1, "Active"));

        SendAck(fixture, 1, AlarmAckCommand.AcknowledgeOne, 7, 101);
        fixture.Scan();

        Assert.Equal(0, ListNum(fixture, 1, "MainErrorId"));
    }

    [Fact]
    [Trait("AC", "7")]
    public void An_alarm_acknowledged_before_Clearing_is_removed_when_the_service_clears()
    {
        HostFixture fixture = NewFixture();
        fixture.Host.AddServiceError(ServiceA, 7, 0, "boom");
        fixture.Scan();
        SendAck(fixture, 1, AlarmAckCommand.AcknowledgeOne, 7, 101);
        fixture.Scan();
        ReleaseAck(fixture);
        fixture.Scan();
        Assert.Equal(1, ListNum(fixture, 1, "Acknowledged"));
        AbortServiceA(fixture);

        fixture.ServiceCommand(1, "A", PackMLCommand.Clear, 11);

        Assert.Equal(0, ListNum(fixture, 1, "MainErrorId"));
    }

    [Fact]
    [Trait("AC", "5")]
    public void Acknowledgement_handshake_runs_through_the_image()
    {
        HostFixture fixture = NewFixture();
        fixture.Host.AddServiceError(ServiceA, 7, 0, "boom");
        fixture.Scan();

        SendAck(fixture, 5, AlarmAckCommand.AcknowledgeOne, 7, 101);
        fixture.Scan();

        Assert.Equal(5, AlarmStatus(fixture, "AckResponseId"));
        Assert.Equal(1, AlarmStatus(fixture, "AckAccepted"));
        Assert.Equal(0, AlarmStatus(fixture, "AckError"));
        Assert.Equal(1, ListNum(fixture, 1, "Acknowledged"));

        ReleaseAck(fixture);
        fixture.Scan();
        SendAck(fixture, 5, AlarmAckCommand.AcknowledgeOne, 7, 101);
        fixture.Scan();

        Assert.Equal(0, AlarmStatus(fixture, "AckAccepted"));
        Assert.Equal((long)AlarmAckError.DuplicateRequest, AlarmStatus(fixture, "AckError"));
    }

    [Fact]
    [Trait("AC", "3")]
    public void Configured_alarm_follows_its_source_and_a_String80_source_never_alarms()
    {
        var text = new ConfiguredAlarm("Sources.Text", AlarmSourceType.String80, AlarmOperator.Equal, 0, 0, 102, 0, "text", AlarmSeverity.Alarm);
        HostFixture fixture = NewFixture(Table(Pressure(), text));
        fixture.Write($"{Module}.Sources.Text", "abc");

        fixture.Scan();
        Assert.Equal(0, ListNum(fixture, 1, "MainErrorId"));

        fixture.Write($"{Module}.Sources.Pressure", 90.0);
        fixture.Scan();
        Assert.Equal(101, ListNum(fixture, 1, "MainErrorId"));
        Assert.Equal(5, ListNum(fixture, 1, "SourceErrorId"));
        Assert.Equal(0, ListNum(fixture, 1, "ServiceId"));
        Assert.Equal(2, ListNum(fixture, 1, "Severity"));
        Assert.Equal(1, ListNum(fixture, 1, "Active"));
        Assert.Equal("pressure high", ListText(fixture, 1, "Message"));
        Assert.Equal($"{Module}.Sources.Pressure", ListText(fixture, 1, "Path"));
        Assert.Equal("20260102030405006", ListText(fixture, 1, "DateTime"));
        Assert.Equal(1, AlarmStatus(fixture, "ConfiguredLatchedCount"));

        fixture.Write($"{Module}.Sources.Pressure", 50.0);
        fixture.Scan();
        Assert.Equal(101, ListNum(fixture, 1, "MainErrorId"));
        Assert.Equal(0, ListNum(fixture, 1, "Active"));
        Assert.Equal(0, ListNum(fixture, 2, "MainErrorId"));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Hook_alarm_appears_goes_inactive_and_is_ignored_when_the_configuration_owns_the_id()
    {
        HostFixture fixture = NewFixture(Table(Pressure(30)));

        fixture.Host.SetModuleAlarm(Module, 30, 2, "hook", AlarmSeverity.Warning, "", true);
        fixture.Scan();
        Assert.Equal(0, ListNum(fixture, 1, "MainErrorId"));

        HostFixture plain = NewFixture();
        plain.Host.SetModuleAlarm(Module, 30, 2, "hook", AlarmSeverity.Warning, "", true);
        plain.Scan();
        Assert.Equal(30, ListNum(plain, 1, "MainErrorId"));
        Assert.Equal(2, ListNum(plain, 1, "Severity"));
        Assert.Equal(Module, ListText(plain, 1, "Path"));
        Assert.Equal("hook", ListText(plain, 1, "Message"));
        Assert.Equal(1, AlarmStatus(plain, "HookLatchedCount"));

        plain.Host.SetModuleAlarm(Module, 30, 2, "hook", AlarmSeverity.Warning, "", false);
        plain.Scan();
        Assert.Equal(30, ListNum(plain, 1, "MainErrorId"));
        Assert.Equal(0, ListNum(plain, 1, "Active"));
    }

    [Fact]
    [Trait("AC", "10")]
    public void A_second_Initialize_zeroes_the_alarm_blocks()
    {
        HostFixture fixture = NewFixture();
        fixture.Host.AddServiceError(ServiceA, 7, 0, "boom");
        fixture.Host.SetModuleAlarm(Module, 30, 0, "hook", AlarmSeverity.Alarm, "", true);
        fixture.Scan();
        SendAck(fixture, 5, AlarmAckCommand.AcknowledgeAll);
        fixture.Scan();
        Assert.Equal(7, ListNum(fixture, 1, "MainErrorId"));
        ReleaseAck(fixture);

        fixture.Host.Initialize(fixture.Image, fixture.ConfiguredAlarms);

        Assert.Equal(0, ListNum(fixture, 1, "MainErrorId"));
        Assert.Equal(0, ListNum(fixture, 2, "MainErrorId"));
        Assert.Equal(0, AlarmStatus(fixture, "AckResponseId"));
        Assert.Equal(0, AlarmStatus(fixture, "ServiceLatchedCount"));
        Assert.Equal(0, ErrorNum(fixture, 1, "MainErrorId"));
        fixture.Scan();
        Assert.Equal(0, ListNum(fixture, 1, "MainErrorId"));
    }

    [Fact]
    [Trait("AC", "11")]
    public void A_module_without_alarm_members_scans_normally_and_refuses_alarm_calls()
    {
        var fixture = new HostFixture();

        fixture.Scan();

        Assert.Throws<InvalidOperationException>(() => fixture.Host.SetModuleAlarm("GVL_Test.Modules[1]", 1, 0, "m", AlarmSeverity.Alarm, "", true));
        Assert.Throws<InvalidOperationException>(() => fixture.Host.AddServiceError(HostFixture.CtrlPath(1, "A"), 1, 0, "m"));
        Assert.Equal(PackMLState.Idle, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "10")]
    public void A_scan_without_alarm_changes_writes_nothing_and_one_Ack_writes_one_entry_and_one_status()
    {
        HostFixture fixture = NewFixture();
        fixture.Host.AddServiceError(ServiceA, 7, 0, "boom");
        fixture.Scan();
        var writes = new List<MemoryWrite>();
        fixture.Image.Written += writes.Add;

        fixture.Scan();
        fixture.Scan();
        Assert.Empty(writes);

        SendAck(fixture, 5, AlarmAckCommand.AcknowledgeOne, 7, 101);
        writes.Clear();
        fixture.Scan();

        int entrySize = fixture.Symbols.Resolve($"{Module}.ModuleAlarmList[1]").Size;
        int statusSize = fixture.Symbols.Resolve($"{Module}.ModuleStatus.Alarm").Size;
        Assert.Equal([entrySize, statusSize], writes.Select(write => write.Length));
    }

    [Fact]
    [Trait("AC", "10")]
    public void A_retired_host_refuses_alarm_calls_and_leaves_the_image_alone()
    {
        HostFixture fixture = NewFixture();
        fixture.Host.Retire();
        byte[] before = fixture.Image.Read(0, fixture.Image.Size);

        var error = Assert.Throws<InvalidOperationException>(() => fixture.Host.AddServiceError(ServiceA, 7, 0, "boom"));
        Assert.Contains("retired", error.Message);
        Assert.Throws<InvalidOperationException>(() => fixture.Host.SetModuleAlarm(Module, 7, 0, "m", AlarmSeverity.Alarm, "", true));

        Assert.Equal(before, fixture.Image.Read(0, fixture.Image.Size));
    }

    [Fact]
    [Trait("AC", "10")]
    public void Alarm_calls_before_Initialize_are_refused()
    {
        HostFixture fixture = NewFixture(initialize: false);

        var error = Assert.Throws<InvalidOperationException>(() => fixture.Host.AddServiceError(ServiceA, 7, 0, "boom"));

        Assert.Contains("not initialized", error.Message);
        Assert.Throws<InvalidOperationException>(() => fixture.Host.SetModuleAlarm(Module, 7, 0, "m", AlarmSeverity.Alarm, "", true));
    }

    [Fact]
    [Trait("AC", "7")]
    public void A_service_that_passes_Aborted_Clearing_Stopped_in_one_scan_has_its_errors_cleared()
    {
        HostFixture fixture = NewFixture();
        AbortServiceA(fixture);
        fixture.Host.AddServiceError(ServiceA, 7, 0, "boom");
        Assert.Equal(7, ErrorNum(fixture, 1, "MainErrorId"));

        fixture.SendToService(1, "A", PackMLCommand.Clear, 11);
        fixture.Scan();

        Assert.Equal(PackMLState.Stopped, fixture.ServiceState(1, "A"));
        Assert.Equal(0, ErrorNum(fixture, 1, "MainErrorId"));
    }

    [Fact]
    [Trait("AC", "6")]
    public void Long_message_and_path_are_cut_to_the_declared_length_and_the_scan_goes_on()
    {
        HostFixture fixture = NewFixture();
        string message = new('m', 200);
        string path = new('p', 300);

        fixture.Host.SetModuleAlarm(Module, 30, 0, message, AlarmSeverity.Alarm, path, true);
        fixture.Scan();

        Assert.Equal(message[..80], ListText(fixture, 1, "Message"));
        Assert.Equal(path[..255], ListText(fixture, 1, "Path"));
    }

    [Fact]
    [Trait("AC", "5")]
    public void A_request_that_is_already_high_at_Initialize_is_processed_once_by_the_first_scan()
    {
        HostFixture fixture = NewFixture(initialize: false);
        SendAck(fixture, 8, AlarmAckCommand.AcknowledgeAll);

        fixture.Host.Initialize(fixture.Image, fixture.ConfiguredAlarms);
        fixture.Scan();

        Assert.Equal(8, AlarmStatus(fixture, "AckResponseId"));
        Assert.Equal(1, AlarmStatus(fixture, "AckAccepted"));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Hook_alarms_are_published_in_the_order_of_their_first_submission()
    {
        HostFixture fixture = NewFixture();
        foreach (uint id in new uint[] { 5, 3, 9 })
            fixture.Host.SetModuleAlarm(Module, id, 0, "m", AlarmSeverity.Alarm, "", true);
        fixture.Host.SetModuleAlarm(Module, 3, 0, "m", AlarmSeverity.Alarm, "", false);
        fixture.Host.SetModuleAlarm(Module, 3, 0, "m", AlarmSeverity.Alarm, "", true);

        fixture.Scan();

        Assert.Equal([5L, 9L, 3L], Enumerable.Range(1, 3).Select(index => ListNum(fixture, index, "MainErrorId")));
    }

    [Fact]
    [Trait("AC", "14")]
    public void A_module_that_is_not_in_the_configuration_publishes_alarms_with_module_id_zero()
    {
        HostFixture fixture = NewFixture();
        fixture.Host.SetModuleAlarm(Module, 30, 0, "m", AlarmSeverity.Alarm, "", true);

        fixture.Scan();

        Assert.Equal(30, ListNum(fixture, 1, "MainErrorId"));
        Assert.Equal(0, ListNum(fixture, 1, "ModuleId"));
    }

    [Fact]
    [Trait("AC", "10")]
    public void An_alarm_that_returns_after_a_second_Initialize_is_written_again()
    {
        HostFixture fixture = NewFixture();
        fixture.Host.AddServiceError(ServiceA, 7, 0, "boom");
        fixture.Scan();
        fixture.Host.Initialize(fixture.Image, fixture.ConfiguredAlarms);
        Assert.Equal(0, ListNum(fixture, 1, "MainErrorId"));

        fixture.Host.AddServiceError(ServiceA, 7, 0, "boom");
        fixture.Scan();

        Assert.Equal(7, ErrorNum(fixture, 1, "MainErrorId"));
        Assert.Equal(7, ListNum(fixture, 1, "MainErrorId"));
        Assert.Equal(1, AlarmStatus(fixture, "ServiceLatchedCount"));
    }

    [Fact]
    [Trait("AC", "9")]
    public void A_thirty_first_hook_id_is_refused_and_switching_off_an_unknown_id_does_nothing()
    {
        HostFixture fixture = NewFixture();
        for (uint id = 1; id <= 30; id++)
            fixture.Host.SetModuleAlarm(Module, id, 0, "m", AlarmSeverity.Alarm, "", true);

        var error = Assert.Throws<InvalidOperationException>(() => fixture.Host.SetModuleAlarm(Module, 31, 0, "m", AlarmSeverity.Alarm, "", true));

        Assert.Contains("30", error.Message);
        fixture.Host.SetModuleAlarm(Module, 99, 0, "m", AlarmSeverity.Alarm, "", false);
        fixture.Host.SetModuleAlarm(Module, 1, 0, "again", AlarmSeverity.Alarm, "", true);
    }

    [Fact]
    [Trait("AC", "11")]
    public void A_module_without_ModuleStatus_ModuleId_has_no_alarms()
    {
        HostFixture fixture = NewFixture(declarations: HostFixture.AlarmDeclarationsWithoutModuleId);

        fixture.Scan();

        Assert.Throws<InvalidOperationException>(() => fixture.Host.SetModuleAlarm(Module, 30, 0, "m", AlarmSeverity.Alarm, "", true));
        Assert.Equal(PackMLState.Idle, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "9")]
    public void SetModuleAlarm_refuses_an_unknown_module_and_a_zero_id()
    {
        HostFixture fixture = NewFixture();

        Assert.False(fixture.Host.ContainsModule("GVL_Test.Modules[9]"));
        Assert.True(fixture.Host.ContainsModule("gvl_test.modules[2]"));
        Assert.Throws<InvalidOperationException>(() => fixture.Host.SetModuleAlarm("GVL_Test.Modules[9]", 1, 0, "m", AlarmSeverity.Alarm, "", true));
        Assert.Throws<InvalidOperationException>(() => fixture.Host.SetModuleAlarm(Module, 0, 0, "m", AlarmSeverity.Alarm, "", true));
        Assert.Throws<InvalidOperationException>(() => fixture.Host.AddServiceError(ServiceA, 0, 0, "m"));
        Assert.Throws<InvalidOperationException>(() => fixture.Host.AddServiceError(HostFixture.CtrlPath(1, "Unbound"), 1, 0, "m"));
    }
}
