using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using AdsPlcSimulator.Core.Flow;
using AdsPlcSimulator.Host.Live;
using Microsoft.Extensions.Logging.Abstractions;
using static AdsPlcSimulator.Tests.TestWait;

namespace AdsPlcSimulator.Tests.Live;

public class LiveHubTests
{
    [Fact]
    [Trait("AC", "13")]
    public async Task Published_flow_event_reaches_a_connected_client()
    {
        var hub = new LiveHub(NullLogger<LiveHub>.Instance);
        var socket = new RecordingSocket();
        using var stop = new CancellationTokenSource();
        Task serving = hub.ServeAsync(socket, stop.Token);
        await WaitUntilAsync(() => socket.Sent.Count >= 1, "the replay of the simulation state");

        hub.Publish(new FlowEvent(FlowEventKind.RunCompleted, "wf", 1, "node"));
        await WaitUntilAsync(() => socket.Sent.Count >= 2, "the flow event");
        await stop.CancelAsync();
        await serving;

        string message = socket.Sent.ToArray()[1];
        Assert.Contains("\"type\":\"flow\"", message);
        Assert.Contains("\"wf\"", message);
    }

    private sealed class RecordingSocket : WebSocket
    {
        public ConcurrentQueue<string> Sent { get; } = new();

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => WebSocketState.Open;

        public override string? SubProtocol => null;

        public override void Abort()
        {
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Dispose()
        {
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Sent.Enqueue(Encoding.UTF8.GetString(buffer));
            return Task.CompletedTask;
        }
    }
}
