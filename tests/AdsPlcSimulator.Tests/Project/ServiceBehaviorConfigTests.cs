using System.Text.Json;
using System.Text.RegularExpressions;
using AdsPlcSimulator.Core.PackML;
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
        TYPE E_Dir : (Up := 1, Down := 2) UINT; END_TYPE
        TYPE ST_Step : STRUCT Kind : UINT; Pick : UINT; Time : TIME; Dir : E_Dir; Speed : REAL; END_STRUCT END_TYPE
        TYPE ST_ParamA : STRUCT Index : UINT; Count : DINT; Time : TIME; Speed : REAL; Flag : BOOL; Big : ULINT; Angle : LREAL; N : UINT; Limit : UINT; Steps : ARRAY[1..5] OF ST_Step; END_STRUCT END_TYPE
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
        PositionsOf(config, out int svid53, out int holderSpeedEcid);

        ServiceBehaviorTable? table = ServiceBehaviorLoader.LoadIfPresent(project, s_polar2.Value.Symbols, config);

        ModuleBehavior module = table!.For(Module1)!;
        Assert.Equal("ModuleVariableList[1].Value", module.ModeSource);
        ServiceBehavior cw = module.Services["Holder.Rotate.MoveVelocityCW"];
        ServiceBehavior ccw = module.Services["Holder.Rotate.MoveVelocityCCW"];
        Assert.Equal($"ModuleVariableList[{svid53}].Value", cw.OnEnter[0].Target);
        Assert.Equal($"ModuleConfigParamList[{holderSpeedEcid}].Value", cw.OnEnter[0].Value.Source);
        Assert.False(cw.OnEnter[0].Value.Negate);
        Assert.True(ccw.OnEnter[0].Value.Negate);
        Assert.Equal("TestTime", module.Services["Chamber.ArmAlignTest"].DurationField);
        Assert.Null(table.For("GVL_Module.Chamber_Control[2]"));

        static void PositionsOf(ModuleConfigFile file, out int svid, out int ecid)
        {
            ModuleConfigEntry slot = file.Modules!.Single(entry => entry!.Enabled)!;
            svid = slot.VariableList!.FindIndex(item => item!.Id == 53) + 1;
            ecid = slot.ConfigParamList!.FindIndex(item => item!.Id == 3001) + 1;
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

    private static bool HasSvidWrites(ServiceBehavior service) =>
        service.OnEnter.Count + service.BeforeComplete.Count + service.OnLeave.Count > 0;

    private static void AssertHasNoSvidWrites(IReadOnlyDictionary<string, ServiceBehavior> services, string key)
    {
        Assert.True(services.TryGetValue(key, out ServiceBehavior? service), key);
        Assert.False(HasSvidWrites(service!), key);
    }

    [Fact]
    [Trait("AC", "1")]
    [Trait("AC", "2")]
    [Trait("AC", "5")]
    [Trait("AC", "6")]
    [Trait("AC", "8")]
    [Trait("AC", "10")]
    public void The_Polar2_file_has_135_services_with_the_batch_2_rules_and_no_SVID_writes_for_services_without_an_SVID()
    {
        ModuleConfigFile config = ModuleConfigPublisher.Parse(File.ReadAllText(System.IO.Path.Combine(TestPaths.Polar2Project, "config", "module-config.json")), "module-config.json");
        ModuleConfigEntry slot = config.Modules!.Single(entry => entry!.Enabled)!;
        int SvidPosition(uint id) => slot.VariableList!.FindIndex(item => item!.Id == id) + 1;
        int EcidPosition(uint id) => slot.ConfigParamList!.FindIndex(item => item!.Id == id) + 1;

        IReadOnlyDictionary<string, ServiceBehavior> services = ServiceBehaviorLoader.LoadIfPresent(TestPaths.Polar2Project, s_polar2.Value.Symbols, config)!.For(Module1)!.Services;

        Assert.Equal(135, services.Count);
        Assert.Equal(new ParamRange("Position", 0, 360), services["Holder.Rotate.MoveModulo"].Ranges.Single());
        Assert.Equal($"ModuleVariableList[{SvidPosition(53)}].Value", services["Holder.Rotate.MoveModulo"].OnEnter.Single().Target);
        Assert.Equal(new SvidValue(SvidValueKind.Ecid, 0, $"ModuleConfigParamList[{EcidPosition(3202)}].Value", true), services["OutsideRing.Rotate.MoveVelocityCCW"].OnEnter.Single().Value);
        Assert.Equal($"ModuleVariableList[{SvidPosition(58)}].Value", services["OutsideRing.Rotate.MoveVelocityCW"].OnLeave.Single().Target);
        Assert.Equal(
            [$"ModuleVariableList[{SvidPosition(51)}].Value", $"ModuleVariableList[{SvidPosition(52)}].Value"],
            services["Cup[4].Lift.IncrementalJog"].BeforeComplete.Select(write => write.Target).ToArray());
        Assert.All(services["Cup[4].Lift.IncrementalJog"].BeforeComplete, write =>
        {
            Assert.True(write.IsReal);
            Assert.Equal(new SvidValue(SvidValueKind.AddParam, 0, "Position", false), write.Value);
        });
        Assert.Equal(
            [$"ModuleConfigParamList[{EcidPosition(2307)}].Value", $"ModuleConfigParamList[{EcidPosition(2307)}].Value"],
            services["Cup[4].Lift.TopPos"].BeforeComplete.Select(write => write.Value.Source!).ToArray());
        Assert.Equal($"ModuleConfigParamList[{EcidPosition(2309)}].Value", services["Cup[4].Lift.MiddlePos"].BeforeComplete[1].Value.Source);
        Assert.Equal($"ModuleConfigParamList[{EcidPosition(2108)}].Value", services["Cup[2].Lift.DownPos"].BeforeComplete[0].Value.Source);
        Assert.Equal($"ModuleVariableList[{SvidPosition(49)}].Value", services["Cup[3].Lift.TopPos"].BeforeComplete[0].Target);
        Assert.All(services["Cup[1].Lift.TopPos"].BeforeComplete, write => Assert.False(write.DryRunZero));
        Assert.All(
            ["Shutter.Open", "Cup[1].Lift.JogPositive", "Cup[2].Drainer.Select3", "Holder.Gripper.Open"],
            key => AssertHasNoSvidWrites(services, key));
    }

    [Fact]
    [Trait("AC", "2")]
    [Trait("AC", "3")]
    [Trait("AC", "4")]
    [Trait("AC", "5")]
    [Trait("AC", "8")]
    [Trait("AC", "9")]
    public void The_Polar2_batch_3_rules_write_the_SVIDs_of_the_spec()
    {
        ModuleConfigFile config = ModuleConfigPublisher.Parse(File.ReadAllText(System.IO.Path.Combine(TestPaths.Polar2Project, "config", "module-config.json")), "module-config.json");
        ModuleConfigEntry slot = config.Modules!.Single(entry => entry!.Enabled)!;
        string Svid(uint id) => $"ModuleVariableList[{slot.VariableList!.FindIndex(item => item!.Id == id) + 1}].Value";
        string Ecid(uint id) => $"ModuleConfigParamList[{slot.ConfigParamList!.FindIndex(item => item!.Id == id) + 1}].Value";

        IReadOnlyDictionary<string, ServiceBehavior> services = ServiceBehaviorLoader.LoadIfPresent(TestPaths.Polar2Project, s_polar2.Value.Symbols, config)!.For(Module1)!.Services;

        ServiceBehavior lift = services["Arm[2].Lift.DownPos"];
        Assert.Equal(Svid(17), lift.OnEnter.Single().Target);
        Assert.Equal(Ecid(1101), lift.OnEnter.Single().Value.Source);
        Assert.Equal(Svid(15), lift.BeforeComplete.Single().Target);
        Assert.True(lift.BeforeComplete.Single().IsReal);
        Assert.Equal(Ecid(1107), lift.BeforeComplete.Single().Value.Source);
        Assert.Equal(Svid(17), lift.OnLeave.Single().Target);
        Assert.Equal(new SvidValue(SvidValueKind.Constant, 0, null, false), lift.OnLeave.Single().Value);

        ServiceBehavior center = services["Arm[3].Rotate.Noz2CenterPos"];
        Assert.Equal(Ecid(1209), center.OnEnter.Single().Value.Source);
        Assert.Equal(Svid(29), center.BeforeComplete.Single().Target);
        Assert.Equal(Ecid(1218), center.BeforeComplete.Single().Value.Source);

        ServiceBehavior jog = services["Arm[4].Rotate.JogNegative"];
        Assert.Equal(Svid(41), jog.OnEnter.Single().Target);
        Assert.Equal(new SvidValue(SvidValueKind.Ecid, 0, Ecid(1310), true), jog.OnEnter.Single().Value);
        Assert.Empty(jog.BeforeComplete);

        SvidWrite increment = services["Arm[1].Rotate.IncrementalJog"].BeforeComplete.Single();
        Assert.Equal(Svid(3), increment.Target);
        Assert.Equal(new SvidValue(SvidValueKind.AddParam, 0, "Position", false), increment.Value);

        ServiceBehavior port = services["Arm[1].NozzlePort.Nozzle[3].Supply"];
        Assert.Equal(Svid(14), port.OnEnter.Single().Target);
        Assert.Equal(new SvidValue(SvidValueKind.Param, 0, "Setpoint", false), port.OnEnter.Single().Value);
        Assert.True(port.OnEnter.Single().DryRunZero);
        Assert.Equal(new SvidValue(SvidValueKind.Constant, 0, null, false), port.OnLeave.Single().Value);
        Assert.False(port.OnLeave.Single().DryRunZero);

        Assert.Equal(Svid(57), services["FixedNozzle[4].ChemicalSupply"].OnEnter.Single().Target);
        Assert.True(services["FixedNozzle[4].ChemicalSupply"].OnEnter.Single().DryRunZero);

        SvidWrite fan = services["Exhaust.Fan[2].SupplyFlowPID"].OnEnter.Single();
        Assert.Equal(Svid(60), fan.Target);
        Assert.True(fan.IsReal);
        Assert.Equal(Ecid(3411), fan.Value.Source);
        Assert.False(fan.DryRunZero);

        Assert.DoesNotContain("Arm[1].Rotate.MoveRecipe", services.Keys);
        Assert.All(
            ["Arm[2].NozzlePort.DrainSelect1", "Exhaust.Damper.AEX", "SideVent[1].Open"],
            key => AssertHasNoSvidWrites(services, key));
    }

    [Fact]
    [Trait("AC", "1")]
    [Trait("AC", "12")]
    [Trait("AC", "13")]
    public void The_Polar2_process_services_have_the_checks_and_SVID_writes_of_the_spec()
    {
        ModuleConfigFile config = ModuleConfigPublisher.Parse(File.ReadAllText(System.IO.Path.Combine(TestPaths.Polar2Project, "config", "module-config.json")), "module-config.json");
        ModuleConfigEntry slot = config.Modules!.Single(entry => entry!.Enabled)!;
        string Svid(uint id) => $"ModuleVariableList[{slot.VariableList!.FindIndex(item => item!.Id == id) + 1}].Value";
        string Ecid(uint id) => $"ModuleConfigParamList[{slot.ConfigParamList!.FindIndex(item => item!.Id == id) + 1}].Value";

        IReadOnlyDictionary<string, ServiceBehavior> services = ServiceBehaviorLoader.LoadIfPresent(TestPaths.Polar2Project, s_polar2.Value.Symbols, config)!.For(Module1)!.Services;

        ProcessBehavior single = services["Chamber.SingleProcess"].Process!;
        ProcessBehavior clean = services["Chamber.CupChamberClean"].Process!;
        Assert.Equal(("ProcessStepCount", 50, "ProcessSteps[i].ProcessTime"), (single.CountField, single.MaxCount, single.TimeField));
        Assert.Equal(new ProcessCycle("ProcessSteps[i].CycleControl", 1, 2, "ProcessSteps[i].CycleCount"), single.Cycle);
        Assert.Equal((9, 6), (single.Checks.Count, single.Writes.Count));
        Assert.Equal((11, 8), (clean.Checks.Count, clean.Writes.Count));
        Assert.Equal(single.Checks.Select(check => check.Field), clean.Checks.Take(9).Select(check => check.Field));
        Assert.Equal(
            single.Writes.Select(write => write.Value.Field),
            clean.Writes.Take(6).Select(write => write.Value.Field));
        Assert.Equal(new SvidTarget(Svid(53), false), single.Writes[0].Target.Choices[""]);
        Assert.Equal(new ProcessCondition("ProcessSteps[i].RotateDirection", 2), single.Writes[0].Value.NegateWhen);

        ProcessWrite armNozzle = single.Writes[1];
        Assert.Equal(9, armNozzle.Target.Choices.Count);
        Assert.Equal(["ProcessSteps[i].ArmIndex", "ProcessSteps[i].ArmNozzleIndex"], armNozzle.Target.KeyFields);
        Assert.Equal(new SvidTarget(Svid(35), false), armNozzle.Target.Choices["3,2"]);
        Assert.Equal(new SvidTarget(Svid(43), false), armNozzle.Target.Choices["4,1"]);
        Assert.True(armNozzle.DryRunZero);
        Assert.Equal(
            [Svid(54), Svid(55), Svid(56), Svid(57)],
            single.Writes.Skip(2).Select(write => write.Target.Choices[""].Target).ToArray());
        Assert.All(single.Writes.Skip(2), write => Assert.True(write.DryRunZero));

        ProcessWrite master = clean.Writes[6], slave = clean.Writes[7];
        Assert.Equal(new SvidTarget(Svid(47), true), master.Target.Choices["2"]);
        Assert.Equal(new SvidTarget(Svid(48), true), slave.Target.Choices["2"]);
        Assert.Equal(Ecid(2107), master.Value.Ecid!.Choices["2"]);
        Assert.Equal(Ecid(2107), slave.Value.Ecid!.Choices["2"]);
        Assert.Equal((true, true), (master.Keep, slave.Keep));
        Assert.Equal(new ProcessCondition("CleanSettings[i].CleanType", 1), master.When);
        Assert.All(["Chamber.ArmTest", "Chamber.FlowCalibration"], key => Assert.Null(services[key].Process));
        Assert.All(
            ["Chamber.MoveRecipe", "Chamber.EMGS", "Chamber.DoNozzleClean", "Chamber.DoNozzlePreflush"],
            key => Assert.DoesNotContain(key, services.Keys));
    }

    [Fact]
    [Trait("AC", "11")]
    public void Every_Polar2_service_entry_is_a_bound_service()
    {
        ModuleConfigFile config = ModuleConfigPublisher.Parse(File.ReadAllText(System.IO.Path.Combine(TestPaths.Polar2Project, "config", "module-config.json")), "module-config.json");
        IReadOnlyDictionary<string, ServiceBehavior> services = ServiceBehaviorLoader.LoadIfPresent(TestPaths.Polar2Project, s_polar2.Value.Symbols, config)!.For(Module1)!.Services;
        string implementation = File.ReadAllText(System.IO.Path.Combine(TestPaths.Polar2Project, "plc", "ServiceIdentity", "F_InitializePolarIIChamberServiceStatus.TcPOU"));

        ServiceIdentityTable identities = ServiceIdentityTable.Parse(implementation, name => s_polar2.Value.Symbols.TryGetEnumValue(name, out long value) ? value : null);

        Assert.All(services.Keys, key =>
        {
            ServiceIdentity? identity = identities.Find(key);
            Assert.NotNull(identity);
            Assert.NotEqual(0u, identity.ServiceId);
        });
    }

    [Fact]
    [Trait("AC", "11")]
    public void Every_batch_3_write_uses_the_SVIDs_and_ECIDs_of_its_own_device()
    {
        ModuleConfigFile config = ModuleConfigPublisher.Parse(File.ReadAllText(System.IO.Path.Combine(TestPaths.Polar2Project, "config", "module-config.json")), "module-config.json");
        ModuleConfigEntry slot = config.Modules!.Single(entry => entry!.Enabled)!;
        IReadOnlyDictionary<string, ServiceBehavior> services = ServiceBehaviorLoader.LoadIfPresent(TestPaths.Polar2Project, s_polar2.Value.Symbols, config)!.For(Module1)!.Services;
        Dictionary<string, string> devices = new() { ["Arm"] = "Arm{0} ", ["FixedNozzle"] = "FixNozzle{0} ", ["Exhaust.Fan"] = "ExhaustFan{0} " };
        Dictionary<string, string> ecidDevices = new() { ["Arm"] = "Arm{0}_", ["Exhaust.Fan"] = "ExhaustFan{0}_" };
        Dictionary<string, string> positions = new()
        {
            [".Lift.TopPos"] = "LiftTopPosition", [".Lift.DownPos"] = "LiftDownPosition",
            [".Rotate.NegLimitPos"] = "RotateNegLimitPos", [".Rotate.PosLimitPos"] = "RotatePosLimitPos",
            [".Rotate.NozzlePortPos"] = "RotateNozzlePortPosition", [".Rotate.Noz1CenterPos"] = "RotateNoz1CenterPos",
            [".Rotate.Noz2CenterPos"] = "RotateNoz2CenterPos", [".Rotate.Noz3CenterPos"] = "RotateNoz3CenterPos",
        };
        string VariableName(string path) => slot.VariableList![PositionOf(path) - 1]!.Name!;
        string EcidName(string path) => slot.ConfigParamList![PositionOf(path) - 1]!.Name!;
        (string Key, string Prefix, string? EcidPrefix)[] batch = [.. services.Keys
            .Select(key => (Key: key, Match: Regex.Match(key, @"^(Arm|FixedNozzle|Exhaust\.Fan)\[(\d)\]")))
            .Where(item => item.Match.Success && HasSvidWrites(services[item.Key]))
            .Select(item => (
                item.Key,
                string.Format(devices[item.Match.Groups[1].Value], item.Match.Groups[2].Value),
                ecidDevices.TryGetValue(item.Match.Groups[1].Value, out string? ecidFormat) ? string.Format(ecidFormat, item.Match.Groups[2].Value) : null))];

        Assert.Equal(62, batch.Length);
        foreach ((string key, string prefix, string? ecidPrefix) in batch)
        {
            ServiceBehavior service = services[key];
            IEnumerable<SvidWrite> writes = [.. service.OnEnter, .. service.BeforeComplete, .. service.OnLeave];
            Assert.All(writes, write => Assert.StartsWith(prefix, VariableName(write.Target)));
            IEnumerable<SvidWrite> ecidWrites = writes.Where(write => write.Value.Kind == SvidValueKind.Ecid);
            if (ecidPrefix is null)
                Assert.Empty(ecidWrites);
            else
                Assert.All(ecidWrites, write => Assert.StartsWith(ecidPrefix, EcidName(write.Value.Source!)));
            Assert.All(
                positions.Where(pair => key.EndsWith(pair.Key)),
                pair => Assert.EndsWith(pair.Value, EcidName(service.BeforeComplete.Single().Value.Source!)));
        }
    }

    [Fact]
    [Trait("AC", "2")]
    public void Service_behavior_ecids_follow_the_ECID_map()
    {
        string[][] occurrences = ReadGolden("EcidOccurrences.golden.tsv");
        string[][] map = ReadGolden("EcidMap.golden.tsv");
        using JsonDocument behaviors = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(TestPaths.Polar2Project, "config", "service-behaviors.json")));
        Dictionary<string, long> actual = [];
        CollectEcids(behaviors.RootElement, "", actual);

        Assert.Equal(242, occurrences.Length);
        Assert.Equal(actual.Count, occurrences.Length);
        foreach (string[] row in occurrences)
        {
            Assert.True(actual.TryGetValue(row[0], out long id), $"{row[0]} is not an ECID occurrence");
            Assert.Equal(long.Parse(row[2]), id);
        }

        Assert.Equal(61, map.Length);
        Assert.Equal(map.Select(row => long.Parse(row[4])).Order(), actual.Values.Distinct().Order());
        using JsonDocument config = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(TestPaths.Polar2Project, "config", "module-config.json")));
        Dictionary<long, JsonElement> ecids = config.RootElement.GetProperty("modules")[0].GetProperty("configParamList")
            .EnumerateArray().ToDictionary(entry => entry.GetProperty("id").GetInt64());
        foreach (string[] row in map)
        {
            JsonElement entry = ecids[long.Parse(row[4])];
            Assert.Equal(row[5], entry.GetProperty("name").GetString());
            Assert.Equal(row[2], entry.GetProperty("unit").GetString());
            Assert.Equal(row[3], entry.GetProperty("dataType").GetString());
        }
    }

    private static string[][] ReadGolden(string name) =>
        [.. File.ReadAllLines(System.IO.Path.Combine(TestPaths.SolutionRoot, "tests", "AdsPlcSimulator.Tests", "Polar2", name))
            .Where(line => line.Length > 0)
            .Select(line => line.Split('\t'))];

    private static void CollectEcids(JsonElement element, string pointer, Dictionary<string, long> found)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
                CollectEcids(item, $"{pointer}/{index++}", found);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (JsonProperty property in element.EnumerateObject())
        {
            string path = $"{pointer}/{property.Name}";
            if (property.Name == "ecid" && property.Value.ValueKind == JsonValueKind.Number)
                found.Add(path, property.Value.GetInt64());
            else if (property.Name == "ecidBy")
                foreach (JsonProperty choice in property.Value.GetProperty("values").EnumerateObject())
                    found.Add($"{path}/values/{choice.Name}", choice.Value.GetInt64());
            else
                CollectEcids(property.Value, path, found);
        }
    }

    private static int PositionOf(string path) => int.Parse(Regex.Match(path, @"\[(\d+)\]").Groups[1].Value);

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
        { Json("""{ "A": { "ranges": null } }"""), "ranges, onEnter, beforeComplete, onLeave and sim must not be null" },
        { Json("""{ "A": { "onEnter": null } }"""), "must not be null" },
        { Json("""{ "A": { "onEnter": [ null ] } }"""), "services.A.onEnter[1] is null" },
        { Json("""{ "A": { "sim": null } }"""), "sim must not be null" },
        { Json("""{ "A": { "sim": [ null ] } }"""), "services.A.sim[1] is null" },
        { Json("""{ "A": { "sim": [ { "when": "later", "target": "T", "action": "X" } ] } }"""), "services.A.sim[1]: when must be enter, beforeComplete or leave" },
        { Json("""{ "A": { "sim": [ { "target": "T", "action": "X" } ] } }"""), "services.A.sim[1]: when must be" },
        { Json("""{ "A": { "sim": [ { "when": "enter", "target": " ", "action": "X" } ] } }"""), "services.A.sim[1]: target is empty" },
        { Json("""{ "A": { "sim": [ { "when": "enter", "target": "T", "action": "" } ] } }"""), "services.A.sim[1]: action is empty" },
        { Json("""{ "A": { "sim": [ { "when": "enter", "target": "T", "action": "X" }, { "when": "leave", "target": "T", "action": "X", "value": { "constant": 1, "param": "Speed" } } ] } }"""), "services.A.sim[2]: value needs exactly one of constant, param and ecid" },
        { Json("""{ "A": { "sim": [ { "when": "enter", "target": "T", "action": "X", "value": { } } ] } }"""), "services.A.sim[1]: value needs exactly one" },
        { Json("""{ "A": { "sim": [ { "when": "enter", "target": "T", "action": "X", "value": { "constant": 1, "addParam": "Speed" } } ] } }"""), "services.A.sim[1]: value cannot use addParam" },
        { Json("""{ "A": { "sim": [ { "when": "enter", "target": "T", "action": "X", "value": { "addParam": "Speed" } } ] } }"""), "services.A.sim[1]: value cannot use addParam" },
        { Json("""{ "A": { "sim": [ { "when": "enter", "target": "T", "action": "X", "value": { "param": " " } } ] } }"""), "services.A.sim[1]: param is empty" },
        { Json("""{ "A": { "sim": [ { "when": "enter", "target": "T", "action": "X", "dryRunAction": " " } ] } }"""), "services.A.sim[1]: dryRunAction is empty" },
        { Json("""{ "A": { "sim": [ { "when": "enter", "target": "T", "action": "X", "label": 1 } ] } }"""), "invalid JSON" },
        { Json("""{ "A": { "sim": [ { "when": "enter", "target": "T", "action": "X", "value": { "ecid": 99 } } ] } }"""), "services.A.sim[1]: ECID 99 is not in module-config.json" },
        { Json("""{ "A": { "sim": [ { "when": "enter", "target": "T", "action": "X", "value": { "ecid": 12 } } ] } }"""), "services.A.sim[1]: ECID 12 is BOOL, not DINT or REAL" },
        { Json("""{ "A": { "sim": [ { "when": "enter", "target": "T", "action": "X", "value": { "param": "Nope" } } ] } }"""), "services.A.sim[1]: cannot resolve" },
        { Json("""{ "A": { "sim": [ { "when": "enter", "target": "T", "action": "X", "value": { "param": "Flag" } } ] } }"""), "services.A.sim[1]: ServiceParam.A.Flag is BOOL, not an integer or REAL" },
        { Json("""{ "A": { "ranges": [ { "field": "Nope", "min": 1, "max": 2 } ] } }"""), "services.A: cannot resolve" },
        { Json("""{ "A": { "ranges": [ { "field": "Time", "min": 1, "max": 2 } ] } }"""), "TIME, not an integer or REAL" },
        { Json("""{ "A": { "ranges": [ { "field": "Big", "min": 1, "max": 2 } ] } }"""), "ULINT, not an integer or REAL" },
        { Json("""{ "A": { "ranges": [ { "field": "Index", "min": 1.5, "max": 4 } ] } }"""), "UINT, so min and max must be integers" },
        { Json("""{ "A": { "ranges": [ { "field": "Index", "min": 1, "max": 1e16 } ] } }"""), "so min and max must be integers" },
        { Json("""{ "A": { "ranges": [ { "field": "Index", "min": 1, "max": 9007199254740994 } ] } }"""), "so min and max must be integers from -9007199254740992 to 9007199254740992" },
        { Json("""{ "A": { "ranges": [ { "field": "Angle", "min": 1e999, "max": 4 } ] } }"""), "services.A.ranges[1]: min and max must be finite numbers" },
        { Json("""{ "A": { "ranges": [ { "field": "Angle", "min": 0, "max": -1e999 } ] } }"""), "min and max must be finite numbers" },
        { Json("""{ "A": { "onEnter": [ { "svid": 5, "value": { "addParam": " " } } ] } }"""), "services.A.onEnter[1]: addParam is empty" },
        { Json("""{ "A": { "onEnter": [ { "svid": 5, "value": { "addParam": "Flag" } } ] } }"""), "BOOL, not an integer or REAL" },
        { Json("""{ "A": { "onEnter": [ { "svid": 5, "value": { "constant": 1, "addParam": "Speed" } } ] } }"""), "exactly one of constant, param, ecid and addParam" },
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
        { Json("""{ "A": { "onEnter": [ { "svid": 5, "value": {} } ] } }"""), "exactly one of constant, param, ecid and addParam" },
        { Json("""{ "A": { "onEnter": [ { "svid": 5, "value": { "constant": 1, "ecid": 10 } } ] } }"""), "exactly one of constant, param, ecid and addParam" },
        { Json("""{ "A": { "onEnter": [ { "svid": 5, "value": { "param": " " } } ] } }"""), "param is empty" },
        { Json("""{ "A": { "onEnter": [ { "svid": 5, "value": { "param": "Flag" } } ] } }"""), "BOOL, not an integer or REAL" },
        { Json("""{ "A": { "onEnter": [ { "svid": 5, "value": { "constant": 2.5 } } ] } }"""), "does not fit a DINT SVID" },
        { Json("""{ "A": { "onEnter": [ { "svid": 5, "value": { "constant": 3e9 } } ] } }"""), "does not fit a DINT SVID" },
        { Json("""{ "A": { "onEnter": [ { "svid": 5, "value": { "constant": 1e999 } } ] } }"""), "services.A.onEnter[1]: constant must be a finite number" },
        { Json("""{ "A": { "ranges": [ { "field": "Index", "min": 1 } ] } }"""), "services.A.ranges[1]: min and max are required" },
        { Json(modes: """[{"value":0,"name":"x","deny":[" "]}]"""), "modes[1].deny contains an empty service name" },
        { Json(modes: """[{"value":0,"name":"x","deny":["A","a"]}]"""), "modes[1].deny: 'a' appears more than once" },
        { Json(modes: """[{"value":0,"name":"x","allow":[""]}]"""), "modes[1].allow contains an empty service name" },
        { Json("""{ "A": { "beforeComplete": [ { "svid": 0, "value": { "constant": 1 } } ] } }"""), "services.A.beforeComplete[1]: svid must be greater than 0" },
        { Json("""{ "A": { "onLeave": [ { "svid": 0, "value": { "constant": 1 } } ] } }"""), "services.A.onLeave[1]: svid must be greater than 0" },
        { Json("""{ "A": { "beforeComplete": [ { "svid": 99, "value": { "constant": 1 } } ] } }"""), "services.A.beforeComplete[1]: SVID 99 is not in module-config.json" },
        { Json("""{ "A": { "onLeave": [ { "svid": 99, "value": { "constant": 1 } } ] } }"""), "services.A.onLeave[1]: SVID 99 is not in module-config.json" },
        { Json("""{ "A": { "onEnter": [ { "svid": 5, "value": { "constant": 2147483648 } } ] } }"""), "does not fit a DINT SVID" },
        { Json("""{ "A": { "duration": "Time", "process": { "count": "N", "maxCount": 5, "time": "Steps[i].Time" } } }"""), "services.A: process and duration cannot both be set." },
        { Proc(head: """ "maxCount": 5, "time": "Steps[i].Time" """), "services.A.process: count and time are required." },
        { Proc(head: """ "count": "N", "maxCount": 5, "time": " " """), "services.A.process: count and time are required." },
        { Proc(head: """ "count": "N", "maxCount": 0, "time": "Steps[i].Time" """), "services.A.process: maxCount must be 1 or more." },
        { Proc(head: """ "count": "N", "time": "Steps[i].Time" """), "services.A.process: maxCount must be 1 or more." },
        { Proc(checks: "null"), "services.A.process: checks and writes must not be null." },
        { Proc(writes: "null"), "services.A.process: checks and writes must not be null." },
        { Proc(checks: "[ null ]"), "services.A.process.checks[1] is null." },
        { Proc(writes: "[ null ]"), "services.A.process.writes[1] is null." },
        { Proc(cycle: """{ "control": "Steps[i].Kind", "start": 1, "count": "Steps[i].Pick" }"""), "services.A.process.cycle: control, start, end and count are required and start must differ from end." },
        { Proc(cycle: """{ "control": "Steps[i].Kind", "start": 1, "end": 1, "count": "Steps[i].Pick" }"""), "services.A.process.cycle: control, start, end and count are required and start must differ from end." },
        { Proc(cycle: """{ "control": " ", "start": 1, "end": 2, "count": "Steps[i].Pick" }"""), "services.A.process.cycle: control, start, end and count are required" },
        { Proc(checks: """[ { "field": " ", "min": 1, "max": 2 } ]"""), "services.A.process.checks[1]: field is empty." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "greaterThan": 0, "max": 2 } ]"""), "services.A.process.checks[1]: greaterThan cannot be used with min, max, maxField or maxBy." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "greaterThan": 0, "min": 0 } ]"""), "services.A.process.checks[1]: greaterThan cannot be used with min, max, maxField or maxBy." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "min": 1, "maxField": "Steps[i].Pick" } ]"""), "services.A.process.checks[1]: maxField must not contain [i]." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "max": 2 } ]"""), "services.A.process.checks[1]: min is required." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "min": 1 } ]"""), "services.A.process.checks[1]: exactly one of max, maxField and maxBy is required." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "min": 1, "max": 2, "maxField": "Limit" } ]"""), "services.A.process.checks[1]: exactly one of max, maxField and maxBy is required." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "min": 1e999, "max": 2 } ]"""), "services.A.process.checks[1]: min, max and greaterThan must be finite numbers." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "greaterThan": 1e999 } ]"""), "services.A.process.checks[1]: min, max and greaterThan must be finite numbers." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "min": 3, "max": 2 } ]"""), "services.A.process.checks[1]: min is greater than max." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "min": 1, "maxBy": { "key": [], "values": { "1": 2 } } } ]"""), "services.A.process.checks[1].maxBy: key is empty or has an empty field." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "min": 1, "maxBy": { "key": [ " " ], "values": { "1": 2 } } } ]"""), "services.A.process.checks[1].maxBy: key is empty or has an empty field." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "min": 1, "maxBy": { "key": [ "Steps[i].Kind", " " ], "values": { "1,1": 2 } } } ]"""), "services.A.process.checks[1].maxBy: key is empty or has an empty field." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "min": 1, "maxBy": { "key": [ "Steps[i].Kind" ], "values": {} } } ]"""), "services.A.process.checks[1].maxBy: values is empty." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "min": 1, "maxBy": { "key": [ "Steps[i].Kind" ], "values": { "1,3": 2 } } } ]"""), "services.A.process.checks[1].maxBy: '1,3' is not 1 integers separated by ','." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "min": 1, "maxBy": { "key": [ "Steps[i].Kind" ], "values": { "01": 2 } } } ]"""), "services.A.process.checks[1].maxBy: '01' is not 1 integers separated by ','." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "min": 1, "maxBy": { "key": [ "Steps[i].Kind" ], "values": { "+1": 2 } } } ]"""), "'+1' is not 1 integers separated by ','." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "min": 1, "maxBy": { "key": [ "Steps[i].Kind", "Steps[i].Pick" ], "values": { "1, 3": 2 } } } ]"""), "'1, 3' is not 2 integers separated by ','." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "min": 1, "maxBy": { "key": [ "Steps[i].Kind" ], "values": { "x": 2 } } } ]"""), "'x' is not 1 integers separated by ','." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "greaterThan": 0, "when": { "field": "Steps[i].Dir" } } ]"""), "services.A.process.checks[1].when: field and equals are required." },
        { Proc(writes: """[ { "svid": 5, "svidBy": { "key": [ "Steps[i].Kind" ], "values": { "1": 5 } }, "value": { "field": "Steps[i].Speed" } } ]"""), "services.A.process.writes[1]: exactly one of svid and svidBy is required." },
        { Proc(writes: """[ { "value": { "field": "Steps[i].Speed" } } ]"""), "services.A.process.writes[1]: exactly one of svid and svidBy is required." },
        { Proc(writes: """[ { "svid": 0, "value": { "field": "Steps[i].Speed" } } ]"""), "services.A.process.writes[1]: svid must be greater than 0." },
        { Proc(writes: """[ { "svid": 5 } ]"""), "services.A.process.writes[1]: value is missing." },
        { Proc(writes: """[ { "svid": 5, "value": {} } ]"""), "services.A.process.writes[1].value: exactly one of field, ecid and ecidBy is required." },
        { Proc(writes: """[ { "svid": 5, "value": { "field": "Steps[i].Speed", "ecid": 10 } } ]"""), "services.A.process.writes[1].value: exactly one of field, ecid and ecidBy is required." },
        { Proc(writes: """[ { "svid": 5, "value": { "field": " " } } ]"""), "services.A.process.writes[1].value: field is empty." },
        { Proc(writes: """[ { "svid": 5, "value": { "ecidBy": { "key": [ "Steps[i].Kind" ], "values": {} } } } ]"""), "services.A.process.writes[1].value.ecidBy: values is empty." },
        { Proc(writes: """[ { "svidBy": { "key": [ "Steps[i].Kind" ], "values": {} }, "value": { "field": "Steps[i].Speed" } } ]"""), "services.A.process.writes[1].svidBy: values is empty." },
        { Proc(writes: """[ { "svid": 5, "value": { "field": "Steps[i].Speed" }, "when": { "equals": 1 } } ]"""), "services.A.process.writes[1].when: field and equals are required." },
        { Proc(writes: """[ { "svid": 5, "value": { "field": "Steps[i].Speed", "negateWhen": { "field": "Steps[i].Dir" } } } ]"""), "services.A.process.writes[1].value.negateWhen: field and equals are required." },
        { Proc(head: """ "count": "N", "maxCount": 5, "time": "Steps[i].Kind" """), "Steps[1].Kind is UINT, not a TIME." },
        { Proc(head: """ "count": "Steps[1].Speed", "maxCount": 5, "time": "Steps[i].Time" """), "Steps[1].Speed is REAL, not an integer or enum." },
        { Proc(head: """ "count": "N", "maxCount": 6, "time": "Steps[i].Time" """), "services.A.process: cannot resolve GVL_Module.Chamber_Control[1].ServiceParam.A.Steps[6].Time" },
        { Proc(cycle: """{ "control": "Steps[i].Speed", "start": 1, "end": 2, "count": "Steps[i].Pick" }"""), "services.A.process.cycle: ServiceParam.A.Steps[1].Speed is REAL, not an integer or enum." },
        { Proc(cycle: """{ "control": "Steps[i].Kind", "start": 1, "end": 2, "count": "Steps[i].Speed" }"""), "Steps[1].Speed is REAL, not an integer or enum." },
        { Proc(checks: """[ { "field": "Flag", "min": 1, "max": 2 } ]"""), "Flag is BOOL, not a number, enum or TIME." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "min": 1, "max": 2 }, { "field": "Flag", "min": 1, "max": 2 } ]"""), "services.A.process.checks[2]: ServiceParam.A.Flag is BOOL, not a number, enum or TIME." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "min": 1, "maxField": "Flag" } ]"""), "Flag is BOOL, not a number." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "min": 1, "maxBy": { "key": [ "Steps[i].Speed" ], "values": { "1": 2 } } } ]"""), "services.A.process.checks[1].maxBy: ServiceParam.A.Steps[1].Speed is REAL, not an integer or enum." },
        { Proc(checks: """[ { "field": "Steps[i].Kind", "greaterThan": 0, "when": { "field": "Steps[i].Speed", "equals": 1 } } ]"""), "services.A.process.checks[1].when: ServiceParam.A.Steps[1].Speed is REAL, not an integer or enum." },
        { Proc(writes: """[ { "svid": 5, "value": { "field": "Flag" } } ]"""), "services.A.process.writes[1].value: ServiceParam.A.Flag is BOOL, not a number or enum." },
        { Proc(writes: """[ { "svid": 5, "value": { "field": "Steps[i].Speed", "negateWhen": { "field": "Steps[i].Speed", "equals": 1 } } } ]"""), "services.A.process.writes[1].value.negateWhen: ServiceParam.A.Steps[1].Speed is REAL, not an integer or enum." },
        { Proc(writes: """[ { "svidBy": { "key": [ "Steps[i].Speed" ], "values": { "1": 5 } }, "value": { "field": "Steps[i].Speed" } } ]"""), "services.A.process.writes[1].svidBy: ServiceParam.A.Steps[1].Speed is REAL, not an integer or enum." },
        { Proc(writes: """[ { "svid": 99, "value": { "field": "Steps[i].Speed" } } ]"""), "services.A.process.writes[1]: SVID 99 is not in module-config.json" },
        { Proc(writes: """[ { "svid": 7, "value": { "field": "Steps[i].Speed" } } ]"""), "SVID 7 is STRING, not DINT or REAL" },
        { Proc(writes: """[ { "svidBy": { "key": [ "Steps[i].Kind" ], "values": { "1": 99 } }, "value": { "field": "Steps[i].Speed" } } ]"""), "SVID 99 is not in module-config.json" },
        { Proc(writes: """[ { "svid": 5, "value": { "ecid": 99 } } ]"""), "ECID 99 is not in module-config.json" },
        { Proc(writes: """[ { "svid": 5, "value": { "ecidBy": { "key": [ "Steps[i].Kind" ], "values": { "1": 12 } } } } ]"""), "ECID 12 is BOOL, not DINT or REAL" },
        { Proc(writes: """[ { "svid": 5, "value": { "ecidBy": { "key": [ "Nope[i]" ], "values": { "1": 10 } } } } ]"""), "services.A.process.writes[1].value.ecidBy: cannot resolve" },
    };

    [Theory]
    [Trait("AC", "2")]
    [Trait("AC", "3")]
    [MemberData(nameof(Failures))]
    public void An_invalid_file_fails_the_Run_with_the_place_and_the_reason(string json, string expected)
    {
        var exception = Assert.Throws<ModuleConfigException>(() => Build(json));

        Assert.Contains(expected, exception.Message);
        Assert.Contains(Path, exception.Message);
    }

    private const string ProcessHead = """ "count": "N", "maxCount": 5, "time": "Steps[i].Time" """;

    private static string Proc(string checks = "[]", string writes = "[]", string cycle = "", string head = ProcessHead) =>
        Json($$"""{ "A": { "process": { {{head}}, "checks": {{checks}}, "writes": {{writes}}{{(cycle.Length == 0 ? "" : ", \"cycle\": " + cycle)}} } } }""");

    [Fact]
    [Trait("AC", "2")]
    public void A_process_section_is_built_with_paths_as_written_and_SVIDs_typed_by_module_config()
    {
        const string cycle = """{ "control": "Steps[i].Kind", "start": 1, "end": 2, "count": "Steps[i].Pick" }""";
        const string checks = """
            [ { "field": "Steps[i].Kind", "min": 1, "maxField": "Limit" },
              { "field": "Steps[i].Pick", "min": 1, "maxBy": { "key": [ "Steps[i].Kind" ], "values": { "1": 3 } } },
              { "field": "Steps[i].Time", "min": 1, "max": 9 },
              { "field": "Steps[i].Speed", "greaterThan": 0, "when": { "field": "Steps[i].Dir", "equals": 2 } } ]
            """;
        const string writes = """
            [ { "svid": 5, "value": { "field": "Steps[i].Speed", "negateWhen": { "field": "Steps[i].Dir", "equals": 2 } } },
              { "svidBy": { "key": [ "Steps[i].Kind", "Steps[i].Pick" ], "values": { "1,1": 5, "1,2": 6 } },
                "value": { "ecidBy": { "key": [ "Steps[i].Kind" ], "values": { "1": 10, "2": 11 } } },
                "dryRunZero": true, "keep": true, "when": { "field": "Steps[i].Dir", "equals": 1 } },
              { "svid": 6, "value": { "ecid": 11 } } ]
            """;

        ProcessBehavior process = Module(Proc(checks, writes, cycle)).Services["A"].Process!;

        Assert.Equal(("N", 5, "Steps[i].Time"), (process.CountField, process.MaxCount, process.TimeField));
        Assert.Equal(new ProcessCycle("Steps[i].Kind", 1, 2, "Steps[i].Pick"), process.Cycle);
        Assert.Equal(("Steps[i].Kind", ProcessCheckKind.Range, 1d, "Limit"), (process.Checks[0].Field, process.Checks[0].Kind, process.Checks[0].Min, process.Checks[0].Max!.Field));
        KeyedChoice<double> lookup = process.Checks[1].Max!.Lookup!;
        Assert.Equal(["Steps[i].Kind"], lookup.KeyFields);
        Assert.Equal(3d, lookup.Choices["1"]);
        Assert.Equal(9d, process.Checks[2].Max!.Constant);
        Assert.Equal((ProcessCheckKind.GreaterThan, 0d, null), (process.Checks[3].Kind, process.Checks[3].Min, process.Checks[3].Max));
        Assert.Equal(new ProcessCondition("Steps[i].Dir", 2), process.Checks[3].When);

        Assert.Equal(new SvidTarget("ModuleVariableList[1].Value", false), process.Writes[0].Target.Choices.Single().Value);
        Assert.Equal("", process.Writes[0].Target.Choices.Single().Key);
        Assert.Empty(process.Writes[0].Target.KeyFields);
        Assert.Equal(new ProcessValue(ProcessValueKind.Field, "Steps[i].Speed", null, new ProcessCondition("Steps[i].Dir", 2)), process.Writes[0].Value);
        Assert.Equal(["Steps[i].Kind", "Steps[i].Pick"], process.Writes[1].Target.KeyFields);
        Assert.Equal(new SvidTarget("ModuleVariableList[1].Value", false), process.Writes[1].Target.Choices["1,1"]);
        Assert.Equal(new SvidTarget("ModuleVariableList[2].Value", true), process.Writes[1].Target.Choices["1,2"]);
        Assert.Equal("ModuleConfigParamList[2].Value", process.Writes[1].Value.Ecid!.Choices["2"]);
        Assert.Equal((true, true, new ProcessCondition("Steps[i].Dir", 1)), (process.Writes[1].DryRunZero, process.Writes[1].Keep, process.Writes[1].When));
        Assert.Equal("ModuleConfigParamList[2].Value", process.Writes[2].Value.Ecid!.Choices[""]);
        Assert.False(process.Writes[2].Keep);
    }

    [Fact]
    [Trait("AC", "3")]
    public void A_service_without_a_process_section_has_no_Process()
    {
        Assert.Null(Module(Json("""{ "A": {} }""")).Services["A"].Process);
    }

    [Fact]
    [Trait("AC", "3")]
    public void An_enum_field_passes_the_type_checks_of_control_condition_and_value()
    {
        const string cycle = """{ "control": "Steps[i].Dir", "start": 1, "end": 2, "count": "Steps[i].Pick" }""";
        const string checks = """[ { "field": "Steps[i].Dir", "min": 1, "max": 2, "when": { "field": "Steps[i].Dir", "equals": 1 } } ]""";
        const string writes = """[ { "svid": 5, "value": { "field": "Steps[i].Dir", "negateWhen": { "field": "Steps[i].Dir", "equals": 2 } } } ]""";

        ProcessBehavior process = Module(Proc(checks, writes, cycle)).Services["A"].Process!;

        Assert.Equal("Steps[i].Dir", process.Cycle!.ControlField);
        Assert.Equal("Steps[i].Dir", process.Writes[0].Value.Field);
    }

    [Fact]
    [Trait("AC", "2")]
    public void A_range_with_min_equal_to_max_is_accepted()
    {
        const string services = """{ "A": { "ranges": [ { "field": "Index", "min": 3, "max": 3 } ] } }""";

        Assert.Equal(new ParamRange("Index", 3, 3), Module(Json(services)).Services["A"].Ranges.Single());
    }

    [Fact]
    [Trait("AC", "3")]
    public void An_integer_range_takes_bounds_of_plus_and_minus_2_pow_53()
    {
        const string services = """{ "A": { "ranges": [ { "field": "Index", "min": -9007199254740992, "max": 9007199254740992 } ] } }""";

        Assert.Equal(new ParamRange("Index", -9007199254740992, 9007199254740992), Module(Json(services)).Services["A"].Ranges.Single());
    }

    [Theory]
    [Trait("AC", "2")]
    [InlineData(2147483647)]
    [InlineData(-2147483648)]
    public void A_DINT_SVID_takes_a_constant_at_the_DINT_limits(long constant)
    {
        string services = $$"""{ "A": { "onEnter": [ { "svid": 5, "value": { "constant": {{constant}} } } ] } }""";

        Assert.Equal((double)constant, Module(Json(services)).Services["A"].OnEnter.Single().Value.Constant);
    }

    [Theory]
    [Trait("AC", "3")]
    [InlineData("Speed", 0.5, 2.5)]
    [InlineData("Angle", 0, 360)]
    [InlineData("Angle", -0.25, 1e300)]
    public void A_REAL_or_LREAL_field_takes_fractional_bounds(string field, double min, double max)
    {
        string range = string.Create(System.Globalization.CultureInfo.InvariantCulture, $$"""{ "field": "{{field}}", "min": {{min}}, "max": {{max}} }""");
        string services = $$"""{ "A": { "ranges": [ {{range}} ] } }""";

        Assert.Equal(new ParamRange(field, min, max), Module(Json(services)).Services["A"].Ranges.Single());
    }

    [Fact]
    [Trait("AC", "7")]
    public void An_addParam_value_is_built_with_the_field_and_the_Negate_flag()
    {
        const string services = """
            { "A": { "beforeComplete": [
                { "svid": 6, "value": { "addParam": "Speed" } },
                { "svid": 5, "value": { "addParam": "Angle", "negate": true } } ] } }
            """;

        IReadOnlyList<SvidWrite> writes = Module(Json(services)).Services["A"].BeforeComplete;

        Assert.Equal(new SvidValue(SvidValueKind.AddParam, 0, "Speed", false), writes[0].Value);
        Assert.Equal(new SvidValue(SvidValueKind.AddParam, 0, "Angle", true), writes[1].Value);
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

    [Fact]
    [Trait("AC", "1")]
    public void A_sim_list_is_built_with_the_moment_the_value_source_and_the_dry_run_action()
    {
        string json = Json("""
            { "A": { "sim": [
                { "when": "ENTER", "target": "Holder", "action": "CW", "value": { "ecid": 10 } },
                { "when": "beforeComplete", "target": "Holder", "action": "CW", "value": { "param": "Speed", "negate": true } },
                { "when": "leave", "target": "Holder", "action": "Stop" },
                { "when": "enter", "target": "Nozzle", "action": "On", "value": { "constant": 1.5 }, "dryRunAction": "Off" } ] },
              "B": {} }
            """);

        IReadOnlyDictionary<string, ServiceBehavior> services = Module(json).Services;

        Assert.Equal(
            [
                new SimAction(SimMoment.Enter, "Holder", "CW", new SvidValue(SvidValueKind.Ecid, 0, "ModuleConfigParamList[1].Value", false), null),
                new SimAction(SimMoment.BeforeComplete, "Holder", "CW", new SvidValue(SvidValueKind.Param, 0, "Speed", true), null),
                new SimAction(SimMoment.Leave, "Holder", "Stop", null, null),
                new SimAction(SimMoment.Enter, "Nozzle", "On", new SvidValue(SvidValueKind.Constant, 1.5, null, false), "Off"),
            ],
            services["A"].Sim);
    }

    [Fact]
    [Trait("AC", "1")]
    public void A_service_without_sim_has_an_empty_sim_list()
    {
        IReadOnlyDictionary<string, ServiceBehavior> services = Module(Json("""{ "B": { "onLeave": [] } }""")).Services;

        Assert.NotNull(services["B"].Sim);
        Assert.Empty(services["B"].Sim!);
    }
}
