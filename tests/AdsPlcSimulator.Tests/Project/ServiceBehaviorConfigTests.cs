using AdsPlcSimulator.Core.PackML.Behavior;
using AdsPlcSimulator.Core.Plc;
using AdsPlcSimulator.Host.Project;

namespace AdsPlcSimulator.Tests.Project;

public sealed class ServiceBehaviorConfigTests : IDisposable
{
    private const string Path = "service-behaviors.json";
    private const string Module1 = "GVL_Module.Chamber_Control[1]";

    private const string SmallProgram = """
        TYPE ST_ServiceCtrl : STRUCT RequestId : UDINT; END_STRUCT END_TYPE
        TYPE ST_Ctrls : STRUCT A : ST_ServiceCtrl; B : ST_ServiceCtrl; Plain : UDINT; END_STRUCT END_TYPE
        TYPE ST_ParamA : STRUCT Index : UINT; Count : DINT; Time : TIME; Speed : REAL; Flag : BOOL; Big : ULINT; END_STRUCT END_TYPE
        TYPE ST_Params : STRUCT A : ST_ParamA; B : ST_ParamA; END_STRUCT END_TYPE
        TYPE ST_ExtDescriptor : STRUCT Id : UDINT; Value : STRING(80); END_STRUCT END_TYPE
        TYPE ST_Module : STRUCT
            ServiceCtrl : ST_Ctrls;
            ServiceParam : ST_Params;
            ModuleVariableList : ARRAY[1..4] OF ST_ExtDescriptor;
            ModuleConfigParamList : ARRAY[1..3] OF ST_ExtDescriptor;
        END_STRUCT END_TYPE
        VAR_GLOBAL
            Chamber_Control : ARRAY[1..2] OF ST_Module;
        END_VAR
        """;

    private const string Modes = """[{"value":0,"name":"Disable","allow":["A"]},{"value":1,"name":"Manual","deny":["B"],"dryRun":true}]""";

    private static readonly Lazy<PlcProgram> s_polar2 = new(() => PlcProgram.LoadDirectory(System.IO.Path.Combine(TestPaths.Polar2Project, "plc")));

