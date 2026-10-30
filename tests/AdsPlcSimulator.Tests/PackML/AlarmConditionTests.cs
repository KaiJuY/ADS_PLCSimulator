using AdsPlcSimulator.Core.PackML.Alarm;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.PackML;

public class AlarmConditionTests
{
    [Theory]
    [InlineData(AlarmOperator.GreaterThan, 10.0, false)]
    [InlineData(AlarmOperator.GreaterThan, 10.5, true)]
    [InlineData(AlarmOperator.GreaterOrEqual, 10.0, true)]
    [InlineData(AlarmOperator.GreaterOrEqual, 9.5, false)]
    [InlineData(AlarmOperator.LessThan, 10.0, false)]
    [InlineData(AlarmOperator.LessThan, 9.5, true)]
    [InlineData(AlarmOperator.LessOrEqual, 10.0, true)]
    [InlineData(AlarmOperator.LessOrEqual, 10.5, false)]
    [Trait("AC", "13")]
    public void Ordering_operators_compare_against_the_threshold_and_ignore_tolerance(AlarmOperator op, double value, bool expected)
    {
        Assert.Equal(expected, AlarmCondition.IsMet(value, op, 10.0, 100.0));
    }

    [Theory]
    [InlineData(10.5, 0.5, true)]
    [InlineData(9.5, 0.5, true)]
    [InlineData(10.6, 0.5, false)]
    [InlineData(9.4, 0.5, false)]
    [InlineData(10.5, -0.5, true)]
    [InlineData(10.6, -0.5, false)]
    [InlineData(10.0, 0.0, true)]
    [InlineData(10.1, 0.0, false)]
    [Trait("AC", "13")]
    public void Equal_uses_the_absolute_tolerance(double value, double tolerance, bool expected)
    {
        Assert.Equal(expected, AlarmCondition.IsMet(value, AlarmOperator.Equal, 10.0, tolerance));
    }

    [Fact]
    [Trait("AC", "13")]
    public void An_undefined_operator_is_never_met()
    {
        Assert.False(AlarmCondition.IsMet(10.0, (AlarmOperator)99, 10.0, 0.0));
    }

    [Theory]
    [InlineData("gt", AlarmOperator.GreaterThan)]
    [InlineData("lt", AlarmOperator.LessThan)]
    [InlineData("eq", AlarmOperator.Equal)]
    [InlineData("ge", AlarmOperator.GreaterOrEqual)]
    [InlineData("le", AlarmOperator.LessOrEqual)]
    [Trait("AC", "13")]
    public void ParseOperator_reads_the_five_names(string text, AlarmOperator expected)
    {
        Assert.Equal(expected, AlarmCondition.ParseOperator(text));
    }

    [Theory]
    [InlineData("GT")]
    [InlineData("Gt")]
    [InlineData(">")]
    [InlineData("")]
    [InlineData(null)]
    [Trait("AC", "13")]
    public void ParseOperator_is_case_sensitive_and_rejects_other_text(string? text)
    {
        Assert.Null(AlarmCondition.ParseOperator(text));
    }

    [Theory]
    [InlineData("BOOL", AlarmSourceType.Boolean)]
    [InlineData("Bool", AlarmSourceType.Boolean)]
    [InlineData("Boolean", AlarmSourceType.Boolean)]
    [InlineData("DINT", AlarmSourceType.DInt)]
    [InlineData("DInt", AlarmSourceType.DInt)]
    [InlineData("Integer_32bit", AlarmSourceType.DInt)]
    [InlineData("UDINT", AlarmSourceType.UDInt)]
    [InlineData("UDInt", AlarmSourceType.UDInt)]
    [InlineData("Unsigned_32bit", AlarmSourceType.UDInt)]
    [InlineData("REAL", AlarmSourceType.Real)]
    [InlineData("Real", AlarmSourceType.Real)]
    [InlineData("Float_32bit", AlarmSourceType.Real)]
    [InlineData("LREAL", AlarmSourceType.LReal)]
    [InlineData("LReal", AlarmSourceType.LReal)]
    [InlineData("Float_64bit", AlarmSourceType.LReal)]
    [InlineData("SINT", AlarmSourceType.SInt)]
    [InlineData("USINT", AlarmSourceType.USInt)]
    [InlineData("INT", AlarmSourceType.Int)]
    [InlineData("UINT", AlarmSourceType.UInt)]
    [InlineData("STRING", AlarmSourceType.String80)]
    [InlineData("STRING(80)", AlarmSourceType.String80)]
    [InlineData("String80", AlarmSourceType.String80)]
    [InlineData("String_80byte", AlarmSourceType.String80)]
    [Trait("AC", "13")]
    public void ParseDataType_reads_every_framework_name(string text, AlarmSourceType expected)
    {
        Assert.Equal(expected, AlarmCondition.ParseDataType(text));
    }

