using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.Plc;

public class EnumValueTests
{
    private const string Declarations = """
        TYPE E_Used : (First := 1, Second := 5) UINT; END_TYPE
        TYPE E_Unused : (Alpha := 7, Beta, Gamma) DINT; END_TYPE
        VAR_GLOBAL
            eState : E_Used;
        END_VAR
        """;

    private static SymbolTable Symbols => TestPaths.BuildFromText(Declarations).Symbols;

    [Fact]
    [Trait("AC", "2")]
    public void Declared_member_returns_its_value()
    {
        Assert.True(Symbols.TryGetEnumValue("E_Used.Second", out long value));
        Assert.Equal(5, value);
    }

    [Fact]
    [Trait("AC", "2")]
    public void Enum_not_used_by_any_variable_is_still_found()
    {
        SymbolTable symbols = Symbols;

        Assert.True(symbols.TryGetEnumValue("E_Unused.Alpha", out long alpha));
        Assert.True(symbols.TryGetEnumValue("E_Unused.Gamma", out long gamma));
        Assert.Equal(7, alpha);
        Assert.Equal(9, gamma);
    }

    [Fact]
    [Trait("AC", "2")]
    public void Lookup_ignores_case()
    {
        Assert.True(Symbols.TryGetEnumValue("e_used.FIRST", out long value));
        Assert.Equal(1, value);
    }

    [Theory]
    [InlineData("E_Used.Missing")]
    [InlineData("E_Missing.First")]
    [InlineData("First")]
    [InlineData("")]
    [Trait("AC", "2")]
    public void Unknown_name_is_not_found(string name)
    {
        Assert.False(Symbols.TryGetEnumValue(name, out long value));
        Assert.Equal(0, value);
    }

    [Fact]
    [Trait("AC", "2")]
    public void Program_without_enums_has_no_enum_values()
    {
        SymbolTable symbols = TestPaths.BuildFromText("VAR_GLOBAL nValue : INT; END_VAR").Symbols;

        Assert.False(symbols.TryGetEnumValue("E_Used.First", out _));
    }
}
