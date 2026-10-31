using AdsPlcSimulator.Core.PackML;
using AdsPlcSimulator.Core.PackML.Behavior;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.PackML;

public class ProcessRulesTests
{
    private const string Module = "GVL_Test.Modules[1]";
    private const string Param = Module + ".ServiceParam.A";
    private const long CycleStart = 1;
    private const long CycleEnd = 2;

    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OneMs = TimeSpan.FromMilliseconds(1);
    private static readonly ProcessCycle Cycle = new("Steps[i].Control", CycleStart, CycleEnd, "Steps[i].Times");

    private readonly PlcProgram _program = TestPaths.BuildFromText(HostFixture.ProcessDeclarations);
    private readonly ProcessImage _image;

    public ProcessRulesTests()
    {
        _image = _program.CreateProcessImage();
    }

    private static ProcessBehavior Behavior(
        int maxCount = 6, ProcessCycle? cycle = null, ProcessCheck[]? checks = null, ProcessWrite[]? writes = null) =>
        new("Count", maxCount, "Steps[i].Time", cycle, checks ?? [], writes ?? []);

    private static ProcessCheck Range(string field, double min, double max, ProcessCondition? when = null) =>
        new(field, ProcessCheckKind.Range, min, new ProcessBound(max, null, null), when);

    private static ProcessCheck Greater(string field, double min) =>
        new(field, ProcessCheckKind.GreaterThan, min, null, null);

    private static KeyedChoice<T> Fixed<T>(T value) => new([], new Dictionary<string, T> { [""] = value });

    private static SvidTarget Target(int svid, bool isReal = false) => new($"ModuleVariableList[{svid}].Value", isReal);

    private static ProcessWrite Write(
        int svid, string field, bool isReal = false, bool dryRunZero = false, bool keep = false,
        ProcessCondition? when = null, ProcessCondition? negateWhen = null) =>
        new(Fixed(Target(svid, isReal)), new ProcessValue(ProcessValueKind.Field, field, null, negateWhen), dryRunZero, keep, when);

    private ProcessRules Rules(ProcessBehavior behavior) =>
        ProcessRules.Create(_program.Symbols, Module, Param, behavior);

    private void Set(string relative, object value) =>
        _image.WriteValue(_program.Symbols.Resolve($"{Param}.{relative}"), value, WriteSource.Client);

    private void SetModule(string relative, object value) =>
        _image.WriteValue(_program.Symbols.Resolve($"{Module}.{relative}"), value, WriteSource.Client);

    private string Svid(int index) => (string)_image.ReadValue(_program.Symbols.Resolve($"{Module}.ModuleVariableList[{index}].Value"));

    private void SetSteps(int count, long timeMs = 1000)
    {
        Set("Count", (long)count);
        for (int step = 1; step <= count; step++)
        {
            Set($"Steps[{step}].Kind", (long)step);
            Set($"Steps[{step}].Time", timeMs);
        }
    }

    private List<string> EntryOrder(ProcessRules rules, int svid)
    {
        ProcessRun run = rules.Start(_image, T0, false);
        List<string> order = [Svid(svid)];
        TimeSpan now = T0;
        while (true)
        {
            now += OneMs;
            if (run.Advance(_image, now))
                return order;

            order.Add(Svid(svid));
        }
    }

    [Fact]
    [Trait("AC", "5")]
    public void A_step_count_outside_1_to_MaxCount_is_refused_with_the_count_as_the_source_id()
    {
        ProcessRules rules = Rules(Behavior());
        SetSteps(1);

        Set("Count", 0L);
        EntryCheck zero = rules.Check(_image);
        Set("Count", 7L);
        EntryCheck seven = rules.Check(_image);
        Set("Count", 6L);
        EntryCheck six = rules.Check(_image);
        Set("Count", 1L);
        EntryCheck one = rules.Check(_image);

        Assert.Equal(new EntryCheck(1, 0, "Count out of range (1~6)."), zero);
        Assert.Equal(new EntryCheck(1, 7, "Count out of range (1~6)."), seven);
        Assert.True(six.Passed);
        Assert.True(one.Passed);
    }

