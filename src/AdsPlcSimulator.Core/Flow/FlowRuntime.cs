using System.Collections.Concurrent;
using System.Threading.Channels;
using AdsPlcSimulator.Core.Flow.Nodes;
using AdsPlcSimulator.Core.PackML;
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
    private readonly ConcurrentDictionary<long, Task> _hostedTasks = new();
    private readonly string? _projectDirectory;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _dispatchLoop;
    private long _nextRunId;

    public FlowRuntime(ProcessImage image, SymbolTable symbols, NodeCatalog catalog, IFlowMonitor monitor, ISimulationSink simulation, string? projectDirectory = null)
    {
        _symbols = symbols;
        _catalog = catalog;
        _projectDirectory = projectDirectory;
        _services = new FlowServices(image, simulation, monitor, symbols, Registry);
        image.Written += write => _writes.Writer.TryWrite(write);
        _dispatchLoop = Task.Run(DispatchWritesAsync);
    }

    public ServiceHostRegistry Registry { get; } = new();

    public IReadOnlyCollection<string> LoadedWorkflows => _workflows.Keys.ToList();

    /// <summary>Compiles and (re)starts a workflow. On errors the previously loaded version keeps running.</summary>
    public IReadOnlyList<WorkflowDiagnostic> Load(string name, WorkflowDocument document)
    {
        CompiledWorkflow? compiled = WorkflowCompiler.Compile(name, document, _catalog, _symbols, out List<WorkflowDiagnostic> diagnostics, _projectDirectory);
        if (compiled is null)
            return diagnostics;

        var loaded = new LoadedWorkflow(compiled, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
        if (_workflows.TryRemove(name, out LoadedWorkflow? previous))
            previous.Stop();
        AttachRegistry(compiled);
        _workflows[name] = loaded;

        foreach (CompiledNode trigger in compiled.Nodes.Where(node => node.Node is CyclicTriggerNode))
            _ = RunCyclicTriggerAsync(loaded, trigger, ((CyclicTriggerNode)trigger.Node).Interval);
        foreach (CompiledNode hosted in compiled.Nodes.Where(node => node.Node is HostedNode))
            StartHostedNode(loaded, hosted);
        foreach (CompiledNode trigger in compiled.Nodes.Where(node => node.Node is StartTriggerNode))
            StartRun(loaded, trigger, new Dictionary<string, object?>());
        return diagnostics;
    }

    public void Unload(string name)
    {
        if (_workflows.TryRemove(name, out LoadedWorkflow? workflow))
            workflow.Stop();
        Registry.SetOverrides(name, []);
    }

    public async ValueTask DisposeAsync()
    {
        _writes.Writer.TryComplete();
        await _shutdown.CancelAsync();
        foreach (LoadedWorkflow workflow in _workflows.Values)
            workflow.Stop();
        await _dispatchLoop;
        await Task.WhenAll(_hostedTasks.Values);
        _shutdown.Dispose();
    }

    private void AttachRegistry(CompiledWorkflow compiled)
    {
        foreach (IRegistryAware aware in compiled.Nodes.Select(node => node.Node).OfType<IRegistryAware>())
            aware.Attach(Registry);

        Registry.SetOverrides(
            compiled.Name,
            compiled.Nodes.Select(node => node.Node).OfType<ServiceExecuteTriggerNode>().SelectMany(trigger => trigger.CtrlPaths).ToList());
    }

    private void StartHostedNode(LoadedWorkflow workflow, CompiledNode node)
    {
        long taskId = Interlocked.Increment(ref _nextRunId);
        _hostedTasks[taskId] = Task.Run(async () =>
        {
            string name = workflow.Compiled.Name;
            var context = new HostedNodeContext(_services.Image, Registry, name);
            try
            {
                await ((HostedNode)node.Node).RunAsync(context, workflow.Cancellation);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _services.Monitor.Publish(new FlowEvent(FlowEventKind.RunFailed, name, taskId, node.Id, Error: exception.Message));
            }
            finally
            // Stryker disable once Block: removing a finished task only frees memory; DisposeAsync awaits finished tasks the same way
            {
                _hostedTasks.TryRemove(taskId, out _);
            }
        });
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
        string busyKey = $"{trigger.Id}#{(trigger.Node as MemoryTriggerNode)?.BusyKey(outputs)}";
        if (!workflow.BusyTriggers.TryAdd(busyKey, 0))
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
                workflow.BusyTriggers.TryRemove(busyKey, out _);
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
