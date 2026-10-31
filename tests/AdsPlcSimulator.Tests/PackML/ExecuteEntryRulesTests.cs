using AdsPlcSimulator.Core.PackML;
using AdsPlcSimulator.Core.PackML.Behavior;
using AdsPlcSimulator.Core.PackML.ConfigParam;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.PackML;

public class ExecuteEntryRulesTests
{
    private const string Module = "GVL_Test.Modules[1]";
    private const string ModeSource = "ModuleVariableList[1].Value";
    private const string Target = "ModuleVariableList[2].Value";

    private static readonly string[] Bound = ["A", "B"];

    private readonly PlcProgram _program = TestPaths.BuildFromText(HostFixture.BehaviorDeclarations);
    private readonly ProcessImage _image;

    public ExecuteEntryRulesTests()
    {
        _image = _program.CreateProcessImage();
    }

    private static ModeRule[] Modes() =>
    [
        new(0, "Disable", false, ModeRuleKind.AllowOnly, new HashSet<string> { "B" }),
        new(1, "Manual", false, ModeRuleKind.DenyListed, new HashSet<string> { "A" }),
        new(2, "DryRun", true, ModeRuleKind.DenyListed, new HashSet<string>()),
    ];

    private static ServiceBehavior Behavior(
        ParamRange[]? ranges = null, string? duration = null, params SvidWrite[] onEnter) =>
        new(ranges ?? [], duration, onEnter, [], []);

    private ModuleRules Rules(IReadOnlyDictionary<string, ServiceBehavior>? services = null) =>
        ModuleRules.Create(
            _program.Symbols, Module,
            new ModuleBehavior(ModeSource, Modes(), services ?? new Dictionary<string, ServiceBehavior>()), Bound)!;

    private void Write(string relative, object value) =>
        _image.WriteValue(_program.Symbols.Resolve($"{Module}.{relative}"), value, WriteSource.Client);

    private string Text(string relative) => (string)_image.ReadValue(_program.Symbols.Resolve($"{Module}.{relative}"));

    private ServiceRules RulesOf(string service, ServiceBehavior behavior) =>
        Rules(new Dictionary<string, ServiceBehavior> { [service] = behavior }).For(service)!;

    [Fact]
    [Trait("AC", "6")]
    public void AllowOnly_mode_refuses_a_service_that_is_not_listed_and_passes_a_listed_one()
    {
        ModuleRules rules = Rules();
        Write(ModeSource, "0");

        EntryCheck refused = rules.CheckMode("A", _image, out bool refusedDryRun);
        EntryCheck allowed = rules.CheckMode("B", _image, out _);

        Assert.Equal(new EntryCheck(2, 0, "Not allowed in Mode 0 (Disable)."), refused);
        Assert.False(refusedDryRun);
        Assert.True(allowed.Passed);
    }

    [Fact]
    [Trait("AC", "6")]
    public void DenyListed_mode_refuses_a_listed_service_and_passes_the_others()
    {
        ModuleRules rules = Rules();
        Write(ModeSource, "1");

        Assert.Equal(new EntryCheck(2, 1, "Not allowed in Mode 1 (Manual)."), rules.CheckMode("A", _image, out _));
        Assert.True(rules.CheckMode("B", _image, out _).Passed);
    }

    [Theory]
    [Trait("AC", "5")]
    [InlineData("7", "Not allowed in Mode '7'.")]
    [InlineData("abc", "Not allowed in Mode 'abc'.")]
    [InlineData("", "Not allowed in Mode ''.")]
    [InlineData("1.0", "Not allowed in Mode '1.0'.")]
    [InlineData(" 1", "Not allowed in Mode ' 1'.")]
    public void A_Mode_text_that_is_not_a_listed_integer_is_refused_with_source_4294967295(string text, string message)
    {
        ModuleRules rules = Rules();
        Write(ModeSource, text);

        Assert.Equal(new EntryCheck(2, 4294967295, message), rules.CheckMode("B", _image, out _));
    }

