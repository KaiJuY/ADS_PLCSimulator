using AdsPlcSimulator.Core.Flow;
using AdsPlcSimulator.Core.PackML;
using AdsPlcSimulator.Core.Plc;
using AdsPlcSimulator.Tests.PackML;
using static AdsPlcSimulator.Tests.TestWait;

namespace AdsPlcSimulator.Tests.Flow;

public sealed class AlarmNodeTests : IAsyncLifetime
{
    private const string Service = "GVL_Test.Modules[#].ServiceCtrl.A";
    private const string Module = "GVL_Test.Modules[#]";
    private const string ErrorList = "GVL_Test.Modules[1].ServiceStatus.A.ErrorList";
    private const string AlarmList = "GVL_Test.Modules[1].ModuleAlarmList";

    private readonly string _projectDirectory = Path.Combine(Path.GetTempPath(), "adsplc-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingMonitor _monitor = new();
    private PlcProgram _program = null!;
    private ProcessImage _image = null!;
    private FlowRuntime _runtime = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_projectDirectory, "plc"));
        File.WriteAllText(Path.Combine(_projectDirectory, "plc", "identity.st"), HostFixture.Identities);
        _program = TestPaths.BuildFromText(HostFixture.AlarmDeclarations);
        _image = _program.CreateProcessImage();
        _runtime = new FlowRuntime(_image, _program.Symbols, NodeCatalog.Default, _monitor, new NoSimulation(), _projectDirectory);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _runtime.DisposeAsync();
        Directory.Delete(_projectDirectory, recursive: true);
    }

    private static WorkflowNode Node(string id, string type, params (string Key, string Value)[] parameters) =>
        new() { Id = id, Type = type, Params = parameters.ToDictionary(pair => pair.Key, pair => pair.Value) };

    private static WorkflowLink Link(string from, string fromPort, string to, string toPort) => new(from, fromPort, to, toPort);

    private static WorkflowDocument OneNode(WorkflowNode node, params (string Value, string Port)[] constants)
    {
        var nodes = new List<WorkflowNode> { Node("start", "trigger.onStart"), node };
        var links = new List<WorkflowLink> { Link("start", "then", node.Id, "exec") };
        foreach ((string value, string port) in constants)
        {
            string id = "const_" + port;
            nodes.Add(Node(id, "data.constant", ("value", value)));
            links.Add(Link(id, "value", node.Id, port));
        }

        return new WorkflowDocument { Nodes = nodes, Links = links };
    }

    private static WorkflowNode AddError(params (string Key, string Value)[] extra) =>
        Node("n", "action.addServiceError", [("service", "GVL_Test.Modules[1].ServiceCtrl.A"), .. extra]);

    private static WorkflowNode SetAlarm(params (string Key, string Value)[] extra) =>
        Node("n", "action.setModuleAlarm", [("module", "GVL_Test.Modules[1]"), .. extra]);

    private object Read(string path) => _image.ReadValue(_program.Symbols.Resolve(path));

    private long Number(string path) => Convert.ToInt64(Read(path));

    private async Task LoadHostAsync()
    {
        var host = new WorkflowDocument
        {
            Nodes = [Node("host", "service.host", ("root", "GVL_Test.Modules"), ("identityFile", "plc/identity.st"), ("defaultDelayMs", "100"))],
        };
        Assert.Empty(_runtime.Load("host", host));
        await WaitUntilAsync(() => Number("GVL_Test.Modules[1].ServiceStatus.A.PackMLOut.eStateCurrent") == (long)PackMLState.Idle, "service A to be Idle");
    }

    private async Task<FlowEvent> RunAsync(WorkflowDocument document, string name = "w")
    {
        Assert.Empty(_runtime.Load(name, document));
        return await _monitor.WaitForAsync(flowEvent => flowEvent.Workflow == name && flowEvent.Kind is FlowEventKind.RunCompleted or FlowEventKind.RunFailed);
    }

    private FlowEvent NodeEvent(string node) =>
        _monitor.Events.Single(flowEvent => flowEvent.Kind == FlowEventKind.NodeCompleted && flowEvent.NodeId == node);

    [Theory]
    [InlineData("action.addServiceError")]
    [InlineData("action.setModuleAlarm")]
    [Trait("AC", "6")]
    public void Definitions_take_message_only_as_a_parameter(string type)
    {
        NodeDefinition definition = NodeCatalog.Default.Find(type)!;

        Assert.DoesNotContain(definition.Inputs, port => port.Name == "message");
        Assert.NotNull(definition.FindParam("message"));
    }

    [Fact]
    [Trait("AC", "6")]
    public async Task AddServiceError_writes_the_entry_and_outputs_added_and_reason()
    {
        await LoadHostAsync();

        FlowEvent result = await RunAsync(OneNode(AddError(("mainErrorId", "7"), ("sourceErrorId", "9"), ("message", "boom"))));

        Assert.Equal(FlowEventKind.RunCompleted, result.Kind);
        Assert.Equal(7, Number($"{ErrorList}[1].MainErrorId"));
        Assert.Equal(9, Number($"{ErrorList}[1].SourceErrorId"));
        Assert.Equal(101, Number($"{ErrorList}[1].ServiceId"));
        Assert.Equal("boom", Read($"{ErrorList}[1].Message"));
        Assert.Equal(true, NodeEvent("n").Outputs!["added"]);
        Assert.Equal("", NodeEvent("n").Outputs!["reason"]);
    }

    [Fact]
    [Trait("AC", "6")]
    public async Task AddServiceError_reports_a_duplicate_through_the_outputs()
    {
        await LoadHostAsync();
        await RunAsync(OneNode(AddError(("mainErrorId", "7"))), "first");
        _runtime.Unload("first");

        await RunAsync(OneNode(AddError(("mainErrorId", "7"))), "second");

        FlowEvent second = _monitor.Events.Single(flowEvent => flowEvent is { Workflow: "second", Kind: FlowEventKind.NodeCompleted, NodeId: "n" });
        Assert.Equal(false, second.Outputs!["added"]);
        Assert.Equal("duplicate MainErrorId 7", second.Outputs["reason"]);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("007", "007")]
    [InlineData("  ", "")]
    [Trait("AC", "6")]
    public async Task Message_param_is_taken_as_written(string param, string expected)
    {
        await LoadHostAsync();

        await RunAsync(OneNode(AddError(("mainErrorId", "7"), ("message", param))));

        Assert.Equal(expected, Read($"{ErrorList}[1].Message"));
    }

    [Fact]
    [Trait("AC", "6")]
    public async Task Ids_come_from_links_when_the_inputs_are_connected()
    {
        await LoadHostAsync();

        await RunAsync(OneNode(AddError(("mainErrorId", "1")), ("12", "mainErrorId"), ("34", "sourceErrorId")));

        Assert.Equal(12, Number($"{ErrorList}[1].MainErrorId"));
        Assert.Equal(34, Number($"{ErrorList}[1].SourceErrorId"));
    }

    [Theory]
    [InlineData("4294967296")]
    [InlineData("-1")]
    [InlineData("0")]
    [Trait("AC", "6")]
    public async Task AddServiceError_refuses_a_main_error_id_outside_1_to_uint_max(string mainErrorId)
    {
        await LoadHostAsync();

        FlowEvent result = await RunAsync(OneNode(AddError(), (mainErrorId, "mainErrorId")));

        Assert.Equal(FlowEventKind.RunFailed, result.Kind);
        Assert.Equal(0, Number($"{ErrorList}[1].MainErrorId"));
    }

    [Fact]
    [Trait("AC", "6")]
    public async Task AddServiceError_accepts_the_largest_uint()
    {
        await LoadHostAsync();

        await RunAsync(OneNode(AddError(), ("4294967295", "mainErrorId")));

        Assert.Equal(uint.MaxValue, (ulong)Number($"{ErrorList}[1].MainErrorId"));
    }

    [Fact]
    [Trait("AC", "6")]
    public async Task AddServiceError_refuses_a_negative_source_error_id()
    {
        await LoadHostAsync();

        FlowEvent result = await RunAsync(OneNode(AddError(("mainErrorId", "7")), ("-1", "sourceErrorId")));

        Assert.Equal(FlowEventKind.RunFailed, result.Kind);
    }

    [Fact]
    [Trait("AC", "6")]
    public async Task AddServiceError_without_a_host_fails_the_run()
    {
        FlowEvent result = await RunAsync(OneNode(AddError(("mainErrorId", "7"))));

        Assert.Equal(FlowEventKind.RunFailed, result.Kind);
        Assert.Contains("No service.host", result.Error);
    }

    [Fact]
    [Trait("AC", "6")]
    public async Task A_bracket_hash_service_takes_its_module_from_the_index_input()
    {
        await LoadHostAsync();
        WorkflowNode node = Node("n", "action.addServiceError", ("service", Service), ("mainErrorId", "7"));

        FlowEvent withoutIndex = await RunAsync(OneNode(node), "no-index");
        FlowEvent withIndex = await RunAsync(OneNode(node, ("1", "index")), "with-index");

        Assert.Equal(FlowEventKind.RunFailed, withoutIndex.Kind);
        Assert.Equal(FlowEventKind.RunCompleted, withIndex.Kind);
        Assert.Equal(7, Number($"{ErrorList}[1].MainErrorId"));
    }

    [Theory]
    [InlineData("GVL_Test.Modules[1].ModuleCtrl.RequestId")]
    [InlineData("GVL_Test.Modules[1].ServiceStatus.A")]
    [Trait("AC", "6")]
    public void AddServiceError_with_a_symbol_that_is_not_ST_ServiceCtrl_reports_a_diagnostic(string service)
    {
        var document = OneNode(Node("n", "action.addServiceError", ("service", service), ("mainErrorId", "7")));

        WorkflowDiagnostic diagnostic = Assert.Single(_runtime.Load("w", document));

        Assert.Equal("n", diagnostic.NodeId);
    }

    [Fact]
    [Trait("AC", "9")]
    public async Task SetModuleAlarm_raises_and_clears_a_hook_alarm()
    {
        await LoadHostAsync();

        await RunAsync(OneNode(SetAlarm(("mainErrorId", "30"), ("sourceErrorId", "4"), ("message", "hook"), ("severity", "Warning"), ("path", "Custom.Path"))), "on");
        await WaitUntilAsync(() => Number($"{AlarmList}[1].MainErrorId") == 30, "the hook alarm");

        Assert.Equal(4, Number($"{AlarmList}[1].SourceErrorId"));
        Assert.Equal(2, Number($"{AlarmList}[1].Severity"));
        Assert.Equal("hook", Read($"{AlarmList}[1].Message"));
        Assert.Equal("Custom.Path", Read($"{AlarmList}[1].Path"));
        Assert.Equal(1, Number($"{AlarmList}[1].Active"));

        _runtime.Unload("on");
        await RunAsync(OneNode(SetAlarm(("mainErrorId", "30"), ("active", "FALSE"))), "off");
        await WaitUntilAsync(() => Number($"{AlarmList}[1].Active") == 0, "the alarm to turn inactive");
    }

    [Fact]
    [Trait("AC", "9")]
    public async Task SetModuleAlarm_defaults_to_Alarm_severity_and_the_module_path()
    {
        await LoadHostAsync();

        await RunAsync(OneNode(SetAlarm(("mainErrorId", "30"))));
        await WaitUntilAsync(() => Number($"{AlarmList}[1].MainErrorId") == 30, "the hook alarm");

        Assert.Equal(1, Number($"{AlarmList}[1].Severity"));
        Assert.Equal("GVL_Test.Modules[1]", Read($"{AlarmList}[1].Path"));
        Assert.Equal("", Read($"{AlarmList}[1].Message"));
    }

    [Fact]
    [Trait("AC", "9")]
    public async Task SetModuleAlarm_takes_ids_and_active_from_links()
    {
        await LoadHostAsync();

        await RunAsync(OneNode(SetAlarm(("mainErrorId", "1")), ("31", "mainErrorId"), ("5", "sourceErrorId"), ("TRUE", "active")));
        await WaitUntilAsync(() => Number($"{AlarmList}[1].MainErrorId") == 31, "the hook alarm");

        Assert.Equal(5, Number($"{AlarmList}[1].SourceErrorId"));
    }

    [Theory]
    [InlineData("4294967296")]
    [InlineData("-1")]
    [InlineData("0")]
    [Trait("AC", "9")]
    public async Task SetModuleAlarm_refuses_a_main_error_id_outside_1_to_uint_max(string mainErrorId)
    {
        await LoadHostAsync();

        FlowEvent result = await RunAsync(OneNode(SetAlarm(), (mainErrorId, "mainErrorId")));

        Assert.Equal(FlowEventKind.RunFailed, result.Kind);
    }

    [Fact]
    [Trait("AC", "9")]
    public async Task SetModuleAlarm_without_a_host_fails_the_run()
    {
        FlowEvent result = await RunAsync(OneNode(SetAlarm(("mainErrorId", "30"))));

        Assert.Equal(FlowEventKind.RunFailed, result.Kind);
    }

    [Fact]
    [Trait("AC", "9")]
    public async Task A_bracket_hash_module_takes_its_index_from_the_index_input()
    {
        await LoadHostAsync();
        WorkflowNode node = Node("n", "action.setModuleAlarm", ("module", Module), ("mainErrorId", "30"));

        FlowEvent withoutIndex = await RunAsync(OneNode(node), "no-index");
        FlowEvent withIndex = await RunAsync(OneNode(node, ("1", "index")), "with-index");

        Assert.Equal(FlowEventKind.RunFailed, withoutIndex.Kind);
        Assert.Equal(FlowEventKind.RunCompleted, withIndex.Kind);
    }

    [Fact]
    [Trait("AC", "9")]
    public void SetModuleAlarm_with_a_symbol_that_is_not_a_module_struct_reports_a_diagnostic()
    {
        var document = OneNode(Node("n", "action.setModuleAlarm", ("module", "GVL_Test.Modules[1].ModuleCtrl.RequestId"), ("mainErrorId", "30")));

        WorkflowDiagnostic diagnostic = Assert.Single(_runtime.Load("w", document));

        Assert.Equal("n", diagnostic.NodeId);
    }

    [Fact]
    [Trait("AC", "9")]
    public void SetModuleAlarm_with_an_unknown_severity_reports_a_diagnostic()
    {
        var document = OneNode(SetAlarm(("mainErrorId", "30"), ("severity", "Fatal")));

        WorkflowDiagnostic diagnostic = Assert.Single(_runtime.Load("w", document));

        Assert.Contains("severity", diagnostic.Message);
    }
}
