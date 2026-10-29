using AdsPlcSimulator.Core.Flow;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.Flow;

public class WorkflowCompilerTests
{
    private const string Declarations = """
        VAR_GLOBAL
            nRequest : UDINT;
        END_VAR
        """;

    private static WorkflowNode Trigger(string id) =>
        new() { Id = id, Type = "trigger.onClientWrite", Params = new Dictionary<string, string> { ["symbol"] = "GVL_Test.nRequest" } };

    [Fact]
    [Trait("AC", "1")]
    public void Compile_rejects_two_nodes_with_the_same_id()
    {
        PlcProgram program = TestPaths.BuildFromText(Declarations);
        var document = new WorkflowDocument { Nodes = [Trigger("same"), Trigger("same")] };

        CompiledWorkflow? compiled = WorkflowCompiler.Compile("t", document, NodeCatalog.Default, program.Symbols, out List<WorkflowDiagnostic> diagnostics);

        Assert.Null(compiled);
        WorkflowDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal("same", diagnostic.NodeId);
        Assert.Equal("Node id 'same' is used twice.", diagnostic.Message);
    }
}
