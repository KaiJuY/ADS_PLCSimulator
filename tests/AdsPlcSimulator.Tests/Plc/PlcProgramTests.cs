using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.Plc;

public class PlcProgramTests
{
    [Fact]
    public void Sample_project_exposes_gvl_module_with_six_modules()
    {
        PlcProgram program = PlcProgram.LoadDirectory(Path.Combine(TestPaths.SampleProject, "plc"));
        ProcessImage image = program.CreateProcessImage();

        Assert.Equal(
            ["GVL_Module.Chamber1", "GVL_Module.Chamber1_Runtime", "GVL_Module.Chamber1_Control", "GVL_Module.Chamber1_SpinAxis", "GVL_Module.Chamber1_LiftPinAxis"],
            program.Symbols.Roots.Select(root => root.Path));
        SymbolRef requestId = program.Symbols.Resolve("gvl_module.chamber1_control[6].servicectrl.dooropen.requestid");
        Assert.Equal("GVL_Module.Chamber1_Control[6].ServiceCtrl.DoorOpen.RequestId", requestId.Path);
        Assert.Equal(true, image.ReadValue(program.Symbols.Resolve("GVL_Module.Chamber1_Runtime[3].Enabled")));
        Assert.Equal("Chamber1", image.ReadValue(program.Symbols.Resolve("GVL_Module.Chamber1_Runtime[3].ModuleType")));
    }

    [Fact]
    public void Constants_enums_and_initializers_are_evaluated()
    {
        PlcProgram program = TestPaths.BuildFromText("""
            TYPE E_State : (Idle := 10, Busy, Done) DINT; END_TYPE
            TYPE ST_Recipe : STRUCT nSteps : INT := 3; eState : E_State; END_STRUCT END_TYPE
            VAR_GLOBAL CONSTANT
                Count : INT := 2 * 2;
            END_VAR
            VAR_GLOBAL
                aValues : ARRAY[1..Count] OF INT := [1, 2(7)];
                stRecipe : ST_Recipe := (eState := E_State.Done);
                tTimeout : TIME := T#1s500ms;
                nMask : WORD := 16#FF;
                sName : STRING(10) := 'Chamber';
                eDefault : E_State;
            END_VAR
            """);
        ProcessImage image = program.CreateProcessImage();
        object Read(string path) => image.ReadValue(program.Symbols.Resolve("GVL_Test." + path));

        Assert.Equal(4, ((ArrayType)program.Symbols.Resolve("GVL_Test.aValues").Type).ElementCount);
        Assert.Equal([1L, 7L, 7L, 0L], Enumerable.Range(1, 4).Select(index => Read($"aValues[{index}]")));
        Assert.Equal(3L, Read("stRecipe.nSteps"));
        Assert.Equal(12L, Read("stRecipe.eState"));
        Assert.Equal(1500L, Read("tTimeout"));
        Assert.Equal(255L, Read("nMask"));
        Assert.Equal("Chamber", Read("sName"));
        Assert.Equal(10L, Read("eDefault"));
    }

    [Fact]
    public void Program_variables_are_scoped_by_program_name()
    {
        PlcProgram program = TestPaths.BuildFromText("""
            PROGRAM MAIN
            VAR
                nCounter : DINT;
            END_VAR
            nCounter := nCounter + 1;
            """);

        Assert.Equal("MAIN.nCounter", program.Symbols.Resolve("MAIN.nCounter").Path);
    }

    [Theory]
    [InlineData("VAR_GLOBAL x : ST_Missing; END_VAR", "unknown type 'ST_Missing'")]
    [InlineData("VAR_GLOBAL x : ARRAY[1..N] OF INT; END_VAR", "'N' is not a known constant")]
    [InlineData("TYPE ST_A : STRUCT b : ST_A; END_STRUCT END_TYPE VAR_GLOBAL x : ST_A; END_VAR", "contains itself")]
    [InlineData("VAR_GLOBAL x : BYTE := 300; END_VAR", "out of range for BYTE")]
    public void Invalid_declarations_fail_with_location(string text, string expectedMessage)
    {
        var exception = Assert.Throws<PlcBuildException>(() => TestPaths.BuildFromText(text));

        Assert.Contains("test.st:1", exception.Message);
        Assert.Contains(expectedMessage, exception.Message);
    }

    [Fact]
    public void Writes_publish_old_and_new_bytes()
    {
        PlcProgram program = TestPaths.BuildFromText("VAR_GLOBAL nValue : INT := 5; END_VAR");
        ProcessImage image = program.CreateProcessImage();
        SymbolRef value = program.Symbols.Resolve("GVL_Test.nValue");
        MemoryWrite? published = null;
        image.Written += write => published = write;

        image.WriteValue(value, 9, WriteSource.Client);

        Assert.Equal(9L, image.ReadValue(value));
        Assert.Equal(new byte[] { 5, 0 }, published!.OldData);
        Assert.Equal(new byte[] { 9, 0 }, published.NewData);
        Assert.Throws<OverflowException>(() => image.WriteValue(value, 40000, WriteSource.Client));
    }

    [Theory]
    [Trait("AC", "1")]
    [Trait("AC", "2")]
    [InlineData(null, "has no PLC declarations")]
    [InlineData("", "has no PLC declarations")]
    [InlineData("TYPE ST_Only : STRUCT nValue : INT; END_STRUCT END_TYPE", "declares no global variables")]
    public void Loading_a_folder_without_root_symbols_fails_with_its_full_path(string? stSource, string expectedMessage)
    {
        string directory = Path.Combine(Path.GetTempPath(), "adsplc-" + Guid.NewGuid().ToString("N"), "plc");
        Directory.CreateDirectory(directory);
        try
        {
            // An unsupported extension is ignored, so the global declared here must not count.
            File.WriteAllText(Path.Combine(directory, "notes.txt"), "VAR_GLOBAL nIgnored : INT; END_VAR");
            if (stSource is not null)
                File.WriteAllText(Path.Combine(directory, "source.st"), stSource);

            var exception = Assert.Throws<PlcBuildException>(() => PlcProgram.LoadDirectory(directory));

            Assert.Contains(expectedMessage, exception.Message);
            Assert.Contains(Path.GetFullPath(directory), exception.Message);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true);
        }
    }
}
