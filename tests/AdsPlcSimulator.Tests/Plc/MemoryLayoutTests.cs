using AdsPlcSimulator.Core.Plc;
using AdsPlcSimulator.Core.Plc.Parsing;

namespace AdsPlcSimulator.Tests.Plc;

/// <summary>
/// Expected offsets come from RobustSolutionDemoProject.tmc, the layout TwinCAT 4026 compiled for the real PLC.
/// </summary>
public class MemoryLayoutTests
{
    private static StructType LoadSampleType(string typeName)
    {
        PlcProgram program = PlcProgram.LoadDirectory(Path.Combine(TestPaths.SampleProject, "plc"));
        RootSymbol control = program.Symbols.Roots.Single(root => root.Path == "GVL_Module.Chamber1_Control");
        var element = (StructType)((ArrayType)control.Type).ElementType;
        return FindStruct(element, typeName) ?? throw new InvalidOperationException($"{typeName} not reachable from Chamber1_Control.");
    }

    private static StructType? FindStruct(PlcType type, string name)
    {
        PlcType layout = type.Unaliased is ArrayType array ? array.ElementType.Unaliased : type.Unaliased;
        if (layout is not StructType structure)
            return null;
        if (structure.Name == name)
            return structure;
        return structure.Members.Select(member => FindStruct(member.Type, name)).FirstOrDefault(found => found is not null);
    }

    private static void AssertLayout(StructType type, int size, params (string Member, int Offset)[] members)
    {
        Assert.Equal(size, type.Size);
        foreach ((string member, int offset) in members)
            Assert.Equal(offset, type.Find(member)!.Offset);
    }

    [Fact]
    public void Signal_input_matches_compiled_plc_layout()
    {
        string tcDut = File.ReadAllText(Path.Combine(TestPaths.SampleProject, "plc", "Chamber1", "ST_Chamber1_Signal_In.TcDUT"));
        DeclarationSet declarations = PlcSourceReader.ReadTwinCatObject(tcDut, "ST_Chamber1_Signal_In.TcDUT");
        declarations.Add(DeclarationParser.Parse("VAR_GLOBAL x : ST_Chamber1_Signal_In; END_VAR", "probe.st", "GVL"));

        var type = (StructType)PlcProgramBuilder.Build(declarations).Symbols.Roots[0].Type;

        AssertLayout(type, 92, ("bDoorClosed", 0), ("bVacuumOn", 1), ("rTemperature", 4), ("bRobotInChamber", 8), ("bDIs", 9), ("rAIs", 28));
    }

    [Fact]
    public void PackML_output_matches_compiled_plc_layout()
    {
        AssertLayout(LoadSampleType("ST_PML_Base_Out"), 24,
            ("eStateCurrent", 0), ("eModeCurrent", 4), ("bCommandAccepted", 8), ("eStateErrorID", 12), ("bModeChanged", 16), ("eModeErrorID", 20));
    }

    [Fact]
    public void Service_ctrl_tables_match_compiled_plc_layout()
    {
        AssertLayout(LoadSampleType("ST_ServiceCtrl"), 12, ("RequestId", 0), ("PackMLIn", 4));
        AssertLayout(LoadSampleType("ST_Chamber1_ServiceCtrl"), 180, ("DoorClose", 72), ("DoorOpen", 84), ("SetConfigParam", 168));
    }

    [Fact]
    public void Derived_struct_members_start_after_the_base_struct()
    {
        PlcProgram program = TestPaths.BuildFromText("""
            TYPE ST_Base : STRUCT a : LREAL; b : BOOL; END_STRUCT END_TYPE
            TYPE ST_Derived EXTENDS ST_Base : STRUCT c : BOOL; END_STRUCT END_TYPE
            VAR_GLOBAL x : ST_Derived; END_VAR
            """);

        AssertLayout((StructType)program.Symbols.Roots[0].Type, 24, ("a", 0), ("b", 8), ("c", 16));
    }

    [Fact]
    public void Function_block_reserves_hidden_pointer_and_stores_in_out_as_reference()
    {
        PlcProgram program = TestPaths.BuildFromText("""
            FUNCTION_BLOCK FB_Axis
            VAR_INPUT bEnable : BOOL; END_VAR
            VAR_IN_OUT stData : LREAL; END_VAR
            VAR_TEMP nScratch : INT; END_VAR
            VAR_OUTPUT nState : INT; END_VAR
            nState := 1;
            END_FUNCTION_BLOCK
            VAR_GLOBAL fbAxis : FB_Axis; END_VAR
            """);

        AssertLayout((StructType)program.Symbols.Roots[0].Type, 32, ("bEnable", 8), ("stData", 16), ("nState", 24));
    }
}
