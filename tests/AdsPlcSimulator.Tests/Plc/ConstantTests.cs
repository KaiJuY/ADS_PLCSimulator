using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.Plc;

public class ConstantTests
{
    private const string Declarations = """
        VAR_GLOBAL CONSTANT
            Timeout : TIME := T#10S;
            Short : TIME := T#100MS;
            Count : INT := 30;
            Total : INT := Count + 70 * 2;
            Zero : INT := 0;
            Broken : INT := 1 / Zero;
            Dependent : INT := Broken + 1;
            Unknown : INT := NoSuchName + 1;
            Fine : INT := 5;
        END_VAR
        VAR_GLOBAL
            nValue : INT;
        END_VAR
        """;

    private static SymbolTable Symbols => TestPaths.BuildFromText(Declarations).Symbols;

    [Fact]
    [Trait("AC", "10")]
    public void Time_literal_is_a_TimeSpan()
    {
        SymbolTable symbols = Symbols;

        Assert.True(symbols.TryGetConstant("GVL_Test.Timeout", out object seconds));
        Assert.True(symbols.TryGetConstant("GVL_Test.Short", out object milliseconds));
        Assert.Equal(TimeSpan.FromSeconds(10), seconds);
        Assert.Equal(TimeSpan.FromMilliseconds(100), milliseconds);
    }

    [Fact]
    [Trait("AC", "10")]
    public void Integer_constant_and_expression_are_evaluated()
    {
        SymbolTable symbols = Symbols;

        Assert.True(symbols.TryGetConstant("GVL_Test.Count", out object count));
        Assert.True(symbols.TryGetConstant("GVL_Test.Total", out object total));
        Assert.Equal(30L, count);
        Assert.Equal(170L, total);
    }

    [Fact]
    [Trait("AC", "10")]
    public void Lookup_ignores_case()
    {
        Assert.True(Symbols.TryGetConstant("gvl_test.TIMEOUT", out object value));
        Assert.Equal(TimeSpan.FromSeconds(10), value);
    }

    [Theory]
    [InlineData("GVL_Test.Missing")]
    [InlineData("Timeout")]
    [InlineData("GVL_Test.nValue")]
    [InlineData("")]
    [Trait("AC", "10")]
    public void Unknown_name_and_non_constant_are_not_found(string name)
    {
        Assert.False(Symbols.TryGetConstant(name, out _));
    }

    [Theory]
    [InlineData("GVL_Test.Broken")]
    [InlineData("GVL_Test.Dependent")]
    [InlineData("GVL_Test.Unknown")]
    [Trait("AC", "10")]
    public void Constant_that_cannot_be_evaluated_is_left_out_and_the_build_succeeds(string name)
    {
        SymbolTable symbols = Symbols;

        Assert.False(symbols.TryGetConstant(name, out _));
        Assert.True(symbols.TryGetConstant("GVL_Test.Fine", out object fine));
        Assert.Equal(5L, fine);
        Assert.True(symbols.TryGetConstant("GVL_Test.Timeout", out _));
    }

    [Fact]
    [Trait("AC", "10")]
    public void Constant_that_depends_on_itself_is_left_out_and_does_not_block_others()
    {
        SymbolTable symbols = TestPaths.BuildFromText("""
            VAR_GLOBAL CONSTANT
                A : INT := B + 1;
                B : INT := A + 1;
                C : INT := 3;
            END_VAR
            """).Symbols;

        Assert.False(symbols.TryGetConstant("GVL_Test.A", out _));
        Assert.False(symbols.TryGetConstant("GVL_Test.B", out _));
        Assert.True(symbols.TryGetConstant("GVL_Test.C", out object value));
        Assert.Equal(3L, value);
    }

    [Fact]
    [Trait("AC", "10")]
    public void Polar2_project_declares_the_three_alive_timeouts()
    {
        SymbolTable symbols = PlcProgram.LoadDirectory(Path.Combine(TestPaths.Polar2Project, "plc")).Symbols;

        Assert.True(symbols.TryGetConstant("Param_Config.ModuleHeartbeat_Timeout", out object heartbeat));
        Assert.True(symbols.TryGetConstant("Param_Config.ServiceAliveTimeoutMin", out object min));
        Assert.True(symbols.TryGetConstant("Param_Config.ServiceAliveTimeoutMax", out object max));
        Assert.True(symbols.TryGetConstant("Param_Config.MaxPublishedModuleAlarms", out object alarms));
        Assert.Equal(TimeSpan.FromSeconds(10), heartbeat);
        Assert.Equal(TimeSpan.FromMilliseconds(100), min);
        Assert.Equal(TimeSpan.FromSeconds(10), max);
        Assert.Equal(100L, alarms);
    }
}
