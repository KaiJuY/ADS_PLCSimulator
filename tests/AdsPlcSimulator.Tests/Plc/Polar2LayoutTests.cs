using System.Text.RegularExpressions;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.Plc;

public class Polar2LayoutTests
{
    [Fact]
    [Trait("AC", "1")]
    public void Polar2_module_layout_with_450_ecid_capacity()
    {
        PlcProgram program = PlcProgram.LoadDirectory(Path.Combine(TestPaths.Polar2Project, "plc"));

        (string Path, int Offset, int Size)[] expected =
        [
            ("GVL_Module.Chamber_Control", 0, 12964944),
            ("GVL_Module.Chamber_Control[1]", 0, 2160824),
            ("GVL_Module.Chamber_Control[2]", 2160824, 2160824),
            ("GVL_Module.Chamber_Control[6]", 10804120, 2160824),
            ("GVL_Module.Chamber_Control[1].ModuleCtrl", 4, 32),
            ("GVL_Module.Chamber_Control[1].ModuleStatus", 36, 432),
            ("GVL_Module.Chamber_Control[1].ModuleVariableList", 468, 13200),
            ("GVL_Module.Chamber_Control[1].ModuleConfigParamList", 58672, 68400),
            ("GVL_Module.Chamber_Control[1].ServiceCtrl", 127072, 3320),
            ("GVL_Module.Chamber_Control[1].ServiceParam", 130392, 90888),
            ("GVL_Module.Chamber_Control[1].ServiceStatus", 221280, 1939544),
            ("GVL_Module.Chamber_Control[1].ServiceCtrl.Shutter.Open", 130072, 20),
            ("GVL_Module.Chamber_Control[1].ServiceStatus.Shutter.Open", 1973880, 11684),
            ("GVL_Module.Chamber_Control[1].ServiceCtrl.Holder.VacuumEstablish", 129892, 20),
            ("GVL_Module.Chamber_Control[1].ServiceParam.Holder.VacuumEstablish", 221232, 1),
            ("GVL_Module.Chamber_Control[1].ServiceStatus.Holder.VacuumEstablish", 1868724, 11684),
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
        Assert.Equal(450, capacity);
        Assert.Equal(152, item.Size);
        Assert.Equal(capacity * item.Size, list.Size);
    }
}
