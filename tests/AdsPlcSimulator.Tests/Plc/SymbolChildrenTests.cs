using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.Plc;

public class SymbolChildrenTests
{
    private const string Declarations = """
        TYPE E_State : (Idle := 10, Busy, Done) DINT; END_TYPE
        TYPE ST_Inner : STRUCT x : INT; END_STRUCT END_TYPE
        TYPE ST_Recipe : STRUCT nSteps : INT; eState : E_State; inner : ST_Inner; END_STRUCT END_TYPE
        TYPE T_List : ARRAY[1..3] OF INT; END_TYPE
        VAR_GLOBAL
            nValue : INT;
            stRecipe : ST_Recipe;
            a : ARRAY[0..2] OF ST_Inner;
            b : ARRAY[1..50] OF INT;
            c : ARRAY[1..120] OF INT;
            d : ARRAY[0..50] OF INT;
            m : ARRAY[1..2,1..3] OF BOOL;
            w : ARRAY[1..2,1..60] OF INT;
            l : T_List;
            t : ARRAY[1..2,1..2,1..60] OF INT;
        END_VAR
        """;

    private static SymbolTable Table() => TestPaths.BuildFromText(Declarations).Symbols;

    private static IReadOnlyList<SymbolChild> Children(SymbolTable table, string? path, int? from = null, int? to = null)
    {
        Assert.True(table.TryGetChildren(path, from, to, out IReadOnlyList<SymbolChild> children, out string? error), error);
        Assert.Null(error);
        return children;
    }

    private static SymbolTable SampleTable() =>
        PlcProgram.LoadDirectory(Path.Combine(TestPaths.SampleProject, "plc")).Symbols;

    [Fact]
    [Trait("AC", "7")]
    public void Root_lists_each_scope_once()
    {
        SymbolTable table = SampleTable();

        Assert.Equal([new SymbolChild("GVL_Module", "GVL_Module", null, false)], Children(table, null));
        Assert.Equal([new SymbolChild("GVL_Module", "GVL_Module", null, false)], Children(table, ""));
        Assert.False(table.TryGetChildren(null, 1, 2, out IReadOnlyList<SymbolChild> children, out string? error));
        Assert.NotEmpty(error!);
        Assert.Empty(children);
    }

    [Fact]
    [Trait("AC", "7")]
    public void Scope_lists_its_roots_in_declaration_order()
    {
        SymbolTable table = SampleTable();
        string[] expected =
            ["GVL_Module.Chamber1", "GVL_Module.Chamber1_Runtime", "GVL_Module.Chamber1_Control", "GVL_Module.Chamber1_SpinAxis", "GVL_Module.Chamber1_LiftPinAxis"];

        IReadOnlyList<SymbolChild> roots = Children(table, "GVL_Module");

        Assert.Equal(expected, roots.Select(root => root.Path));
        Assert.Equal(expected.Select(path => path["GVL_Module.".Length..]), roots.Select(root => root.Name));
        Assert.Equal(roots, Children(table, "gvl_module"));
        Assert.False(table.TryGetChildren("Nope", null, null, out _, out string? error));
        Assert.NotEmpty(error!);
    }

    [Fact]
    [Trait("AC", "7")]
    public void Struct_lists_members_with_canonical_paths()
    {
        IReadOnlyList<SymbolChild> members = Children(Table(), "gvl_test.strecipe");

        Assert.Equal(
            [
                new SymbolChild("nSteps", "GVL_Test.stRecipe.nSteps", "INT", true),
                new SymbolChild("eState", "GVL_Test.stRecipe.eState", "E_State", true),
                new SymbolChild("inner", "GVL_Test.stRecipe.inner", "ST_Inner", false),
            ],
            members);
    }

    [Fact]
    [Trait("AC", "7")]
    public void Small_array_lists_elements()
    {
        SymbolTable table = Table();

        IReadOnlyList<SymbolChild> elements = Children(table, "GVL_Test.a");
        IReadOnlyList<SymbolChild> fifty = Children(table, "GVL_Test.b");

        Assert.Equal(
            [
                new SymbolChild("[0]", "GVL_Test.a[0]", "ST_Inner", false),
                new SymbolChild("[1]", "GVL_Test.a[1]", "ST_Inner", false),
                new SymbolChild("[2]", "GVL_Test.a[2]", "ST_Inner", false),
            ],
            elements);
        Assert.Equal(50, fifty.Count);
        Assert.All(fifty, element => Assert.True(element.IsScalar && element.From is null));
    }

    [Fact]
    [Trait("AC", "7")]
    public void Large_array_lists_ranges_then_elements()
    {
        SymbolTable table = Table();

        IReadOnlyList<SymbolChild> ranges = Children(table, "GVL_Test.c");
        IReadOnlyList<SymbolChild> page = Children(table, "GVL_Test.c", 51, 100);
        IReadOnlyList<SymbolChild> edge = Children(table, "GVL_Test.d");

        Assert.Equal(
            [
                new SymbolChild("[1..50]", "GVL_Test.c", null, false, 1, 50),
                new SymbolChild("[51..100]", "GVL_Test.c", null, false, 51, 100),
                new SymbolChild("[101..120]", "GVL_Test.c", null, false, 101, 120),
            ],
            ranges);
        Assert.Equal(50, page.Count);
        Assert.Equal(new SymbolChild("[51]", "GVL_Test.c[51]", "INT", true), page[0]);
        Assert.Equal(
            [
                new SymbolChild("[0..49]", "GVL_Test.d", null, false, 0, 49),
                new SymbolChild("[50..50]", "GVL_Test.d", null, false, 50, 50),
            ],
            edge);
    }

