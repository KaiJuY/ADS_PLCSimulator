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
        StructMember? flag = status is null ? null : Find(status.Type, "IsControlSourceAlive");
        if (index is null || status is null || flag is null)
            return null;
        if (!IsKind(index.Type, PrimitiveKind.UDInt) || !IsKind(flag.Type, PrimitiveKind.Bool))
            return null;

        return new ModuleHeartbeat(
            new SymbolRef($"{module.Path}.HeartbeatIndex", index.Type, module.Offset + index.Offset),
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

    private static bool IsKind(PlcType type, PrimitiveKind kind) => type.Unaliased is PrimitiveType primitive && primitive.Kind == kind;

    private long ReadIndex(ProcessImage image) => PlcValue.ToInt64(image.ReadValue(_index));
}
