using System.Collections.Concurrent;
using System.Threading.Channels;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.Flow;

/// <summary>
/// Hosts the loaded workflows: watches process-image writes and timers, and starts a <see cref="FlowRun"/>
/// whenever a trigger fires. Write events are queued and dispatched on one background loop, so a workflow
/// write never re-enters trigger matching on the writer's call stack.
/// </summary>
public sealed class FlowRuntime : IAsyncDisposable
{
    private readonly SymbolTable _symbols;
    private readonly NodeCatalog _catalog;
    private readonly FlowServices _services;
    private readonly Channel<MemoryWrite> _writes = Channel.CreateUnbounded<MemoryWrite>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, LoadedWorkflow> _workflows = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _dispatchLoop;
    private long _nextRunId;

    public FlowRuntime(ProcessImage image, SymbolTable symbols, NodeCatalog catalog, IFlowMonitor monitor, ISimulationSink simulation)
    {
        _symbols = symbols;
        _catalog = catalog;
        _services = new FlowServices(image, simulation, monitor);
        image.Written += write => _writes.Writer.TryWrite(write);
        _dispatchLoop = Task.Run(DispatchWritesAsync);
    }

    public IReadOnlyCollection<string> LoadedWorkflows => _workflows.Keys.ToList();

    /// <summary>Compiles and (re)starts a workflow. On errors the previously loaded version keeps running.</summary>
    public IReadOnlyList<WorkflowDiagnostic> Load(string name, WorkflowDocument document)
    {
        CompiledWorkflow? compiled = WorkflowCompiler.Compile(name, document, _catalog, _symbols, out List<WorkflowDiagnostic> diagnostics);
        if (compiled is null)
            return diagnostics;

        var loaded = new LoadedWorkflow(compiled, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
        if (_workflows.TryRemove(name, out LoadedWorkflow? previous))
            previous.Stop();
        _workflows[name] = loaded;

        foreach (CompiledNode trigger in compiled.Nodes.Where(node => node.Node is CyclicTriggerNode))
            _ = RunCyclicTriggerAsync(loaded, trigger, ((CyclicTriggerNode)trigger.Node).Interval);
        return diagnostics;
    }

    public void Unload(string name)
    {
        if (_workflows.TryRemove(name, out LoadedWorkflow? workflow))
            workflow.Stop();
    }

    public async ValueTask DisposeAsync()
    {
        _writes.Writer.TryComplete();
        await _shutdown.CancelAsync();
        foreach (LoadedWorkflow workflow in _workflows.Values)
            workflow.Stop();
        await _dispatchLoop;
        _shutdown.Dispose();
    }

    private async Task DispatchWritesAsync()
    {
        await foreach (MemoryWrite write in _writes.Reader.ReadAllAsync())
        {
            foreach (LoadedWorkflow workflow in _workflows.Values)
            {
                foreach (CompiledNode trigger in workflow.Compiled.Nodes)
                {
                    if (trigger.Node is MemoryTriggerNode memoryTrigger
                        && memoryTrigger.TryFire(write, _services.Image, out Dictionary<string, object?> outputs))
                    {
                        StartRun(workflow, trigger, outputs);
                    }
                }
            }
        }
    }

    private async Task RunCyclicTriggerAsync(LoadedWorkflow workflow, CompiledNode trigger, TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(workflow.Cancellation))
                StartRun(workflow, trigger, new Dictionary<string, object?>());
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// A trigger runs at most one flow at a time; firing while busy is reported and dropped,
    /// the way a busy PLC function block ignores a new request edge.
    /// </summary>
    private void StartRun(LoadedWorkflow workflow, CompiledNode trigger, IReadOnlyDictionary<string, object?> outputs)
    {
        long runId = Interlocked.Increment(ref _nextRunId);
        string name = workflow.Compiled.Name;
        if (!workflow.BusyTriggers.TryAdd(trigger.Id, 0))
        {
            _services.Monitor.Publish(new FlowEvent(FlowEventKind.RunSkipped, name, runId, trigger.Id));
            return;
        }

        _ = Task.Run(async () =>
        {
            var run = new FlowRun(runId, workflow.Compiled, _services, workflow.Cancellation);
            _services.Monitor.Publish(new FlowEvent(FlowEventKind.RunStarted, name, runId, trigger.Id));
            try
            {
                await run.StartAsync(trigger, outputs);
                _services.Monitor.Publish(new FlowEvent(FlowEventKind.RunCompleted, name, runId, trigger.Id));
            }
            catch (OperationCanceledException)
            {
                _services.Monitor.Publish(new FlowEvent(FlowEventKind.RunFailed, name, runId, trigger.Id, Error: "Stopped because the workflow was reloaded or the simulator shut down."));
            }
            catch (Exception exception)
            {
                string? failedNode = (exception as FlowNodeException)?.NodeId ?? trigger.Id;
                _services.Monitor.Publish(new FlowEvent(FlowEventKind.RunFailed, name, runId, failedNode, Error: exception.Message));
            }
            finally
            {
                workflow.BusyTriggers.TryRemove(trigger.Id, out _);
            }
        });
    }

    private sealed class LoadedWorkflow(CompiledWorkflow compiled, CancellationTokenSource cancellation)
    {
        public CompiledWorkflow Compiled { get; } = compiled;

        public CancellationToken Cancellation { get; } = cancellation.Token;

        public ConcurrentDictionary<string, byte> BusyTriggers { get; } = new();

        // Not disposed: runs still in flight observe the token after the workflow is replaced.
        public void Stop() => cancellation.Cancel();
    }
}
