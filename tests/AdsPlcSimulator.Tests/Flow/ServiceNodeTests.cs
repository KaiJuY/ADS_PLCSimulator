using System.Collections.Concurrent;
using AdsPlcSimulator.Core.Flow;
using AdsPlcSimulator.Core.Flow.Nodes;
using AdsPlcSimulator.Core.PackML;
using AdsPlcSimulator.Core.Plc;
using AdsPlcSimulator.Host.Live;
using AdsPlcSimulator.Tests.PackML;
using Microsoft.Extensions.Logging;

namespace AdsPlcSimulator.Tests.Flow;

public sealed class ServiceNodeTests : IAsyncLifetime
{
    private const string ServiceA = "GVL_Test.Modules[#].ServiceCtrl.A";
    private const string ValueA = "GVL_Test.Modules[#].ServiceParam.A.Value";
    private const string ValueB = "GVL_Test.Modules[#].ServiceParam.B.Value";

    private readonly string _projectDirectory = Path.Combine(Path.GetTempPath(), "adsplc-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingMonitor _monitor = new();
    private PlcProgram _program = null!;
    private ProcessImage _image = null!;
    private FlowRuntime _runtime = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_projectDirectory, "plc"));
        File.WriteAllText(Path.Combine(_projectDirectory, "plc", "identity.st"), HostFixture.Identities);
        _program = TestPaths.BuildFromText(HostFixture.Declarations);
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

    private static WorkflowNode HostNode(string root = "GVL_Test.Modules", string file = "plc/identity.st", string delayMs = "100") =>
        Node("host", "service.host", ("root", root), ("identityFile", file), ("defaultDelayMs", delayMs));

    private static WorkflowDocument HostDocument(string root = "GVL_Test.Modules", string file = "plc/identity.st", string delayMs = "100") =>
        new() { Nodes = [HostNode(root, file, delayMs)] };

    private static WorkflowDocument CompleteAfterDelay(int waitMs, string service = ServiceA, string mark = ValueA) => new()
    {
        Nodes =
        [
            Node("trigger", "trigger.onServiceExecute", ("service", service)),
            Node("wait", "flow.delay", ("ms", waitMs.ToString())),
            Node("complete", "action.completeService", ("service", service)),
            Node("mark", "action.writeSymbol", ("symbol", mark), ("value", "42")),
        ],
        Links =
        [
            Link("trigger", "then", "wait", "exec"),
            Link("wait", "then", "complete", "exec"),
            Link("complete", "then", "mark", "exec"),
            Link("trigger", "index", "complete", "index"),
            Link("trigger", "generation", "complete", "generation"),
            Link("trigger", "index", "mark", "index"),
        ],
    };

    private static WorkflowDocument AbortOnExecute() => new()
    {
        Nodes =
        [
            Node("trigger", "trigger.onServiceExecute", ("service", ServiceA)),
            Node("abort", "action.abortService", ("service", ServiceA)),
        ],
        Links =
        [
            Link("trigger", "then", "abort", "exec"),
            Link("trigger", "index", "abort", "index"),
            Link("trigger", "generation", "abort", "generation"),
        ],
    };

    private static WorkflowDocument OnStartWrites(string symbol, params (string Key, string Value)[] extra) => new()
    {
        Nodes = [Node("start", "trigger.onStart"), Node("write", "action.writeSymbol", [("symbol", symbol), .. extra])],
        Links = [Link("start", "then", "write", "exec")],
    };

    private static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(5);
        }
    }

    private object Read(string path) => _image.ReadValue(_program.Symbols.Resolve(path));

    private void Write(string path, object value) => _image.WriteValue(_program.Symbols.Resolve(path), value, WriteSource.Client);

    private PackMLState State(int module, string service) =>
        (PackMLState)(long)Read($"GVL_Test.Modules[{module}].ServiceStatus.{service}.PackMLOut.eStateCurrent");

    private long Value(int module, string field) => (long)Read($"GVL_Test.Modules[{module}].ServiceParam.{field}.Value");

    private Task WaitForStateAsync(int module, string service, PackMLState expected) =>
        WaitUntilAsync(() => State(module, service) == expected, $"{service} of module {module} to be {expected}");

    private async Task LoadHostAsync(string name = "host", string delayMs = "100")
    {
        Assert.Empty(_runtime.Load(name, HostDocument(delayMs: delayMs)));
        await WaitForStateAsync(1, "A", PackMLState.Idle);
    }

    private async Task PulseAsync(int module, string service, PackMLCommand command, PackMLState expected)
    {
        string ctrl = HostFixture.CtrlPath(module, service);
        Write($"{ctrl}.RequestId", 1L);
        Write($"{ctrl}.PackMLIn.eCommand", (long)command);
        Write($"{ctrl}.PackMLIn.bCommandChangeRequest", true);
        await WaitForStateAsync(module, service, expected);
        Write($"{ctrl}.PackMLIn.bCommandChangeRequest", false);
        await WaitUntilAsync(() => !(bool)Read($"GVL_Test.Modules[{module}].ServiceStatus.{service}.PackMLOut.bCommandAccepted"), "the accepted flag to clear");
    }

    private async Task RestartServiceAsync(int module, string service)
    {
        await PulseAsync(module, service, PackMLCommand.Stop, PackMLState.Stopped);
        await PulseAsync(module, service, PackMLCommand.Reset, PackMLState.Idle);
        await PulseAsync(module, service, PackMLCommand.Start, PackMLState.Execute);
    }

    private ServiceExecuteTriggerNode CompileTrigger(string service)
    {
        var document = new WorkflowDocument { Nodes = [Node("trigger", "trigger.onServiceExecute", ("service", service))] };
        CompiledWorkflow? compiled = WorkflowCompiler.Compile("t", document, NodeCatalog.Default, _program.Symbols, out List<WorkflowDiagnostic> diagnostics);
        Assert.True(compiled is not null, string.Join("; ", diagnostics.Select(diagnostic => diagnostic.Message)));
        return (ServiceExecuteTriggerNode)compiled!.Nodes.Single().Node;
    }

    private MemoryWrite StateWrite(int module, PackMLState from, PackMLState to)
    {
        SymbolRef state = _program.Symbols.Resolve($"GVL_Test.Modules[{module}].ServiceStatus.A.PackMLOut.eStateCurrent");
        var oldData = new byte[state.Size];
        var newData = new byte[state.Size];
        ValueCodec.Encode(state.Type, (long)from, oldData);
        ValueCodec.Encode(state.Type, (long)to, newData);
        return new MemoryWrite(state.Offset, oldData, newData, WriteSource.Workflow);
    }

    [Fact]
    [Trait("AC", "13")]
    public async Task Host_node_runs_the_handshake_in_the_background()
    {
        await LoadHostAsync();

        await PulseAsync(1, "A", PackMLCommand.Start, PackMLState.Execute);

        await WaitForStateAsync(1, "A", PackMLState.Complete);
        Assert.Equal(PackMLState.Idle, State(1, "B"));
    }

    [Theory]
    [InlineData("GVL_Test.Missing", "plc/identity.st", "100", "Missing")]
    [InlineData("GVL_Test.Modules[1].ModuleCtrl.RequestId", "plc/identity.st", "100", "module struct")]
    [InlineData("GVL_Test.Modules[1].ModuleCtrl", "plc/identity.st", "100", "no member")]
    [InlineData("GVL_Test.Modules", "plc/none.st", "100", "not found")]
    [InlineData("GVL_Test.Modules", "../outside.st", "100", "outside the project folder")]
    [InlineData("GVL_Test.Modules", "", "100", "needs a file path")]
    [InlineData("GVL_Test.Modules", "plc/identity.st", "-1", "at least 0")]
    [InlineData("GVL_Test.Modules", "plc/identity.st", "abc", "must be a number")]
    [Trait("AC", "13")]
    public void Host_with_invalid_settings_reports_diagnostic(string root, string file, string delayMs, string expectedMessage)
    {
        IReadOnlyList<WorkflowDiagnostic> diagnostics = _runtime.Load("host", HostDocument(root, file, delayMs));

        WorkflowDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("host", diagnostic.NodeId);
        Assert.Contains(expectedMessage, diagnostic.Message);
        Assert.Empty(_runtime.LoadedWorkflows);
    }

    [Fact]
    [Trait("AC", "13")]
    public void Host_with_unknown_enum_reports_diagnostic()
    {
        File.WriteAllText(
            Path.Combine(_projectDirectory, "plc", "bad.st"),
            "F_Set(Status := ServiceStatus.A, ServiceId := E_TestServiceId.Nope, CompletionBehavior := E_ServiceCompletionBehavior.NaturalCompletion);");

        WorkflowDiagnostic diagnostic = Assert.Single(_runtime.Load("host", HostDocument(file: "plc/bad.st")));

        Assert.Equal("Unknown enum value 'E_TestServiceId.Nope'.", diagnostic.Message);
    }

    [Fact]
    [Trait("AC", "13")]
    public void Host_without_bound_service_reports_diagnostic()
    {
        File.WriteAllText(Path.Combine(_projectDirectory, "plc", "none.st"), "ServiceStatus.A.CompletionBehavior := E_ServiceCompletionBehavior.NaturalCompletion;");

        WorkflowDiagnostic diagnostic = Assert.Single(_runtime.Load("host", HostDocument(file: "plc/none.st")));

        Assert.Equal("No service under GVL_Test.Modules has a ServiceId.", diagnostic.Message);
    }

    [Fact]
    [Trait("AC", "13")]
    public async Task Two_hosts_of_different_workflows_on_same_root_fail_second_run()
    {
        await LoadHostAsync("first");

        Assert.Empty(_runtime.Load("second", HostDocument()));

        FlowEvent failure = await _monitor.WaitForAsync(flowEvent => flowEvent.Kind == FlowEventKind.RunFailed && flowEvent.Workflow == "second");
        Assert.Equal("host", failure.NodeId);
        Assert.Contains("already hosts", failure.Error);
        Assert.NotNull(_runtime.Registry.FindByService(HostFixture.CtrlPath(1, "A")));
    }

    [Fact]
    [Trait("AC", "13")]
    public async Task Reloading_host_workflow_keeps_scanning()
    {
        await LoadHostAsync();
        await PulseAsync(1, "A", PackMLCommand.Start, PackMLState.Execute);
        await WaitForStateAsync(1, "A", PackMLState.Complete);

        await LoadHostAsync();
        await PulseAsync(1, "A", PackMLCommand.Start, PackMLState.Execute);

        Assert.Equal(PackMLState.Execute, State(1, "A"));
    }

    [Fact]
    [Trait("AC", "13")]
    public async Task Reload_clears_stale_status_and_ignores_held_request()
    {
        await LoadHostAsync(delayMs: "60000");
        string ctrl = HostFixture.CtrlPath(1, "A");
        Write($"{ctrl}.PackMLIn.eCommand", (long)PackMLCommand.Start);
        Write($"{ctrl}.PackMLIn.bCommandChangeRequest", true);
        await WaitForStateAsync(1, "A", PackMLState.Execute);

        Assert.Empty(_runtime.Load("host", HostDocument(delayMs: "60000")));
        await WaitForStateAsync(1, "A", PackMLState.Idle);
        await Task.Delay(100);

        Assert.Equal(PackMLState.Idle, State(1, "A"));
        Assert.Equal(0L, Read("GVL_Test.Modules[1].ServiceStatus.A.ResponseId"));
    }

    [Fact]
    [Trait("AC", "13")]
    public async Task Unloading_host_workflow_unregisters_the_host_and_stops_scanning()
    {
        await LoadHostAsync();

        _runtime.Unload("host");

        await WaitUntilAsync(() => _runtime.Registry.FindByService(HostFixture.CtrlPath(1, "A")) is null, "the host to unregister");
        string ctrl = HostFixture.CtrlPath(1, "A");
        Write($"{ctrl}.PackMLIn.eCommand", (long)PackMLCommand.Start);
        Write($"{ctrl}.PackMLIn.bCommandChangeRequest", true);
        await Task.Delay(100);
        Assert.Equal(PackMLState.Idle, State(1, "A"));
    }

    [Fact]
    [Trait("AC", "13")]
    public async Task Disposing_the_runtime_waits_for_the_hosted_scan_to_stop()
    {
        PlcProgram program = TestPaths.BuildFromText(HostFixture.Declarations);
        ProcessImage image = program.CreateProcessImage();
        SymbolRef state = program.Symbols.Resolve("GVL_Test.Modules[1].ServiceStatus.A.PackMLOut.eStateCurrent");
        var runtime = new FlowRuntime(image, program.Symbols, NodeCatalog.Default, new RecordingMonitor(), new NoSimulation(), _projectDirectory);
        Assert.Empty(runtime.Load("host", HostDocument()));
        await WaitUntilAsync(() => (long)image.ReadValue(state) == (long)PackMLState.Idle, "the host to initialize");

        await runtime.DisposeAsync();
        byte[] before = image.Read(0, image.Size);
        image.WriteValue(program.Symbols.Resolve(HostFixture.CtrlPath(1, "A") + ".PackMLIn.eCommand"), (long)PackMLCommand.Start, WriteSource.Client);
        image.WriteValue(program.Symbols.Resolve(HostFixture.CtrlPath(1, "A") + ".PackMLIn.bCommandChangeRequest"), true, WriteSource.Client);
        byte[] expected = image.Read(0, image.Size);
        await Task.Delay(100);

        Assert.NotEqual(before, expected);
        Assert.Equal(expected, image.Read(0, image.Size));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Trigger_collects_ctrl_paths_of_every_index()
    {
        ServiceExecuteTriggerNode trigger = CompileTrigger(ServiceA);

        Assert.Equal(["GVL_Test.Modules[1].ServiceCtrl.A", "GVL_Test.Modules[2].ServiceCtrl.A"], trigger.CtrlPaths);
    }

    [Fact]
    [Trait("AC", "12")]
    public void Trigger_without_placeholder_has_one_ctrl_path()
    {
        ServiceExecuteTriggerNode trigger = CompileTrigger("GVL_Test.Modules[2].ServiceCtrl.B");

        Assert.Equal(["GVL_Test.Modules[2].ServiceCtrl.B"], trigger.CtrlPaths);
    }

    [Theory]
    [InlineData("GVL_Test.Modules[#].ModuleCtrl", "not ST_ServiceCtrl")]
    [InlineData("GVL_Test.Modules[#].ServiceCtrl", "not ST_ServiceCtrl")]
    [InlineData("GVL_Test.Modules[#].ServiceCtrl.Missing", "Missing")]
    [InlineData("", "needs a PLC symbol path")]
    [Trait("AC", "12")]
    public void Trigger_with_invalid_service_reports_diagnostic(string service, string expectedMessage)
    {
        var document = new WorkflowDocument { Nodes = [Node("trigger", "trigger.onServiceExecute", ("service", service))] };

        IReadOnlyList<WorkflowDiagnostic> diagnostics = _runtime.Load("t", document);

        Assert.Contains(expectedMessage, Assert.Single(diagnostics).Message);
    }

    [Fact]
    [Trait("AC", "12")]
    public void Trigger_on_a_service_outside_a_ServiceCtrl_member_reports_diagnostic()
    {
        PlcProgram program = TestPaths.BuildFromText(HostFixture.Declarations + "\nVAR_GLOBAL Loose : ST_ServiceCtrl; END_VAR");
        var document = new WorkflowDocument { Nodes = [Node("trigger", "trigger.onServiceExecute", ("service", "GVL_Test.Loose"))] };

        CompiledWorkflow? compiled = WorkflowCompiler.Compile("t", document, NodeCatalog.Default, program.Symbols, out List<WorkflowDiagnostic> diagnostics);

        Assert.Null(compiled);
        Assert.Contains(".ServiceCtrl.", Assert.Single(diagnostics).Message);
    }

    [Fact]
    [Trait("AC", "12")]
    public void Trigger_fires_with_index_when_a_service_enters_execute()
    {
        ServiceExecuteTriggerNode trigger = CompileTrigger(ServiceA);

        bool fired = trigger.TryFire(StateWrite(2, PackMLState.Starting, PackMLState.Execute), _image, out Dictionary<string, object?> outputs);

        Assert.True(fired);
        Assert.Equal(2L, outputs["index"]);
        Assert.Null(outputs["generation"]);
    }

    [Fact]
    [Trait("AC", "12")]
    public void Trigger_without_placeholder_gives_a_null_index()
    {
        ServiceExecuteTriggerNode trigger = CompileTrigger("GVL_Test.Modules[2].ServiceCtrl.A");

        trigger.TryFire(StateWrite(2, PackMLState.Starting, PackMLState.Execute), _image, out Dictionary<string, object?> outputs);

        Assert.Null(outputs["index"]);
    }

    [Theory]
    [InlineData(PackMLState.Execute, PackMLState.Execute)]
    [InlineData(PackMLState.Execute, PackMLState.Completing)]
    [InlineData(PackMLState.Idle, PackMLState.Starting)]
    [InlineData(PackMLState.Starting, PackMLState.Idle)]
    [Trait("AC", "12")]
    public void Trigger_ignores_writes_that_do_not_enter_execute(PackMLState from, PackMLState to)
    {
        ServiceExecuteTriggerNode trigger = CompileTrigger(ServiceA);

        Assert.False(trigger.TryFire(StateWrite(1, from, to), _image, out _));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Trigger_ignores_writes_elsewhere()
    {
        ServiceExecuteTriggerNode trigger = CompileTrigger(ServiceA);
        SymbolRef other = _program.Symbols.Resolve("GVL_Test.Modules[1].ServiceStatus.B.PackMLOut.eStateCurrent");
        var write = new MemoryWrite(other.Offset, [4, 0], [6, 0], WriteSource.Workflow);

        Assert.False(trigger.TryFire(write, _image, out _));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Trigger_fires_for_a_workflow_write_and_for_a_client_write()
    {
        ServiceExecuteTriggerNode trigger = CompileTrigger(ServiceA);
        MemoryWrite write = StateWrite(1, PackMLState.Starting, PackMLState.Execute);

        Assert.True(trigger.TryFire(write, _image, out _));
        Assert.True(trigger.TryFire(write with { Source = WriteSource.Client }, _image, out _));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Trigger_reads_the_generation_from_the_host_that_binds_the_service()
    {
        var fixture = new HostFixture();
        var registry = new ServiceHostRegistry();
        registry.Register("host", fixture.Host);
        ServiceExecuteTriggerNode trigger = CompileTrigger(ServiceA);
        trigger.Attach(registry);
        fixture.StartServiceA();

        trigger.TryFire(StateWrite(1, PackMLState.Starting, PackMLState.Execute), _image, out Dictionary<string, object?> outputs);

        Assert.Equal(fixture.Host.ExecuteGeneration(HostFixture.CtrlPath(1, "A")), outputs["generation"]);
        Assert.True((long)outputs["generation"]! > 0);
    }

    [Fact]
    [Trait("AC", "12")]
    public void Trigger_busy_key_combines_index_and_generation()
    {
        ServiceExecuteTriggerNode trigger = CompileTrigger(ServiceA);

        Assert.Equal("2:7", trigger.BusyKey(new Dictionary<string, object?> { ["index"] = 2L, ["generation"] = 7L }));
        Assert.Equal(":7", trigger.BusyKey(new Dictionary<string, object?> { ["index"] = null, ["generation"] = 7L }));
        Assert.Equal("2:", trigger.BusyKey(new Dictionary<string, object?> { ["index"] = 2L, ["generation"] = null }));
    }

    [Fact]
    [Trait("AC", "12")]
    public void Other_memory_triggers_keep_the_default_busy_key()
    {
        var document = new WorkflowDocument { Nodes = [Node("trigger", "trigger.onClientWrite", ("symbol", "GVL_Test.Modules[1].ServiceParam.A.Value"))] };
        CompiledWorkflow compiled = WorkflowCompiler.Compile("t", document, NodeCatalog.Default, _program.Symbols, out _)!;

        Assert.Equal(string.Empty, ((MemoryTriggerNode)compiled.Nodes.Single().Node).BusyKey(new Dictionary<string, object?>()));
    }

    [Fact]
    [Trait("AC", "12")]
    public async Task Overridden_service_waits_for_complete_service()
    {
        await LoadHostAsync();
        Assert.Empty(_runtime.Load("override", CompleteAfterDelay(500)));

        await PulseAsync(2, "A", PackMLCommand.Start, PackMLState.Execute);
        await Task.Delay(300);

        Assert.Equal(PackMLState.Execute, State(2, "A"));
        await WaitForStateAsync(2, "A", PackMLState.Complete);
        await WaitUntilAsync(() => Value(2, "A") == 42, "the workflow to write the mark");
        Assert.Equal(0, Value(1, "A"));
    }

    [Fact]
    [Trait("AC", "12")]
    public async Task Service_that_no_workflow_overrides_keeps_the_default_delay()
    {
        await LoadHostAsync();
        Assert.Empty(_runtime.Load(
            "override",
            CompleteAfterDelay(10_000, "GVL_Test.Modules[1].ServiceCtrl.A", "GVL_Test.Modules[1].ServiceParam.A.Value")));

        await PulseAsync(2, "A", PackMLCommand.Start, PackMLState.Execute);

        await WaitForStateAsync(2, "A", PackMLState.Complete);
    }

    [Fact]
    [Trait("AC", "12")]
    public async Task Override_unloaded_falls_back_to_default_delay()
    {
        await LoadHostAsync();
        Assert.Empty(_runtime.Load("override", CompleteAfterDelay(10_000)));
        await PulseAsync(1, "A", PackMLCommand.Start, PackMLState.Execute);

        _runtime.Unload("override");

        await WaitForStateAsync(1, "A", PackMLState.Complete);
        Assert.Equal(0, Value(1, "A"));
    }

    [Fact]
    [Trait("AC", "12")]
    public async Task Override_loaded_during_execute_uses_default_for_that_execute()
    {
        await LoadHostAsync(delayMs: "400");
        await PulseAsync(1, "A", PackMLCommand.Start, PackMLState.Execute);

        Assert.Empty(_runtime.Load("override", CompleteAfterDelay(10_000)));

        await WaitForStateAsync(1, "A", PackMLState.Complete);
        Assert.DoesNotContain(_monitor.Events, flowEvent => flowEvent.Kind == FlowEventKind.RunStarted && flowEvent.Workflow == "override");
    }

    [Fact]
    [Trait("AC", "12")]
    public async Task Next_execute_after_the_override_was_loaded_is_handled_by_the_workflow()
    {
        await LoadHostAsync(delayMs: "60000");
        await PulseAsync(1, "A", PackMLCommand.Start, PackMLState.Execute);
        Assert.Empty(_runtime.Load("override", CompleteAfterDelay(100)));

        await RestartServiceAsync(1, "A");

        await WaitForStateAsync(1, "A", PackMLState.Complete);
        await WaitUntilAsync(() => Value(1, "A") == 42, "the workflow to write the mark");
    }

    [Fact]
    [Trait("AC", "12")]
    public async Task Complete_service_outside_execute_fails_run()
    {
        await LoadHostAsync();
        var document = new WorkflowDocument
        {
            Nodes =
            [
                Node("trigger", "trigger.onClientWrite", ("symbol", "GVL_Test.Modules[1].ServiceParam.B.Value")),
                Node("complete", "action.completeService", ("service", HostFixture.CtrlPath(1, "A"))),
            ],
            Links = [Link("trigger", "then", "complete", "exec")],
        };
        Assert.Empty(_runtime.Load("outside", document));

        Write("GVL_Test.Modules[1].ServiceParam.B.Value", 1L);

        FlowEvent failure = await _monitor.WaitForAsync(flowEvent => flowEvent.Kind == FlowEventKind.RunFailed && flowEvent.Workflow == "outside");
        Assert.Equal("complete", failure.NodeId);
        Assert.Contains("Idle", failure.Error);
    }

    [Fact]
    [Trait("AC", "12")]
    public async Task Complete_service_without_a_host_fails_run()
    {
        var document = new WorkflowDocument
        {
            Nodes =
            [
                Node("trigger", "trigger.onClientWrite", ("symbol", "GVL_Test.Modules[1].ServiceParam.B.Value")),
                Node("complete", "action.completeService", ("service", HostFixture.CtrlPath(1, "A"))),
            ],
            Links = [Link("trigger", "then", "complete", "exec")],
        };
        Assert.Empty(_runtime.Load("nohost", document));

        Write("GVL_Test.Modules[1].ServiceParam.B.Value", 1L);

        FlowEvent failure = await _monitor.WaitForAsync(flowEvent => flowEvent.Kind == FlowEventKind.RunFailed && flowEvent.Workflow == "nohost");
        Assert.Equal($"No service.host runs {HostFixture.CtrlPath(1, "A")}.", failure.Error?.Split(": ").Last());
    }

    [Fact]
    [Trait("AC", "12")]
    public async Task Abort_service_aborts_overridden_service()
    {
        await LoadHostAsync();
        Assert.Empty(_runtime.Load("override", AbortOnExecute()));
        string ctrl = HostFixture.CtrlPath(1, "A");

        Write($"{ctrl}.PackMLIn.eCommand", (long)PackMLCommand.Start);
        Write($"{ctrl}.PackMLIn.bCommandChangeRequest", true);

        await WaitForStateAsync(1, "A", PackMLState.Aborted);
        Assert.Equal((long)PackMLState.Aborting, Read("GVL_Test.Modules[1].ServiceStatus.A.PackMLOut.eStatePrevious"));
    }

    [Fact]
    [Trait("AC", "12")]
    public async Task Stale_run_cannot_complete_next_execute()
    {
        await LoadHostAsync();
        Assert.Empty(_runtime.Load("override", CompleteAfterDelay(700)));
        await PulseAsync(1, "A", PackMLCommand.Start, PackMLState.Execute);
        await _monitor.WaitForAsync(flowEvent => flowEvent.Kind == FlowEventKind.RunStarted && flowEvent.Workflow == "override");

        await RestartServiceAsync(1, "A");

        FlowEvent stale = await _monitor.WaitForAsync(flowEvent => flowEvent.Kind == FlowEventKind.RunFailed && flowEvent.Workflow == "override");
        Assert.Equal("complete", stale.NodeId);
        Assert.Contains("stale", stale.Error);
        await WaitForStateAsync(1, "A", PackMLState.Complete);
        await WaitUntilAsync(() => Value(1, "A") == 42, "the new run to write the mark");
    }

    [Fact]
    [Trait("AC", "12")]
    public async Task Stale_run_after_host_reload_cannot_complete_next_execute()
    {
        await LoadHostAsync();
        Assert.Empty(_runtime.Load("override", CompleteAfterDelay(700)));
        await PulseAsync(1, "A", PackMLCommand.Start, PackMLState.Execute);
        await _monitor.WaitForAsync(flowEvent => flowEvent.Kind == FlowEventKind.RunStarted && flowEvent.Workflow == "override");

        await LoadHostAsync();
        await PulseAsync(1, "A", PackMLCommand.Start, PackMLState.Execute);

        FlowEvent stale = await _monitor.WaitForAsync(flowEvent => flowEvent.Kind == FlowEventKind.RunFailed && flowEvent.Workflow == "override");
        Assert.Contains("stale", stale.Error);
        await WaitForStateAsync(1, "A", PackMLState.Complete);
    }

    [Fact]
    [Trait("AC", "12")]
    public async Task Runs_with_different_busy_keys_overlap()
    {
        await LoadHostAsync();
        Assert.Empty(_runtime.Load("override", CompleteAfterDelay(500)));

        await PulseAsync(1, "A", PackMLCommand.Start, PackMLState.Execute);
        await PulseAsync(2, "A", PackMLCommand.Start, PackMLState.Execute);

        await WaitForStateAsync(1, "A", PackMLState.Complete);
        await WaitForStateAsync(2, "A", PackMLState.Complete);
        Assert.DoesNotContain(_monitor.Events, flowEvent => flowEvent.Kind == FlowEventKind.RunSkipped);
        await WaitUntilAsync(() => Value(1, "A") == 42 && Value(2, "A") == 42, "both runs to write their marks");
    }

    [Fact]
    [Trait("AC", "3")]
    public async Task On_start_runs_once_per_load()
    {
        WorkflowDocument document = OnStartWrites("GVL_Test.Modules[1].ServiceParam.A.Value", ("value", "7"));

        Assert.Empty(_runtime.Load("start", document));
        await _monitor.WaitForAsync(flowEvent => flowEvent.Kind == FlowEventKind.RunCompleted && flowEvent.Workflow == "start");
        await Task.Delay(100);

        Assert.Equal(7, Value(1, "A"));
        Assert.Equal(1, _monitor.Events.Count(flowEvent => flowEvent.Kind == FlowEventKind.RunStarted && flowEvent.Workflow == "start"));

        Assert.Empty(_runtime.Load("start", document));
        await WaitUntilAsync(() => _monitor.Events.Count(flowEvent => flowEvent.Kind == FlowEventKind.RunCompleted && flowEvent.Workflow == "start") == 2, "the second load to run");
        await Task.Delay(100);
        Assert.Equal(2, _monitor.Events.Count(flowEvent => flowEvent.Kind == FlowEventKind.RunStarted && flowEvent.Workflow == "start"));
    }

    [Fact]
    [Trait("AC", "3")]
    public async Task Placeholder_nodes_write_and_read_every_module_by_index()
    {
        var document = new WorkflowDocument
        {
            Nodes =
            [
                Node("start", "trigger.onStart"),
                Node("loop", "flow.forLoop", ("from", "1"), ("to", "2")),
                Node("writeA", "action.writeSymbol", ("symbol", ValueA), ("value", "9")),
                Node("read", "data.readSymbol", ("symbol", ValueA)),
                Node("writeB", "action.writeSymbol", ("symbol", ValueB)),
            ],
            Links =
            [
                Link("start", "then", "loop", "exec"),
                Link("loop", "body", "writeA", "exec"),
                Link("writeA", "then", "writeB", "exec"),
                Link("loop", "index", "writeA", "index"),
                Link("loop", "index", "read", "index"),
                Link("loop", "index", "writeB", "index"),
                Link("read", "value", "writeB", "value"),
            ],
        };

        Assert.Empty(_runtime.Load("indexed", document));

        await WaitUntilAsync(() => Value(1, "B") == 9 && Value(2, "B") == 9, "both modules to be written");
        Assert.Equal(9, Value(1, "A"));
        Assert.Equal(9, Value(2, "A"));
    }

    [Fact]
    [Trait("AC", "3")]
    public async Task Placeholder_without_index_input_fails_run()
    {
        Assert.Empty(_runtime.Load("noindex", OnStartWrites(ValueA, ("value", "1"))));

        FlowEvent failure = await _monitor.WaitForAsync(flowEvent => flowEvent.Kind == FlowEventKind.RunFailed && flowEvent.Workflow == "noindex");

        Assert.Equal("write", failure.NodeId);
        Assert.Contains("needs input 'index'", failure.Error);
    }

    [Fact]
    [Trait("AC", "3")]
    public async Task Placeholder_index_outside_the_array_fails_run()
    {
        var document = new WorkflowDocument
        {
            Nodes =
            [
                Node("start", "trigger.onStart"),
                Node("three", "data.constant", ("value", "3")),
                Node("write", "action.writeSymbol", ("symbol", ValueA), ("value", "1")),
            ],
            Links = [Link("start", "then", "write", "exec"), Link("three", "value", "write", "index")],
        };
        Assert.Empty(_runtime.Load("range", document));

        FlowEvent failure = await _monitor.WaitForAsync(flowEvent => flowEvent.Kind == FlowEventKind.RunFailed && flowEvent.Workflow == "range");

        Assert.Contains("Index 3 is outside 1..2", failure.Error);
    }

    [Theory]
    [InlineData("GVL_Test.Modules[#].ServiceParam[#]", "Only one [#] is allowed.")]
    [InlineData("GVL_Test.Modules[1].ServiceParam.A.Value[#]", "one-dimensional array")]
    [InlineData("GVL_Test.Modules[#].ServiceParam", "scalar")]
    [Trait("AC", "3")]
    public void Write_symbol_with_invalid_placeholder_reports_diagnostic(string symbol, string expectedMessage)
    {
        IReadOnlyList<WorkflowDiagnostic> diagnostics = _runtime.Load("bad", OnStartWrites(symbol, ("value", "1")));

        Assert.Contains(expectedMessage, Assert.Single(diagnostics).Message);
    }

    [Fact]
    [Trait("AC", "13")]
    public void Failed_run_is_logged_as_a_warning_by_the_live_hub()
    {
        var logger = new RecordingLogger();
        var hub = new LiveHub(logger);

        hub.Publish(new FlowEvent(FlowEventKind.RunCompleted, "wf", 1, "node"));
        hub.Publish(new FlowEvent(FlowEventKind.RunFailed, "wf", 2, "host", Error: "boom"));

        (LogLevel level, string message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("wf", message);
        Assert.Contains("host", message);
        Assert.Contains("boom", message);
    }

    private sealed class RecordingLogger : ILogger<LiveHub>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class NoSimulation : ISimulationSink
    {
        public void Invoke(string target, string action, object? value)
        {
        }
    }

    private sealed class RecordingMonitor : IFlowMonitor
    {
        private readonly ConcurrentQueue<FlowEvent> _events = new();

        public IEnumerable<FlowEvent> Events => _events.ToArray();

        public void Publish(FlowEvent flowEvent) => _events.Enqueue(flowEvent);

        public async Task<FlowEvent> WaitForAsync(Func<FlowEvent, bool> match)
        {
            FlowEvent? found = null;
            await WaitUntilAsync(() => (found = _events.FirstOrDefault(match)) is not null, "a flow event");
            return found!;
        }
    }
}
