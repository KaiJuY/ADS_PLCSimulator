using System.Collections.Concurrent;
using AdsPlcSimulator.Core.Flow;
using AdsPlcSimulator.Core.Flow.Nodes;

namespace AdsPlcSimulator.Tests;

internal static class TestWait
{
    public static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(5);
        }
    }
}

internal sealed class NoSimulation : ISimulationSink
{
    public void Invoke(string target, string action, object? value, int? durationMs = null)
    {
    }
}

internal sealed class RecordingSimulationSink : ISimulationSink
{
    private readonly ConcurrentQueue<SimCall> _calls = new();

    public IReadOnlyList<SimCall> Calls => _calls.ToArray();

    public void Invoke(string target, string action, object? value, int? durationMs = null) =>
        _calls.Enqueue(new SimCall(target, action, value, durationMs));

    internal readonly record struct SimCall(string Target, string Action, object? Value, int? DurationMs);
}

internal sealed class RecordingMonitor : IFlowMonitor
{
    private readonly ConcurrentQueue<FlowEvent> _events = new();

    public IEnumerable<FlowEvent> Events => _events.ToArray();

    public void Publish(FlowEvent flowEvent) => _events.Enqueue(flowEvent);

    public async Task<FlowEvent> WaitForAsync(Func<FlowEvent, bool> match)
    {
        FlowEvent? found = null;
        await TestWait.WaitUntilAsync(() => (found = _events.FirstOrDefault(match)) is not null, "a flow event");
        return found!;
    }
}
