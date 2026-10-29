using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.Flow;

/// <summary>A node instance with its parameters already parsed and validated.</summary>
public abstract class RuntimeNode;

/// <summary>A node on the exec path. Returns the exec output to continue with, or null to end this branch.</summary>
public abstract class ExecNode : RuntimeNode
{
    public abstract Task<string?> ExecuteAsync(NodeExecution execution);
}

/// <summary>A side-effect free node, evaluated each time a downstream node reads one of its outputs.</summary>
public abstract class PureNode : RuntimeNode
{
    public abstract void Evaluate(NodeExecution execution);
}

/// <summary>Starts a run when PLC memory is written; its outputs are the values seen in that write.</summary>
public abstract class MemoryTriggerNode : RuntimeNode
{
    public abstract bool TryFire(MemoryWrite write, ProcessImage image, out Dictionary<string, object?> outputs);
}

/// <summary>Starts a run at a fixed interval, like a PLC task cycle.</summary>
public sealed class CyclicTriggerNode(TimeSpan interval) : RuntimeNode
{
    public TimeSpan Interval { get; } = interval;
}

/// <summary>What a node can see and do while it runs.</summary>
public sealed class NodeExecution
{
    private readonly FlowRun _run;
    private readonly CompiledNode _node;

    internal NodeExecution(FlowRun run, CompiledNode node)
    {
        _run = run;
        _node = node;
    }

    public ProcessImage Image => _run.Services.Image;

    public ISimulationSink Simulation => _run.Services.Simulation;

    public CancellationToken Cancellation => _run.Cancellation;

    public object? Input(string port) => _run.ResolveInput(_node, port);

    public void Output(string port, object? value) => _run.SetOutput(_node, port, value);

    /// <summary>Runs everything linked to one of this node's exec outputs and waits until it finishes.</summary>
    public Task RunAsync(string execPort) => _run.RunExecOutputAsync(_node, execPort);
}