    [Theory]
    [Trait("AC", "5")]
    [InlineData("+1", "A", false)]
    [InlineData("-0", "A", false)]
    [InlineData("-0", "B", true)]
    public void A_signed_Mode_text_is_read_as_that_integer(string text, string service, bool passes)
    {
        ModuleRules rules = Rules();
        Write(ModeSource, text);

        Assert.Equal(passes, rules.CheckMode(service, _image, out _).Passed);
    }

    [Theory]
    [Trait("AC", "8")]
    [InlineData("2", true)]
    [InlineData("1", false)]
    public void DryRun_follows_the_matched_rule(string mode, bool expected)
    {
        ModuleRules rules = Rules();
        Write(ModeSource, mode);

        rules.CheckMode("B", _image, out bool dryRun);

        Assert.Equal(expected, dryRun);
    }

    [Theory]
    [Trait("AC", "9")]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public void A_range_check_fails_outside_min_and_max_and_reports_the_value(long index, bool passes)
    {
        ServiceRules rules = RulesOf("A", Behavior([new ParamRange("Index", 1, 4)]));
        Write("ServiceParam.A.Index", index);

        EntryCheck check = rules.CheckParams(_image);

        Assert.Equal(passes, check.Passed);
        if (!passes)
            Assert.Equal(new EntryCheck(1, (uint)index, "Index out of range (1~4)."), check);
    }

    [Fact]
    [Trait("AC", "9")]
    public void When_two_ranges_fail_the_first_one_is_reported()
    {
        ServiceRules rules = RulesOf("A", Behavior([new ParamRange("Index", 1, 4), new ParamRange("Value", 1, 3)]));
        Write("ServiceParam.A.Index", 9);
        Write("ServiceParam.A.Value", 9);

        Assert.Equal(new EntryCheck(1, 9, "Index out of range (1~4)."), rules.CheckParams(_image));
    }

    [Fact]
    [Trait("AC", "11")]
    public void A_zero_duration_is_refused_and_a_positive_one_is_read_in_milliseconds()
    {
        ServiceRules rules = RulesOf("A", Behavior(duration: "Time"));
        Write("ServiceParam.A.Time", 0);
        EntryCheck zero = rules.CheckParams(_image);
        Write("ServiceParam.A.Time", 2000);

        Assert.Equal(new EntryCheck(1, 0, "Time must be greater than 0."), zero);
        Assert.True(rules.CheckParams(_image).Passed);
        Assert.Equal(TimeSpan.FromSeconds(2), rules.ReadDuration(_image));
    }

    [Fact]
    [Trait("AC", "11")]
    public void A_service_without_a_duration_field_has_no_duration()
    {
        ServiceRules rules = RulesOf("A", Behavior());

        Assert.Null(rules.ReadDuration(_image));
        Assert.True(rules.CheckParams(_image).Passed);
    }

    [Fact]
    [Trait("AC", "11")]
    public void A_failing_range_is_reported_before_a_zero_duration()
    {
        ServiceRules rules = RulesOf("A", Behavior([new ParamRange("Index", 1, 4)], "Time"));
        Write("ServiceParam.A.Index", 5);
        Write("ServiceParam.A.Time", 0);

        Assert.Equal(new EntryCheck(1, 5, "Index out of range (1~4)."), rules.CheckParams(_image));
    }

    private string Written(SvidValue value, bool isReal = false, bool dryRunZero = false, bool dryRun = false)
    {
        ServiceRules rules = RulesOf("A", Behavior(null, null, new SvidWrite(Target, isReal, value, dryRunZero)));
        rules.WriteOnEnter(_image, dryRun);
        return Text(Target);
    }

    [Fact]
    [Trait("AC", "12")]
    public void A_constant_is_written_as_integer_text()
    {
        Assert.Equal("5", Written(new SvidValue(SvidValueKind.Constant, 5, null, false)));
    }

    [Theory]
    [Trait("AC", "13")]
    [InlineData("30", false, "30")]
    [InlineData("30", true, "-30")]
    [InlineData("abc", false, "0")]
    [InlineData("0", true, "0")]
    [InlineData("2.5", false, "3")]
    [InlineData("1e999", false, "0")]
    [InlineData("NaN", false, "0")]
    [InlineData("-2.5", false, "-3")]
    public void An_ECID_value_is_read_as_a_number_and_negated_on_request(string ecid, bool negate, string expected)
    {
        Write("ModuleConfigParamList[1].Value", ecid);

        Assert.Equal(expected, Written(new SvidValue(SvidValueKind.Ecid, 0, "ModuleConfigParamList[1].Value", negate)));
    }