    [Fact]
    [Trait("AC", "6")]
    [Trait("AC", "9")]
    public void A_constant_range_check_names_the_step_and_passes_at_the_bounds()
    {
        ProcessRules rules = Rules(Behavior(checks: [Range("Steps[i].Kind", 1, 4)]));
        SetSteps(2);
        Set("Steps[2].Kind", 5L);

        EntryCheck refused = rules.Check(_image);
        Set("Steps[2].Kind", 1L);
        EntryCheck lower = rules.Check(_image);
        Set("Steps[2].Kind", 4L);
        EntryCheck upper = rules.Check(_image);

        Assert.Equal(new EntryCheck(1, 5, "Steps[2].Kind out of range (1~4)."), refused);
        Assert.True(lower.Passed);
        Assert.True(upper.Passed);
    }

    [Fact]
    [Trait("AC", "6")]
    public void A_range_check_can_take_its_upper_bound_from_a_field()
    {
        ProcessRules rules = Rules(Behavior(checks: [new ProcessCheck("Steps[i].Pick", ProcessCheckKind.Range, 1, new ProcessBound(null, "Limit", null), null)]));
        SetSteps(1);
        Set("Limit", 2L);
        Set("Steps[1].Pick", 3L);

        EntryCheck refused = rules.Check(_image);
        Set("Steps[1].Pick", 2L);

        Assert.Equal(new EntryCheck(1, 3, "Steps[1].Pick out of range (1~2)."), refused);
        Assert.True(rules.Check(_image).Passed);
    }

    [Fact]
    [Trait("AC", "6")]
    public void A_range_check_can_take_its_upper_bound_from_a_lookup_and_an_unknown_key_always_fails()
    {
        var lookup = new KeyedChoice<double>(["Steps[i].Kind"], new Dictionary<string, double> { ["1"] = 2 });
        ProcessRules rules = Rules(Behavior(checks: [new ProcessCheck("Steps[i].Pick", ProcessCheckKind.Range, 1, new ProcessBound(null, null, lookup), null)]));
        SetSteps(1);
        Set("Steps[1].Kind", 1L);
        Set("Steps[1].Pick", 3L);

        EntryCheck over = rules.Check(_image);
        Set("Steps[1].Kind", 2L);
        Set("Steps[1].Pick", 1L);
        EntryCheck unknown = rules.Check(_image);

        Assert.Equal(new EntryCheck(1, 3, "Steps[1].Pick out of range (1~2)."), over);
        Assert.Equal(new EntryCheck(1, 1, "Steps[1].Pick out of range (1~0)."), unknown);
    }

    [Fact]
    [Trait("AC", "6")]
    public void A_greater_than_check_refuses_zero_and_not_a_number_and_passes_a_small_positive_real()
    {
        ProcessRules rules = Rules(Behavior(checks: [Greater("Steps[i].Time", 0), Greater("Steps[i].Speed", 0)]));
        SetSteps(1);
        Set("Steps[1].Speed", 0.5);
        Assert.True(rules.Check(_image).Passed);

        Set("Steps[1].Time", 0L);
        EntryCheck zeroTime = rules.Check(_image);
        Set("Steps[1].Time", 1000L);
        Set("Steps[1].Speed", double.NaN);
        EntryCheck notANumber = rules.Check(_image);

        Assert.Equal(new EntryCheck(1, 0, "Steps[1].Time must be greater than 0."), zeroTime);
        Assert.Equal(new EntryCheck(1, uint.MaxValue, "Steps[1].Speed must be greater than 0."), notANumber);
    }

