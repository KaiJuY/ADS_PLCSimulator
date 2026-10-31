using AdsPlcSimulator.Core.PackML;
using AdsPlcSimulator.Core.PackML.Alarm;
using AdsPlcSimulator.Core.PackML.Behavior;

namespace AdsPlcSimulator.Tests.PackML;

public class ServiceHostBehaviorTests
{
    private const string Module = "GVL_Test.Modules[1]";
    private const string ModeValue = "GVL_Test.Modules[1].ModuleVariableList[1].Value";
    private const string ErrorOfA = "GVL_Test.Modules[1].ServiceStatus.A.ErrorList";
    private const string PathA = "GVL_Test.Modules[1].ServiceCtrl.A";

    private static readonly TimeSpan Tick = ServiceHost.ScanInterval;

    internal static ServiceBehaviorTable StandardBehaviors(string? durationOfB = null) => new(new Dictionary<string, ModuleBehavior>
    {
        [Module] = new ModuleBehavior(
            "ModuleVariableList[1].Value",
            [
                new ModeRule(0, "Disable", false, ModeRuleKind.AllowOnly, new HashSet<string> { "B" }),
                new ModeRule(1, "Manual", false, ModeRuleKind.DenyListed, new HashSet<string> { "Unbound" }),
                new ModeRule(2, "DryRun", true, ModeRuleKind.DenyListed, new HashSet<string>()),
            ],
            new Dictionary<string, ServiceBehavior>
            {
                ["A"] = new([new ParamRange("Index", 1, 4)], "Time", [], [WriteOf(3, SvidValueKind.Constant, 7)], []),
                ["B"] = new(
                    [], durationOfB,
                    [WriteOf(2, SvidValueKind.Ecid, 0, "ModuleConfigParamList[1].Value"), WriteOf(4, SvidValueKind.Constant, 9, dryRunZero: true)],
                    [],
                    [WriteOf(2, SvidValueKind.Constant, 0), WriteOf(4, SvidValueKind.Constant, 0)]),
            }),
    });

    private static SvidWrite WriteOf(int index, SvidValueKind kind, double constant, string? source = null, bool dryRunZero = false) =>
        new($"ModuleVariableList[{index}].Value", false, new SvidValue(kind, constant, source, false), dryRunZero);

    private static HostFixture Arrange(string mode = "1", ServiceBehaviorTable? behaviors = null)
    {
        var fixture = new HostFixture(declarations: HostFixture.BehaviorDeclarations, initialize: false);
        fixture.Write(ModeValue, mode);
        fixture.Write($"{Module}.ServiceParam.A.Index", 1);
        fixture.Write($"{Module}.ServiceParam.A.Time", 2000);
        fixture.Write($"{Module}.ModuleConfigParamList[1].Value", "30");
        fixture.Host.Initialize(fixture.Image, ConfiguredAlarmTable.Empty, behaviors ?? StandardBehaviors());
        return fixture;
    }

    private static void StartAndScan(HostFixture fixture, string service = "A")
    {
        fixture.SendToService(1, service, PackMLCommand.Start);
        fixture.Scan();
    }

    private static void ReleaseAndScan(HostFixture fixture, string service = "A")
    {
        fixture.ReleaseService(1, service);
        fixture.Scan();
    }

    private static string Svid(HostFixture fixture, int index) => (string)fixture.Read($"{Module}.ModuleVariableList[{index}].Value");

    private static long ErrorField(HostFixture fixture, int slot, string field) => Convert.ToInt64(fixture.Read($"{ErrorOfA}[{slot}].{field}"));

    private static void ScanUpTo(HostFixture fixture, TimeSpan upTo)
    {
        while (fixture.Now < upTo)
            fixture.Scan(Tick);
    }

