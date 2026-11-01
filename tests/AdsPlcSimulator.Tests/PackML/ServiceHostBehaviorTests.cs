using AdsPlcSimulator.Core.Flow;
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

    private static HostFixture Arrange(string mode = "1", ServiceBehaviorTable? behaviors = null, ISimulationSink? sink = null)
    {
        var fixture = new HostFixture(declarations: HostFixture.BehaviorDeclarations, initialize: false);
        fixture.Write(ModeValue, mode);
        fixture.Write($"{Module}.ServiceParam.A.Index", 1);
        fixture.Write($"{Module}.ServiceParam.A.Time", 2000);
        fixture.Write($"{Module}.ModuleConfigParamList[1].Value", "30");
        fixture.Host.Initialize(fixture.Image, ConfiguredAlarmTable.Empty, behaviors ?? StandardBehaviors(), sink);
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
        ReleaseAndScan(fixture);
        fixture.Host.Initialize(fixture.Image, ConfiguredAlarmTable.Empty, StandardBehaviors());
        StartAndScan(fixture);
        ReleaseAndScan(fixture);
        Assert.True(fixture.Host.IsExecuteRejected(PathA));
        fixture.Host.Initialize(fixture.Image, ConfiguredAlarmTable.Empty, StandardBehaviors());
        Assert.False(fixture.Host.IsExecuteRejected(PathA));
    }

    private static ServiceBehaviorTable AngleBehaviors(ParamRange[] ranges, SvidWrite[] beforeComplete) => new(new Dictionary<string, ModuleBehavior>
    {
        [Module] = new ModuleBehavior(
            "ModuleVariableList[1].Value",
            [new ModeRule(1, "Manual", false, ModeRuleKind.DenyListed, new HashSet<string>())],
            new Dictionary<string, ServiceBehavior> { ["A"] = new(ranges, "Time", [], beforeComplete, []) }),
    });

    [Fact]
    [Trait("AC", "4")]
    public void An_LREAL_parameter_out_of_range_aborts_with_the_integer_part_and_the_bounds_in_the_message()
    {
        HostFixture fixture = Arrange("1", AngleBehaviors([new ParamRange("Angle", 0, 360)], []));
        fixture.Write($"{Module}.ServiceParam.A.Angle", 400.5);

        StartAndScan(fixture);
        ReleaseAndScan(fixture);

        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "A"));
        Assert.Equal(1, ErrorField(fixture, 1, "MainErrorId"));
        Assert.Equal(400, ErrorField(fixture, 1, "SourceErrorId"));
        Assert.Equal("Angle out of range (0~360).", fixture.Read($"{ErrorOfA}[1].Message"));
    }

    private static HostFixture ArrangeAddAngle()
    {
        var add = new SvidWrite("ModuleVariableList[3].Value", true, new SvidValue(SvidValueKind.AddParam, 0, "Angle", false), false);
        HostFixture fixture = Arrange("1", AngleBehaviors([], [add]));
        fixture.Write($"{Module}.ServiceParam.A.Angle", 5.0);
        fixture.Write($"{Module}.ModuleVariableList[3].Value", "10.0");
        return fixture;
    }

    [Fact]
    [Trait("AC", "8")]
    public void AddParam_adds_the_field_once_per_Complete()
    {
        HostFixture fixture = ArrangeAddAngle();

        StartAndScan(fixture);
        ReleaseAndScan(fixture);
        fixture.Scan(TimeSpan.FromMilliseconds(2000));
        fixture.Scan();
        Assert.Equal(PackMLState.Complete, fixture.ServiceState(1, "A"));
        Assert.Equal("15.0", Svid(fixture, 3));

        fixture.ServiceCommand(1, "A", PackMLCommand.Reset, 2);
        fixture.Scan();
        Assert.Equal(PackMLState.Idle, fixture.ServiceState(1, "A"));
        fixture.ServiceCommand(1, "A", PackMLCommand.Start, 3);
        fixture.Scan(TimeSpan.FromMilliseconds(2000));
        fixture.Scan();

        Assert.Equal(PackMLState.Complete, fixture.ServiceState(1, "A"));
        Assert.Equal("20.0", Svid(fixture, 3));
    }

    [Fact]
    [Trait("AC", "8")]
    public void AddParam_writes_nothing_when_the_service_is_stopped_in_Execute()
    {
        HostFixture fixture = ArrangeAddAngle();
        StartAndScan(fixture);
        ReleaseAndScan(fixture);

        fixture.SendToService(1, "A", PackMLCommand.Stop, 2);
        fixture.Scan();

        Assert.NotEqual(PackMLState.Execute, fixture.ServiceState(1, "A"));
        Assert.Equal("10.0", Svid(fixture, 3));
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

    private static ServiceBehaviorTable ProcessBehaviors() => new(new Dictionary<string, ModuleBehavior>
    {
        [Module] = new ModuleBehavior(
            "ModuleVariableList[1].Value",
            [new ModeRule(1, "Manual", false, ModeRuleKind.DenyListed, new HashSet<string>())],
            new Dictionary<string, ServiceBehavior>
            {
                ["A"] = new([], null, [], [], [], new ProcessBehavior(
                    "Count", 6, "Steps[i].Time", null,
                    [new ProcessCheck("Steps[i].Kind", ProcessCheckKind.Range, 1, new ProcessBound(4, null, null), null)],
                    [new ProcessWrite(
                        new KeyedChoice<SvidTarget>([], new Dictionary<string, SvidTarget> { [""] = new("ModuleVariableList[2].Value", false) }),
                        new ProcessValue(ProcessValueKind.Field, "Steps[i].Kind", null, null), false, false, null)])),
            }),
    });

    private static HostFixture ArrangeProcess(int count, int timeMs, int firstKind = 1)
    {
        var fixture = new HostFixture(declarations: HostFixture.ProcessDeclarations, initialize: false);
        fixture.Write(ModeValue, "1");
        fixture.Write($"{Module}.ServiceParam.A.Count", (long)count);
        for (int step = 1; step <= count; step++)
        {
            fixture.Write($"{Module}.ServiceParam.A.Steps[{step}].Kind", (long)(step == 1 ? firstKind : step));
            fixture.Write($"{Module}.ServiceParam.A.Steps[{step}].Time", (long)timeMs);
        }
        fixture.Host.Initialize(fixture.Image, ConfiguredAlarmTable.Empty, ProcessBehaviors());
        return fixture;
    }

    [Fact]
    [Trait("AC", "5")]
    public void A_step_parameter_out_of_range_aborts_the_service_with_the_step_error()
    {
        HostFixture fixture = ArrangeProcess(1, 1000, firstKind: 9);

        StartAndScan(fixture);
        ReleaseAndScan(fixture);

        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "A"));
        Assert.Equal(1, ErrorField(fixture, 1, "MainErrorId"));
        Assert.Equal(9, ErrorField(fixture, 1, "SourceErrorId"));
        Assert.Equal("Steps[1].Kind out of range (1~4).", fixture.Read($"{ErrorOfA}[1].Message"));
    }

    [Fact]
    [Trait("AC", "11")]
    public void The_step_times_decide_when_the_service_completes_not_the_default_delay()
    {
        HostFixture fixture = ArrangeProcess(2, 250);
        StartAndScan(fixture);
        ReleaseAndScan(fixture);

        ScanUpTo(fixture, TimeSpan.FromMilliseconds(400));
        PackMLState during = fixture.ServiceState(1, "A");
        string svid = Svid(fixture, 2);
        ScanUpTo(fixture, TimeSpan.FromMilliseconds(600));
        fixture.Scan();

        Assert.Equal(PackMLState.Execute, during);
        Assert.Equal("2", svid);
        Assert.Equal(PackMLState.Complete, fixture.ServiceState(1, "A"));
        Assert.Equal("0", Svid(fixture, 2));
    }

    [Fact]
    [Trait("AC", "14")]
    public void Hold_zeroes_the_step_SVID_and_Unhold_writes_it_again_and_restarts_the_step_time()
    {
        HostFixture fixture = ArrangeProcess(1, 1000);
        StartAndScan(fixture);
        ReleaseAndScan(fixture);
        ScanUpTo(fixture, TimeSpan.FromMilliseconds(800));

        fixture.ServiceCommand(1, "A", PackMLCommand.Hold, 2);
        string held = Svid(fixture, 2);
        fixture.ServiceCommand(1, "A", PackMLCommand.Unhold, 3);
        string resumed = Svid(fixture, 2);
        ScanUpTo(fixture, TimeSpan.FromMilliseconds(1700));
        PackMLState beforeEnd = fixture.ServiceState(1, "A");
        ScanUpTo(fixture, TimeSpan.FromMilliseconds(1900));
        fixture.Scan();

        Assert.Equal("0", held);
        Assert.Equal("1", resumed);
        Assert.Equal(PackMLState.Execute, beforeEnd);
        Assert.Equal(PackMLState.Complete, fixture.ServiceState(1, "A"));
    }

    [Fact]
    [Trait("AC", "14")]
    [Trait("AC", "15")]
    public void Stop_zeroes_the_step_SVID_and_a_new_Start_begins_at_the_first_step()
    {
        HostFixture fixture = ArrangeProcess(2, 1000);
        StartAndScan(fixture);
        ReleaseAndScan(fixture);
        ScanUpTo(fixture, TimeSpan.FromMilliseconds(1200));
        string secondStep = Svid(fixture, 2);

        fixture.ServiceCommand(1, "A", PackMLCommand.Stop, 2);
        string stopped = Svid(fixture, 2);
        fixture.ServiceCommand(1, "A", PackMLCommand.Reset, 3);
        fixture.ServiceCommand(1, "A", PackMLCommand.Start, 4);

        Assert.Equal("2", secondStep);
        Assert.Equal("0", stopped);
        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));
        Assert.Equal("1", Svid(fixture, 2));
    }

    [Fact]
    [Trait("AC", "16")]
    public void A_service_a_workflow_took_over_runs_no_steps()
    {
        HostFixture fixture = ArrangeProcess(1, 1000);
        fixture.SetOverrideToken(1, "A", new object());

        StartAndScan(fixture);
        ReleaseAndScan(fixture);
        ScanUpTo(fixture, TimeSpan.FromMilliseconds(1500));

        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));
        Assert.Equal("", Svid(fixture, 2));
    }

    private const string EcidOfHolder = "ModuleConfigParamList[1].Value";

    private static SimAction SimOf(SimMoment when, string action, SvidValue? value = null, string? dryRunAction = null, string target = "T") =>
        new(when, target, action, value, dryRunAction);

    private static SvidValue EcidValue(bool negate = false) => new(SvidValueKind.Ecid, 0, EcidOfHolder, negate);

    private static SvidValue ParamValue(string field) => new(SvidValueKind.Param, 0, field, false);

    private static SvidValue ConstantValue(double constant) => new(SvidValueKind.Constant, constant, null, false);

    private static ServiceBehaviorTable SimBehaviors(SimAction[]? forA = null, SimAction[]? forB = null, string? durationOfA = null) => new(new Dictionary<string, ModuleBehavior>
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
                ["A"] = new([new ParamRange("Index", 1, 4)], durationOfA, [], [], [], Sim: forA),
                ["B"] = new([], null, [], [], [], Sim: forB),
            }),
    });

    private static RecordingSimulationSink.SimCall Call(string action, object? value, int? durationMs, string target = "T") =>
        new(target, action, value, durationMs);

    private static HostFixture ArrangeSim(RecordingSimulationSink sink, SimAction[]? forA = null, SimAction[]? forB = null, string mode = "1", string? durationOfA = null) =>
        Arrange(mode, SimBehaviors(forA, forB, durationOfA), sink);

    private static void RunToComplete(HostFixture fixture, TimeSpan length)
    {
        StartAndScan(fixture);
        ReleaseAndScan(fixture);
        fixture.Scan(length);
        fixture.Scan();
    }

    [Fact]
    [Trait("AC", "3")]
    public void A_NaturalCompletion_service_sends_Enter_with_the_default_delay_BeforeComplete_with_0_and_Leave_with_null_in_order()
    {
        var sink = new RecordingSimulationSink();
        HostFixture fixture = ArrangeSim(sink, forA:
        [
            SimOf(SimMoment.Leave, "Hold"),
            SimOf(SimMoment.Enter, "Top", EcidValue()),
            SimOf(SimMoment.BeforeComplete, "Top", EcidValue()),
        ]);

        StartAndScan(fixture);
        Assert.Equal([Call("Top", 30.0, 5000)], sink.Calls);
        ReleaseAndScan(fixture);
        fixture.Scan(HostFixture.DefaultDelay);
        fixture.Scan();

        Assert.Equal(PackMLState.Complete, fixture.ServiceState(1, "A"));
        Assert.Equal([Call("Top", 30.0, 5000), Call("Top", 30.0, 0), Call("Hold", null, null)], sink.Calls);
    }

    [Fact]
    [Trait("AC", "3")]
    public void The_duration_field_is_the_Enter_duration_of_a_NaturalCompletion_service()
    {
        var sink = new RecordingSimulationSink();
        HostFixture fixture = ArrangeSim(sink, forA: [SimOf(SimMoment.Enter, "Top")], durationOfA: "Time");

        StartAndScan(fixture);

        Assert.Equal([Call("Top", null, 2000)], sink.Calls);
    }

    [Fact]
    [Trait("AC", "3")]
    public void An_ExecuteUntilExternalStop_service_sends_Enter_with_a_null_duration_and_Leave_after_Stop()
    {
        var sink = new RecordingSimulationSink();
        HostFixture fixture = ArrangeSim(sink, forB: [SimOf(SimMoment.Enter, "On"), SimOf(SimMoment.Leave, "Off")]);
        StartAndScan(fixture, "B");
        ReleaseAndScan(fixture, "B");
        fixture.Scan(TimeSpan.FromSeconds(10));
        Assert.Equal([Call("On", null, null)], sink.Calls);

        fixture.SendToService(1, "B", PackMLCommand.Stop, 2);
        fixture.Scan();

        Assert.Equal([Call("On", null, null), Call("Off", null, null)], sink.Calls);
    }

    [Fact]
    [Trait("AC", "8")]
    public void Hold_sends_Leave_without_BeforeComplete_and_Unhold_sends_Enter_again_with_the_full_length()
    {
        var sink = new RecordingSimulationSink();
        HostFixture fixture = ArrangeSim(sink, forA:
        [
            SimOf(SimMoment.Enter, "Top"),
            SimOf(SimMoment.BeforeComplete, "Top"),
            SimOf(SimMoment.Leave, "Hold"),
        ]);
        StartAndScan(fixture);
        ReleaseAndScan(fixture);
        fixture.Scan(TimeSpan.FromSeconds(2));

        fixture.ServiceCommand(1, "A", PackMLCommand.Hold, 2);
        Assert.Equal([Call("Top", null, 5000), Call("Hold", null, null)], sink.Calls);
        fixture.ServiceCommand(1, "A", PackMLCommand.Unhold, 3);

        Assert.Equal(PackMLState.Execute, fixture.ServiceState(1, "A"));
        Assert.Equal([Call("Top", null, 5000), Call("Hold", null, null), Call("Top", null, 5000)], sink.Calls);
    }

    [Fact]
    [Trait("AC", "8")]
    public void Abort_sends_Leave()
    {
        var sink = new RecordingSimulationSink();
        HostFixture fixture = ArrangeSim(sink, forB: [SimOf(SimMoment.Enter, "On"), SimOf(SimMoment.Leave, "Off")]);
        StartAndScan(fixture, "B");
        ReleaseAndScan(fixture, "B");

        fixture.SendToService(1, "B", PackMLCommand.Abort, 2);
        fixture.Scan();

        Assert.NotEqual(PackMLState.Execute, fixture.ServiceState(1, "B"));
        Assert.Equal([Call("On", null, null), Call("Off", null, null)], sink.Calls);
    }

    [Theory]
    [Trait("AC", "7")]
    [InlineData("1", "On", 5.0)]
    [InlineData("2", "Off", null)]
    public void In_DryRun_an_entry_with_a_dry_run_action_sends_it_without_a_value_and_the_other_entries_are_unchanged(string mode, string expectedAction, double? expectedValue)
    {
        var sink = new RecordingSimulationSink();
        HostFixture fixture = ArrangeSim(sink, forA:
        [
            SimOf(SimMoment.Enter, "On", ConstantValue(5), dryRunAction: "Off"),
            SimOf(SimMoment.Enter, "Mark", ConstantValue(6)),
        ], mode: mode);

        StartAndScan(fixture);

        Assert.Equal([Call(expectedAction, expectedValue, 5000), Call("Mark", 6.0, 5000)], sink.Calls);
    }

    [Fact]
    [Trait("AC", "3")]
    public void The_value_comes_from_an_ECID_text_a_param_a_constant_and_Negate_and_is_null_without_a_source()
    {
        var sink = new RecordingSimulationSink();
        HostFixture fixture = ArrangeSim(sink, forB:
        [
            SimOf(SimMoment.Enter, "Ecid", EcidValue()),
            SimOf(SimMoment.Enter, "Param", ParamValue("Speed")),
            SimOf(SimMoment.Enter, "Constant", ConstantValue(4)),
            SimOf(SimMoment.Enter, "Negated", EcidValue(negate: true)),
            SimOf(SimMoment.Enter, "None"),
        ]);
        fixture.Write($"{Module}.ServiceParam.B.Speed", 2.5);

        StartAndScan(fixture, "B");

        Assert.Equal(
            [Call("Ecid", 30.0, null), Call("Param", 2.5, null), Call("Constant", 4.0, null), Call("Negated", -30.0, null), Call("None", null, null)],
            sink.Calls);
    }

    [Theory]
    [Trait("AC", "3")]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_value_that_is_not_a_finite_number_is_sent_as_null(double angle)
    {
        var sink = new RecordingSimulationSink();
        HostFixture fixture = ArrangeSim(sink, forB: [SimOf(SimMoment.Enter, "Param", ParamValue("Angle"))]);
        fixture.Write($"{Module}.ServiceParam.B.Angle", angle);

        StartAndScan(fixture, "B");

        Assert.Equal([Call("Param", null, null)], sink.Calls);
    }

    [Theory]
    [Trait("AC", "3")]
    [InlineData("0", 1)]
    [InlineData("1", 5)]
    public void A_refused_entry_sends_no_sim_action(string mode, int index)
    {
        var sink = new RecordingSimulationSink();
        HostFixture fixture = ArrangeSim(
            sink,
            forA: [SimOf(SimMoment.Enter, "Top"), SimOf(SimMoment.BeforeComplete, "Top"), SimOf(SimMoment.Leave, "Hold")],
            mode: mode);
        fixture.Write($"{Module}.ServiceParam.A.Index", index);

        StartAndScan(fixture);
        ReleaseAndScan(fixture);

        Assert.Equal(PackMLState.Aborted, fixture.ServiceState(1, "A"));
        Assert.Empty(sink.Calls);
    }

    [Fact]
    [Trait("AC", "3")]
    public void A_service_a_workflow_took_over_sends_no_sim_action()
    {
        var sink = new RecordingSimulationSink();
        HostFixture fixture = ArrangeSim(sink, forB: [SimOf(SimMoment.Enter, "On"), SimOf(SimMoment.Leave, "Off")]);
        fixture.SetOverrideToken(1, "B", new object());
        StartAndScan(fixture, "B");
        ReleaseAndScan(fixture, "B");

        fixture.SendToService(1, "B", PackMLCommand.Stop, 2);
        fixture.Scan();

        Assert.NotEqual(PackMLState.Execute, fixture.ServiceState(1, "B"));
        Assert.Empty(sink.Calls);
    }

    [Fact]
    [Trait("AC", "3")]
    public void Without_a_sink_and_without_a_behavior_table_a_service_runs_as_before_and_sends_nothing()
    {
        var sink = new RecordingSimulationSink();
        var withoutTable = new HostFixture(declarations: HostFixture.BehaviorDeclarations, initialize: false);
        withoutTable.Host.Initialize(withoutTable.Image, ConfiguredAlarmTable.Empty, ServiceBehaviorTable.Empty, sink);
        HostFixture withoutSink = Arrange("1", SimBehaviors(forA: [SimOf(SimMoment.Enter, "Top"), SimOf(SimMoment.Leave, "Hold")]));

        RunToComplete(withoutTable, HostFixture.DefaultDelay);
        RunToComplete(withoutSink, HostFixture.DefaultDelay);

        Assert.Equal(PackMLState.Complete, withoutTable.ServiceState(1, "A"));
        Assert.Equal(PackMLState.Complete, withoutSink.ServiceState(1, "A"));
        Assert.Empty(sink.Calls);
    }

    [Fact]
    [Trait("AC", "3")]
    public void A_NaturalCompletion_rotation_sends_Enter_CW_with_the_default_delay_and_Leave_Stop()
    {
        var sink = new RecordingSimulationSink();
        HostFixture fixture = ArrangeSim(sink, forA:
        [
            SimOf(SimMoment.Enter, "CW", EcidValue(), target: "Holder"),
            SimOf(SimMoment.Leave, "Stop", target: "Holder"),
        ]);

        RunToComplete(fixture, HostFixture.DefaultDelay);

        Assert.Equal(PackMLState.Complete, fixture.ServiceState(1, "A"));
        Assert.Equal([Call("CW", 30.0, 5000, "Holder"), Call("Stop", null, null, "Holder")], sink.Calls);
    }
}
