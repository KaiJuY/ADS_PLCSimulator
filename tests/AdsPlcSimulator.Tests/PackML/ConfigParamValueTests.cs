using AdsPlcSimulator.Core.PackML.ConfigParam;

namespace AdsPlcSimulator.Tests.PackML;

public class ConfigParamValueTests
{
    private const int Boolean = 1;
    private const int Integer32 = 2;
    private const int UnsignedInteger32 = 3;
    private const int Float32 = 4;
    private const int Float64 = 5;
    private const int String80 = 6;
    private const int Integer8 = 7;
    private const int UnsignedInteger8 = 8;
    private const int Integer16 = 9;
    private const int UnsignedInteger16 = 10;

    [Theory]
    [Trait("AC", "4")]
    [InlineData(".", Float64, false)]
    [InlineData("+.", Float64, false)]
    [InlineData("-.e5", Float64, false)]
    [InlineData("1.e3", Float64, true)]
    [InlineData("1", Integer32, true)]
    [InlineData("+1", Integer32, true)]
    [InlineData("-1", Integer32, true)]
    [InlineData("+1", UnsignedInteger32, false)]
    [InlineData("-1", UnsignedInteger8, false)]
    [InlineData("1", UnsignedInteger32, true)]
    [InlineData("", Integer32, false)]
    [InlineData("", Float32, false)]
    [InlineData("1.0", Integer32, false)]
    [InlineData(" 1", Integer32, false)]
    [InlineData("1a", Integer32, false)]
    [InlineData("-", Integer16, false)]
    [InlineData("-1", Integer16, true)]
    [InlineData("+1", Integer16, true)]
    [InlineData("-1", UnsignedInteger16, false)]
    [InlineData("+", Integer8, false)]
    [InlineData("1.", Float32, true)]
    [InlineData(".5", Float32, true)]
    [InlineData("1e5", Float32, true)]
    [InlineData("1E-5", Float32, true)]
    [InlineData("-1.5e+3", Float64, true)]
    [InlineData("+1.5", Float32, true)]
    [InlineData("e5", Float64, false)]
    [InlineData("1e", Float64, false)]
    [InlineData("1e+", Float64, false)]
    [InlineData("1.2.3", Float64, false)]
    [InlineData("1e5.0", Float64, false)]
    [InlineData("1e5e5", Float64, false)]
    [InlineData("1e+-5", Float64, false)]
    [InlineData("1+5", Float64, false)]
    [InlineData("1e5+", Float64, false)]
    [InlineData("+", Float64, false)]
    [InlineData("TRUE", Boolean, true)]
    [InlineData("FALSE", Boolean, true)]
    [InlineData("true", Boolean, false)]
    [InlineData("1", Boolean, false)]
    [InlineData("1", 0, false)]
    [InlineData("1", 11, false)]
    public void IsStrict_follows_F_IsStrictConfigParamValue(string value, int dataType, bool expected) =>
        Assert.Equal(expected, ConfigParamValue.IsStrict(value, dataType));

    [Fact]
    [Trait("AC", "4")]
    public void IsStrict_String80_accepts_up_to_80_characters_and_anything_in_them()
    {
        Assert.True(ConfigParamValue.IsStrict(new string('x', 80), String80));
        Assert.True(ConfigParamValue.IsStrict("", String80));
        Assert.True(ConfigParamValue.IsStrict("any text 1e", String80));
        Assert.False(ConfigParamValue.IsStrict(new string('x', 81), String80));
    }

    [Theory]
    [Trait("AC", "4")]
    [InlineData(Integer8, -128, 127)]
    [InlineData(UnsignedInteger8, 0, 255)]
    [InlineData(Integer16, -32768, 32767)]
    [InlineData(UnsignedInteger16, 0, 65535)]
    [InlineData(Integer32, -2147483648.0, 2147483647.0)]
    [InlineData(UnsignedInteger32, 0, 4294967295.0)]
    [InlineData(Float32, -3.402823466E38, 3.402823466E38)]
    public void IsInTypeRange_accepts_the_bounds_and_rejects_a_step_beyond(int dataType, double lower, double upper)
    {
        Assert.True(ConfigParamValue.IsInTypeRange(lower, dataType));
        Assert.True(ConfigParamValue.IsInTypeRange(upper, dataType));
        Assert.False(ConfigParamValue.IsInTypeRange(Math.BitDecrement(lower), dataType));
        Assert.False(ConfigParamValue.IsInTypeRange(Math.BitIncrement(upper), dataType));
    }

    [Fact]
    [Trait("AC", "4")]
    public void IsInTypeRange_does_not_limit_Float64()
    {
        Assert.True(ConfigParamValue.IsInTypeRange(double.MaxValue, Float64));
        Assert.True(ConfigParamValue.IsInTypeRange(double.MinValue, Float64));
    }

    [Theory]
    [Trait("AC", "5")]
    [InlineData("+007", Integer32, "7")]
    [InlineData("-0", Integer32, "0")]
    [InlineData("4294967295", UnsignedInteger32, "4294967295")]
    [InlineData("1.5", Float32, "1.5")]
    [InlineData("0.1", Float32, "0.1")]
    [InlineData("+2", Float32, "2")]
    [InlineData("1e2", Float64, "100")]
    [InlineData("0.1", Float64, "0.1")]
    [InlineData("TRUE", Boolean, "TRUE")]
    [InlineData("some text", String80, "some text")]
    public void Format_returns_the_text_the_PLC_publishes(string value, int dataType, string expected) =>
        Assert.Equal(expected, ConfigParamValue.Format(value, dataType));

    [Theory]
    [Trait("AC", "4")]
    [InlineData(1, Integer32)]
    [InlineData(2, Float32)]
    [InlineData(3, String80)]
    [InlineData(5, Boolean)]
    [InlineData(6, UnsignedInteger32)]
    [InlineData(7, Float64)]
    [InlineData(8, Integer8)]
    [InlineData(9, UnsignedInteger8)]
    [InlineData(10, Integer16)]
    [InlineData(11, UnsignedInteger16)]
    public void ToConfigDataType_reverses_F_ConfigDataTypeToDescriptor(long descriptorType, int expected) =>
        Assert.Equal(expected, ConfigParamValue.ToConfigDataType(descriptorType));

    [Theory]
    [Trait("AC", "4")]
    [InlineData(4)]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(-1)]
    public void ToConfigDataType_is_null_for_DateTime_and_unknown_values(long descriptorType) =>
        Assert.Null(ConfigParamValue.ToConfigDataType(descriptorType));
}