    [Theory]
    [Trait("AC", "12")]
    [InlineData(2.0, "2.0")]
    [InlineData(2.5, "2.5")]
    public void A_REAL_SVID_gets_the_TwinCAT_text_of_the_number(double constant, string expected)
    {
        Assert.Equal(expected, Written(new SvidValue(SvidValueKind.Constant, constant, null, false), isReal: true));
    }

    [Fact]
    [Trait("AC", "12")]
    public void A_parameter_field_is_written_as_its_value()
    {
        Write("ServiceParam.A.Index", 3);
        Write("ServiceParam.A.Speed", 1.5);

        Assert.Equal("3", Written(new SvidValue(SvidValueKind.Param, 0, "Index", false)));
        Assert.Equal("1.5", Written(new SvidValue(SvidValueKind.Param, 0, "Speed", false), isReal: true));
    }

    [Theory]
    [Trait("AC", "8")]
    [InlineData(true, "0")]
    [InlineData(false, "9")]
    public void DryRunZero_writes_zero_only_when_the_entry_Mode_has_DryRun(bool dryRun, string expected)
    {
        Assert.Equal(expected, Written(new SvidValue(SvidValueKind.Constant, 9, null, false), dryRunZero: true, dryRun: dryRun));
    }

    [Fact]
    [Trait("AC", "12")]
    public void A_negated_constant_is_written_with_its_sign()
    {
        ServiceRules rules = RulesOf("A", Behavior(null, null, new SvidWrite(Target, false, new SvidValue(SvidValueKind.Constant, 4, null, true), false)));

        rules.WriteOnEnter(_image, false);

        Assert.Equal("-4", Text(Target));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Create_returns_the_no_op_rules_without_a_behavior()
    {
        Assert.Same(ModuleRules.None, ModuleRules.Create(_program.Symbols, Module, null, Bound));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Create_refuses_a_Mode_source_that_does_not_resolve()
    {
        var behavior = new ModuleBehavior("Nope.Value", Modes(), new Dictionary<string, ServiceBehavior>());

        var exception = Assert.Throws<ServiceHostException>(() => ModuleRules.Create(_program.Symbols, Module, behavior, Bound));

        Assert.Contains($"{Module}.Nope.Value", exception.Message);
    }

    [Fact]
    [Trait("AC", "3")]
    public void Create_skips_entries_of_services_that_are_not_bound()
    {
        var services = new Dictionary<string, ServiceBehavior>
        {
            ["Unbound"] = Behavior([new ParamRange("NoSuchField", 1, 2)]),
        };

        ModuleRules rules = Rules(services);

        Assert.Same(ServiceRules.None, rules.For("Unbound"));
    }

    [Fact]
    [Trait("AC", "2")]
    public void Create_refuses_a_bound_entry_whose_field_does_not_resolve()
    {
        var services = new Dictionary<string, ServiceBehavior> { ["A"] = Behavior([new ParamRange("NoSuchField", 1, 2)]) };
        var behavior = new ModuleBehavior(ModeSource, Modes(), services);

        Assert.Throws<ServiceHostException>(() => ModuleRules.Create(_program.Symbols, Module, behavior, Bound));
    }

    [Fact]
    [Trait("AC", "6")]
    public void Service_names_in_a_Mode_rule_match_without_regard_to_case()
    {
        ModuleRules rules = Rules();
        Write(ModeSource, "0");

        Assert.True(rules.CheckMode("b", _image, out _).Passed);
    }

    [Theory]
    [Trait("AC", "12")]
    [InlineData(2.0, "2.0")]
    [InlineData(0.1, "0.1")]
    public void FormatFloat32_gives_the_REAL_TO_STRING_text(double number, string expected)
    {
        Assert.Equal(expected, ConfigParamValue.FormatFloat32(number));
    }
}
