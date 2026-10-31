using AdsPlcSimulator.Core.PackML.ConfigParam;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.PackML;

public class ConfigParamServiceTests
{
    private const string Declarations = """
        TYPE ST_Item : STRUCT Id : UDINT; DataType : UDINT; Value : STRING(80); END_STRUCT END_TYPE
        TYPE ST_Meta : STRUCT
            Id : UDINT; DataType : UDINT; Value : STRING(80); IsBound : BOOL; LowerLimit : LREAL; UpperLimit : LREAL;
        END_STRUCT END_TYPE
        TYPE ST_Holder : STRUCT
            Batch : ARRAY[1..2] OF ST_Item;
            BatchTail : UDINT;
            Meta : ARRAY[1..2] OF ST_Meta;
            MetaTail : UDINT;
        END_STRUCT END_TYPE
        VAR_GLOBAL
            Holder : ST_Holder;
        END_VAR
        """;

    private const int Integer32 = 2;
    private const long DescriptorInteger32 = 1;

    private readonly ProcessImage _image;
    private readonly SymbolTable _symbols;
    private readonly ConfigParamService _service;

    public ConfigParamServiceTests()
    {
        PlcProgram program = TestPaths.BuildFromText(Declarations);
        _image = program.CreateProcessImage();
        _symbols = program.Symbols;
        _service = ConfigParamService.TryCreate(_symbols.Resolve("GVL_Test.Holder.Batch"), _symbols.Resolve("GVL_Test.Holder.Meta"))!;
        Metadata(1, 101, "5");
        Metadata(2, 102, "6");
    }

    private void Write(string path, object value) => _image.WriteValue(_symbols.Resolve($"GVL_Test.Holder.{path}"), value, WriteSource.Client);

    private string MetadataValue(int index) => (string)_image.ReadValue(_symbols.Resolve($"GVL_Test.Holder.Meta[{index}].Value"));

    private void Metadata(int index, long id, string value)
    {
        Write($"Meta[{index}].Id", id);
        Write($"Meta[{index}].DataType", DescriptorInteger32);
        Write($"Meta[{index}].Value", value);
        Write($"Meta[{index}].IsBound", true);
        Write($"Meta[{index}].LowerLimit", 0d);
        Write($"Meta[{index}].UpperLimit", 100d);
    }

    private void Item(int index, long id, string value)
    {
        Write($"Batch[{index}].Id", id);
        Write($"Batch[{index}].DataType", (long)Integer32);
        Write($"Batch[{index}].Value", value);
    }

    [Fact]
    [Trait("AC", "3")]
    public void A_batch_that_fills_every_element_does_not_read_the_memory_after_the_array()
    {
        Item(1, 101, "1");
        Item(2, 102, "2");
        Write("BatchTail", 101L);

        ConfigParamCheck check = _service.ValidateAndStage(_image);

        Assert.True(check.Passed);
    }

    [Fact]
    [Trait("AC", "3")]
    public void An_Id_in_the_memory_after_the_metadata_array_is_not_found()
    {
        Write("MetaTail", 103L);
        Item(1, 103, "1");

        ConfigParamCheck check = _service.ValidateAndStage(_image);

        Assert.Equal(ConfigParamError.MetadataNotFound, check.Error);
        Assert.Equal(103u, check.ParamId);
    }

    [Fact]
    [Trait("AC", "3")]
    public void A_failed_validation_discards_the_snapshot_of_the_earlier_passed_one()
    {
        Item(1, 101, "50");
        Assert.True(_service.ValidateAndStage(_image).Passed);
        Item(1, 999, "50");

        Assert.False(_service.ValidateAndStage(_image).Passed);
        _service.Apply(_image);

        Assert.Equal("5", MetadataValue(1));
    }

    [Fact]
    [Trait("AC", "5")]
    public void Apply_writes_the_staged_Values_once()
    {
        Item(1, 101, "+050");
        Item(2, 102, "7");
        Assert.True(_service.ValidateAndStage(_image).Passed);

        _service.Apply(_image);
        Write("Meta[1].Value", "changed");
        _service.Apply(_image);

        Assert.Equal("changed", MetadataValue(1));
        Assert.Equal("7", MetadataValue(2));
    }
}
