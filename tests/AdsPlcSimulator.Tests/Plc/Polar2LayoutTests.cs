using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.Plc;

/// <summary>
/// Expected offsets and sizes come from Polar_II_ChamberDemo.tmc, the layout TwinCAT compiled for the real PLC (BitOffs and BitSize divided by 8).
/// </summary>
public class Polar2LayoutTests
{
    [Fact]
    [Trait("AC", "1")]
    public void Polar2_module_layout_matches_tmc()
    {
        PlcProgram program = PlcProgram.LoadDirectory(Path.Combine(TestPaths.Polar2Project, "plc"));

        (string Path, int Offset, int Size)[] expected =
        [
            ("GVL_Module.Chamber_Control", 0, 12678720),
            ("GVL_Module.Chamber_Control[1]", 0, 2113120),
            ("GVL_Module.Chamber_Control[2]", 2113120, 2113120),
            ("GVL_Module.Chamber_Control[6]", 10565600, 2113120),
            ("GVL_Module.Chamber_Control[1].ModuleCtrl", 4, 32),
            ("GVL_Module.Chamber_Control[1].ModuleStatus", 36, 432),
            ("GVL_Module.Chamber_Control[1].ModuleVariableList", 468, 13200),
            ("GVL_Module.Chamber_Control[1].ServiceCtrl", 104272, 3300),
            ("GVL_Module.Chamber_Control[1].ServiceParam", 107576, 77680),
            ("GVL_Module.Chamber_Control[1].ServiceStatus", 185256, 1927860),
            ("GVL_Module.Chamber_Control[1].ServiceCtrl.Shutter.Open", 107252, 20),
            ("GVL_Module.Chamber_Control[1].ServiceStatus.Shutter.Open", 1926172, 11684),
        ];

        foreach ((string path, int offset, int size) in expected)
        {
            SymbolRef symbol = program.Symbols.Resolve(path);
            Assert.True(offset == symbol.Offset && size == symbol.Size, $"{path}: expected {offset}/{size}, got {symbol.Offset}/{symbol.Size}");
        }
    }
}
