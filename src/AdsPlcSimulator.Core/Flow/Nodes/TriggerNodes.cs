using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.Flow.Nodes;

/// <summary>
/// Fires when the watched symbol's memory is written. <see cref="_clientWritesOnly"/> limits it to ADS client writes;
/// <see cref="_requireChange"/> ignores writes that leave the bytes unchanged.
/// </summary>
public sealed class SymbolTriggerNode : MemoryTriggerNode
{
    private static readonly IReadOnlyList<ParamDefinition> SymbolParam = [new("symbol", ParamKind.Symbol, "Symbol")];

    public static readonly NodeDefinition OnClientWrite = new(
        "trigger.onClientWrite",
        "Trigger",
        "On Client Write",
        "Runs when an ADS client writes the symbol (or a struct that contains it), even with an unchanged value.",
        [],
        [new("then", PortKind.Exec, "then"), new("value", PortKind.Data, "value")],
        SymbolParam,
        settings => new SymbolTriggerNode(settings.Symbol("symbol"), clientWritesOnly: true, requireChange: false));

    public static readonly NodeDefinition OnChange = new(
        "trigger.onChange",
        "Trigger",
        "On Change",
        "Runs when the symbol's value changes, whoever wrote it (ADS client or workflow).",
        [],
        [new("then", PortKind.Exec, "then"), new("value", PortKind.Data, "value"), new("previous", PortKind.Data, "previous")],
        SymbolParam,
        settings => new SymbolTriggerNode(settings.Symbol("symbol"), clientWritesOnly: false, requireChange: true));

    private readonly SymbolRef _symbol;
    private readonly bool _clientWritesOnly;
    private readonly bool _requireChange;

    private SymbolTriggerNode(SymbolRef symbol, bool clientWritesOnly, bool requireChange)
    {
        _symbol = symbol;
        _clientWritesOnly = clientWritesOnly;
        _requireChange = requireChange;
    }

    public override bool TryFire(MemoryWrite write, ProcessImage image, out Dictionary<string, object?> outputs)
    {
        outputs = [];
        if (_clientWritesOnly && write.Source != WriteSource.Client)
            return false;
        if (!write.Overlaps(_symbol.Offset, _symbol.Size))
            return false;

        // Rebuild the symbol's bytes as they were right before and right after this particular write.
        byte[] after = image.Read(_symbol.Offset, _symbol.Size);
        byte[] before = (byte[])after.Clone();
        int start = Math.Max(write.Offset, _symbol.Offset);
        int end = Math.Min(write.Offset + write.Length, _symbol.Offset + _symbol.Size);
        write.NewData.AsSpan(start - write.Offset, end - start).CopyTo(after.AsSpan(start - _symbol.Offset));
        write.OldData.AsSpan(start - write.Offset, end - start).CopyTo(before.AsSpan(start - _symbol.Offset));

        if (_requireChange && before.AsSpan().SequenceEqual(after))
            return false;

        bool isScalar = _symbol.Type.IsScalar;
        outputs["value"] = isScalar ? ValueCodec.Decode(_symbol.Type, after) : null;
        outputs["previous"] = isScalar ? ValueCodec.Decode(_symbol.Type, before) : null;
        return true;
    }
}

public static class StartTrigger
{
    public static readonly NodeDefinition Definition = new(
        "trigger.onStart",
        "Trigger",
        "On Start",
        "Runs once right after the workflow is loaded.",
        [],
        [new("then", PortKind.Exec, "then")],
        [],
        _ => new StartTriggerNode());
}

public static class CyclicTrigger
{
    public static readonly NodeDefinition Definition = new(
        "trigger.cyclic",
        "Trigger",
        "Cyclic",
        "Runs every interval, like a PLC task. A cycle is skipped while the previous one is still running.",
        [],
        [new("then", PortKind.Exec, "then")],
        [new("intervalMs", ParamKind.Number, "Interval (ms)", "100")],
        settings => new CyclicTriggerNode(TimeSpan.FromMilliseconds(settings.Number("intervalMs", minimum: 10))));
}
