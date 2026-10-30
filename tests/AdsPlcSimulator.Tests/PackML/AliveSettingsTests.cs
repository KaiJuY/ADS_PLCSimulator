using AdsPlcSimulator.Core.PackML;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.PackML;

public class AliveSettingsTests
{
    private static AliveSettings Read(string constants) =>
        AliveSettings.FromSymbols(TestPaths.BuildFromText($"VAR_GLOBAL CONSTANT\n{constants}\nEND_VAR", "Param_Config").Symbols);

    private static TimeSpan Milliseconds(double value) => TimeSpan.FromMilliseconds(value);

    [Fact]
    [Trait("AC", "10")]
    public void All_three_values_are_read()
    {
        AliveSettings settings = Read("""
            ModuleHeartbeat_Timeout : TIME := T#3S;
            ServiceAliveTimeoutMin : TIME := T#200MS;
            ServiceAliveTimeoutMax : TIME := T#7S;
            """);

        Assert.Equal(new AliveSettings(Milliseconds(3000), Milliseconds(200), Milliseconds(7000)), settings);
    }

    [Fact]
    [Trait("AC", "10")]
    public void Project_without_the_constants_uses_the_defaults()
    {
        AliveSettings settings = AliveSettings.FromSymbols(TestPaths.BuildFromText("VAR_GLOBAL n : INT; END_VAR").Symbols);

        Assert.Equal(AliveSettings.Default, settings);
        Assert.Equal(Milliseconds(10000), settings.HeartbeatTimeout);
        Assert.Equal(Milliseconds(100), settings.ServiceAliveMin);
        Assert.Equal(Milliseconds(10000), settings.ServiceAliveMax);
    }

    [Fact]
    [Trait("AC", "10")]
    public void Missing_value_takes_the_default_of_that_field_only()
    {
        AliveSettings settings = Read("""
            ModuleHeartbeat_Timeout : TIME := T#3S;
            ServiceAliveTimeoutMax : TIME := T#7S;
            """);

        Assert.Equal(new AliveSettings(Milliseconds(3000), Milliseconds(100), Milliseconds(7000)), settings);
    }

    [Theory]
    [InlineData("INT := 5")]
    [InlineData("TIME := T#0S")]
    [InlineData("TIME := T#0S - T#1S")]
    [InlineData("TIME := T#5S * 2")]
    [Trait("AC", "10")]
    public void Value_that_is_not_a_positive_TIME_literal_takes_the_default(string declaration)
    {
        AliveSettings settings = Read($"ServiceAliveTimeoutMax : {declaration};");

        Assert.Equal(Milliseconds(10000), settings.ServiceAliveMax);
    }

    [Fact]
    [Trait("AC", "10")]
    public void Min_greater_than_Max_throws_and_the_message_has_both_values()
    {
        var exception = Assert.Throws<ServiceHostException>(() => Read("""
            ServiceAliveTimeoutMin : TIME := T#2S;
            ServiceAliveTimeoutMax : TIME := T#1S;
            """));

        Assert.Equal("Param_Config.ServiceAliveTimeoutMin (T#2000MS) is greater than ServiceAliveTimeoutMax (T#1000MS).", exception.Message);
    }

    [Fact]
    [Trait("AC", "10")]
    public void Max_missing_is_compared_as_the_default_and_the_message_marks_it()
    {
        var exception = Assert.Throws<ServiceHostException>(() => Read("ServiceAliveTimeoutMin : TIME := T#20S;"));

        Assert.Equal("Param_Config.ServiceAliveTimeoutMin (T#20000MS) is greater than ServiceAliveTimeoutMax (T#10000MS (default)).", exception.Message);
    }

    [Fact]
    [Trait("AC", "10")]
    public void Min_missing_is_compared_as_the_default_and_the_message_marks_it()
    {
        var exception = Assert.Throws<ServiceHostException>(() => Read("ServiceAliveTimeoutMax : TIME := T#50MS;"));

        Assert.Equal("Param_Config.ServiceAliveTimeoutMin (T#100MS (default)) is greater than ServiceAliveTimeoutMax (T#50MS).", exception.Message);
    }

    [Fact]
    [Trait("AC", "10")]
    public void Max_that_is_an_expression_takes_the_default_and_a_large_Min_then_fails()
    {
        Assert.Throws<ServiceHostException>(() => Read("""
            ServiceAliveTimeoutMin : TIME := T#20S;
            ServiceAliveTimeoutMax : TIME := T#5S * 2;
            """));
    }

    [Theory]
    [InlineData(50, 100)]
    [InlineData(20000, 10000)]
    [InlineData(500, 500)]
    [InlineData(100, 100)]
    [InlineData(10000, 10000)]
    [Trait("AC", "5")]
    public void Clamp_limits_the_timeout_to_the_range(double input, double expected)
    {
        Assert.Equal(Milliseconds(expected), AliveSettings.Default.Clamp(Milliseconds(input)));
    }
}
