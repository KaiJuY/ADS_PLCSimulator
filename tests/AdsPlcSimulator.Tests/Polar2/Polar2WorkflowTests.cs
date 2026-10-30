using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AdsPlcSimulator.Core.Flow;
using AdsPlcSimulator.Core.PackML;
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
    public async Task Polar2_host_binds_145_services_per_module()
    {
        Load("host", HostDocument());
        await WaitUntilAsync(() => _runtime.Registry.FindByService(Ctrl("Shutter.Open")) is not null, "the host to register");

        IReadOnlyList<string> bound = _runtime.Registry.FindByService(Ctrl("Shutter.Open"))!.BoundServices;

        Assert.Equal(6 * 145, bound.Count);
        for (int module = 1; module <= 6; module++)
            Assert.Equal(145, bound.Count(path => path.StartsWith($"{Root}[{module}].ServiceCtrl.", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    [Trait("AC", "2")]
    public async Task Polar2_initialize_writes_identity_of_all_165_slots()
    {
        string identityFile = File.ReadAllText(Path.Combine(TestPaths.Polar2Project, "plc", "ServiceIdentity", "F_InitializePolarIIChamberServiceStatus.TcPOU"));
        ServiceIdentityTable table = ServiceIdentityTable.Parse(identityFile, name => _program.Symbols.TryGetEnumValue(name, out long value) ? value : null);
        List<string> slots = IdentityPathPattern().Matches(identityFile).Select(match => match.Groups["rel"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(165, slots.Count);
        Assert.Equal(145, slots.Count(slot => table.Find(slot)!.ServiceId != 0));

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

        Assert.True(_program.Symbols.TryGetEnumValue("E_PolarIIChamberServiceId.ShutterOpen", out long shutterOpen));
        Assert.Equal(shutterOpen, ReadNumber($"{Status("Shutter.Open")}.ServiceId"));
        Assert.Equal(0, ReadNumber($"{Root}[1].ServiceStatus.Arm[3].Rotate.NozzlePortPos.ServiceId"));
        Assert.Equal((long)CompletionBehavior.NaturalCompletion, ReadNumber($"{Root}[1].ServiceStatus.Arm[3].Rotate.NozzlePortPos.CompletionBehavior"));
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
}