    private readonly PlcProgram _small = TestPaths.BuildFromText(SmallProgram, globalScope: "GVL_Module");
    private readonly string _directory = Directory.CreateTempSubdirectory("adsplc-behaviors-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static ModuleConfigFile Config(bool secondModule = false)
    {
        DescriptorConfig[] variables = [new() { Id = 5, DataType = "DINT" }, new() { Id = 6, DataType = "REAL" }, new() { Id = 7, DataType = "STRING" }, new() { Id = 8, DataType = "DINT" }];
        DescriptorConfig[] ecids = [new() { Id = 10, DataType = "DINT" }, new() { Id = 11, DataType = "REAL" }, new() { Id = 12, DataType = "BOOL" }];
        return new ModuleConfigFile
        {
            SchemaVersion = 4,
            Modules =
            [
                new ModuleConfigEntry { Enabled = true, Slot = 1, VariableList = [.. variables], ConfigParamList = [.. ecids] },
                new ModuleConfigEntry { Enabled = secondModule, Slot = 2, VariableList = [.. variables], ConfigParamList = [.. ecids] },
            ],
        };
    }

    private static string Json(string services = "{}", string modes = Modes, string head = "\"schemaVersion\":1,\"modeSvid\":5") =>
        $$"""{ {{head}}, "modes": {{modes}}, "services": {{services}} }""";

    private ServiceBehaviorTable Build(string json, bool secondModule = false) =>
        ServiceBehaviorLoader.Build(ServiceBehaviorLoader.Parse(json, Path), Path, _small.Symbols, Config(secondModule));

    private ModuleBehavior Module(string json) => Build(json).For(Module1)!;

    [Fact]
    [Trait("AC", "1")]
    public void The_Polar2_file_builds_with_the_Polar2_module_config()
    {
        string project = TestPaths.Polar2Project;
        ModuleConfigFile config = ModuleConfigPublisher.Parse(File.ReadAllText(System.IO.Path.Combine(project, "config", "module-config.json")), "module-config.json");
        PositionsOf(config, out int svid53, out int ecid318);

        ServiceBehaviorTable? table = ServiceBehaviorLoader.LoadIfPresent(project, s_polar2.Value.Symbols, config);

        ModuleBehavior module = table!.For(Module1)!;
        Assert.Equal("ModuleVariableList[1].Value", module.ModeSource);
        ServiceBehavior cw = module.Services["Holder.Rotate.MoveVelocityCW"];
        ServiceBehavior ccw = module.Services["Holder.Rotate.MoveVelocityCCW"];
        Assert.Equal($"ModuleVariableList[{svid53}].Value", cw.OnEnter[0].Target);
        Assert.Equal($"ModuleConfigParamList[{ecid318}].Value", cw.OnEnter[0].Value.Source);
        Assert.False(cw.OnEnter[0].Value.Negate);
        Assert.True(ccw.OnEnter[0].Value.Negate);
        Assert.Equal("TestTime", module.Services["Chamber.ArmAlignTest"].DurationField);
        Assert.Null(table.For("GVL_Module.Chamber_Control[2]"));

        static void PositionsOf(ModuleConfigFile file, out int svid, out int ecid)
        {
            ModuleConfigEntry slot = file.Modules!.Single(entry => entry!.Enabled)!;
            svid = slot.VariableList!.FindIndex(item => item!.Id == 53) + 1;
            ecid = slot.ConfigParamList!.FindIndex(item => item!.Id == 318) + 1;
        }
    }

    [Fact]
    [Trait("AC", "1")]
    public void The_Polar2_rules_are_the_ones_of_the_spec()
    {
        ModuleConfigFile config = ModuleConfigPublisher.Parse(File.ReadAllText(System.IO.Path.Combine(TestPaths.Polar2Project, "config", "module-config.json")), "module-config.json");

        ModuleBehavior module = ServiceBehaviorLoader.LoadIfPresent(TestPaths.Polar2Project, s_polar2.Value.Symbols, config)!.For(Module1)!;

        Assert.Equal(new long[] { 0, 1, 2, 3 }, module.Modes.Select(mode => mode.Value).ToArray());
        Assert.Equal(ModeRuleKind.AllowOnly, module.Modes[0].Kind);
        Assert.Equal(new[] { "Chamber.SetMode" }, module.Modes[0].Services.ToArray());
        Assert.Equal(new[] { "Chamber.SingleProcess", "Chamber.EMGS" }, module.Modes[1].Services.ToArray());
        Assert.True(module.Modes[2].DryRun);
        Assert.Equal(new ParamRange("ArmIndex", 1, 4), module.Services["Chamber.ArmTest"].Ranges[0]);
        Assert.Equal(new ParamRange("ArmNozzleIndex", 1, 3), module.Services["Chamber.FlowCalibration"].Ranges[1]);
        Assert.Null(module.Services["Chamber.ArmTest"].DurationField);
    }

    [Fact]
    [Trait("AC", "1")]
    public void A_service_entry_is_built_with_ranges_duration_and_typed_SVID_writes()
    {
        const string services = """
            { "A": {
                "ranges": [ { "field": "Index", "min": 1, "max": 4 } ],
                "duration": "Time",
                "onEnter": [ { "svid": 6, "value": { "param": "Speed", "negate": true } }, { "svid": 5, "value": { "ecid": 10 }, "dryRunZero": true } ],
                "beforeComplete": [ { "svid": 8, "value": { "constant": -3 } } ],
                "onLeave": [ { "svid": 5, "value": { "param": "Count" } } ] } }
            """;

        ServiceBehavior service = Module(Json(services)).Services["A"];

        Assert.Equal(new ParamRange("Index", 1, 4), service.Ranges.Single());
        Assert.Equal("Time", service.DurationField);
        Assert.Equal(new SvidWrite("ModuleVariableList[2].Value", true, new SvidValue(SvidValueKind.Param, 0, "Speed", true), false), service.OnEnter[0]);
        Assert.Equal(new SvidWrite("ModuleVariableList[1].Value", false, new SvidValue(SvidValueKind.Ecid, 0, "ModuleConfigParamList[1].Value", false), true), service.OnEnter[1]);
        Assert.Equal(new SvidWrite("ModuleVariableList[4].Value", false, new SvidValue(SvidValueKind.Constant, -3, null, false), false), service.BeforeComplete.Single());
        Assert.Equal("Count", service.OnLeave.Single().Value.Source);
    }

    [Fact]
    [Trait("AC", "1")]
    public void Modes_are_built_as_AllowOnly_or_DenyListed_and_the_Mode_SVID_may_be_of_any_type()
    {
        ModuleBehavior module = Module(Json(head: "\"schemaVersion\":1,\"modeSvid\":7"));

        Assert.Equal("ModuleVariableList[3].Value", module.ModeSource);
        Assert.Equal(ModeRuleKind.AllowOnly, module.Modes[0].Kind);
        Assert.Contains("A", module.Modes[0].Services);
        Assert.Equal(ModeRuleKind.DenyListed, module.Modes[1].Kind);
        Assert.True(module.Modes[1].DryRun);
    }

    [Fact]
    [Trait("AC", "1")]
    public void Only_enabled_modules_get_an_entry()
    {
        Assert.Null(Build(Json()).For("GVL_Module.Chamber_Control[2]"));
        Assert.NotNull(Build(Json(), secondModule: true).For("GVL_Module.Chamber_Control[2]"));
    }

    [Fact]
    [Trait("AC", "2")]
    public void A_service_that_the_host_may_not_bind_is_still_accepted()
    {
        Assert.Contains("B", Module(Json("""{ "B": { "ranges": [ { "field": "Index", "min": 0, "max": 9 } ] } }""")).Services.Keys);
    }

    public static TheoryData<string, string> Failures => new()
    {
        { "null", "the file is empty" },
        { Json(services: "null"), "services is null" },
        { Json(head: "\"schemaVersion\":2,\"modeSvid\":5"), "schemaVersion must be 1" },
        { Json(head: "\"schemaVersion\":1,\"modeSvid\":0"), "modeSvid must be greater than 0" },
        { Json(head: "\"schemaVersion\":1,\"modeSvid\":99"), "modeSvid: SVID 99 is not in module-config.json" },
        { Json(modes: "[]"), "modes is missing, null or empty" },
        { """{"schemaVersion":1,"modeSvid":5}""", "modes is missing, null or empty" },
        { Json(modes: """[{"name":"x","deny":[]}]"""), "modes[1]: value is missing" },
        { Json(modes: """[{"value":4294967295,"name":"x","deny":[]}]"""), "modes[1]: value must be 0..4294967294" },
        { Json(modes: """[{"value":0,"name":"x","deny":[]},{"value":0,"name":"y","deny":[]}]"""), "modes[2]: value 0 appears more than once" },
        { Json(modes: """[{"value":0,"name":"x","deny":[]},{"value":1,"name":"X","deny":[]}]"""), "modes[2]: name 'X' appears more than once" },
        { Json(modes: """[{"value":0,"name":" ","deny":[]}]"""), "modes[1]: name is empty" },
        { Json(modes: """[{"value":0,"name":"x","allow":[],"deny":[]}]"""), "modes[1]: exactly one of allow and deny" },
        { Json(modes: """[{"value":0,"name":"x"}]"""), "modes[1]: exactly one of allow and deny" },
        { Json(modes: """[{"value":0,"name":"x","allow":["A","a"]}]"""), "modes[1].allow: 'a' appears more than once" },
        { Json(modes: """[{"value":0,"name":"x","allow":["Nope"]}]"""), "modes[1]: cannot resolve GVL_Module.Chamber_Control[1].ServiceCtrl.Nope" },
        { Json(modes: """[{"value":-1,"name":"x","deny":[]}]"""), "modes[1]: value must be 0..4294967294" },
        { Json(modes: "[null]"), "modes[1] is null" },
        { Json("""{ "A": { "durationMs": 5 } }"""), "invalid JSON" },
        { Json("""{ "A": { "ranges": [ { "field": "Index", "min": "1", "max": 2 } ] } }"""), "invalid JSON" },
        { Json("""{ "Nope": {} }"""), "services.Nope: cannot resolve GVL_Module.Chamber_Control[1].ServiceCtrl.Nope" },
        { Json("""{ "Plain": {} }"""), "is UDINT, not ST_ServiceCtrl" },
        { Json("""{ "A": null }"""), "services.A is null" },
        { Json("""{ "A": {}, "a": {} }"""), "appears more than once" },
        { Json("""{ "A": { "ranges": null } }"""), "ranges, onEnter, beforeComplete and onLeave must not be null" },
        { Json("""{ "A": { "onEnter": null } }"""), "must not be null" },
        { Json("""{ "A": { "onEnter": [ null ] } }"""), "services.A.onEnter[1] is null" },
        { Json("""{ "A": { "ranges": [ { "field": "Nope", "min": 1, "max": 2 } ] } }"""), "services.A: cannot resolve" },
        { Json("""{ "A": { "ranges": [ { "field": "Time", "min": 1, "max": 2 } ] } }"""), "TIME, not an integer" },
        { Json("""{ "A": { "ranges": [ { "field": "Big", "min": 1, "max": 2 } ] } }"""), "ULINT, not an integer" },
        { Json("""{ "A": { "ranges": [ { "field": " ", "min": 1, "max": 2 } ] } }"""), "services.A.ranges[1]: field is empty" },
        { Json("""{ "A": { "ranges": [ { "field": "Index", "min": 3, "max": 2 } ] } }"""), "services.A.ranges[1]: min is greater than max" },
        { Json("""{ "A": { "ranges": [ { "field": "Index", "max": 2 } ] } }"""), "min and max are required" },
        { Json("""{ "A": { "duration": "Index" } }"""), "UINT, not a TIME" },
        { Json("""{ "A": { "duration": " " } }"""), "duration is empty" },
        { Json("""{ "A": { "onEnter": [ { "svid": 99, "value": { "constant": 1 } } ] } }"""), "services.A.onEnter[1]: SVID 99 is not in module-config.json" },
        { Json("""{ "A": { "onEnter": [ { "svid": 0, "value": { "constant": 1 } } ] } }"""), "svid must be greater than 0" },
        { Json("""{ "A": { "onEnter": [ { "svid": 7, "value": { "constant": 1 } } ] } }"""), "SVID 7 is STRING, not DINT or REAL" },
        { Json("""{ "A": { "onEnter": [ { "svid": 5, "value": { "ecid": 12 } } ] } }"""), "ECID 12 is BOOL, not DINT or REAL" },
        { Json("""{ "A": { "onEnter": [ { "svid": 5, "value": { "ecid": 99 } } ] } }"""), "ECID 99 is not in module-config.json" },
        { Json("""{ "A": { "onEnter": [ { "svid": 5 } ] } }"""), "services.A.onEnter[1]: value is missing" },
        { Json("""{ "A": { "onEnter": [ { "svid": 5, "value": {} } ] } }"""), "exactly one of constant, param and ecid" },
        { Json("""{ "A": { "onEnter": [ { "svid": 5, "value": { "constant": 1, "ecid": 10 } } ] } }"""), "exactly one of constant, param and ecid" },
        { Json("""{ "A": { "onEnter": [ { "svid": 5, "value": { "param": " " } } ] } }"""), "param is empty" },
        { Json("""{ "A": { "onEnter": [ { "svid": 5, "value": { "param": "Flag" } } ] } }"""), "BOOL, not an integer or REAL" },
        { Json("""{ "A": { "onEnter": [ { "svid": 5, "value": { "constant": 2.5 } } ] } }"""), "does not fit a DINT SVID" },
        { Json("""{ "A": { "onEnter": [ { "svid": 5, "value": { "constant": 3e9 } } ] } }"""), "does not fit a DINT SVID" },
        { Json("""{ "A": { "onEnter": [ { "svid": 5, "value": { "constant": 1e999 } } ] } }"""), "service-behaviors.json" },
    };

    [Theory]
    [Trait("AC", "2")]
    [MemberData(nameof(Failures))]
    public void An_invalid_file_fails_the_Run_with_the_place_and_the_reason(string json, string expected)
    {
        var exception = Assert.Throws<ModuleConfigException>(() => Build(json));

        Assert.Contains(expected, exception.Message);
        Assert.Contains(Path, exception.Message);
    }

    [Fact]
    [Trait("AC", "2")]
    public void A_REAL_SVID_takes_a_fractional_constant()
    {
        const string services = """{ "A": { "onEnter": [ { "svid": 6, "value": { "constant": 2.5 } } ] } }""";

        Assert.True(Module(Json(services)).Services["A"].OnEnter[0].IsReal);
    }

    [Fact]
    [Trait("AC", "2")]
    public void A_missing_file_gives_no_table_and_a_missing_module_config_fails()
    {
        Assert.Null(ServiceBehaviorLoader.LoadIfPresent(_directory, _small.Symbols, Config()));
        Directory.CreateDirectory(System.IO.Path.Combine(_directory, "config"));
        File.WriteAllText(System.IO.Path.Combine(_directory, "config", "service-behaviors.json"), Json());

        var exception = Assert.Throws<ModuleConfigException>(() => ServiceBehaviorLoader.LoadIfPresent(_directory, _small.Symbols, null));

        Assert.Contains("module-config.json", exception.Message);
    }

    [Fact]
    [Trait("AC", "2")]
    public void An_existing_file_is_read_from_the_config_folder()
    {
        Directory.CreateDirectory(System.IO.Path.Combine(_directory, "config"));
        File.WriteAllText(System.IO.Path.Combine(_directory, "config", "service-behaviors.json"), Json());

        ServiceBehaviorTable? table = ServiceBehaviorLoader.LoadIfPresent(_directory, _small.Symbols, Config());

        Assert.NotNull(table!.For(Module1));
    }

    [Fact]
    [Trait("AC", "2")]
    public void An_empty_file_is_refused()
    {
        var exception = Assert.Throws<ModuleConfigException>(() => ServiceBehaviorLoader.Parse("null", Path));

        Assert.Contains("the file is empty", exception.Message);
    }
}
