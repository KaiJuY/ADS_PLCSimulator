using AdsPlcSimulator.Core.PackML;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.Flow;

public sealed record FlowServices(ProcessImage Image, ISimulationSink Simulation, IFlowMonitor Monitor, SymbolTable Symbols, ServiceHostRegistry Registry);

public sealed class FlowNodeException(string nodeId, string message, Exception innerException)
    : Exception(message, innerException)
{
    public string NodeId { get; } = nodeId;
}

/// <summary>
/// One execution of a workflow, started by one trigger. Nodes inside a run execute one at a time,
/// so the output table needs no locking.
/// </summary>
internal sealed class FlowRun
{
    /// <summary>Guards against exec loops without a delay, which would otherwise spin forever.</summary>
    private const int MaxNodeExecutions = 100_000;

    private readonly CompiledWorkflow _workflow;
    private readonly Dictionary<(string NodeId, string Port), object?> _outputs = [];
    private int _executions;

    public FlowRun(long id, CompiledWorkflow workflow, FlowServices services, CancellationToken cancellation)
    {
        Id = id;
        _workflow = workflow;
        Services = services;
        Cancellation = cancellation;
    }

    public long Id { get; }

    public FlowServices Services { get; }

    public CancellationToken Cancellation { get; }

    public async Task StartAsync(CompiledNode trigger, IReadOnlyDictionary<string, object?> triggerOutputs)
    {
        foreach ((string port, object? value) in triggerOutputs)
            _outputs[(trigger.Id, port)] = value;
        Publish(FlowEventKind.NodeCompleted, trigger.Id, OutputsOf(trigger));
        await RunExecOutputAsync(trigger, "then");
    }

    public async Task RunExecOutputAsync(CompiledNode node, string execPort)
    {
        foreach (CompiledNode target in _workflow.TargetsOf(node, execPort))
            await RunChainAsync(target);
    }

    public object? ResolveInput(CompiledNode node, string port)
    {
        if (_workflow.DataSources.TryGetValue((node.Id, port), out DataSource? source))
        {
            if (source.Node.Node is PureNode pure)
                EvaluatePure(source.Node, pure);

            return _outputs.TryGetValue((source.Node.Id, source.Port), out object? value)
                ? value
                : throw new InvalidOperationException($"Input '{port}' reads '{source.Port}' of node '{source.Node.Id}', which has not run yet in this flow.");
        }

        string? parameter = node.Params.GetValueOrDefault(port);
        if (string.IsNullOrWhiteSpace(parameter))
            parameter = node.Definition.FindParam(port)?.DefaultValue;
        if (!string.IsNullOrWhiteSpace(parameter))
            return PlcValue.ParseLiteral(parameter);

        return node.Definition.FindInput(port)?.Optional == true
            ? null
            : throw new InvalidOperationException($"Input '{port}' has no link and no value.");
    }

    public void SetOutput(CompiledNode node, string port, object? value) => _outputs[(node.Id, port)] = value;

    /// <summary>Follows single-target exec links in a loop so long chains do not nest awaits.</summary>
    private async Task RunChainAsync(CompiledNode start)
    {
        CompiledNode? current = start;
        while (current is not null)
        {
            string? next = await ExecuteAsync(current);
            if (next is null)
                return;

            IReadOnlyList<CompiledNode> targets = _workflow.TargetsOf(current, next);
            if (targets.Count == 1)
            {
                current = targets[0];
                continue;
            }

            foreach (CompiledNode target in targets)
                await RunChainAsync(target);
            return;
        }
    }

    private async Task<string?> ExecuteAsync(CompiledNode node)
    {
        Cancellation.ThrowIfCancellationRequested();
        if (++_executions > MaxNodeExecutions)
            throw new FlowNodeException(node.Id, $"Run exceeded {MaxNodeExecutions} node executions; is there an exec loop without a delay?", new InvalidOperationException());

        if (node.Node is not ExecNode execNode)
            throw new InvalidOperationException($"Node '{node.Id}' ({node.Definition.Type}) cannot be on the exec path.");

        Publish(FlowEventKind.NodeStarted, node.Id);
        string? next;
        try
        {
            next = await execNode.ExecuteAsync(new NodeExecution(this, node));
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not FlowNodeException)
        {
            throw Fail(node, exception);
        }

        Publish(FlowEventKind.NodeCompleted, node.Id, OutputsOf(node));
        return next;
    }

    private void EvaluatePure(CompiledNode node, PureNode pure)
    {
        try
        {
            pure.Evaluate(new NodeExecution(this, node));
        }
        catch (Exception exception) when (exception is not FlowNodeException)
        {
            throw Fail(node, exception);
        }

        Publish(FlowEventKind.NodeCompleted, node.Id, OutputsOf(node));
    }

    private FlowNodeException Fail(CompiledNode node, Exception exception)
    {
        Publish(FlowEventKind.NodeFailed, node.Id, error: exception.Message);
        return new FlowNodeException(node.Id, $"Node '{node.Id}' ({node.Definition.Title}): {exception.Message}", exception);
    }

    private Dictionary<string, object?> OutputsOf(CompiledNode node) =>
        node.Definition.Outputs
            .Where(port => port.Kind == PortKind.Data && _outputs.ContainsKey((node.Id, port.Name)))
            .ToDictionary(port => port.Name, port => _outputs[(node.Id, port.Name)]);

    private void Publish(FlowEventKind kind, string nodeId, IReadOnlyDictionary<string, object?>? outputs = null, string? error = null) =>
        Services.Monitor.Publish(new FlowEvent(kind, _workflow.Name, Id, nodeId, outputs, error));
}
