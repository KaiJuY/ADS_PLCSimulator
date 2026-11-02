using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.Plc;

public class EnumerateScalarsTests
{
    private const string Declarations = """
        TYPE ST_Inner : STRUCT x : INT; y : BOOL; END_STRUCT END_TYPE
        TYPE ST_Outer : STRUCT n : INT; inner : ST_Inner; END_STRUCT END_TYPE
        TYPE T_List : ARRAY[1..2] OF INT; END_TYPE
        TYPE T_Pair : ST_Inner; END_TYPE
        VAR_GLOBAL
            s : ST_Inner;
            o : ST_Outer;
            a : ARRAY[0..2] OF INT;
            m : ARRAY[1..2,1..2] OF BOOL;
            l : T_List;
            p : T_Pair;
            arr : ARRAY[1..2] OF ST_Inner;
        END_VAR
        """;

    private static List<string> Paths(int? take = null)
    {
        SymbolTable table = TestPaths.BuildFromText(Declarations).Symbols;
        IEnumerable<SymbolRef> scalars = table.EnumerateScalars();
        return (take is null ? scalars : scalars.Take(take.Value)).Select(scalar => scalar.Path).ToList();
    }

    [Fact]
    [Trait("AC", "7")]
    public void Struct_nested_struct_and_arrays_are_listed_depth_first()
    {
        string[] expected =
        [
            "GVL_Test.s.x", "GVL_Test.s.y",
            "GVL_Test.o.n", "GVL_Test.o.inner.x", "GVL_Test.o.inner.y",
            "GVL_Test.a[0]", "GVL_Test.a[1]", "GVL_Test.a[2]",
            "GVL_Test.m[1,1]", "GVL_Test.m[1,2]", "GVL_Test.m[2,1]", "GVL_Test.m[2,2]",
            "GVL_Test.l[1]", "GVL_Test.l[2]",
            "GVL_Test.p.x", "GVL_Test.p.y",
            "GVL_Test.arr[1].x", "GVL_Test.arr[1].y", "GVL_Test.arr[2].x", "GVL_Test.arr[2].y",
        ];

        Assert.Equal(expected, Paths());
    }

    [Fact]
    [Trait("AC", "7")]
    public void Stopping_early_returns_only_the_requested_prefix()
    {
        Assert.Equal(["GVL_Test.s.x", "GVL_Test.s.y", "GVL_Test.o.n"], Paths(3));
        Assert.Equal(["GVL_Test.s.x", "GVL_Test.s.y", "GVL_Test.o.n", "GVL_Test.o.inner.x", "GVL_Test.o.inner.y", "GVL_Test.a[0]"], Paths(6));
    }
}
