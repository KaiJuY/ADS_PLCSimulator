using AdsPlcSimulator.Core.Flow;
using AdsPlcSimulator.Host.Live;

namespace AdsPlcSimulator.Host.Project;

internal sealed class SessionLiveChannel(LiveHub hub) : IFlowMonitor, ISimulationSink
{
    private bool _closed;

    public void Publish(FlowEvent flowEvent)
    {
        if (Volatile.Read(ref _closed))
            return;
        hub.Publish(flowEvent);
    }

    public void Invoke(string target, string action, object? value)
    {
        if (Volatile.Read(ref _closed))
            return;
        hub.Invoke(target, action, value);
    }

    public void Close() => Volatile.Write(ref _closed, true);
}