    [Fact]
    [Trait("AC", "6")]
    public void A_check_with_a_condition_is_skipped_until_the_condition_holds()
    {
        var control = new ProcessCondition("Steps[i].Control", 1);
        ProcessRules rules = Rules(Behavior(checks: [new ProcessCheck("Steps[i].Times", ProcessCheckKind.GreaterThan, 0, null, control)]));
        SetSteps(1);
        Set("Steps[1].Times", 0L);

        Set("Steps[1].Control", 0L);
        EntryCheck skipped = rules.Check(_image);
        Set("Steps[1].Control", 1L);
        EntryCheck checkedNow = rules.Check(_image);

        Assert.True(skipped.Passed);
        Assert.Equal(new EntryCheck(1, 0, "Steps[1].Times must be greater than 0."), checkedNow);
    }

    [Fact]
    [Trait("AC", "9")]
    public void The_first_failure_wins_in_step_order_then_list_order()
    {
        ProcessRules rules = Rules(Behavior(checks: [Range("Steps[i].Kind", 1, 4), Range("Steps[i].Pick", 1, 4)]));
        SetSteps(2);
        Set("Steps[1].Pick", 1L);
        Set("Steps[2].Pick", 1L);
        Set("Steps[1].Kind", 9L);
        Set("Steps[2].Kind", 8L);

        EntryCheck stepOrder = rules.Check(_image);
        Set("Steps[1].Kind", 1L);
        Set("Steps[1].Pick", 7L);
        Set("Steps[2].Pick", 6L);
        EntryCheck listOrder = rules.Check(_image);
        Set("Steps[1].Pick", 1L);
        EntryCheck second = rules.Check(_image);

        Assert.Equal(new EntryCheck(1, 9, "Steps[1].Kind out of range (1~4)."), stepOrder);
        Assert.Equal(new EntryCheck(1, 7, "Steps[1].Pick out of range (1~4)."), listOrder);
        Assert.Equal(new EntryCheck(1, 8, "Steps[2].Kind out of range (1~4)."), second);
    }

    [Fact]
    [Trait("AC", "7")]
    public void Cycle_pairing_refuses_an_End_before_a_Start_and_a_Start_without_an_End()
    {
        ProcessRules rules = Rules(Behavior(cycle: Cycle));
        SetSteps(4);

        Set("Steps[1].Control", CycleEnd);
        EntryCheck endFirst = rules.Check(_image);
        Set("Steps[1].Control", 0L);
        Set("Steps[2].Control", CycleStart);
        Set("Steps[3].Control", CycleStart);
        EntryCheck unclosed = rules.Check(_image);
        Set("Steps[4].Control", CycleEnd);
        EntryCheck stillOpen = rules.Check(_image);
        Set("Steps[3].Control", 0L);
        EntryCheck paired = rules.Check(_image);
        Set("Steps[3].Control", CycleStart);
        Set("Steps[4].Control", CycleEnd);
        Set("Count", 5L);
        Set("Steps[5].Time", 1000L);
        Set("Steps[5].Control", CycleEnd);
        EntryCheck nested = rules.Check(_image);

        Assert.Equal(new EntryCheck(1, 1, "Steps[1].Control End without Start."), endFirst);
        Assert.Equal(new EntryCheck(1, 3, "Steps[3].Control Start without End."), unclosed);
        Assert.Equal(new EntryCheck(1, 2, "Steps[2].Control Start without End."), stillOpen);
        Assert.True(paired.Passed);
        Assert.True(nested.Passed);
    }

    [Fact]
    [Trait("AC", "8")]
    public void A_check_on_an_array_element_of_another_array_runs_for_every_step()
    {
        ProcessRules rules = Rules(Behavior(checks: [Range("Extra[i].Target", 1, 4)]));
        SetSteps(3);
        for (int step = 1; step <= 3; step++)
            Set($"Extra[{step}].Target", 1L);
        Set("Extra[2].Target", 5L);

        EntryCheck refused = rules.Check(_image);

        Assert.Equal(new EntryCheck(1, 5, "Extra[2].Target out of range (1~4)."), refused);
    }

