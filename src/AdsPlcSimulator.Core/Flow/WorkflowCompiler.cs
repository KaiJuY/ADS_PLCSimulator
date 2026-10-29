using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.Flow;

/// <summary>A problem found while compiling a workflow; <see cref="NodeId"/> is null for workflow-level issues.</summary>
public sealed record WorkflowDiagnostic(string? NodeId, string Message);

public sealed record CompiledNode(string Id, NodeDefinition Definition, RuntimeNode Node, IReadOnlyDictionary<string, string> Params);

public sealed record DataSource(CompiledNode Node, string Port);

/// <summary>A validated workflow with its links indexed for execution.</summary>
public sealed class CompiledWorkflow
{
    private static readonly IReadOnlyList<CompiledNode> NoTargets = [];

    internal CompiledWorkflow(
        string name,
        IReadOnlyList<CompiledNode> nodes,
        Dictionary<(string NodeId, string Port), List<CompiledNode>> execTargets,
        Dictionary<(string NodeId, string Port), DataSource> dataSources)
    {
        Name = name;
        Nodes = nodes;
        ExecTargets = execTargets;
        DataSources = dataSources;
    }

    public string Name { get; }

    public IReadOnlyList<CompiledNode> Nodes { get; }

    internal Dictionary<(string NodeId, string Port), List<CompiledNode>> ExecTargets { get; }

    internal Dictionary<(string NodeId, string Port), DataSource> DataSources { get; }

    public IReadOnlyList<CompiledNode> TargetsOf(CompiledNode node, string execPort) =>
        ExecTargets.TryGetValue((node.Id, execPort), out List<CompiledNode>? targets) ? targets : NoTargets;
}

/// <summary>
/// Validates a <see cref="WorkflowDocument"/> against the node catalog and the PLC symbol table.
/// Reports every problem at once so the editor can mark all offending nodes.
/// </summary>
public static class WorkflowCompiler
{
    public static CompiledWorkflow? Compile(
        string name,
        WorkflowDocument document,
        NodeCatalog catalog,
        SymbolTable symbols,
        out List<WorkflowDiagnostic> diagnostics)
    {
        diagnostics = [];
        var nodes = new Dictionary<string, CompiledNode>();
        foreach (WorkflowNode node in document.Nodes)
        {
            CompiledNode? compiled = CompileNode(node, catalog, symbols, diagnostics);
            if (compiled is null)
                continue;
            if (!nodes.TryAdd(node.Id, compiled))
                diagnostics.Add(new WorkflowDiagnostic(node.Id, $"Node id '{node.Id}' is used twice."));
        }

        var execTargets = new Dictionary<(string, string), List<CompiledNode>>();
        var dataSources = new Dictionary<(string, string), DataSource>();
        foreach (WorkflowLink link in document.Links)
            AddLink(link, nodes, execTargets, dataSources, diagnostics);

        foreach (CompiledNode node in nodes.Values)
            CheckDataInputs(node, dataSources, diagnostics);
        CheckPureNodeCycles(nodes.Values, dataSources, diagnostics);

        if (diagnostics.Count > 0)
            return null;
        return new CompiledWorkflow(name, nodes.Values.ToList(), execTargets, dataSources);
    }

    private static CompiledNode? CompileNode(WorkflowNode node, NodeCatalog catalog, SymbolTable symbols, List<WorkflowDiagnostic> diagnostics)
    {
        NodeDefinition? definition = catalog.Find(node.Type);
        if (definition is null)
        {
            diagnostics.Add(new WorkflowDiagnostic(node.Id, $"Unknown node type '{node.Type}'."));
            return null;
        }

        try
        {
            RuntimeNode runtimeNode = definition.Create(new NodeSettings(definition, node.Params, symbols));
            return new CompiledNode(node.Id, definition, runtimeNode, node.Params);
        }
        catch (NodeConfigurationException exception)
        {
            diagnostics.Add(new WorkflowDiagnostic(node.Id, exception.Message));
            return null;
        }
    }

    private static void AddLink(
        WorkflowLink link,
        Dictionary<string, CompiledNode> nodes,
        Dictionary<(string, string), List<CompiledNode>> execTargets,
        Dictionary<(string, string), DataSource> dataSources,
        List<WorkflowDiagnostic> diagnostics)
    {
        // Links to nodes that failed to compile were already reported on those nodes.
        if (!nodes.TryGetValue(link.FromNode, out CompiledNode? from) || !nodes.TryGetValue(link.ToNode, out CompiledNode? to))
            return;

        PortDefinition? output = from.Definition.FindOutput(link.FromPort);
        PortDefinition? input = to.Definition.FindInput(link.ToPort);
        if (output is null || input is null)
        {
            diagnostics.Add(new WorkflowDiagnostic(to.Id, $"Link {link.FromNode}.{link.FromPort} -> {link.ToNode}.{link.ToPort} uses a port that does not exist."));
            return;
        }

        if (output.Kind != input.Kind)
        {
            diagnostics.Add(new WorkflowDiagnostic(to.Id, $"Cannot link {output.Kind} output '{output.Name}' to {input.Kind} input '{input.Name}'."));
            return;
        }

        if (input.Kind == PortKind.Exec)
        {
            if (!execTargets.TryGetValue((from.Id, output.Name), out List<CompiledNode>? targets))
                execTargets[(from.Id, output.Name)] = targets = [];
            targets.Add(to);
            return;
        }

        if (!dataSources.TryAdd((to.Id, input.Name), new DataSource(from, output.Name)))
            diagnostics.Add(new WorkflowDiagnostic(to.Id, $"Input '{input.Name}' has more than one link."));
    }

    private static void CheckDataInputs(CompiledNode node, Dictionary<(string, string), DataSource> dataSources, List<WorkflowDiagnostic> diagnostics)
    {
        foreach (PortDefinition input in node.Definition.Inputs.Where(port => port.Kind == PortKind.Data && !port.Optional))
        {
            bool hasLink = dataSources.ContainsKey((node.Id, input.Name));
            bool hasValue = node.Definition.FindParam(input.Name) is { } param
                && (!string.IsNullOrWhiteSpace(node.Params.GetValueOrDefault(input.Name)) || param.DefaultValue.Length > 0);
            if (!hasLink && !hasValue)
                diagnostics.Add(new WorkflowDiagnostic(node.Id, $"Input '{input.Name}' needs a link or a value."));
        }
    }

    /// <summary>Pure nodes are evaluated recursively, so a data cycle between them would never terminate.</summary>
    private static void CheckPureNodeCycles(
        IEnumerable<CompiledNode> nodes,
        Dictionary<(string NodeId, string Port), DataSource> dataSources,
        List<WorkflowDiagnostic> diagnostics)
    {
        var state = new Dictionary<string, bool>();

        bool HasCycle(CompiledNode node)
        {
            if (node.Node is not PureNode)
                return false;
            if (state.TryGetValue(node.Id, out bool visiting))
                return visiting;

            state[node.Id] = true;
            bool cycle = dataSources
                .Where(entry => entry.Key.NodeId == node.Id)
                .Any(entry => HasCycle(entry.Value.Node));
            state[node.Id] = false;
            return cycle;
        }

        foreach (CompiledNode node in nodes.Where(HasCycle))
            diagnostics.Add(new WorkflowDiagnostic(node.Id, "Data links form a cycle through this node."));
    }
}
