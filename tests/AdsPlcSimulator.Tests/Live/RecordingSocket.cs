using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace AdsPlcSimulator.Tests.Live;

internal sealed class RecordingSocket : WebSocket
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
