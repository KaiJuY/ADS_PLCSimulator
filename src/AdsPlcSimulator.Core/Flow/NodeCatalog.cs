using AdsPlcSimulator.Core.Flow.Nodes;

namespace AdsPlcSimulator.Core.Flow;

/// <summary>Every node type a workflow can use. The editor builds its palette from this list.</summary>
public sealed class NodeCatalog
{
    private readonly Dictionary<string, NodeDefinition> _byType;

    public NodeCatalog(IEnumerable<NodeDefinition> definitions)
    {
        Definitions = definitions.ToList();
        _byType = Definitions.ToDictionary(definition => definition.Type);
    }

    public static NodeCatalog Default { get; } = new(
    [
        SymbolTriggerNode.OnClientWrite,
        SymbolTriggerNode.OnChange,
        CyclicTrigger.Definition,
        BranchNode.Definition,
        ForLoopNode.Definition,
        DelayNode.Definition,
        ReadSymbolNode.Definition,
        ConstantNode.Definition,
        CompareNode.Definition,
        ArithmeticNode.Definition,
        WriteSymbolNode.Definition,
        SimulationInvokeNode.Definition,
    ]);

    public IReadOnlyList<NodeDefinition> Definitions { get; }

    public NodeDefinition? Find(string type) => _byType.GetValueOrDefault(type);
}
