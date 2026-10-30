using System.Text.RegularExpressions;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.Plc;

public class Polar2LayoutTests
{
    [Fact]
    [Trait("AC", "1")]
    public void Polar2_module_layout_with_500_ecid_capacity()
    {
        PlcProgram program = PlcProgram.LoadDirectory(Path.Combine(TestPaths.Polar2Project, "plc"));

        (string Path, int Offset, int Size)[] expected =
        [
            ("GVL_Module.Chamber_Control", 0, 12966720),
            ("GVL_Module.Chamber_Control[1]", 0, 2161120),
            ("GVL_Module.Chamber_Control[2]", 2161120, 2161120),
            ("GVL_Module.Chamber_Control[6]", 10805600, 2161120),
            ("GVL_Module.Chamber_Control[1].ModuleCtrl", 4, 32),
            ("GVL_Module.Chamber_Control[1].ModuleStatus", 36, 432),
            ("GVL_Module.Chamber_Control[1].ModuleVariableList", 468, 13200),
            ("GVL_Module.Chamber_Control[1].ModuleConfigParamList", 58672, 76000),
            ("GVL_Module.Chamber_Control[1].ServiceCtrl", 134672, 3300),
            ("GVL_Module.Chamber_Control[1].ServiceParam", 137976, 95280),
            ("GVL_Module.Chamber_Control[1].ServiceStatus", 233256, 1927860),
            ("GVL_Module.Chamber_Control[1].ServiceCtrl.Shutter.Open", 137652, 20),
            ("GVL_Module.Chamber_Control[1].ServiceStatus.Shutter.Open", 1974172, 11684),
        ];

        foreach ((string path, int offset, int size) in expected)
        {
            SymbolRef symbol = program.Symbols.Resolve(path);
            Assert.True(offset == symbol.Offset && size == symbol.Size, $"{path}: expected {offset}/{size}, got {symbol.Offset}/{symbol.Size}");
        }

        string gvl = File.ReadAllText(Path.Combine(TestPaths.Polar2Project, "plc", "Framework", "GVLs", "Param_Config.TcGVL"));
        int capacity = int.Parse(Regex.Match(gvl, @"MaxConfigParamPerModule\s*:\s*UINT\s*:=\s*(\d+)").Groups[1].Value);
        SymbolRef list = program.Symbols.Resolve("GVL_Module.Chamber_Control[1].ModuleConfigParamList");
        SymbolRef item = program.Symbols.Resolve("GVL_Module.Chamber_Control[1].ModuleConfigParamList[1]");
        Assert.Equal(500, capacity);
        Assert.Equal(152, item.Size);
        Assert.Equal(capacity * item.Size, list.Size);
    }
}
