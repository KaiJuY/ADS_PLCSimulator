using System.Text.Json.Serialization;

namespace AdsPlcSimulator.Core.Flow;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FlowEventKind
{
    RunStarted,
    RunCompleted,
    RunFailed,

    /// <summary>The trigger fired while its previous run was still busy, so it was ignored.</summary>
    RunSkipped,

    NodeStarted,
    NodeCompleted,
    NodeFailed,
}

public sealed record FlowEvent(
    FlowEventKind Kind,
    string Workflow,
    long RunId,
    string? NodeId = null,
    IReadOnlyDictionary<string, object?>? Outputs = null,
    string? Error = null)
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}

/// <summary>Receives execution progress, e.g. to show it live in the editor.</summary>
public interface IFlowMonitor
{
    void Publish(FlowEvent flowEvent);
}

/// <summary>Receives commands for the visual simulation, e.g. target "Chamber1.Door", action "Open".</summary>
public interface ISimulationSink
{
    void Invoke(string target, string action, object? value);
}
