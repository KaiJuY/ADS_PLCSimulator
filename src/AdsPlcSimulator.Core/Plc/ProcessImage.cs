namespace AdsPlcSimulator.Core.Plc;

public enum WriteSource
{
    /// <summary>An ADS client wrote the value.</summary>
    Client,

    /// <summary>A workflow node wrote the value.</summary>
    Workflow,
}

/// <param name="OldData">Bytes of the written range before the write.</param>
/// <param name="NewData">Bytes of the written range after the write.</param>
public sealed record MemoryWrite(int Offset, byte[] OldData, byte[] NewData, WriteSource Source)
{
    public int Length => NewData.Length;

    public bool HasChanged => !OldData.AsSpan().SequenceEqual(NewData);

    public bool Overlaps(int offset, int length) => Offset < offset + length && offset < Offset + Length;
}

/// <summary>
/// The simulated PLC memory: one contiguous byte array that holds every GVL and PROGRAM variable,
/// the same way the PLC runtime keeps its data area. Thread-safe; every write is published.
/// </summary>
public sealed class ProcessImage
{
    private readonly byte[] _memory;
    private readonly Lock _lock = new();

    public ProcessImage(ReadOnlySpan<byte> initialContent)
    {
        _memory = initialContent.ToArray();
    }

    /// <summary>Raised after every write, including writes that leave the bytes unchanged.</summary>
    public event Action<MemoryWrite>? Written;

    public int Size => _memory.Length;

    public void Read(int offset, Span<byte> destination)
    {
        CheckRange(offset, destination.Length);
        lock (_lock)
            _memory.AsSpan(offset, destination.Length).CopyTo(destination);
    }

    public byte[] Read(int offset, int length)
    {
        var data = new byte[length];
        Read(offset, data);
        return data;
    }

    public object ReadValue(SymbolRef symbol) => ValueCodec.Decode(symbol.Type, Read(symbol.Offset, symbol.Size));

    public void Write(int offset, ReadOnlySpan<byte> data, WriteSource source)
    {
        CheckRange(offset, data.Length);
        byte[] oldData;
        lock (_lock)
        {
            Span<byte> target = _memory.AsSpan(offset, data.Length);
            oldData = target.ToArray();
            data.CopyTo(target);
        }

        Written?.Invoke(new MemoryWrite(offset, oldData, data.ToArray(), source));
    }

    public void WriteValue(SymbolRef symbol, object? value, WriteSource source)
    {
        var data = new byte[symbol.Size];
        ValueCodec.Encode(symbol.Type, value, data);
        Write(symbol.Offset, data, source);
    }

    private void CheckRange(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset + length > _memory.Length)
            throw new ArgumentOutOfRangeException(nameof(offset), $"Range [{offset}, +{length}) is outside the process image of {_memory.Length} bytes.");
    }
}
