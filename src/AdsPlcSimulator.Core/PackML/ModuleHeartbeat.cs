using AdsPlcSimulator.Core.Plc;
using static AdsPlcSimulator.Core.PackML.PlcLayout;

namespace AdsPlcSimulator.Core.PackML;

internal sealed class ModuleHeartbeat
{
    private readonly SymbolRef _index;
    private readonly SymbolRef _flag;
    private readonly TimeSpan _timeout;
    private long _lastIndex;
    private TimeSpan? _unchangedSince;
    // Stryker disable once Boolean: ServiceHost.Scan throws before Initialize, and Initialize calls Reset, which sets _alive, so the initial value is never read
    private bool _alive = true;

    private ModuleHeartbeat(SymbolRef index, SymbolRef flag, TimeSpan timeout)
    {
        _index = index;
        _flag = flag;
        _timeout = timeout;
    }

    public static ModuleHeartbeat? TryCreate(SymbolRef module, TimeSpan timeout)
    {
        StructMember? index = Find(module.Type, "HeartbeatIndex");
        StructMember? status = Find(module.Type, "ModuleStatus");
        // Stryker disable once Conditional: ModuleUnit.Create requires ModuleStatus before it calls TryCreate, so status is never null here
        StructMember? flag = status is null ? null : Find(status.Type, "IsControlSourceAlive");
        if (index is null || status is null || flag is null)
            return null;
        if (!IsKind(index.Type, PrimitiveKind.UDInt) || !IsKind(flag.Type, PrimitiveKind.Bool))
            return null;

        return new ModuleHeartbeat(
            // Stryker disable once String: ProcessImage.ReadValue uses only the offset and type of the symbol, never its path
            new SymbolRef($"{module.Path}.HeartbeatIndex", index.Type, module.Offset + index.Offset),
            // Stryker disable once String: ProcessImage.WriteValue uses only the offset and type of the symbol, never its path
            new SymbolRef($"{module.Path}.ModuleStatus.IsControlSourceAlive", flag.Type, module.Offset + status.Offset + flag.Offset),
            timeout);
    }

    public void Reset(ProcessImage image)
    {
        _lastIndex = ReadIndex(image);
        _unchangedSince = null;
        _alive = true;
        image.WriteValue(_flag, true, WriteSource.Workflow);
    }

    public void Scan(ProcessImage image, TimeSpan now)
    {
        long index = ReadIndex(image);
        if (index != _lastIndex)
        {
            _lastIndex = index;
            _unchangedSince = now;
        }

        _unchangedSince ??= now;
        bool alive = now - _unchangedSince.Value < _timeout;
        if (alive == _alive)
            return;

        _alive = alive;
        image.WriteValue(_flag, alive, WriteSource.Workflow);
    }

    private long ReadIndex(ProcessImage image) => PlcValue.ToInt64(image.ReadValue(_index));
}