    [Fact]
    [Trait("AC", "11")]
    [Trait("AC", "12")]
    [Trait("AC", "14")]
    public void Advance_changes_the_step_when_its_time_has_passed_and_zeroes_the_SVIDs_at_the_end()
    {
        ProcessRules rules = Rules(Behavior(writes: [
            Write(2, "Steps[i].Kind"),
            Write(4, "Steps[i].Kind", when: new ProcessCondition("Steps[i].Pick", 1))]));
        SetSteps(2);
        Set("Steps[1].Kind", 10L);
        Set("Steps[2].Kind", 20L);
        Set("Steps[1].Pick", 1L);
        Set("Steps[2].Pick", 2L);
        ProcessRun run = rules.Start(_image, T0, false);
        string firstStep2 = Svid(2), firstStep4 = Svid(4);

        bool early = run.Advance(_image, T0 + TimeSpan.FromMilliseconds(999));
        string stillFirst = Svid(2);
        bool second = run.Advance(_image, T0 + TimeSpan.FromSeconds(1));
        string secondSvid2 = Svid(2), secondSvid4 = Svid(4);
        bool done = run.Advance(_image, T0 + TimeSpan.FromSeconds(2));

        Assert.Equal(("10", "10"), (firstStep2, firstStep4));
        Assert.False(early);
        Assert.Equal("10", stillFirst);
        Assert.False(second);
        Assert.Equal(("20", "0"), (secondSvid2, secondSvid4));
        Assert.True(done);
        Assert.Equal(("0", "0"), (Svid(2), Svid(4)));
    }

    [Fact]
    [Trait("AC", "11")]
    public void Several_short_steps_pass_in_one_Advance()
    {
        ProcessRules rules = Rules(Behavior(writes: [Write(2, "Steps[i].Kind")]));
        SetSteps(3, timeMs: 1);
        ProcessRun run = rules.Start(_image, T0, false);

        bool done = run.Advance(_image, T0 + TimeSpan.FromMilliseconds(10));

        Assert.True(done);
        Assert.Equal("0", Svid(2));
    }

    [Fact]
    [Trait("AC", "10")]
    public void A_cycle_repeats_its_steps_CycleCount_times()
    {
        ProcessRules rules = Rules(Behavior(cycle: Cycle, writes: [Write(2, "Steps[i].Kind")]));
        SetSteps(3, timeMs: 1);
        Set("Steps[2].Control", CycleStart);
        Set("Steps[2].Times", 2L);
        Set("Steps[3].Control", CycleEnd);

        List<string> order = EntryOrder(rules, 2);

        Assert.Equal(["1", "2", "3", "2", "3"], order);
    }

    [Fact]
    [Trait("AC", "10")]
    public void Nested_cycles_expand_to_the_documented_sequence()
    {
        ProcessRules rules = Rules(Behavior(cycle: Cycle, writes: [Write(2, "Steps[i].Kind")]));
        SetSteps(6, timeMs: 1);
        Set("Steps[2].Control", CycleStart);
        Set("Steps[2].Times", 3L);
        Set("Steps[4].Control", CycleStart);
        Set("Steps[4].Times", 3L);
        Set("Steps[5].Control", CycleEnd);
        Set("Steps[6].Control", CycleEnd);

        List<string> order = EntryOrder(rules, 2);

        Assert.Equal(28, order.Count);
        Assert.Equal(["1", "2", "3", "4", "5", "4", "5", "4", "5", "6", "2", "3", "4", "5"], order.Take(14));
        Assert.Equal("6", order[^1]);
    }

