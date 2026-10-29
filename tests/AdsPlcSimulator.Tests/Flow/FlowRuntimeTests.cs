using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AdsPlcSimulator.Core.Flow;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.Flow;

public class FlowRuntimeTests : IAsyncLifetime
{
    private const string Declarations = """
        VAR_GLOBAL
            nRequest : UDINT;
            nResponse : UDINT;
            bDoorOpen : BOOL;
            nSum : DINT;
            rTemperature : REAL := 20.0;
        END_VAR
        """;

    private readonly RecordingMonitor _monitor = new();
    private readonly RecordingSimulation _simulation = new();
    private PlcProgram _program = null!;
    private ProcessImage _image = null!;
    private FlowRuntime _runtime = null!;

    public Task InitializeAsync()
    {
        _program = TestPaths.BuildFromText(Declarations);
        _image = _program.CreateProcessImage();
        _runtime = new FlowRuntime(_image, _program.Symbols, NodeCatalog.Default, _monitor, _simulation);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _runtime.DisposeAsync();

    private SymbolRef Symbol(string name) => _program.Symbols.Resolve("GVL_Test." + name);

    private static WorkflowNode Node(string id, string type, params (string Key, string Value)[] parameters) =>
        new() { Id = id, Type = type, Params = parameters.ToDictionary(pair => pair.Key, pair => pair.Value) };

    private static WorkflowLink Link(string from, string fromPort, string to, string toPort) => new(from, fromPort, to, toPort);

    [Fact]
    public async Task Client_write_runs_handshake_that_echoes_request_and_animates_door()
    {
        var document = new WorkflowDocument
        {
            Nodes =
            [
                Node("trigger", "trigger.onClientWrite", ("symbol", "GVL_Test.nRequest")),
                Node("isNew", "logic.compare", ("operator", ">"), ("b", "0")),
                Node("branch", "flow.branch"),
                Node("open", "sim.invoke", ("target", "Chamber1.Door"), ("action", "Open")),
                Node("setDoor", "action.writeSymbol", ("symbol", "GVL_Test.bDoorOpen"), ("value", "TRUE")),
                Node("respond", "action.writeSymbol", ("symbol", "GVL_Test.nResponse")),
            ],
            Links =
            [
                Link("trigger", "value", "isNew", "a"),
                Link("isNew", "result", "branch", "condition"),
                Link("trigger", "then", "branch", "exec"),
                Link("branch", "true", "open", "exec"),
                Link("open", "then", "setDoor", "exec"),
                Link("setDoor", "then", "respond", "exec"),
                Link("trigger", "value", "respond", "value"),
            ],
        };
        Assert.Empty(_runtime.Load("handshake", document));

        _image.WriteValue(Symbol("nRequest"), 7, WriteSource.Client);
        await _monitor.WaitForAsync(FlowEventKind.RunCompleted);

        Assert.Equal(7L, _image.ReadValue(Symbol("nResponse")));
        Assert.Equal(true, _image.ReadValue(Symbol("bDoorOpen")));
        Assert.Equal(("Chamber1.Door", "Open"), _simulation.Calls.Single());
    }

    [Fact]
    public async Task Workflow_writes_do_not_fire_client_write_triggers_but_fire_change_triggers()
    {
        var document = new WorkflowDocument
        {
            Nodes =
            [
                Node("clientOnly", "trigger.onClientWrite", ("symbol", "GVL_Test.nResponse")),
                Node("onChange", "trigger.onChange", ("symbol", "GVL_Test.nResponse")),
                Node("record", "action.writeSymbol", ("symbol", "GVL_Test.nSum")),
            ],
            Links = [Link("onChange", "then", "record", "exec"), Link("onChange", "previous", "record", "value")],
        };
        Assert.Empty(_runtime.Load("change", document));

        _image.WriteValue(Symbol("nResponse"), 3, WriteSource.Workflow);
        await _monitor.WaitForAsync(FlowEventKind.RunCompleted);

        Assert.Equal(0L, _image.ReadValue(Symbol("nSum")));
        Assert.DoesNotContain(_monitor.Events, flowEvent => flowEvent.Kind == FlowEventKind.RunStarted && flowEvent.NodeId == "clientOnly");
    }

    [Fact]
    public async Task For_loop_accumulates_with_arithmetic()
    {
        var document = new WorkflowDocument
        {
            Nodes =
            [
                Node("trigger", "trigger.onClientWrite", ("symbol", "GVL_Test.nRequest")),
                Node("loop", "flow.forLoop", ("from", "1"), ("to", "4")),
                Node("current", "data.readSymbol", ("symbol", "GVL_Test.nSum")),
                Node("add", "math.arithmetic", ("operator", "+")),
                Node("store", "action.writeSymbol", ("symbol", "GVL_Test.nSum")),
            ],
            Links =
            [
                Link("trigger", "then", "loop", "exec"),
                Link("loop", "body", "store", "exec"),
                Link("current", "value", "add", "a"),
                Link("loop", "index", "add", "b"),
                Link("add", "result", "store", "value"),
            ],
        };
        Assert.Empty(_runtime.Load("loop", document));

        _image.WriteValue(Symbol("nRequest"), 1, WriteSource.Client);
        await _monitor.WaitForAsync(FlowEventKind.RunCompleted);

        Assert.Equal(10L, _image.ReadValue(Symbol("nSum")));
    }

    [Fact]
    public async Task Trigger_firing_while_busy_is_skipped()
    {
        var document = new WorkflowDocument
        {
            Nodes = [Node("trigger", "trigger.onClientWrite", ("symbol", "GVL_Test.nRequest")), Node("wait", "flow.delay", ("ms", "300"))],
            Links = [Link("trigger", "then", "wait", "exec")],
        };
        Assert.Empty(_runtime.Load("busy", document));

        _image.WriteValue(Symbol("nRequest"), 1, WriteSource.Client);
        await _monitor.WaitForAsync(FlowEventKind.RunStarted);
        _image.WriteValue(Symbol("nRequest"), 2, WriteSource.Client);

        await _monitor.WaitForAsync(FlowEventKind.RunSkipped);
        await _monitor.WaitForAsync(FlowEventKind.RunCompleted);
    }

    [Fact]
    public async Task Failing_node_reports_its_id_and_message()
    {
        var document = new WorkflowDocument
        {
            Nodes =
            [
                Node("trigger", "trigger.onClientWrite", ("symbol", "GVL_Test.nRequest")),
                Node("divide", "math.arithmetic", ("operator", "/"), ("a", "1"), ("b", "0")),
                Node("store", "action.writeSymbol", ("symbol", "GVL_Test.nSum")),
            ],
            Links = [Link("trigger", "then", "store", "exec"), Link("divide", "result", "store", "value")],
        };
        Assert.Empty(_runtime.Load("fail", document));

        _image.WriteValue(Symbol("nRequest"), 1, WriteSource.Client);
        FlowEvent failure = await _monitor.WaitForAsync(FlowEventKind.RunFailed);

        Assert.Equal("divide", failure.NodeId);
        Assert.Contains(_monitor.Events, flowEvent => flowEvent.Kind == FlowEventKind.NodeFailed && flowEvent.NodeId == "divide");
    }

    [Fact]
    public void Invalid_workflow_reports_every_problem_and_is_not_loaded()
    {
        var document = new WorkflowDocument
        {
            Nodes =
            [
                Node("trigger", "trigger.onClientWrite", ("symbol", "GVL_Test.nMissing")),
                Node("mystery", "does.not.exist"),
                Node("store", "action.writeSymbol", ("symbol", "GVL_Test.nSum")),
                Node("compare", "logic.compare", ("operator", "=~")),
            ],
        };

        IReadOnlyList<WorkflowDiagnostic> diagnostics = _runtime.Load("broken", document);

        Assert.Equal(["compare", "mystery", "store", "trigger"], diagnostics.Select(diagnostic => diagnostic.NodeId).Order());
        Assert.Empty(_runtime.LoadedWorkflows);
    }

    [Fact]
    [Trait("AC", "13")]
    public async Task Dispose_waits_for_a_hosted_node_to_finish_stopping()
    {
        var started = new TaskCompletionSource();
        var stopped = new StrongBox<bool>();
        var definition = new NodeDefinition(
            "test.hosted", "Test", "Hosted", "Stops slowly.", [], [], [], _ => new SlowStoppingNode(started, stopped));
        var catalog = new NodeCatalog([.. NodeCatalog.Default.Definitions, definition]);
        var runtime = new FlowRuntime(_image, _program.Symbols, catalog, _monitor, _simulation);
        Assert.Empty(runtime.Load("hosted", new WorkflowDocument { Nodes = [Node("node", "test.hosted")] }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await runtime.DisposeAsync();

        Assert.True(stopped.Value);
    }

    private sealed class SlowStoppingNode(TaskCompletionSource started, StrongBox<bool> stopped) : HostedNode
    {
        public override async Task RunAsync(HostedNodeContext context, CancellationToken cancellation)
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellation);
            }
            finally
            {
                await Task.Delay(300, CancellationToken.None);
                stopped.Value = true;
            }
        }
    }

    private sealed class RecordingMonitor : IFlowMonitor
    {
        private readonly ConcurrentQueue<FlowEvent> _events = new();
        private readonly SemaphoreSlim _published = new(0);

        public IEnumerable<FlowEvent> Events => _events;

        public void Publish(FlowEvent flowEvent)
        {
            _events.Enqueue(flowEvent);
            _published.Release();
        }

        public async Task<FlowEvent> WaitForAsync(FlowEventKind kind)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                FlowEvent? match = _events.FirstOrDefault(flowEvent => flowEvent.Kind == kind);
                if (match is not null)
                    return match;
                await _published.WaitAsync(timeout.Token);
            }
        }
    }

    private sealed class RecordingSimulation : ISimulationSink
    {
        public ConcurrentQueue<(string Target, string Action)> Calls { get; } = new();

        public void Invoke(string target, string action, object? value) => Calls.Enqueue((target, action));
    }
}
