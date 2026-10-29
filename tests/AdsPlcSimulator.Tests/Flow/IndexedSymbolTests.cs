using AdsPlcSimulator.Core.Flow;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.Flow;

public class IndexedSymbolTests
{
    private const string Declarations = """
        TYPE ST_Item : STRUCT nValue : INT; sName : STRING(10); END_STRUCT END_TYPE
        VAR_GLOBAL
            aItems : ARRAY[2..5] OF ST_Item;
            aMatrix : ARRAY[1..2, 1..2] OF INT;
            nPlain : INT;
        END_VAR
        """;

    private static readonly SymbolTable Symbols = TestPaths.BuildFromText(Declarations).Symbols;

    [Fact]
    [Trait("AC", "3")]
    public void Path_without_placeholder_is_a_plain_symbol_and_ignores_the_index()
    {
        IndexedSymbol symbol = IndexedSymbol.Parse("GVL_Test.nPlain", Symbols, scalar: true);

        Assert.False(symbol.IsIndexed);
        Assert.Equal("GVL_Test.nPlain", symbol.Template);
        Assert.Equal("INT", symbol.Type.Name);
        Assert.Equal("GVL_Test.nPlain", symbol.Resolve(Symbols, null).Path);
        Assert.Equal("GVL_Test.nPlain", symbol.Resolve(Symbols, 99).Path);
    }

    [Fact]
    [Trait("AC", "3")]
    public void Path_without_placeholder_resolves_all_to_one_entry_without_index()
    {
        IndexedSymbol symbol = IndexedSymbol.Parse("GVL_Test.nPlain", Symbols, scalar: true);

        (long? Index, SymbolRef Symbol) only = Assert.Single(symbol.ResolveAll(Symbols));

        Assert.Null(only.Index);
        Assert.Equal("GVL_Test.nPlain", only.Symbol.Path);
    }

    [Fact]
    [Trait("AC", "3")]
    public void One_placeholder_takes_bounds_and_type_from_the_array()
    {
        IndexedSymbol symbol = IndexedSymbol.Parse("GVL_Test.aItems[#].nValue", Symbols, scalar: true);

        Assert.True(symbol.IsIndexed);
        Assert.Equal(2, symbol.LowerBound);
        Assert.Equal(5, symbol.UpperBound);
        Assert.Equal("INT", symbol.Type.Name);
    }

    [Theory]
    [InlineData(2L, "GVL_Test.aItems[2].nValue")]
    [InlineData(5L, "GVL_Test.aItems[5].nValue")]
    [InlineData(3, "GVL_Test.aItems[3].nValue")]
    [InlineData(4.0, "GVL_Test.aItems[4].nValue")]
    [InlineData("3", "GVL_Test.aItems[3].nValue")]
    [Trait("AC", "3")]
    public void Index_inside_the_bounds_gives_the_element(object index, string expectedPath)
    {
        IndexedSymbol symbol = IndexedSymbol.Parse("gvl_test.aitems[#].nvalue", Symbols, scalar: true);

        Assert.Equal(expectedPath, symbol.Resolve(Symbols, index).Path);
    }

    [Fact]
    [Trait("AC", "3")]
    public void Missing_index_fails_and_names_the_input()
    {
        IndexedSymbol symbol = IndexedSymbol.Parse("GVL_Test.aItems[#].nValue", Symbols, scalar: true);

        var exception = Assert.Throws<InvalidOperationException>(() => symbol.Resolve(Symbols, null));

        Assert.Equal("GVL_Test.aItems[#].nValue needs input 'index'.", exception.Message);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(6L)]
    [InlineData(-1L)]
    [Trait("AC", "3")]
    public void Index_outside_the_bounds_fails_with_the_range(long index)
    {
        IndexedSymbol symbol = IndexedSymbol.Parse("GVL_Test.aItems[#].nValue", Symbols, scalar: true);

        var exception = Assert.Throws<InvalidOperationException>(() => symbol.Resolve(Symbols, index));

        Assert.Equal($"Index {index} is outside 2..5 of GVL_Test.aItems[#].nValue.", exception.Message);
    }

    [Fact]
    [Trait("AC", "3")]
    public void Resolve_all_gives_every_element_in_order()
    {
        IndexedSymbol symbol = IndexedSymbol.Parse("GVL_Test.aItems[#].nValue", Symbols, scalar: true);

        IReadOnlyList<(long? Index, SymbolRef Symbol)> all = symbol.ResolveAll(Symbols);

        Assert.Equal([2L, 3L, 4L, 5L], all.Select(entry => entry.Index!.Value));
        Assert.Equal("GVL_Test.aItems[5].nValue", all[^1].Symbol.Path);
    }

    [Fact]
    [Trait("AC", "3")]
    public void Two_placeholders_are_rejected()
    {
        var exception = Assert.Throws<NodeConfigurationException>(() => IndexedSymbol.Parse("GVL_Test.aItems[#].aMatrix[#]", Symbols, scalar: true));

        Assert.Equal("Only one [#] is allowed.", exception.Message);
    }

    [Theory]
    [InlineData("GVL_Test.nPlain[#]")]
    [InlineData("GVL_Test.aMatrix[#]")]
    [InlineData("GVL_Test.aItems[2].nValue[#]")]
    [Trait("AC", "3")]
    public void Prefix_that_is_not_a_one_dimensional_array_is_rejected(string path)
    {
        var exception = Assert.Throws<NodeConfigurationException>(() => IndexedSymbol.Parse(path, Symbols, scalar: false));

        Assert.Contains("one-dimensional array", exception.Message);
    }

    [Theory]
    [InlineData("GVL_Test.aItems[#].nMissing")]
    [InlineData("GVL_Test.aMissing[#]")]
    [InlineData("GVL_Test.nMissing")]
    [InlineData("")]
    [Trait("AC", "3")]
    public void Path_that_does_not_resolve_is_rejected(string path)
    {
        Assert.Throws<NodeConfigurationException>(() => IndexedSymbol.Parse(path, Symbols, scalar: false));
    }

    [Fact]
    [Trait("AC", "3")]
    public void Struct_element_is_rejected_when_a_scalar_is_required_and_accepted_otherwise()
    {
        Assert.Throws<NodeConfigurationException>(() => IndexedSymbol.Parse("GVL_Test.aItems[#]", Symbols, scalar: true));

        IndexedSymbol symbol = IndexedSymbol.Parse("GVL_Test.aItems[#]", Symbols, scalar: false);

        Assert.Equal("ST_Item", symbol.Type.Name);
    }

    [Fact]
    [Trait("AC", "3")]
    public void Placeholder_at_the_end_of_an_array_of_scalars_works()
    {
        SymbolTable symbols = TestPaths.BuildFromText("VAR_GLOBAL aValues : ARRAY[0..2] OF INT; END_VAR").Symbols;

        IndexedSymbol symbol = IndexedSymbol.Parse("GVL_Test.aValues[#]", symbols, scalar: true);

        Assert.Equal(0, symbol.LowerBound);
        Assert.Equal("GVL_Test.aValues[2]", symbol.Resolve(symbols, 2).Path);
    }
}