    [Fact]
    [Trait("AC", "10")]
    public void An_End_without_a_Start_in_an_unchecked_plan_moves_on()
    {
        ProcessRules rules = Rules(Behavior(cycle: Cycle, writes: [Write(2, "Steps[i].Kind")]));
        SetSteps(2, timeMs: 1);
        Set("Steps[1].Control", CycleEnd);

        List<string> order = EntryOrder(rules, 2);

        Assert.Equal(["1", "2"], order);
    }

    [Fact]
    [Trait("AC", "12")]
    public void A_direction_condition_negates_the_value_and_a_DINT_rounds_half_away_from_zero()
    {
        var down = new ProcessCondition("Steps[i].Dir", 2);
        ProcessRules rules = Rules(Behavior(writes: [Write(2, "Steps[i].Speed", negateWhen: down)]));
        SetSteps(1);

        Set("Steps[1].Speed", 50.0);
        Set("Steps[1].Dir", 2L);
        rules.Start(_image, T0, false);
        string negated = Svid(2);
        Set("Steps[1].Dir", 1L);
        rules.Start(_image, T0, false);
        string plain = Svid(2);
        Set("Steps[1].Speed", 2.5);
        rules.Start(_image, T0, false);
        string up = Svid(2);
        Set("Steps[1].Dir", 2L);
        rules.Start(_image, T0, false);
        string down25 = Svid(2);
        Set("Steps[1].Speed", 0.4);
        rules.Start(_image, T0, false);
        string small = Svid(2);

        Assert.Equal(("-50", "50", "3", "-3", "0"), (negated, plain, up, down25, small));
    }

    [Fact]
    [Trait("AC", "12")]
    public void A_value_that_is_not_finite_is_written_as_zero()
    {
        ProcessRules rules = Rules(Behavior(writes: [Write(2, "Steps[i].Speed"), Write(3, "Steps[i].Speed", isReal: true)]));
        SetSteps(1);
        Set("Steps[1].Speed", double.NaN);

        rules.Start(_image, T0, false);

        Assert.Equal(("0", "0.0"), (Svid(2), Svid(3)));
    }

    [Fact]
    [Trait("AC", "12")]
    public void A_write_picks_its_SVID_by_the_key_fields_and_skips_an_unknown_key()
    {
        var targets = new KeyedChoice<SvidTarget>(
            ["Steps[i].Kind", "Steps[i].Pick"],
            new Dictionary<string, SvidTarget> { ["1,3"] = Target(2), ["2,1"] = Target(4) });
        var write = new ProcessWrite(targets, new ProcessValue(ProcessValueKind.Field, "Steps[i].Speed", null, null), false, false, null);
        ProcessRules rules = Rules(Behavior(writes: [write]));
        SetSteps(1);
        Set("Steps[1].Speed", 7.0);

        Set("Steps[1].Kind", 1L);
        Set("Steps[1].Pick", 3L);
        rules.Start(_image, T0, false);
        (string, string) first = (Svid(2), Svid(4));
        Set("Steps[1].Kind", 2L);
        Set("Steps[1].Pick", 1L);
        rules.Start(_image, T0, false);
        (string, string) second = (Svid(2), Svid(4));
        SetModule("ModuleVariableList[2].Value", "");
        SetModule("ModuleVariableList[4].Value", "");
        Set("Steps[1].Kind", 3L);
        rules.Start(_image, T0, false);

        Assert.Equal(("7", ""), first);
        Assert.Equal(("7", "7"), second);
        Assert.Equal(("", ""), (Svid(2), Svid(4)));
    }

    [Fact]
    [Trait("AC", "12")]
    [Trait("AC", "13")]
    public void DryRunZero_writes_zero_in_a_dry_run_and_a_write_whose_condition_fails_is_skipped()
    {
        var picked = new ProcessCondition("Steps[i].Pick", 1);
        ProcessRules rules = Rules(Behavior(writes: [
            Write(2, "Steps[i].Speed", dryRunZero: true),
            Write(4, "Steps[i].Speed", when: picked)]));
        SetSteps(1);
        Set("Steps[1].Speed", 9.0);
        Set("Steps[1].Pick", 2L);

        rules.Start(_image, T0, true);
        (string, string) dry = (Svid(2), Svid(4));
        rules.Start(_image, T0, false);
        (string, string) live = (Svid(2), Svid(4));

        Assert.Equal(("0", ""), dry);
        Assert.Equal(("9", ""), live);
    }