    [Theory]
    [InlineData("LINT")]
    [InlineData("dint")]
    [InlineData("TIME")]
    [InlineData("")]
    [InlineData(null)]
    [Trait("AC", "13")]
    public void ParseDataType_rejects_other_text(string? text)
    {
        Assert.Null(AlarmCondition.ParseDataType(text));
    }

    [Theory]
    [InlineData("BOOL", AlarmSourceType.Boolean)]
    [InlineData("SINT", AlarmSourceType.SInt)]
    [InlineData("USINT", AlarmSourceType.USInt)]
    [InlineData("INT", AlarmSourceType.Int)]
    [InlineData("UINT", AlarmSourceType.UInt)]
    [InlineData("DINT", AlarmSourceType.DInt)]
    [InlineData("UDINT", AlarmSourceType.UDInt)]
    [InlineData("REAL", AlarmSourceType.Real)]
    [InlineData("LREAL", AlarmSourceType.LReal)]
    [Trait("AC", "13")]
    public void Matches_accepts_the_primitive_of_the_same_name(string plcName, AlarmSourceType sourceType)
    {
        Assert.True(AlarmCondition.Matches(sourceType, PrimitiveType.FromName(plcName)));
    }

    [Fact]
    [Trait("AC", "13")]
    public void Matches_rejects_a_symbol_of_another_type()
    {
        Assert.False(AlarmCondition.Matches(AlarmSourceType.Real, PrimitiveType.FromName("DINT")));
        Assert.False(AlarmCondition.Matches(AlarmSourceType.DInt, PrimitiveType.FromName("UDINT")));
        Assert.False(AlarmCondition.Matches(AlarmSourceType.Boolean, PrimitiveType.FromName("BYTE")));
        Assert.False(AlarmCondition.Matches(AlarmSourceType.String80, PrimitiveType.FromName("DINT")));
        Assert.False(AlarmCondition.Matches(AlarmSourceType.DInt, new StringType(80, false)));
    }

    [Fact]
    [Trait("AC", "13")]
    public void Matches_follows_an_alias_to_its_target()
    {
        var alias = new AliasType("T_Pressure", new AliasType("T_Inner", PrimitiveType.FromName("REAL")));

        Assert.True(AlarmCondition.Matches(AlarmSourceType.Real, alias));
        Assert.False(AlarmCondition.Matches(AlarmSourceType.LReal, alias));
    }

    [Theory]
    [InlineData(80, true)]
    [InlineData(20, true)]
    [InlineData(255, true)]
    [Trait("AC", "13")]
    public void Matches_accepts_any_STRING_length_for_String80(int length, bool expected)
    {
        Assert.Equal(expected, AlarmCondition.Matches(AlarmSourceType.String80, new StringType(length, false)));
    }

    [Fact]
    [Trait("AC", "13")]
    public void Matches_rejects_a_wide_string_for_String80()
    {
        Assert.False(AlarmCondition.Matches(AlarmSourceType.String80, new StringType(80, true)));
    }

    [Fact]
    [Trait("AC", "1")]
    public void AlarmClock_formats_seventeen_UTC_digits()
    {
        var utc = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 6, TimeSpan.Zero);

        Assert.Equal("20260102030405006", AlarmClock.Format(utc));
    }

    [Fact]
    [Trait("AC", "1")]
    public void AlarmClock_converts_a_local_offset_to_UTC()
    {
        var local = new DateTimeOffset(2026, 1, 2, 11, 4, 5, 6, TimeSpan.FromHours(8));

        Assert.Equal("20260102030405006", AlarmClock.Format(local));
    }

    [Fact]
    [Trait("AC", "12")]
    public void ConfiguredAlarmTable_returns_the_alarms_of_a_module_ignoring_case_and_empty_when_absent()
    {
        var alarm = new ConfiguredAlarm("A.B", AlarmSourceType.Real, AlarmOperator.GreaterThan, 1, 0, 101, 0, "m", AlarmSeverity.Alarm);
        var table = new ConfiguredAlarmTable(new Dictionary<string, IReadOnlyList<ConfiguredAlarm>>
        {
            ["GVL_Module.Chamber_Control[1]"] = [alarm],
        });

        Assert.Equal([alarm], table.For("gvl_module.chamber_control[1]"));
        Assert.Empty(table.For("GVL_Module.Chamber_Control[2]"));
        Assert.Empty(ConfiguredAlarmTable.Empty.For("GVL_Module.Chamber_Control[1]"));
    }
}
