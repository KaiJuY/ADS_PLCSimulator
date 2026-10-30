using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.PackML;

/// <summary>A scalar inside a byte block: the offset from the block start and its PLC type.</summary>
internal readonly record struct Slot(int Offset, PlcType Type)
{
    public object Read(ReadOnlySpan<byte> block) => ValueCodec.Decode(Type, block.Slice(Offset, Type.Size));

    public void Write(Span<byte> block, object? value) => ValueCodec.Encode(Type, value, block.Slice(Offset, Type.Size));
}

internal static class PlcLayout
{
    public static StructMember Require(PlcType type, string name, string owner) =>
        (type.Unaliased as StructType)?.Find(name)
            ?? throw new ServiceHostException($"{owner} has no member '{name}'.");

    public static void RequireType(StructMember member, string typeName, string owner)
    {
        if (member.Type.Unaliased.Name != typeName)
            throw new ServiceHostException($"{owner}.{member.Name} is {member.Type.Name}, not {typeName}.");
    }
}