    [Fact]
    [Trait("AC", "13")]
    public void An_ECID_value_is_read_as_text_and_a_text_that_is_not_a_number_counts_as_zero()
    {
        var ecids = new KeyedChoice<string>(
            ["Steps[i].Pick"],
            new Dictionary<string, string> { ["1"] = "ModuleConfigParamList[1].Value" });
        var write = new ProcessWrite(Fixed(Target(3, isReal: true)), new ProcessValue(ProcessValueKind.Ecid, null, ecids, null), false, false, null);
        ProcessRules rules = Rules(Behavior(writes: [write]));
        SetSteps(1);
        Set("Steps[1].Pick", 1L);

        SetModule("ModuleConfigParamList[1].Value", "80");
        rules.Start(_image, T0, false);
        string number = Svid(3);
        SetModule("ModuleConfigParamList[1].Value", "abc");
        rules.Start(_image, T0, false);
        string text = Svid(3);
        Set("Steps[1].Pick", 2L);
        SetModule("ModuleConfigParamList[1].Value", "80");
        rules.Start(_image, T0, false);

        Assert.Equal(("80.0", "0.0"), (number, text));
        Assert.Equal("0.0", Svid(3));
    }

    [Fact]
    [Trait("AC", "14")]
    public void Leave_zeroes_the_written_SVIDs_once_and_a_Keep_SVID_stays()
    {
        ProcessRules rules = Rules(Behavior(writes: [Write(2, "Steps[i].Kind"), Write(3, "Steps[i].Kind", isReal: true, keep: true)]));
        SetSteps(1);
        Set("Steps[1].Kind", 6L);
        ProcessRun run = rules.Start(_image, T0, false);

        run.Leave(_image);
        (string, string) afterLeave = (Svid(2), Svid(3));
        SetModule("ModuleVariableList[2].Value", "99");
        run.Leave(_image);

        Assert.Equal(("0", "6.0"), afterLeave);
        Assert.Equal("99", Svid(2));
    }

    [Fact]
    [Trait("AC", "14")]
    public void Resume_restarts_the_current_step_and_does_nothing_once_the_run_has_finished()
    {
        ProcessRules rules = Rules(Behavior(writes: [Write(2, "Steps[i].Kind")]));
        SetSteps(1);
        Set("Steps[1].Kind", 6L);
        ProcessRun run = rules.Start(_image, T0, false);
        run.Leave(_image);

        run.Resume(_image, T0 + TimeSpan.FromMilliseconds(800));
        string resumed = Svid(2);
        bool early = run.Advance(_image, T0 + TimeSpan.FromMilliseconds(1700));
        bool done = run.Advance(_image, T0 + TimeSpan.FromMilliseconds(1800));
        run.Resume(_image, T0 + TimeSpan.FromSeconds(9));

        Assert.Equal("6", resumed);
        Assert.False(early);
        Assert.True(done);
        Assert.Equal("0", Svid(2));
    }

    [Fact]
    [Trait("AC", "15")]
    public void A_run_keeps_the_plan_it_copied_when_the_parameters_change()
    {
        ProcessRules rules = Rules(Behavior(cycle: Cycle, writes: [Write(2, "Steps[i].Kind")]));
        SetSteps(2);
        ProcessRun run = rules.Start(_image, T0, false);

        Set("Count", 255L);
        Set("Steps[1].Time", 0L);
        Set("Steps[1].Control", CycleStart);
        Set("Steps[2].Control", CycleEnd);
        Set("Steps[1].Times", 9L);
        bool early = run.Advance(_image, T0 + TimeSpan.FromMilliseconds(500));
        bool second = run.Advance(_image, T0 + TimeSpan.FromSeconds(1));
        bool done = run.Advance(_image, T0 + TimeSpan.FromSeconds(2));

        Assert.False(early);
        Assert.False(second);
        Assert.True(done);
    }

