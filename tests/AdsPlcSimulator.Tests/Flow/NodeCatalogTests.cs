using AdsPlcSimulator.Core.Flow;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Tests.Flow;

public class NodeCatalogTests
{
    private static string Ports(IEnumerable<PortDefinition> ports) =>
        string.Join(',', ports.Select(port => $"{port.Name}:{port.Kind}:{port.Label}{(port.Optional ? "?" : string.Empty)}"));

    private static string Describe(string type)
    {
        NodeDefinition definition = NodeCatalog.Default.Find(type) ?? throw new InvalidOperationException($"{type} is not in the catalog.");
        string parameters = string.Join(',', definition.Params.Select(param => $"{param.Name}:{param.Kind}:{param.Label}:{param.DefaultValue}"));
        return $"{definition.Type}|{definition.Category}|{definition.Title}|in:{Ports(definition.Inputs)}|out:{Ports(definition.Outputs)}|params:{parameters}";
    }

    [Theory]
    [InlineData(
        "action.writeSymbol",
        "action.writeSymbol|Action|Write Symbol|in:exec:Exec:exec,value:Data:value,index:Data:index?|out:then:Exec:then|params:symbol:Symbol:Symbol:,value:Text:Value:")]
    [InlineData(
        "data.readSymbol",
        "data.readSymbol|Data|Read Symbol|in:index:Data:index?|out:value:Data:value|params:symbol:Symbol:Symbol:")]
    [InlineData(
        "service.host",
        "service.host|Service|Service Host|in:|out:|params:root:Symbol:Root:,identityFile:Text:Identity file:,defaultDelayMs:Number:Default delay (ms):5000")]
    [InlineData(
        "trigger.onServiceExecute",
        "trigger.onServiceExecute|Trigger|On Service Execute|in:|out:then:Exec:then,index:Data:index,generation:Data:generation|params:service:Symbol:Service (ST_ServiceCtrl):")]
    [InlineData(
        "action.completeService",
        "action.completeService|Action|Complete Service|in:exec:Exec:exec,index:Data:index?,generation:Data:generation?|out:then:Exec:then|params:service:Symbol:Service (ST_ServiceCtrl):")]
    [InlineData(
        "action.abortService",
        "action.abortService|Action|Abort Service|in:exec:Exec:exec,index:Data:index?,generation:Data:generation?|out:then:Exec:then|params:service:Symbol:Service (ST_ServiceCtrl):")]
    [Trait("AC", "3")]
    public void Node_type_keeps_the_ports_and_parameters_that_workflow_files_refer_to(string type, string expected)
    {
        Assert.Equal(expected, Describe(type));
    }

    [Fact]
    [Trait("AC", "3")]
    public void Every_node_type_has_a_description_for_the_editor()
    {
        Assert.All(NodeCatalog.Default.Definitions, definition => Assert.False(string.IsNullOrWhiteSpace(definition.Description), definition.Type));
    }

    [Fact]
    [Trait("AC", "3")]
    public void Read_symbol_refuses_a_symbol_that_is_not_a_scalar()
    {
        PlcProgram program = TestPaths.BuildFromText("TYPE ST_Item : STRUCT nValue : INT; END_STRUCT END_TYPE VAR_GLOBAL Item : ST_Item; END_VAR");
        var node = new WorkflowNode { Id = "read", Type = "data.readSymbol", Params = new Dictionary<string, string> { ["symbol"] = "GVL_Test.Item" } };
        var document = new WorkflowDocument { Nodes = [node] };

        CompiledWorkflow? compiled = WorkflowCompiler.Compile("t", document, NodeCatalog.Default, program.Symbols, out List<WorkflowDiagnostic> diagnostics);

        Assert.Null(compiled);
        Assert.Contains("pick one of its scalar members", Assert.Single(diagnostics).Message);
    }
}
