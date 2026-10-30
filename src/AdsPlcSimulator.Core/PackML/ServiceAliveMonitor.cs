using AdsPlcSimulator.Core.Plc;
using static AdsPlcSimulator.Core.PackML.PlcLayout;

namespace AdsPlcSimulator.Core.PackML;

internal readonly record struct AliveInput(uint AliveIndex, TimeSpan AliveTimeout);

// FW: FB_ServiceBase.M_InitializeAliveMonitoring, M_EvaluateAliveTimeout and the Abort part of M_PreparePackMLInput
internal sealed class ServiceAliveMonitor
{
    private readonly Slot _aliveIndex;
    private readonly Slot _aliveTimeout;
    private bool _active;
    private TimeSpan _timeout;
    private uint _lastIndex;
    private TimeSpan _unchangedSince;
    private bool _timedOut;

    private ServiceAliveMonitor(Slot aliveIndex, Slot aliveTimeout)
    {
        _aliveIndex = aliveIndex;
        _aliveTimeout = aliveTimeout;
    }

    public bool AbortPending { get; private set; }

    public static ServiceAliveMonitor? TryCreate(SymbolRef serviceCtrl, int ctrlOffsetInBlock)
    {
        StructMember? index = Find(serviceCtrl.Type, "AliveIndex");
        StructMember? timeout = Find(serviceCtrl.Type, "AliveTimeout");
        if (index is null || timeout is null)
            return null;
        if (!IsKind(index.Type, PrimitiveKind.UDInt) || !IsKind(timeout.Type, PrimitiveKind.Time))
            return null;

        return new ServiceAliveMonitor(
            new Slot(ctrlOffsetInBlock + index.Offset, index.Type),
            new Slot(ctrlOffsetInBlock + timeout.Offset, timeout.Type));
    }

    // A module that is not in Execute reads as (0, 0), like the framework coordinator
    public AliveInput Read(ReadOnlySpan<byte> serviceCtrlBlock, bool moduleInExecute)
    {
        if (!moduleInExecute)
            return default;

        return new AliveInput(
            (uint)PlcValue.ToInt64(_aliveIndex.Read(serviceCtrlBlock)),
            TimeSpan.FromMilliseconds(PlcValue.ToInt64(_aliveTimeout.Read(serviceCtrlBlock))));
    }

    public void Reset()
    {
        // Stryker disable once Boolean: _active only gates Evaluate in Execute, and every entry into Execute calls EnterExecute, which sets _active again
        _active = false;
        AbortPending = false;
    }

    public void ClearPendingAbort() => AbortPending = false;

    public void EnterExecute(AliveInput input, CompletionBehavior behavior, AliveSettings settings, TimeSpan now)
    {
        _active = behavior == CompletionBehavior.ExecuteUntilExternalStop && input.AliveTimeout > TimeSpan.Zero;
        _timeout = settings.Clamp(input.AliveTimeout);
        _lastIndex = input.AliveIndex;
        _unchangedSince = now;
        _timedOut = false;
        AbortPending = false;
    }

    // Stryker disable once Boolean: _active only gates Evaluate in Execute, and every entry into Execute calls EnterExecute, which sets _active again
    public void LeaveExecute() => _active = false;

    public TimeSpan? Evaluate(AliveInput input, bool inExecute, TimeSpan now)
    {
        if (!inExecute || !_active)
            return null;

        if (input.AliveIndex != _lastIndex)
        {
            _lastIndex = input.AliveIndex;
            _unchangedSince = now;
        }

        if (_timedOut || now - _unchangedSince < _timeout)
            return null;

        // Stryker disable once Boolean: only Abort is ever queued internally, so the timeout Abort leaves Execute in the same scan, and EnterExecute clears _timedOut when the service returns
        _timedOut = true;
        AbortPending = true;
        return _timeout;
    }
}
