using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdsPlcSimulator.Core.Flow;
using AdsPlcSimulator.Core.PackML;
using AdsPlcSimulator.Core.PackML.Behavior;
using AdsPlcSimulator.Core.Plc;
using AdsPlcSimulator.Host.Project;
using static AdsPlcSimulator.Tests.TestWait;

namespace AdsPlcSimulator.Tests.Polar2;

/// <summary>Loads the real workflow files of projects/Polar2Chamber; only the waiting times are shortened in memory.</summary>
public sealed partial class Polar2WorkflowTests : IAsyncLifetime
{
    private const string Root = "GVL_Module.Chamber_Control";
    private const string Module1 = Root + "[1]";
    private const string SvidValue = Module1 + ".ModuleVariableList[1].Value";
    private const string ShortDefaultDelayMs = "200";

    private static readonly Lazy<PlcProgram> s_program = new(() => PlcProgram.LoadDirectory(Path.Combine(TestPaths.Polar2Project, "plc")));

    private readonly RecordingMonitor _monitor = new();
    private PlcProgram _program = null!;
    private ProcessImage _image = null!;
    private FlowRuntime _runtime = null!;

    public Task InitializeAsync()
    {
        _program = s_program.Value;
        _image = _program.CreateProcessImage();
        ModuleConfigPublisher.PublishIfPresent(TestPaths.Polar2Project, _program.Symbols, _image);
        _runtime = new FlowRuntime(_image, _program.Symbols, NodeCatalog.Default, _monitor, new NoSimulation(), TestPaths.Polar2Project);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _runtime.DisposeAsync();

    [GeneratedRegex(@"ServiceStatus\.(?<rel>[\w.\[\]]+?)(?:\.CompletionBehavior\s*:=|\s*,)")]
    private static partial Regex IdentityPathPattern();

    [GeneratedRegex(@"(\w+)\s*:=\s*(\d+)")]
    private static partial Regex EnumMemberPattern();

    private static WorkflowDocument LoadDocument(string name) =>
        WorkflowDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Polar2Project, "workflows", name + ".flow.json")));

    private static WorkflowDocument HostDocument()
    {
        WorkflowDocument document = LoadDocument("Polar2_ServiceHost");
        document.Nodes.Single(node => node.Type == "service.host").Params["defaultDelayMs"] = ShortDefaultDelayMs;
        return document;
    }

    private static WorkflowDocument InitialDocument(string delayMs)
    {
        WorkflowDocument document = LoadDocument("Polar2_Chamber_Initial");
        document.Nodes.Single(node => node.Type == "flow.delay").Params["ms"] = delayMs;
        return document;
    }

    private static WorkflowDocument SetModeWithHoldBeforeComplete(string delayMs)
    {
        WorkflowDocument document = LoadDocument("Polar2_Chamber_SetMode");
        document.Links.RemoveAll(link => link is { FromNode: "branch", FromPort: "true" });
        document.Nodes.Add(new WorkflowNode { Id = "hold", Type = "flow.delay", Params = { ["ms"] = delayMs } });
        document.Links.Add(new WorkflowLink("branch", "true", "hold", "exec"));
        document.Links.Add(new WorkflowLink("hold", "then", "complete", "exec"));
        return document;
    }

    private object Read(string path) => _image.ReadValue(_program.Symbols.Resolve(path));

    private long ReadNumber(string path) => Convert.ToInt64(Read(path));

    private void Write(string path, object value) => _image.WriteValue(_program.Symbols.Resolve(path), value, WriteSource.Client);

    private static string Ctrl(string service) => $"{Module1}.ServiceCtrl.{service}";

    private static string Status(string service) => $"{Module1}.ServiceStatus.{service}";

    private PackMLState State(string service) => (PackMLState)ReadNumber($"{Status(service)}.PackMLOut.eStateCurrent");

    private Task WaitForStateAsync(string service, PackMLState expected) =>
        WaitUntilAsync(() => State(service) == expected, $"{service} to be {expected}");

    private void Load(string name, WorkflowDocument document) => Assert.Empty(_runtime.Load(name, document));

    private async Task StartPlcAsync()
    {
        Load("host", HostDocument());
        Load("modeinit", LoadDocument("Polar2_ModuleModeInit"));
        await WaitForRunAsync("modeinit", FlowEventKind.RunCompleted);
        await WaitForStateAsync("Chamber.SetMode", PackMLState.Idle);
        await WaitForStateAsync("Chamber.Initial", PackMLState.Idle);
    }

    private async Task CommandAsync(string service, PackMLCommand command, PackMLState expected)
    {
        Write($"{Ctrl(service)}.RequestId", 1L);
        Write($"{Ctrl(service)}.PackMLIn.eCommand", (long)command);
        Write($"{Ctrl(service)}.PackMLIn.bCommandChangeRequest", true);
        await WaitForStateAsync(service, expected);

        Write($"{Ctrl(service)}.PackMLIn.bCommandChangeRequest", false);
        await WaitUntilAsync(() => !(bool)Read($"{Status(service)}.PackMLOut.bCommandAccepted"), "the accepted flag to clear");
    }

    private Task<FlowEvent> WaitForRunAsync(string workflow, FlowEventKind kind) =>
        _monitor.WaitForAsync(flowEvent => flowEvent.Kind == kind && flowEvent.Workflow == workflow);

    [Fact]
    [Trait("AC", "2")]
    [Trait("AC", "11")]
    public void Polar2_workflows_use_five_second_hardware_delay()
    {
        WorkflowNode host = LoadDocument("Polar2_ServiceHost").Nodes.Single(node => node.Type == "service.host");
        WorkflowNode delay = LoadDocument("Polar2_Chamber_Initial").Nodes.Single(node => node.Type == "flow.delay");

        Assert.Equal("5000", host.Params["defaultDelayMs"]);
        Assert.Equal("5000", delay.Params["ms"]);
    }

    [Fact]
    [Trait("AC", "2")]
    public async Task Polar2_host_binds_146_services_per_module()
    {
        Load("host", HostDocument());
        await WaitUntilAsync(() => _runtime.Registry.FindByService(Ctrl("Shutter.Open")) is not null, "the host to register");

        IReadOnlyList<string> bound = _runtime.Registry.FindByService(Ctrl("Shutter.Open"))!.BoundServices;

        Assert.Equal(6 * 146, bound.Count);
        for (int module = 1; module <= 6; module++)
            Assert.Equal(146, bound.Count(path => path.StartsWith($"{Root}[{module}].ServiceCtrl.", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    [Trait("AC", "2")]
    public async Task Polar2_initialize_writes_identity_of_all_166_slots()
    {
        string identityFile = File.ReadAllText(Path.Combine(TestPaths.Polar2Project, "plc", "ServiceIdentity", "F_InitializePolarIIChamberServiceStatus.TcPOU"));
        ServiceIdentityTable table = ServiceIdentityTable.Parse(identityFile, name => _program.Symbols.TryGetEnumValue(name, out long value) ? value : null);
        List<string> slots = IdentityPathPattern().Matches(identityFile).Select(match => match.Groups["rel"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(166, slots.Count);
        Assert.Equal(146, slots.Count(slot => table.Find(slot)!.ServiceId != 0));

        Load("host", HostDocument());
        await WaitUntilAsync(() => ReadNumber($"{Root}[6].ModuleStatus.PackMLOut.eStateCurrent") == (long)PackMLState.Execute, "the last module to initialize");

        for (int module = 1; module <= 6; module++)
        {
            foreach (string slot in slots)
            {
                ServiceIdentity expected = table.Find(slot)!;
                Assert.Equal(expected.ServiceId, (uint)ReadNumber($"{Root}[{module}].ServiceStatus.{slot}.ServiceId"));
                Assert.Equal((long)expected.CompletionBehavior, ReadNumber($"{Root}[{module}].ServiceStatus.{slot}.CompletionBehavior"));
            }
        }

        Assert.True(_program.Symbols.TryGetEnumValue("E_PolarIIChamber_ServiceId.Shutter_Open", out long shutterOpen));
        Assert.False(_program.Symbols.TryGetEnumValue("E_PolarIIChamberServiceId.ShutterOpen", out _));
        Assert.Equal(shutterOpen, ReadNumber($"{Status("Shutter.Open")}.ServiceId"));
        Assert.Equal(0, ReadNumber($"{Root}[1].ServiceStatus.Arm[3].Rotate.NozzlePortPos.ServiceId"));
        Assert.Equal((long)CompletionBehavior.NaturalCompletion, ReadNumber($"{Root}[1].ServiceStatus.Arm[3].Rotate.NozzlePortPos.CompletionBehavior"));
    }

    [Fact]
    [Trait("AC", "1")]
    public async Task Holder_vacuum_establish_has_service_id_3007_and_completes()
    {
        await StartPlcAsync();

        Assert.True(_program.Symbols.TryGetEnumValue("E_PolarIIChamber_ServiceId.Holder_VacuumEstablish", out long serviceId));
        Assert.Equal(3007, serviceId);
        for (int module = 1; module <= 6; module++)
        {
            Assert.Equal(3007, ReadNumber($"{Root}[{module}].ServiceStatus.Holder.VacuumEstablish.ServiceId"));
            Assert.Equal((long)CompletionBehavior.NaturalCompletion, ReadNumber($"{Root}[{module}].ServiceStatus.Holder.VacuumEstablish.CompletionBehavior"));
        }

        await CommandAsync("Holder.VacuumEstablish", PackMLCommand.Start, PackMLState.Complete);
    }

    [Fact]
    [Trait("AC", "2")]
    public void Service_id_enum_values_are_unique()
    {
        string enumFile = File.ReadAllText(Path.Combine(TestPaths.Polar2Project, "plc", "Polar2", "DUTs", "External", "Enums", "E_PolarIIChamber_ServiceId.TcDUT"));
        List<(string Name, long Value)> members = EnumMemberPattern().Matches(enumFile)
            .Select(match => (match.Groups[1].Value, long.Parse(match.Groups[2].Value)))
            .ToList();

        Assert.Equal(146, members.Count);
        Assert.Equal(146, members.Select(member => member.Value).Distinct().Count());
        Assert.Equal(146, members.Select(member => member.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach ((string name, long value) in members)
        {
            Assert.True(_program.Symbols.TryGetEnumValue($"E_PolarIIChamber_ServiceId.{name}", out long resolved), name);
            Assert.Equal(value, resolved);
        }
    }

    [Fact]
    [Trait("AC", "2")]
    public void Service_ids_match_golden_list()
    {
        string identityFile = File.ReadAllText(Path.Combine(TestPaths.Polar2Project, "plc", "ServiceIdentity", "F_InitializePolarIIChamberServiceStatus.TcPOU"));
        ServiceIdentityTable table = ServiceIdentityTable.Parse(identityFile, name => _program.Symbols.TryGetEnumValue(name, out long value) ? value : null);
        string[] golden = File.ReadAllLines(Path.Combine(TestPaths.SolutionRoot, "tests", "AdsPlcSimulator.Tests", "Polar2", "ServiceIds.golden.tsv"));

        Assert.Equal(166, golden.Length);
        foreach (string line in golden)
        {
            string[] columns = line.Split('\t');
            Assert.True(table.Find(columns[0]) is not null, $"{columns[0]} has no identity");
            Assert.Equal(uint.Parse(columns[1]), table.Find(columns[0])!.ServiceId);
        }
    }

    [Fact]
    [Trait("AC", "3")]
    public async Task ModuleModeInit_sets_module_mode_of_slot_1()
    {
        Assert.Equal("0", Read(SvidValue));

        Load("modeinit", LoadDocument("Polar2_ModuleModeInit"));
        await WaitForRunAsync("modeinit", FlowEventKind.RunCompleted);

        string svid = $"{Module1}.ModuleVariableList[1]";
        Assert.Equal("1", Read($"{svid}.Value"));
        Assert.Equal(1, ReadNumber($"{svid}.Id"));
        Assert.Equal("Module Mode", Read($"{svid}.Name"));
        Assert.Equal(1, ReadNumber($"{svid}.DataType"));
        Assert.Equal(0, ReadNumber($"{Root}[6].ModuleVariableList[1].Id"));
        Assert.Equal(0, ReadNumber($"{Root}[6].ModuleStatus.Variable.PublishedCount"));
    }

    [Fact]
    [Trait("AC", "11")]
    public void Module_config_publishes_identity_of_slot_1_only()
    {
        Assert.Equal(1, ReadNumber($"{Root}[1].ModuleStatus.ModuleId"));
        Assert.True((bool)Read($"{Root}[1].ModuleStatus.Enabled"));
        Assert.Equal("Polar2", Read($"{Root}[1].ModuleStatus.ModuleType"));

        for (int module = 2; module <= 6; module++)
        {
            Assert.Equal(0, ReadNumber($"{Root}[{module}].ModuleStatus.ModuleId"));
            Assert.False((bool)Read($"{Root}[{module}].ModuleStatus.Enabled"));
            Assert.Equal("", Read($"{Root}[{module}].ModuleStatus.ModuleType"));
        }
    }

    [Theory]
    [Trait("AC", "10")]
    [InlineData(0, "0")]
    [InlineData(1, "1")]
    [InlineData(2, "2")]
    [InlineData(3, "3")]
    public async Task SetMode_writes_target_mode_to_svid(long targetMode, string expected)
    {
        await StartPlcAsync();
        Load("setmode", LoadDocument("Polar2_Chamber_SetMode"));

        Write($"{Module1}.ServiceParam.Chamber.SetMode.TargetMode", targetMode);
        await CommandAsync("Chamber.SetMode", PackMLCommand.Start, PackMLState.Complete);

        await WaitUntilAsync(() => (string)Read(SvidValue) == expected, $"SVID 1 to be '{expected}'");
    }

    [Fact]
    [Trait("AC", "5")]
    public async Task SetConfig_writes_ECID_318_Value_and_completes()
    {
        const int Integer32 = 2;
        await StartPlcAsync();
        string batch = $"{Module1}.ServiceParam.Chamber.SetConfig[1]";
        Write($"{batch}.Id", 318L);
        Write($"{batch}.DataType", (long)Integer32);
        Write($"{batch}.Value", "30");

        await CommandAsync("Chamber.SetConfig", PackMLCommand.Start, PackMLState.Complete);

        string descriptor = Enumerable.Range(1, 400)
            .Select(index => $"{Module1}.ModuleConfigParamList[{index}]")
            .First(path => ReadNumber($"{path}.Id") == 318);
        Assert.Equal("30", Read($"{descriptor}.Value"));
    }

    [Theory]
    [Trait("AC", "10")]
    [InlineData(4)]
    [InlineData(65535)]
    public async Task SetMode_out_of_range_adds_error_and_aborts(long targetMode)
    {
        await StartPlcAsync();
        Load("setmode", LoadDocument("Polar2_Chamber_SetMode"));

        Write($"{Module1}.ServiceParam.Chamber.SetMode.TargetMode", targetMode);
        await CommandAsync("Chamber.SetMode", PackMLCommand.Start, PackMLState.Aborted);
        await WaitForRunAsync("setmode", FlowEventKind.RunCompleted);

        Assert.Equal("1", Read(SvidValue));
        Assert.Equal(1, ReadNumber($"{Status("Chamber.SetMode")}.ErrorList[1].MainErrorId"));
        Assert.Equal(targetMode, ReadNumber($"{Status("Chamber.SetMode")}.ErrorList[1].SourceErrorId"));
        Assert.Equal("TargetMode out of range (0~3).", Read($"{Status("Chamber.SetMode")}.ErrorList[1].Message"));
        await WaitUntilAsync(() => ReadNumber($"{Module1}.ModuleAlarmList[1].MainErrorId") == 1, "the alarm in ModuleAlarmList");
        Assert.Equal(targetMode, ReadNumber($"{Module1}.ModuleAlarmList[1].SourceErrorId"));
        Assert.Equal(1, ReadNumber($"{Module1}.ModuleAlarmList[1].Active"));
    }

    [Theory]
    [Trait("AC", "11")]
    [InlineData(1, "2")]
    [InlineData(2, "3")]
    public async Task Initial_maps_switch_mode_to_svid(long switchMode, string expected)
    {
        await StartPlcAsync();
        Load("initial", InitialDocument("100"));

        Write($"{Module1}.ServiceParam.Chamber.Initial.SwitchModeAfterFinish", switchMode);
        await CommandAsync("Chamber.Initial", PackMLCommand.Start, PackMLState.Complete);

        await WaitUntilAsync(() => (string)Read(SvidValue) == expected, $"SVID 1 to be '{expected}'");
    }

    [Theory]
    [Trait("AC", "11")]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Initial_no_change_keeps_svid(long switchMode)
    {
        await StartPlcAsync();
        Load("initial", InitialDocument("100"));

        Write($"{Module1}.ServiceParam.Chamber.Initial.SwitchModeAfterFinish", switchMode);
        await CommandAsync("Chamber.Initial", PackMLCommand.Start, PackMLState.Complete);
        await WaitForRunAsync("initial", FlowEventKind.RunCompleted);

        Assert.Equal("1", Read(SvidValue));
    }

    [Fact]
    [Trait("AC", "11")]
    public async Task Initial_stopped_during_delay_keeps_svid()
    {
        await StartPlcAsync();
        Load("initial", InitialDocument("2000"));

        Write($"{Module1}.ServiceParam.Chamber.Initial.SwitchModeAfterFinish", 1L);
        await CommandAsync("Chamber.Initial", PackMLCommand.Start, PackMLState.Execute);
        await CommandAsync("Chamber.Initial", PackMLCommand.Stop, PackMLState.Stopped);
        FlowEvent failure = await WaitForRunAsync("initial", FlowEventKind.RunFailed);

        Assert.Equal("complete", failure.NodeId);
        Assert.Equal(PackMLState.Stopped, State("Chamber.Initial"));
        Assert.Equal("1", Read(SvidValue));
    }

    [Fact]
    [Trait("AC", "10")]
    [Trait("AC", "11")]
    public async Task SetMode_aborted_before_complete_keeps_svid()
    {
        await StartPlcAsync();
        Load("setmode", SetModeWithHoldBeforeComplete("2000"));

        Write($"{Module1}.ServiceParam.Chamber.SetMode.TargetMode", 2L);
        await CommandAsync("Chamber.SetMode", PackMLCommand.Start, PackMLState.Execute);
        await CommandAsync("Chamber.SetMode", PackMLCommand.Abort, PackMLState.Aborted);
        FlowEvent failure = await WaitForRunAsync("setmode", FlowEventKind.RunFailed);

        Assert.Equal("complete", failure.NodeId);
        Assert.Equal(PackMLState.Aborted, State("Chamber.SetMode"));
        Assert.Equal("1", Read(SvidValue));
    }

    private sealed record ExpectedSim(string When, string Target, string Action, long? Ecid, string? Param, string? DryRunAction);

    private static long EcidOf(JsonElement service, string section) =>
        service.GetProperty(section)[0].GetProperty("value").GetProperty("ecid").GetInt64();

    private static Dictionary<string, ExpectedSim[]> ExpectedSims(JsonElement services)
    {
        var result = new Dictionary<string, ExpectedSim[]>();

        void Rotation(string key, string target, string action)
        {
            long ecid = EcidOf(services.GetProperty(key), "onEnter");
            result[key] = [new("enter", target, action, ecid, null, null), new("leave", target, "Stop", null, null, null)];
        }

        void Position(string key, string[] targets, string name)
        {
            long ecid = EcidOf(services.GetProperty(key), "beforeComplete");
            result[key] =
            [
                .. targets.Select(target => new ExpectedSim("enter", target, name, ecid, null, null)),
                .. targets.Select(target => new ExpectedSim("beforeComplete", target, name, ecid, null, null)),
                .. targets.Select(target => new ExpectedSim("leave", target, "Hold", null, null, null)),
            ];
        }

        void Supply(string key, string target) =>
            result[key] = [new("enter", target, "On", null, "Setpoint", "Off"), new("leave", target, "Off", null, null, null)];

        void Jog(string key, string[] targets, string action, long ecid) =>
            result[key] =
            [
                .. targets.Select(target => new ExpectedSim("enter", target, action, ecid, null, null)),
                .. targets.Select(target => new ExpectedSim("leave", target, "Hold", null, null, null)),
            ];

        void Step(string key, string[] targets) =>
            result[key] =
            [
                .. targets.Select(target => new ExpectedSim("enter", target, "IncrementalJog", null, "Position", null)),
                .. targets.Select(target => new ExpectedSim("beforeComplete", target, "StepDone", null, null, null)),
                .. targets.Select(target => new ExpectedSim("leave", target, "Hold", null, null, null)),
            ];

        void Switch(string key, string target, string action) =>
            result[key] =
            [
                new("enter", target, action, null, null, null),
                new("beforeComplete", target, action, null, null, null),
                new("leave", target, "Hold", null, null, null),
            ];

        void Once(string key, string[] targets, string action) =>
            result[key] = [.. targets.Select(target => new ExpectedSim("enter", target, action, null, null, null))];

        Rotation("Holder.Rotate.MoveVelocityCW", "Holder", "CW");
        Rotation("Holder.Rotate.MoveVelocityCCW", "Holder", "CCW");
        Rotation("Holder.Rotate.MoveModulo", "Holder", "CW");
        Rotation("OutsideRing.Rotate.MoveVelocityCW", "OutsideRing", "CW");
        Rotation("OutsideRing.Rotate.MoveVelocityCCW", "OutsideRing", "CCW");

        foreach (int cup in new[] { 1, 2, 3, 4 })
        {
            foreach (string name in new[] { "Top", "Middle", "Down" })
                Position($"Cup[{cup}].Lift.{name}Pos", [$"Cup{cup}.Master", $"Cup{cup}.Slave"], name);
        }

        var rotates = new Dictionary<int, string[]>
        {
            [1] = ["NegLimit", "PosLimit", "NozzlePort", "Noz1Center", "Noz2Center", "Noz3Center"],
            [2] = ["NegLimit", "PosLimit", "NozzlePort", "Noz1Center", "Noz2Center", "Noz3Center"],
            [3] = ["NegLimit", "PosLimit", "Noz1Center", "Noz2Center"],
            [4] = ["NegLimit", "PosLimit", "Noz1Center"],
        };
        foreach ((int arm, string[] names) in rotates)
        {
            Position($"Arm[{arm}].Lift.TopPos", [$"Arm{arm}.Lift"], "Top");
            Position($"Arm[{arm}].Lift.DownPos", [$"Arm{arm}.Lift"], "Down");
            foreach (string name in names)
                Position($"Arm[{arm}].Rotate.{name}Pos", [$"Arm{arm}.Rotate"], name);
        }

        var nozzles = new Dictionary<int, int> { [1] = 3, [2] = 3, [3] = 2, [4] = 1 };
        foreach ((int arm, int count) in nozzles)
        {
            foreach (int nozzle in Enumerable.Range(1, count))
                Supply($"Arm[{arm}].Nozzle[{nozzle}].ChemicalSupply", $"Arm{arm}.Nozzle{nozzle}");
        }

        foreach (int arm in new[] { 1, 2 })
        {
            foreach (int port in new[] { 1, 2, 3 })
                Supply($"Arm[{arm}].NozzlePort.Nozzle[{port}].Supply", $"Arm{arm}.Port{port}");
        }

        foreach (int index in new[] { 1, 2, 3, 4 })
        {
            Supply($"FixedNozzle[{index}].ChemicalSupply", $"FixedNozzle{index}");
            long ecid = EcidOf(services.GetProperty($"Exhaust.Fan[{index}].SupplyFlowPID"), "onEnter");
            result[$"Exhaust.Fan[{index}].SupplyFlowPID"] =
                [new("enter", $"ExhaustFan{index}", "On", ecid, null, null), new("leave", $"ExhaustFan{index}", "Off", null, null, null)];
        }

        var armJogEcids = new Dictionary<int, long> { [1] = 16, [2] = 94, [3] = 172, [4] = 234 };
        foreach ((int arm, long ecid) in armJogEcids)
        {
            string[] target = [$"Arm{arm}.Rotate"];
            Jog($"Arm[{arm}].Rotate.JogPositive", target, "JogPositive", ecid);
            Jog($"Arm[{arm}].Rotate.JogNegative", target, "JogNegative", ecid);
            Step($"Arm[{arm}].Rotate.IncrementalJog", target);
        }

        var cupJogEcids = new Dictionary<int, long> { [1] = 270, [2] = 282, [3] = 294, [4] = 306 };
        foreach ((int cup, long ecid) in cupJogEcids)
        {
            string[] targets = [$"Cup{cup}.Master", $"Cup{cup}.Slave"];
            Jog($"Cup[{cup}].Lift.JogPositive", targets, "JogPositive", ecid);
            Jog($"Cup[{cup}].Lift.JogNegative", targets, "JogNegative", ecid);
            Step($"Cup[{cup}].Lift.IncrementalJog", targets);
            Once($"Cup[{cup}].Lift.DecoupleStop", targets, "Hold");
            foreach (int select in new[] { 1, 2, 3 })
                Once($"Cup[{cup}].Drainer.Select{select}", [$"Cup{cup}.Drain"], $"Select{select}");
        }

        foreach (int arm in new[] { 1, 2 })
        {
            foreach (int select in new[] { 1, 2, 3 })
                Once($"Arm[{arm}].NozzlePort.DrainSelect{select}", [$"Arm{arm}.PortDrain"], $"Select{select}");
        }

        result["Holder.Rotate.OPR"] =
            [new("enter", "Holder", "Home", null, null, null), new("beforeComplete", "Holder", "Home", null, null, null), new("leave", "Holder", "Hold", null, null, null)];
        Switch("Holder.Gripper.Open", "Holder.Gripper", "Open");
        Switch("Holder.Gripper.Close", "Holder.Gripper", "Close");
        Switch("OutsideRing.Lift.TopPos", "OutsideRing.Lift", "Top");
        Switch("OutsideRing.Lift.DownPos", "OutsideRing.Lift", "Down");
        Switch("Shutter.Open", "Shutter", "Open");
        Switch("Shutter.Close", "Shutter", "Close");
        Switch("Exhaust.Damper.AEX", "Damper", "AEX");
        Switch("Exhaust.Damper.SEX", "Damper", "SEX");
        foreach (int vent in new[] { 1, 2, 3, 4 })
        {
            Switch($"SideVent[{vent}].Open", $"SideVent{vent}", "Open");
            Switch($"SideVent[{vent}].Close", $"SideVent{vent}", "Close");
        }

        return result;
    }

    private static ExpectedSim[] ReadSims(JsonElement sim) => sim.EnumerateArray().Select(item =>
    {
        JsonElement value = default;
        bool hasValue = item.TryGetProperty("value", out value);
        return new ExpectedSim(
            item.GetProperty("when").GetString()!,
            item.GetProperty("target").GetString()!,
            item.GetProperty("action").GetString()!,
            hasValue && value.TryGetProperty("ecid", out JsonElement ecid) ? ecid.GetInt64() : null,
            hasValue && value.TryGetProperty("param", out JsonElement param) ? param.GetString() : null,
            item.TryGetProperty("dryRunAction", out JsonElement dryRun) ? dryRun.GetString() : null);
    }).ToArray();

    [Fact]
    [Trait("AC", "1")]
    [Trait("AC", "6")]
    [Trait("AC", "7")]
    public void The_service_behaviors_file_gives_sim_actions_to_exactly_the_130_services_of_the_spec_and_loads()
    {
        string configFolder = Path.Combine(TestPaths.Polar2Project, "config");
        using JsonDocument file = JsonDocument.Parse(File.ReadAllText(Path.Combine(configFolder, "service-behaviors.json")));
        JsonElement services = file.RootElement.GetProperty("services");
        Dictionary<string, ExpectedSim[]> expected = ExpectedSims(services);

        string[] withSim = services.EnumerateObject().Where(service => service.Value.TryGetProperty("sim", out _)).Select(service => service.Name).Order().ToArray();

        Assert.Equal(130, expected.Count);
        Assert.Equal(expected.Keys.Order(), withSim);
        foreach ((string key, ExpectedSim[] sims) in expected)
            Assert.Equal(sims, ReadSims(services.GetProperty(key).GetProperty("sim")));

        ModuleConfigFile config = ModuleConfigPublisher.Parse(File.ReadAllText(Path.Combine(configFolder, "module-config.json")), "module-config.json");
        ModuleBehavior module = ServiceBehaviorLoader.LoadIfPresent(TestPaths.Polar2Project, s_program.Value.Symbols, config)!.For(Module1)!;
        Assert.Equal(expected.Keys.Order(), module.Services.Where(service => service.Value.Sim!.Count > 0).Select(service => service.Key).Order());
        Assert.All(expected, pair => Assert.Equal(
            pair.Value.Select(sim => (sim.When.ToLowerInvariant(), sim.Target, sim.Action, sim.DryRunAction)),
            module.Services[pair.Key].Sim!.Select(sim => (sim.When.ToString().ToLowerInvariant(), sim.Target, sim.Action, sim.DryRunAction))));
    }
}
