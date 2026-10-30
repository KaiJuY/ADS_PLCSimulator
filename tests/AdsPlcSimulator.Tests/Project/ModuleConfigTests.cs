using System.Text.RegularExpressions;
using AdsPlcSimulator.Core.Plc;
using AdsPlcSimulator.Host.Project;

namespace AdsPlcSimulator.Tests.Project;

public sealed partial class ModuleConfigTests : IDisposable
{
    private const string Path = "module-config.json";

    private const string SmallProgram = """
        TYPE E_DescriptorType : (Integer_32bit := 1, Float_32bit, String_80byte, DateTime, Boolean); END_TYPE
        TYPE ST_ExtDescriptor : STRUCT Id : UDINT; Name : STRING(30); DataType : E_DescriptorType; Unit : STRING(10); Value : STRING(80); END_STRUCT END_TYPE
        TYPE ST_ConfigParamDescriptor EXTENDS ST_ExtDescriptor : STRUCT IsBound : BOOL; HasLimits : BOOL; LowerLimit : LREAL; UpperLimit : LREAL; END_STRUCT END_TYPE
        TYPE ST_VariableStatus : STRUCT PublishedCount : UINT; END_STRUCT END_TYPE
        TYPE ST_ModuleStatus : STRUCT ModuleId : UDINT; Enabled : BOOL; ModuleType : STRING(16); Variable : ST_VariableStatus; END_STRUCT END_TYPE
        TYPE ST_Module : STRUCT
            ModuleStatus : ST_ModuleStatus;
            ModuleVariableList : ARRAY[1..3] OF ST_ExtDescriptor;
            ModuleConfigParamList : ARRAY[1..2] OF ST_ConfigParamDescriptor;
        END_STRUCT END_TYPE
        VAR_GLOBAL
            Chamber_Control : ARRAY[1..2] OF ST_Module;
        END_VAR
        """;

    private static readonly Lazy<PlcProgram> s_polar2 = new(() => PlcProgram.LoadDirectory(System.IO.Path.Combine(TestPaths.Polar2Project, "plc")));

