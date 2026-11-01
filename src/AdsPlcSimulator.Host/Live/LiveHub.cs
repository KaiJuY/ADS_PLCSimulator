using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using AdsPlcSimulator.Core.Flow;

namespace AdsPlcSimulator.Host.Live;

/// <summary>
/// Pushes workflow execution events and simulation commands to every open editor and simulation page.
/// Each client has a bounded outbox, so a slow browser loses old events instead of slowing the PLC side down.
/// </summary>
public sealed class LiveHub : IFlowMonitor, ISimulationSink
{
    private const int OutboxCapacity = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<LiveHub> _logger;
    private readonly ConcurrentDictionary<Guid, Channel<string>> _clients = new();

    /// <summary>Last action per simulation target, replayed to pages that connect later.</summary>
    private readonly ConcurrentDictionary<string, SimulationCommand> _simulationState = new(StringComparer.OrdinalIgnoreCase);

    public LiveHub(ILogger<LiveHub> logger)
    {
        _logger = logger;
    }

    private volatile ProjectState _project = new(null, false);

    public sealed record SimulationCommand(string Target, string Action, object? Value, int? DurationMs = null);

    private sealed record ProjectState(string? Name, bool Running);

    public void Publish(FlowEvent flowEvent)
    {
        // Stryker disable Statement,String: a log line has no test value
        if (flowEvent.Kind == FlowEventKind.RunFailed)
            _logger.LogWarning("Workflow {Workflow} run {RunId} failed at node {NodeId}: {Error}", flowEvent.Workflow, flowEvent.RunId, flowEvent.NodeId, flowEvent.Error);
        // Stryker restore Statement,String
        Broadcast(new { type = "flow", @event = flowEvent });
    }

    /// <summary>Hold is only sent: it means "stop where you are", and storing it would replace the last position name that a page needs after it reconnects.</summary>
    public void Invoke(string target, string action, object? value, int? durationMs = null)
    {
        var command = new SimulationCommand(target, action, value, durationMs);
        if (!string.Equals(action, "Hold", StringComparison.OrdinalIgnoreCase))
            _simulationState[target] = command;
        Broadcast(new { type = "sim", command });
    }

    /// <summary>Tells the pages which project runs, then repeats simState: the scan may have sent commands before the project was published.</summary>
    public void PublishProject(string? name, bool running)
    {
        _project = new ProjectState(name, running);
        Broadcast(ProjectMessage());
        Broadcast(SimStateMessage());
    }

    public void ClearSimulationState() => _simulationState.Clear();

    internal int SimulationStateCount => _simulationState.Count;

    public void PublishWorkflowStatus(object status) => Broadcast(new { type = "workflow", status });

    public async Task ServeAsync(WebSocket socket, CancellationToken cancellation)
    {
        var outbox = Channel.CreateBounded<string>(new BoundedChannelOptions(OutboxCapacity) { FullMode = BoundedChannelFullMode.DropOldest });
        var clientId = Guid.NewGuid();
        _clients[clientId] = outbox;
        outbox.Writer.TryWrite(Serialize(ProjectMessage()));
        outbox.Writer.TryWrite(Serialize(SimStateMessage()));

        using var closed = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task receiving = DrainUntilClosedAsync(socket, closed);
        try
        {
            await foreach (string message in outbox.Reader.ReadAllAsync(closed.Token))
                await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, closed.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            await closed.CancelAsync();
            await receiving;
        }
    }

    private object ProjectMessage()
    {
        ProjectState project = _project;
        return new { type = "project", name = project.Name, running = project.Running };
    }

    private object SimStateMessage() => new { type = "simState", commands = _simulationState.Values };

    private void Broadcast(object message)
    {
        if (_clients.IsEmpty)
            return;
        string json = Serialize(message);
        foreach (Channel<string> outbox in _clients.Values)
            outbox.Writer.TryWrite(json);
    }

    private static string Serialize(object message) => JsonSerializer.Serialize(message, JsonOptions);

    private static async Task DrainUntilClosedAsync(WebSocket socket, CancellationTokenSource closed)
    {
        var buffer = new byte[1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, closed.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
        {
        }

        await closed.CancelAsync();
    }
}
