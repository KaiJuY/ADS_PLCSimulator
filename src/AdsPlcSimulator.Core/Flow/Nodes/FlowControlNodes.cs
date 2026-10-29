using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.Flow.Nodes;

public sealed class BranchNode : ExecNode
{
    public static readonly NodeDefinition Definition = new(
        "flow.branch",
        "Flow",
        "Branch",
        "Continues on 'true' or 'false' depending on the condition.",
        [new("exec", PortKind.Exec, "exec"), new("condition", PortKind.Data, "condition")],
        [new("true", PortKind.Exec, "true"), new("false", PortKind.Exec, "false")],
        [],
        _ => new BranchNode());

    public override Task<string?> ExecuteAsync(NodeExecution execution) =>
        Task.FromResult<string?>(PlcValue.ToBoolean(execution.Input("condition")) ? "true" : "false");
}

public sealed class ForLoopNode : ExecNode
{
    public static readonly NodeDefinition Definition = new(
        "flow.forLoop",
        "Flow",
        "For Loop",
        "Runs 'body' once per index from 'from' to 'to' (inclusive), then continues on 'completed'.",
        [new("exec", PortKind.Exec, "exec"), new("from", PortKind.Data, "from"), new("to", PortKind.Data, "to")],
        [new("body", PortKind.Exec, "body"), new("index", PortKind.Data, "index"), new("completed", PortKind.Exec, "completed")],
        [new("from", ParamKind.Number, "From", "1"), new("to", ParamKind.Number, "To", "10")],
        _ => new ForLoopNode());

    public override async Task<string?> ExecuteAsync(NodeExecution execution)
    {
        long from = PlcValue.ToInt64(execution.Input("from"));
        long to = PlcValue.ToInt64(execution.Input("to"));
        for (long index = from; index <= to; index++)
        {
            execution.Output("index", index);
            await execution.RunAsync("body");
        }

        return "completed";
    }
}

public sealed class DelayNode : ExecNode
{
    public static readonly NodeDefinition Definition = new(
        "flow.delay",
        "Flow",
        "Delay",
        "Waits, e.g. to model how long a door or an axis takes to move.",
        [new("exec", PortKind.Exec, "exec"), new("ms", PortKind.Data, "ms")],
        [new("then", PortKind.Exec, "then")],
        [new("ms", ParamKind.Number, "Delay (ms)", "1000")],
        _ => new DelayNode());

    public override async Task<string?> ExecuteAsync(NodeExecution execution)
    {
        double milliseconds = PlcValue.ToDouble(execution.Input("ms"));
        if (milliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(execution), $"Delay of {milliseconds} ms is negative.");
        await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), execution.Cancellation);
        return "then";
    }
}