    [Fact]
    [Trait("AC", "7")]
    public void Multi_dimensional_array_expands_one_dimension_per_level()
    {
        SymbolTable table = Table();

        IReadOnlyList<SymbolChild> prefixes = Children(table, "GVL_Test.m");
        IReadOnlyList<SymbolChild> row = Children(table, "GVL_Test.m[2]");
        IReadOnlyList<SymbolChild> wideRanges = Children(table, "GVL_Test.w[1]");
        IReadOnlyList<SymbolChild> wideTail = Children(table, "GVL_Test.w[1]", 51, 60);

        Assert.Equal(
            [
                new SymbolChild("[1]", "GVL_Test.m[1]", null, false),
                new SymbolChild("[2]", "GVL_Test.m[2]", null, false),
            ],
            prefixes);
        Assert.Equal(
            [
                new SymbolChild("[1]", "GVL_Test.m[2,1]", "BOOL", true),
                new SymbolChild("[2]", "GVL_Test.m[2,2]", "BOOL", true),
                new SymbolChild("[3]", "GVL_Test.m[2,3]", "BOOL", true),
            ],
            row);
        Assert.Equal(
            [
                new SymbolChild("[1..50]", "GVL_Test.w[1]", null, false, 1, 50),
                new SymbolChild("[51..60]", "GVL_Test.w[1]", null, false, 51, 60),
            ],
            wideRanges);
        Assert.Equal(10, wideTail.Count);
        Assert.Equal("GVL_Test.w[1,51]", wideTail[0].Path);
    }

    [Fact]
    [Trait("AC", "7")]
    public void Fully_indexed_array_element_lists_its_members()
    {
        IReadOnlyList<SymbolChild> members = Children(Table(), "GVL_Test.a[1]");

        Assert.Equal([new SymbolChild("x", "GVL_Test.a[1].x", "INT", true)], members);
    }

    [Fact]
    [Trait("AC", "7")]
    public void Three_dimensional_array_joins_indices_with_commas()
    {
        SymbolTable table = Table();

        IReadOnlyList<SymbolChild> prefixes = Children(table, "GVL_Test.t[1]");
        IReadOnlyList<SymbolChild> ranges = Children(table, "GVL_Test.t[1,2]");

        Assert.Equal(["GVL_Test.t[1,1]", "GVL_Test.t[1,2]"], prefixes.Select(child => child.Path));
        Assert.Equal(
            [
                new SymbolChild("[1..50]", "GVL_Test.t[1,2]", null, false, 1, 50),
                new SymbolChild("[51..60]", "GVL_Test.t[1,2]", null, false, 51, 60),
            ],
            ranges);
    }

    [Fact]
    [Trait("AC", "7")]
    public void Page_bounds_include_first_element_and_single_element_pages()
    {
        SymbolTable table = Table();

        IReadOnlyList<SymbolChild> firstPage = Children(table, "GVL_Test.c", 1, 50);
        IReadOnlyList<SymbolChild> single = Children(table, "GVL_Test.c", 5, 5);

        Assert.Equal(50, firstPage.Count);
        Assert.Equal("GVL_Test.c[1]", firstPage[0].Path);
        Assert.Equal(["GVL_Test.c[5]"], single.Select(child => child.Path));
    }

    [Fact]
    [Trait("AC", "7")]
    public void Aliased_array_lists_elements()
    {
        SymbolTable table = Table();

        IReadOnlyList<SymbolChild> elements = Children(table, "GVL_Test.l");
        SymbolChild root = Children(table, "GVL_Test").Single(child => child.Name == "l");

        Assert.Equal(
            [
                new SymbolChild("[1]", "GVL_Test.l[1]", "INT", true),
                new SymbolChild("[2]", "GVL_Test.l[2]", "INT", true),
                new SymbolChild("[3]", "GVL_Test.l[3]", "INT", true),
            ],
            elements);
        Assert.False(root.IsScalar);
    }

    [Theory]
    [Trait("AC", "7")]
    [InlineData("GVL_Test.nValue", null, null, "has no children")]
    [InlineData("GVL_Test.stRecipe.nope", null, null, "nope")]
    [InlineData("GVL_Test.stRecipe", 1, 2, "")]
    [InlineData("GVL_Test", 1, 2, "only valid for an array")]
    [InlineData("GVL_Test.stRecipe", 1, null, "only valid for an array")]
    [InlineData("GVL_Test.c", 1, null, "together")]
    [InlineData("GVL_Test.c", 101, 121, "outside")]
    [InlineData("GVL_Test.c", 0, 10, "outside")]
    [InlineData("GVL_Test.c", 60, 51, "outside")]
    [InlineData("GVL_Test.c", 1, 51, "longer than 50")]
    [InlineData("GVL_Test.m[1].x", null, null, "")]
    [InlineData("GVL_Test.c[99999999999]", null, null, "")]
    [InlineData("GVL_Test.w[9]", null, null, "outside")]
    public void Invalid_requests_fail_with_message(string path, int? from, int? to, string messagePart)
    {
        bool ok = Table().TryGetChildren(path, from, to, out IReadOnlyList<SymbolChild> children, out string? error);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Contains(messagePart, error);
        Assert.Empty(children);
    }
}