    [Fact]
    [Trait("AC", "15")]
    public void A_step_time_below_one_millisecond_counts_as_one_millisecond()
    {
        ProcessRules rules = Rules(Behavior());
        SetSteps(3, timeMs: 0);
        ProcessRun run = rules.Start(_image, T0, false);

        bool early = run.Advance(_image, T0 + TimeSpan.FromMilliseconds(2.5));
        bool done = run.Advance(_image, T0 + TimeSpan.FromMilliseconds(3));

        Assert.False(early);
        Assert.True(done);
    }

    [Fact]
    [Trait("AC", "12")]
    [Trait("AC", "14")]
    public void A_dry_run_stays_dry_for_every_step_and_after_Resume()
    {
        ProcessRules rules = Rules(Behavior(writes: [Write(2, "Steps[i].Speed", dryRunZero: true)]));
        SetSteps(2);
        Set("Steps[1].Speed", 5.0);
        Set("Steps[2].Speed", 6.0);
        ProcessRun run = rules.Start(_image, T0, true);
        string first = Svid(2);
        SetModule("ModuleVariableList[2].Value", "x");

        run.Advance(_image, T0 + TimeSpan.FromSeconds(1));
        string second = Svid(2);
        run.Leave(_image);
        run.Resume(_image, T0 + TimeSpan.FromSeconds(1));

        Assert.Equal(("0", "0"), (first, second));
        Assert.Equal("0", Svid(2));
    }

    [Fact]
    [Trait("AC", "12")]
    public void An_enum_field_is_read_as_its_integer_value_in_a_check_and_a_condition()
    {
        var down = new ProcessCondition("Steps[i].Dir", 2);
        ProcessRules rules = Rules(Behavior(
            checks: [Range("Steps[i].Dir", 1, 2)],
            writes: [Write(2, "Steps[i].Speed", negateWhen: down), Write(3, "Steps[i].Dir")]));
        SetSteps(1);
        Set("Steps[1].Speed", 8.0);
        Set("Steps[1].Dir", 2L);

        EntryCheck check = rules.Check(_image);
        rules.Start(_image, T0, false);

        Assert.True(check.Passed);
        Assert.Equal(("-8", "2"), (Svid(2), Svid(3)));
    }

    [Fact]
    [Trait("AC", "13")]
    public void A_Keep_value_stays_after_the_run_and_a_later_run_that_does_not_write_it_leaves_it()
    {
        var topped = new ProcessCondition("Steps[i].Pick", 1);
        ProcessRules rules = Rules(Behavior(writes: [Write(3, "Steps[i].Speed", isReal: true, keep: true, when: topped)]));
        SetSteps(1);
        Set("Steps[1].Speed", 80.0);
        Set("Steps[1].Pick", 1L);
        ProcessRun run = rules.Start(_image, T0, false);
        run.Advance(_image, T0 + TimeSpan.FromSeconds(1));
        string afterRun = Svid(3);

        Set("Steps[1].Pick", 2L);
        Set("Steps[1].Speed", 10.0);
        rules.Start(_image, T0, false);

        Assert.Equal("80.0", afterRun);
        Assert.Equal("80.0", Svid(3));
    }

    [Fact]
    [Trait("AC", "2")]
    public void A_path_that_does_not_resolve_is_reported_with_the_resolver_message()
    {
        ServiceHostException error = Assert.Throws<ServiceHostException>(
            () => Rules(Behavior(maxCount: 7)));

        Assert.Contains("cannot resolve", error.Message);
    }
}