    [Fact]
    [Trait("AC", "3")]
    public void Without_a_behavior_table_a_service_completes_after_the_default_delay_as_before()
    {
        var fixture = new HostFixture(declarations: HostFixture.BehaviorDeclarations);
        fixture.Write(ModeValue, "0");

        fixture.StartServiceA();
        fixture.Scan(HostFixture.DefaultDelay);
        fixture.Scan();

        Assert.Equal(PackMLState.Complete, fixture.ServiceState(1, "A"));
        Assert.Equal(0, ErrorField(fixture, 1, "MainErrorId"));
    }

    [Fact]
    [Trait("AC", "5")]
    public void A_service_refused_by_the_Mode_enters_Execute_with_the_error_then_aborts_one_scan_later()
    {
        HostFixture fixture = Arrange("0");

        StartAndScan(fixture);

        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));
        Assert.Equal(2, ErrorField(fixture, 1, "MainErrorId"));
        Assert.Equal(0, ErrorField(fixture, 1, "SourceErrorId"));
        Assert.Equal("Not allowed in Mode 0 (Disable).", fixture.Read($"{ErrorOfA}[1].Message"));
        Assert.True(fixture.Host.IsExecuteRejected(PathA));
        ReleaseAndScan(fixture);
        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "6")]
    public void A_service_the_Mode_allows_runs_and_writes_its_entry_SVID()
    {
        HostFixture fixture = Arrange("0");

        StartAndScan(fixture, "B");

        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "B"));
        Assert.Equal("30", Svid(fixture, 2));
        Assert.False(fixture.Host.IsExecuteRejected(HostFixture.CtrlPath(1, "B")));
    }

    [Fact]
    [Trait("AC", "7")]
    public void A_Mode_change_during_Execute_does_not_interrupt_the_service()
    {
        HostFixture fixture = Arrange("1");
        StartAndScan(fixture);
        ReleaseAndScan(fixture);

        fixture.Write(ModeValue, "0");
        ScanUpTo(fixture, TimeSpan.FromMilliseconds(2000));
        fixture.Scan();

        Assert.Equal(PackMLState.Complete, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "5")]
    public void A_refusal_that_cannot_queue_Abort_because_a_mode_request_is_high_retries_until_it_is_low()
    {
        HostFixture fixture = Arrange("0");
        fixture.Write($"{PathA}.PackMLIn.bModeChangeRequest", true);

        StartAndScan(fixture);
        ReleaseAndScan(fixture);
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));

        fixture.Write($"{PathA}.PackMLIn.bModeChangeRequest", false);
        fixture.Scan();
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));
        fixture.Scan();
        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "9")]
    public void A_parameter_out_of_range_aborts_with_MainErrorId_1_and_the_value()
    {
        HostFixture fixture = Arrange("1");
        fixture.Write($"{Module}.ServiceParam.A.Index", 5);

        StartAndScan(fixture);
        ReleaseAndScan(fixture);

        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "A"));
        Assert.Equal(1, ErrorField(fixture, 1, "MainErrorId"));
        Assert.Equal(5, ErrorField(fixture, 1, "SourceErrorId"));
        Assert.Equal("Index out of range (1~4).", fixture.Read($"{ErrorOfA}[1].Message"));
    }

    [Fact]
    [Trait("AC", "9")]
    public void The_Mode_check_comes_before_the_parameter_check()
    {
        HostFixture fixture = Arrange("0");
        fixture.Write($"{Module}.ServiceParam.A.Index", 5);

        StartAndScan(fixture);

        Assert.Equal(2, ErrorField(fixture, 1, "MainErrorId"));
        Assert.Equal(0, ErrorField(fixture, 2, "MainErrorId"));
    }

    [Fact]
    [Trait("AC", "11")]
    public void The_duration_field_sets_how_long_a_NaturalCompletion_service_stays_in_Execute()
    {
        HostFixture fixture = Arrange("1");
        StartAndScan(fixture);
        ReleaseAndScan(fixture);

        fixture.Scan(TimeSpan.FromMilliseconds(1990));
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));
        fixture.Scan(Tick);
        Assert.Equal(PackMLState.Completing, fixture.ServiceState(1, "A"));
        fixture.Scan();
        Assert.Equal(PackMLState.Complete, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "11")]
    public void A_duration_on_an_ExecuteUntilExternalStop_service_has_no_effect()
    {
        HostFixture fixture = Arrange("1", StandardBehaviors(durationOfB: "Time"));
        fixture.Write($"{Module}.ServiceParam.B.Time", 100);
        StartAndScan(fixture, "B");
        ReleaseAndScan(fixture, "B");

        fixture.Scan(TimeSpan.FromSeconds(5));

        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "B"));
    }

    [Theory]
    [Trait("AC", "12")]
    [InlineData(PackMLCommand.Stop)]
    [InlineData(PackMLCommand.Abort)]
    public void Entry_SVIDs_are_written_on_entering_Execute_and_reset_on_leaving_it(PackMLCommand leave)
    {
        HostFixture fixture = Arrange("1");

        StartAndScan(fixture, "B");
        Assert.Equal(["30", "9"], [Svid(fixture, 2), Svid(fixture, 4)]);
        ReleaseAndScan(fixture, "B");
        fixture.SendToService(1, "B", leave, 2);
        fixture.Scan();

        Assert.Equal(["0", "0"], [Svid(fixture, 2), Svid(fixture, 4)]);
    }

    [Fact]
    [Trait("AC", "12")]
    public void A_before_complete_SVID_is_written_in_the_scan_that_starts_Completing_not_Complete()
    {
        HostFixture fixture = Arrange("1");
        StartAndScan(fixture);
        ReleaseAndScan(fixture);
        Assert.Equal("", Svid(fixture, 3));

        fixture.Scan(TimeSpan.FromMilliseconds(2000));

        Assert.Equal(PackMLState.Completing, fixture.ServiceState(1, "A"));
        Assert.Equal("7", Svid(fixture, 3));
    }

    [Fact]
    [Trait("AC", "8")]
    public void In_DryRun_a_marked_SVID_is_zero_and_the_others_are_unchanged()
    {
        HostFixture fixture = Arrange("2");

        StartAndScan(fixture, "B");
        StartAndScan(fixture);
        ReleaseAndScan(fixture);
        fixture.Scan(TimeSpan.FromMilliseconds(2000));
        fixture.Scan();

        Assert.Equal(["30", "0"], [Svid(fixture, 2), Svid(fixture, 4)]);
        Assert.Equal(PackMLState.Complete, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "7")]
    public void Unhold_back_to_Execute_skips_the_checks_and_writes_the_entry_SVIDs_again()
    {
        HostFixture fixture = Arrange("1");
        StartAndScan(fixture, "B");
        ReleaseAndScan(fixture, "B");

        fixture.ServiceCommand(1, "B", PackMLCommand.Hold, 2);
        Assert.Equal("0", Svid(fixture, 2));
        fixture.Write(ModeValue, "9");
        fixture.Write($"{Module}.ModuleConfigParamList[1].Value", "31");
        fixture.ServiceCommand(1, "B", PackMLCommand.Unhold, 3);

        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "B"));
        Assert.Equal("31", Svid(fixture, 2));
        Assert.Equal(0, Convert.ToInt64(fixture.Read($"{Module}.ServiceStatus.B.ErrorList[1].MainErrorId")));
        Assert.False(fixture.Host.IsExecuteRejected(HostFixture.CtrlPath(1, "B")));
    }

    [Fact]
    [Trait("AC", "4")]
    public void An_overridden_service_writes_no_entry_SVID_on_entering_Execute()
    {
        HostFixture fixture = Arrange("1");
        fixture.SetOverrideToken(1, "B", new object());

        StartAndScan(fixture, "B");

        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "B"));
        Assert.Equal(["", ""], [Svid(fixture, 2), Svid(fixture, 4)]);
    }

    [Fact]
    [Trait("AC", "4")]
    public void An_overridden_service_writes_no_SVID_on_leaving_Execute()
    {
        HostFixture fixture = Arrange("1");
        fixture.SetOverrideToken(1, "B", new object());
        StartAndScan(fixture, "B");
        ReleaseAndScan(fixture, "B");

        fixture.SendToService(1, "B", PackMLCommand.Stop, 2);
        fixture.Scan();

        Assert.NotEqual(PackMLState.Execute, fixture.ServiceState(1, "B"));
        Assert.Equal(["", ""], [Svid(fixture, 2), Svid(fixture, 4)]);
    }

    [Fact]
    [Trait("AC", "5")]
    public void A_refused_entry_writes_no_SVID_on_leaving_Execute()
    {
        HostFixture fixture = Arrange("9");
        StartAndScan(fixture, "B");
        ReleaseAndScan(fixture, "B");

        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "B"));
        Assert.Equal(["", ""], [Svid(fixture, 2), Svid(fixture, 4)]);
    }

    [Fact]
    [Trait("AC", "4")]
    public void An_overridden_service_is_checked_for_the_Mode_only_and_uses_no_duration_or_SVID_write()
    {
        HostFixture fixture = Arrange("1");
        fixture.SetOverrideToken(1, "A", new object());
        fixture.Write($"{Module}.ServiceParam.A.Index", 5);

        StartAndScan(fixture);
        ReleaseAndScan(fixture);
        fixture.Scan(TimeSpan.FromSeconds(3));

        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));
        Assert.Equal(0, ErrorField(fixture, 1, "MainErrorId"));
        fixture.Host.Complete(PathA, null);
        fixture.Scan();
        Assert.Equal(PackMLState.Completing, fixture.ServiceState(1, "A"));
        Assert.Equal("", Svid(fixture, 3));
    }

    [Fact]
    [Trait("AC", "4")]
    public void An_overridden_service_refused_by_the_Mode_cannot_be_completed_by_a_workflow()
    {
        HostFixture fixture = Arrange("0");
        fixture.SetOverrideToken(1, "A", new object());

        StartAndScan(fixture);

        Assert.True(fixture.Host.IsExecuteRejected(PathA));
        Assert.Throws<InvalidOperationException>(() => fixture.Host.Complete(PathA, null));
    }

    [Fact]
    [Trait("AC", "5")]
    public void The_refusal_ends_with_the_next_Start_and_with_a_new_Initialize()
    {
        HostFixture fixture = Arrange("0");
        StartAndScan(fixture);
        ReleaseAndScan(fixture);
        Assert.True(fixture.Host.IsExecuteRejected(PathA));
        fixture.ServiceCommand(1, "A", PackMLCommand.Clear, 2);
        fixture.ServiceCommand(1, "A", PackMLCommand.Reset, 3);
        fixture.Write(ModeValue, "1");

        StartAndScan(fixture);
        Assert.False(fixture.Host.IsExecuteRejected(PathA));

        fixture.Write(ModeValue, "0");
        fixture.Host.Initialize(fixture.Image, ConfiguredAlarmTable.Empty, StandardBehaviors());
        Assert.False(fixture.Host.IsExecuteRejected(PathA));
    }

    [Fact]
    [Trait("AC", "5")]
    public void IsExecuteRejected_refuses_a_path_that_is_not_a_bound_service()
    {
        HostFixture fixture = Arrange();

        Assert.Throws<InvalidOperationException>(() => fixture.Host.IsExecuteRejected(HostFixture.CtrlPath(1, "Unbound")));
    }

    [Fact]
    [Trait("AC", "3")]
    public void A_module_without_behavior_is_not_checked()
    {
        HostFixture fixture = Arrange("0");

        fixture.SendToService(2, "A", PackMLCommand.Start);
        fixture.Scan();

        Assert.Equal(PackMLState.Execute, fixture.ServiceState(2, "A"));
        Assert.False(fixture.Host.IsExecuteRejected(HostFixture.CtrlPath(2, "A")));
    }
}
