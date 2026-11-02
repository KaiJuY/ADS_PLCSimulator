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
        await WaitUntilAsync(() => socket.Sent.Count >= 2, "the replay of the project and the simulation state");

        hub.Publish(new FlowEvent(FlowEventKind.RunCompleted, "wf", 1, "node"));
        await WaitUntilAsync(() => socket.Sent.Count >= 3, "the flow event");
        await stop.CancelAsync();
        await serving;

        string message = socket.Sent.ToArray()[2];
        Assert.Contains("\"type\":\"flow\"", message);
        Assert.Contains("\"wf\"", message);
    }

    private static async Task<(RecordingSocket Socket, CancellationTokenSource Stop, Task Serving)> ConnectAsync(LiveHub hub, int initialMessages = 2)
    {
        var socket = new RecordingSocket();
        var stop = new CancellationTokenSource();
        Task serving = hub.ServeAsync(socket, stop.Token);
        await WaitUntilAsync(() => socket.Sent.Count >= initialMessages, "the replay on connect");
        return (socket, stop, serving);
    }

    private static async Task<string[]> SnapshotAsync(LiveHub hub)
    {
        (RecordingSocket socket, CancellationTokenSource stop, Task serving) = await ConnectAsync(hub);
        await stop.CancelAsync();
        await serving;
        stop.Dispose();
        return socket.Sent.ToArray();
    }

    [Theory]
    [Trait("AC", "2")]
    [InlineData(1500, "\"durationMs\":1500")]
    [InlineData(null, "\"durationMs\":null")]
    public async Task Invoke_broadcasts_the_duration_or_null(int? durationMs, string expected)
    {
        var hub = new LiveHub(NullLogger<LiveHub>.Instance);
        (RecordingSocket socket, CancellationTokenSource stop, Task serving) = await ConnectAsync(hub);

        hub.Invoke("Cup1.Master", "Top", 12.5, durationMs);
        await WaitUntilAsync(() => socket.Sent.Count >= 3, "the sim message");
        await stop.CancelAsync();
        await serving;
        stop.Dispose();

        string message = socket.Sent.ToArray()[2];
        Assert.Contains("\"type\":\"sim\"", message);
        Assert.Contains("\"target\":\"Cup1.Master\"", message);
        Assert.Contains("\"action\":\"Top\"", message);
        Assert.Contains("\"value\":12.5", message);
        Assert.Contains(expected, message);
    }

    [Fact]
    [Trait("AC", "8")]
    public async Task Hold_is_broadcast_but_not_stored_and_another_action_replaces_the_last_one_of_the_target()
    {
        var hub = new LiveHub(NullLogger<LiveHub>.Instance);
        (RecordingSocket socket, CancellationTokenSource stop, Task serving) = await ConnectAsync(hub);

        hub.Invoke("Cup1.Master", "Top", 12.5, 5000);
        hub.Invoke("Cup1.Master", "hold", null, null);
        await WaitUntilAsync(() => socket.Sent.Count >= 4, "both sim messages");
        await stop.CancelAsync();
        await serving;
        stop.Dispose();

        Assert.Contains("\"action\":\"hold\"", socket.Sent.ToArray()[3]);
        Assert.Equal(1, hub.SimulationStateCount);
        string afterHold = (await SnapshotAsync(hub))[1];
        Assert.Contains("\"action\":\"Top\"", afterHold);
        Assert.DoesNotContain("hold", afterHold);

        hub.Invoke("Cup1.Master", "Down", null, 5000);
        string afterDown = (await SnapshotAsync(hub))[1];
        Assert.Contains("\"action\":\"Down\"", afterDown);
        Assert.DoesNotContain("\"Top\"", afterDown);
    }

    [Fact]
    [Trait("AC", "10")]
    public async Task PublishProject_broadcasts_the_project_then_the_sim_state_and_a_new_client_gets_them_in_that_order()
    {
        var hub = new LiveHub(NullLogger<LiveHub>.Instance);
        (RecordingSocket socket, CancellationTokenSource stop, Task serving) = await ConnectAsync(hub);
        hub.Invoke("Holder", "CW", 30.0, null);
        await WaitUntilAsync(() => socket.Sent.Count >= 3, "the sim message");

        hub.PublishProject("Polar2Chamber", true);
        await WaitUntilAsync(() => socket.Sent.Count >= 5, "the project and the sim state");
        await stop.CancelAsync();
        await serving;
        stop.Dispose();

        string[] live = socket.Sent.ToArray();
        Assert.Equal("{\"type\":\"project\",\"name\":\"Polar2Chamber\",\"running\":true}", live[3]);
        Assert.StartsWith("{\"type\":\"simState\"", live[4]);
        Assert.Contains("\"target\":\"Holder\"", live[4]);

        string[] connecting = await SnapshotAsync(hub);
        Assert.Equal(live[3], connecting[0]);
        Assert.Equal(live[4], connecting[1]);
    }

    [Fact]
    [Trait("AC", "10")]
    public async Task A_client_that_connects_before_any_project_was_published_gets_running_false()
    {
        var hub = new LiveHub(NullLogger<LiveHub>.Instance);

        string[] connecting = await SnapshotAsync(hub);

        Assert.Equal("{\"type\":\"project\",\"name\":null,\"running\":false}", connecting[0]);
        Assert.StartsWith("{\"type\":\"simState\"", connecting[1]);
    }
}
