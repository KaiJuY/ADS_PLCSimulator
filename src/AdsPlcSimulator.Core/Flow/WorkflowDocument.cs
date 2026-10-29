using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdsPlcSimulator.Core.Flow;

/// <summary>A workflow file (<c>*.flow.json</c>): nodes, their parameters and the links between their ports.</summary>
public sealed class WorkflowDocument
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public List<WorkflowNode> Nodes { get; init; } = [];

    public List<WorkflowLink> Links { get; init; } = [];

    public List<WorkflowGroup> Groups { get; init; } = [];

    public static WorkflowDocument Parse(string json) =>
        JsonSerializer.Deserialize<WorkflowDocument>(json, JsonOptions)
        ?? throw new JsonException("Workflow file is empty.");

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

public sealed class WorkflowNode
{
    public required string Id { get; init; }

    public required string Type { get; init; }

    /// <summary>Editor canvas position; ignored by the runtime.</summary>
    public double X { get; init; }

    public double Y { get; init; }

    public Dictionary<string, string> Params { get; init; } = [];
}

public sealed record WorkflowLink(string FromNode, string FromPort, string ToNode, string ToPort);

/// <summary>A titled frame on the editor canvas that moves the nodes inside it; ignored by the runtime.</summary>
public sealed record WorkflowGroup(string Id, string Title, double X, double Y, double Width, double Height);
