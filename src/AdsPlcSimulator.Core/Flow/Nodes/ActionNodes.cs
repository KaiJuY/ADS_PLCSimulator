using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.Flow.Nodes;

public sealed class WriteSymbolNode(IndexedSymbol symbol) : ExecNode
{
    public static readonly NodeDefinition Definition = new(
        "action.writeSymbol",
        "Action",
        "Write Symbol",
        "Writes a value into a PLC variable. ADS clients see it immediately. A [#] in the symbol is replaced by the 'index' input.",
        [new("exec", PortKind.Exec, "exec"), new("value", PortKind.Data, "value"), new("index", PortKind.Data, "index", Optional: true)],
        [new("then", PortKind.Exec, "then")],
        [new("symbol", ParamKind.Symbol, "Symbol"), new("value", ParamKind.Text, "Value")],
        settings => new WriteSymbolNode(settings.IndexedSymbol("symbol", scalar: true)));

    public override Task<string?> ExecuteAsync(NodeExecution execution)
    {
        SymbolRef target = symbol.Resolve(execution.Symbols, execution.Input("index"));
        execution.Image.WriteValue(target, execution.Input("value"), WriteSource.Workflow);
        return Task.FromResult<string?>("then");
    }
}

public sealed class SimulationInvokeNode(string target, string action) : ExecNode
{
    public static readonly NodeDefinition Definition = new(
        "sim.invoke",
        "Simulation",
        "Simulation Action",
        "Tells the simulation view to animate, e.g. target 'Chamber1.Door' action 'Open'. Does not wait for the animation.",
        [new("exec", PortKind.Exec, "exec"), new("value", PortKind.Data, "value", Optional: true)],
        [new("then", PortKind.Exec, "then")],
        [new("target", ParamKind.Text, "Target"), new("action", ParamKind.Text, "Action"), new("value", ParamKind.Text, "Value")],
        settings => new SimulationInvokeNode(Required(settings, "target"), Required(settings, "action")));

    public override Task<string?> ExecuteAsync(NodeExecution execution)
    {
        execution.Simulation.Invoke(target, action, execution.Input("value"));
        return Task.FromResult<string?>("then");
    }

    private static string Required(NodeSettings settings, string name)
    {
        string value = settings.Text(name);
        return value.Length > 0 ? value : throw new NodeConfigurationException($"Parameter '{name}' is required.");
    }
}