    private readonly PlcProgram _small = TestPaths.BuildFromText(SmallProgram, globalScope: "GVL_Module");
    private readonly string _directory = Directory.CreateTempSubdirectory("adsplc-config-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    public static TheoryData<string, string> ParseErrors => new()
    {
        { "{", "invalid JSON" },
        { "null", "empty" },
        { """{"schemaVersion":3,"modules":[]}""", "schemaVersion" },
        { """{"schemaVersion":4}""", "modules" },
        { """{"schemaVersion":4,"modules":null}""", "modules" },
        { """{"schemaVersion":4,"modules":{}}""", "invalid JSON" },
        { """{"schemaVersion":4,"modules":[null]}""", "null element" },
        { Module("""{"slot":1,"variableList":null}"""), "variableList is null" },
        { Module("""{"slot":1,"configParamList":null}"""), "configParamList is null" },
        { Module("""{"slot":1,"variableList":[null]}"""), "variableList contains a null element" },
        { Module("""{"slot":1,"variableList":[{"id":1,"unit":"","dataType":"DINT"}]}"""), "slot 1 variableList: id 1 needs name" },
        { Module("""{"slot":1,"variableList":[{"id":1,"name":" ","unit":"","dataType":"DINT"}]}"""), "has an empty name" },
        { Module("""{"slot":1,"variableList":[{"id":1,"name":"a\u0080","unit":"","dataType":"DINT"}]}"""), "non-ASCII" },
        { Module("""{"slot":1,"variableList":[{"id":1,"name":"a","unit":"","dataType":null}]}"""), "needs name" },
        { Module("""{"slot":1},{"slot":1}"""), "slot 1 appears more than once" },
        { Module("""{"slot":1,"variableList":[{"id":1,"name":"a","unit":"","dataType":"DINT"},{"id":1,"name":"b","unit":"","dataType":"DINT"}]}"""), "id 1 appears more than once" },
        { Module("""{"slot":1,"configParamList":[{"id":0,"name":"a","unit":"","dataType":"DINT"}]}"""), "must not be 0" },
        { Module("""{"slot":1,"variableList":[{"id":1,"name":"a","unit":"","dataType":"LREAL"}]}"""), "dataType 'LREAL'" },
        { Module("""{"slot":1,"variableList":[{"id":1,"name":"Tempé","unit":"","dataType":"DINT"}]}"""), "non-ASCII" },
        { Module("""{"slot":1,"variableList":[{"id":1,"name":"a","unit":"°C","dataType":"DINT"}]}"""), "non-ASCII" },
        { Module("""{"slot":1,"moduleType":"Polaré"}"""), "moduleType contains non-ASCII" },
    };

    public static TheoryData<string, string> PublishErrors => new()
    {
        { Module("""{"enabled":true,"slot":3}"""), "slot 3 is outside" },
        { Module($$"""{"enabled":true,"slot":2,"variableList":[{{Descriptors(4)}}]}"""), "holds 3 entries" },
        { Module($$"""{"enabled":true,"slot":2,"configParamList":[{{Descriptors(3)}}]}"""), "holds 2 entries" },
        { Module($$"""{"enabled":true,"slot":2,"variableList":[{"id":1,"name":"{{new string('a', 31)}}","unit":"","dataType":"DINT"}]}"""), "name 'aaaa" },
        { Module($$"""{"enabled":true,"slot":2,"configParamList":[{"id":1,"name":"a","unit":"{{new string('u', 11)}}","dataType":"DINT"}]}"""), "unit 'uuuu" },
        { Module($$"""{"enabled":true,"slot":2,"moduleType":"{{new string('t', 17)}}"}"""), "moduleType 'tttt" },
    };

    [Fact]
    [Trait("AC", "8")]
    public void PublishIfPresent_writes_identity_and_lists_of_an_enabled_module()
    {
        WriteConfig(Module("""
            {"enabled":true,"moduleType":"Polar2","slot":2,"id":7,
             "variableList":[{"id":10,"name":"Speed","unit":"rpm","dataType":"DINT"},{"id":11,"name":"Label","unit":"","dataType":"STRING"}],
             "configParamList":[{"id":20,"name":"Disable","unit":"","dataType":"BOOL"}]}
            """));
        ProcessImage image = _small.CreateProcessImage();

        string? published = ModuleConfigPublisher.PublishIfPresent(_directory, _small.Symbols, image);

        Assert.Equal(System.IO.Path.Combine(_directory, "config", "module-config.json"), published);
        const string module = "GVL_Module.Chamber_Control[2]";
        Assert.Equal(7L, Read(image, _small, $"{module}.ModuleStatus.ModuleId"));
        Assert.Equal(true, Read(image, _small, $"{module}.ModuleStatus.Enabled"));
        Assert.Equal("Polar2", Read(image, _small, $"{module}.ModuleStatus.ModuleType"));
        Assert.Equal(2L, Read(image, _small, $"{module}.ModuleStatus.Variable.PublishedCount"));
        AssertDescriptor(image, _small, $"{module}.ModuleVariableList[1]", 10, "Speed", "Integer_32bit", "rpm", "0");
        AssertDescriptor(image, _small, $"{module}.ModuleVariableList[2]", 11, "Label", "String_80byte", "", "");
        Assert.Equal(0L, Read(image, _small, $"{module}.ModuleVariableList[3].Id"));
        AssertDescriptor(image, _small, $"{module}.ModuleConfigParamList[1]", 20, "Disable", "Boolean", "", "FALSE");
        Assert.Equal(true, Read(image, _small, $"{module}.ModuleConfigParamList[1].IsBound"));
        Assert.Equal(false, Read(image, _small, $"{module}.ModuleConfigParamList[1].HasLimits"));
        Assert.Equal(0L, Read(image, _small, $"{module}.ModuleConfigParamList[2].Id"));
        Assert.Equal(0L, Read(image, _small, "GVL_Module.Chamber_Control[1].ModuleStatus.ModuleId"));
        Assert.Equal(false, Read(image, _small, "GVL_Module.Chamber_Control[1].ModuleStatus.Enabled"));
    }

    [Theory]
    [Trait("AC", "8")]
    [InlineData("DINT", "Integer_32bit", "0")]
    [InlineData("REAL", "Float_32bit", "0")]
    [InlineData("BOOL", "Boolean", "FALSE")]
    [InlineData("STRING", "String_80byte", "")]
    public void Publish_maps_each_data_type_to_descriptor_and_initial_value(string dataType, string descriptor, string value)
    {
        ProcessImage image = _small.CreateProcessImage();
        ModuleConfigFile config = ModuleConfigPublisher.Parse(
            Module($$"""{"enabled":true,"slot":1,"id":1,"variableList":[{"id":1,"name":"a","unit":"","dataType":"{{dataType}}"}]}"""), Path);

        ModuleConfigPublisher.Publish(config, Path, _small.Symbols, image);

        AssertDescriptor(image, _small, "GVL_Module.Chamber_Control[1].ModuleVariableList[1]", 1, "a", descriptor, "", value);
    }

    [Fact]
    [Trait("AC", "8")]
    public void Publish_writes_nothing_for_a_disabled_module()
    {
        ProcessImage image = _small.CreateProcessImage();
        byte[] before = image.Read(0, image.Size);
        ModuleConfigFile config = ModuleConfigPublisher.Parse(
            Module("""{"enabled":false,"moduleType":"Polar2","slot":2,"id":7,"variableList":[{"id":1,"name":"a","unit":"","dataType":"DINT"}]}"""), Path);

        ModuleConfigPublisher.Publish(config, Path, _small.Symbols, image);

        Assert.Equal(before, image.Read(0, image.Size));
    }

    [Fact]
    [Trait("AC", "9")]
    public void PublishIfPresent_returns_null_without_a_config_file()
    {
        ProcessImage image = _small.CreateProcessImage();
        byte[] before = image.Read(0, image.Size);

        string? published = ModuleConfigPublisher.PublishIfPresent(_directory, _small.Symbols, image);

        Assert.Null(published);
        Assert.Equal(before, image.Read(0, image.Size));
    }

    [Theory]
    [Trait("AC", "10")]
    [MemberData(nameof(ParseErrors))]
    public void Parse_rejects_a_broken_file_with_path_and_reason(string json, string reason)
    {
        var exception = Assert.Throws<ModuleConfigException>(() => ModuleConfigPublisher.Parse(json, Path));

        Assert.StartsWith(Path + ": ", exception.Message);
        Assert.Contains(reason, exception.Message);
    }

    [Fact]
    [Trait("AC", "10")]
    public void Parse_gives_empty_lists_for_missing_fields()
    {
        ModuleConfigFile config = ModuleConfigPublisher.Parse(Module("""{"slot":1}"""), Path);

        ModuleConfigEntry module = Assert.Single(config.Modules!)!;
        Assert.Empty(module.VariableList!);
        Assert.Empty(module.ConfigParamList!);
        Assert.False(module.Enabled);
    }

    [Theory]
    [Trait("AC", "10")]
    [MemberData(nameof(PublishErrors))]
    public void Publish_rejects_a_config_that_does_not_fit_and_writes_nothing(string json, string reason)
    {
        ProcessImage image = _small.CreateProcessImage();
        byte[] before = image.Read(0, image.Size);
        ModuleConfigFile config = ModuleConfigPublisher.Parse(json, Path);

        var exception = Assert.Throws<ModuleConfigException>(() => ModuleConfigPublisher.Publish(config, Path, _small.Symbols, image));

        Assert.StartsWith(Path + ": ", exception.Message);
        Assert.Contains(reason, exception.Message);
        Assert.Equal(before, image.Read(0, image.Size));
    }

    [Fact]
    [Trait("AC", "10")]
    public void Publish_rejects_a_program_without_the_module_array()
    {
        PlcProgram program = TestPaths.BuildFromText("VAR_GLOBAL n : INT; END_VAR");
        ModuleConfigFile config = ModuleConfigPublisher.Parse(Module("""{"enabled":true,"slot":1}"""), Path);

        var exception = Assert.Throws<ModuleConfigException>(() => ModuleConfigPublisher.Publish(config, Path, program.Symbols, program.CreateProcessImage()));

        Assert.Contains(ModuleConfigPublisher.ModulesRoot, exception.Message);
    }

    [Fact]
    [Trait("AC", "1")]
    [Trait("AC", "2")]
    [Trait("AC", "3")]
    [Trait("AC", "4")]
    [Trait("AC", "5")]
    [Trait("AC", "6")]
    [Trait("AC", "8")]
    public void Polar2_config_publishes_slot_1_identity_svids_and_ecids()
    {
        PlcProgram program = s_polar2.Value;
        ProcessImage image = program.CreateProcessImage();

        ModuleConfigPublisher.PublishIfPresent(TestPaths.Polar2Project, program.Symbols, image);

        const string module = "GVL_Module.Chamber_Control[1]";
        Assert.Equal(1L, Read(image, program, $"{module}.ModuleStatus.ModuleId"));
        Assert.Equal(true, Read(image, program, $"{module}.ModuleStatus.Enabled"));
        Assert.Equal("Polar2", Read(image, program, $"{module}.ModuleStatus.ModuleType"));
        Assert.Equal(64L, Read(image, program, $"{module}.ModuleStatus.Variable.PublishedCount"));
        AssertDescriptor(image, program, $"{module}.ModuleVariableList[1]", 1, "Module Mode", "Integer_32bit", "", "0");
        Assert.Equal("Arm1 Lift Position", Read(image, program, $"{module}.ModuleVariableList[2].Name"));
        Assert.Equal("Float_32bit", EnumName(image, program, $"{module}.ModuleVariableList[2].DataType"));
        Assert.Equal("mm", Read(image, program, $"{module}.ModuleVariableList[2].Unit"));
        Assert.Equal(0L, Read(image, program, $"{module}.ModuleVariableList[65].Id"));
        Assert.Equal("Module Disable", Read(image, program, $"{module}.ModuleConfigParamList[1].Name"));
        Assert.Equal("Boolean", EnumName(image, program, $"{module}.ModuleConfigParamList[1].DataType"));
        Assert.NotEqual(0L, Read(image, program, $"{module}.ModuleConfigParamList[438].Id"));
        Assert.Equal(0L, Read(image, program, $"{module}.ModuleConfigParamList[439].Id"));
        for (int slot = 2; slot <= 6; slot++)
            Assert.Equal(0L, Read(image, program, $"GVL_Module.Chamber_Control[{slot}].ModuleStatus.Variable.PublishedCount"));

        string[] names = Enumerable.Range(1, 438)
            .Select(index => (string)Read(image, program, $"{module}.ModuleConfigParamList[{index}].Name"))
            .ToArray();
        Assert.All(names, name => Assert.True(name.Length is > 0 and <= 30, name));
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.DoesNotContain(names, name => name.StartsWith("Arm4 ", StringComparison.Ordinal) && NozzleTwoOrThree().IsMatch(name));
    }

    [GeneratedRegex("No(z|zzle)[23]")]
    private static partial Regex NozzleTwoOrThree();

    private static string Module(string modules) => $$"""{"schemaVersion":4,"modules":[{{modules}}]}""";

    private static string Descriptors(int count) =>
        string.Join(",", Enumerable.Range(1, count).Select(index => $$"""{"id":{{index}},"name":"n{{index}}","unit":"","dataType":"DINT"}"""));

    private void WriteConfig(string json)
    {
        Directory.CreateDirectory(System.IO.Path.Combine(_directory, "config"));
        File.WriteAllText(System.IO.Path.Combine(_directory, "config", "module-config.json"), json);
    }

    private static object Read(ProcessImage image, PlcProgram program, string path) => image.ReadValue(program.Symbols.Resolve(path));

    private static string EnumName(ProcessImage image, PlcProgram program, string path)
    {
        var type = (EnumType)program.Symbols.Resolve(path).Type;
        long number = Convert.ToInt64(Read(image, program, path));
        return type.Values.Single(value => value.Value == number).Name;
    }

    private static void AssertDescriptor(ProcessImage image, PlcProgram program, string path, long id, string name, string dataType, string unit, string value)
    {
        Assert.Equal(id, Read(image, program, path + ".Id"));
        Assert.Equal(name, Read(image, program, path + ".Name"));
        Assert.Equal(dataType, EnumName(image, program, path + ".DataType"));
        Assert.Equal(unit, Read(image, program, path + ".Unit"));
        Assert.Equal(value, Read(image, program, path + ".Value"));
    }
}
