using System.Buffers.Binary;
using System.Text;

namespace AdsPlcSimulator.Core.Plc;

/// <summary>
/// Converts between scalar PLC memory (little endian, TwinCAT encoding) and workflow values.
/// TIME is exposed in milliseconds, LTIME in nanoseconds, enums as their numeric value.
/// </summary>
public static class ValueCodec
{
    public static object Decode(PlcType type, ReadOnlySpan<byte> data)
    {
        PlcType layout = type.Unaliased;
        return layout switch
        {
            PrimitiveType primitive => DecodePrimitive(primitive, data),
            EnumType enumeration => DecodePrimitive(enumeration.BaseType, data),
            PointerType => BinaryPrimitives.ReadInt64LittleEndian(data),
            StringType text => DecodeString(text, data),
            _ => throw new InvalidOperationException($"{type.Name} is not a scalar type and has no single value."),
        };
    }

    public static void Encode(PlcType type, object? value, Span<byte> destination)
    {
        PlcType layout = type.Unaliased;
        switch (layout)
        {
            case PrimitiveType primitive:
                EncodePrimitive(primitive, value, destination);
                return;
            case EnumType enumeration:
                EncodePrimitive(enumeration.BaseType, ToEnumNumber(enumeration, value), destination);
                return;
            case PointerType:
                BinaryPrimitives.WriteInt64LittleEndian(destination, PlcValue.ToInt64(value));
                return;
            case StringType text:
                EncodeString(text, value, destination);
                return;
            default:
                throw new InvalidOperationException($"{type.Name} is not a scalar type; write its members instead.");
        }
    }

    private static object DecodePrimitive(PrimitiveType type, ReadOnlySpan<byte> data) => type.Kind switch
    {
        PrimitiveKind.Bool => data[0] != 0,
        PrimitiveKind.Byte or PrimitiveKind.USInt => (long)data[0],
        PrimitiveKind.SInt => (long)(sbyte)data[0],
        PrimitiveKind.Word or PrimitiveKind.UInt => (long)BinaryPrimitives.ReadUInt16LittleEndian(data),
        PrimitiveKind.Int => (long)BinaryPrimitives.ReadInt16LittleEndian(data),
        PrimitiveKind.DWord or PrimitiveKind.UDInt or PrimitiveKind.Time or PrimitiveKind.Date
            or PrimitiveKind.TimeOfDay or PrimitiveKind.DateAndTime => (long)BinaryPrimitives.ReadUInt32LittleEndian(data),
        PrimitiveKind.DInt => (long)BinaryPrimitives.ReadInt32LittleEndian(data),
        PrimitiveKind.LWord or PrimitiveKind.ULInt or PrimitiveKind.LInt or PrimitiveKind.LTime => BinaryPrimitives.ReadInt64LittleEndian(data),
        PrimitiveKind.Real => (double)BinaryPrimitives.ReadSingleLittleEndian(data),
        PrimitiveKind.LReal => BinaryPrimitives.ReadDoubleLittleEndian(data),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type.Kind, null),
    };

    private static void EncodePrimitive(PrimitiveType type, object? value, Span<byte> destination)
    {
        switch (type.Kind)
        {
            case PrimitiveKind.Bool:
                destination[0] = PlcValue.ToBoolean(value) ? (byte)1 : (byte)0;
                return;
            case PrimitiveKind.Real:
                BinaryPrimitives.WriteSingleLittleEndian(destination, (float)PlcValue.ToDouble(value));
                return;
            case PrimitiveKind.LReal:
                BinaryPrimitives.WriteDoubleLittleEndian(destination, PlcValue.ToDouble(value));
                return;
            case PrimitiveKind.LTime when value is TimeSpan duration:
                BinaryPrimitives.WriteInt64LittleEndian(destination, duration.Ticks * 100);
                return;
        }

        long integer = PlcValue.ToInt64(value);
        switch (type.Kind)
        {
            case PrimitiveKind.Byte or PrimitiveKind.USInt:
                destination[0] = (byte)CheckRange(type, integer, byte.MinValue, byte.MaxValue);
                return;
            case PrimitiveKind.SInt:
                destination[0] = (byte)(sbyte)CheckRange(type, integer, sbyte.MinValue, sbyte.MaxValue);
                return;
            case PrimitiveKind.Word or PrimitiveKind.UInt:
                BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)CheckRange(type, integer, ushort.MinValue, ushort.MaxValue));
                return;
            case PrimitiveKind.Int:
                BinaryPrimitives.WriteInt16LittleEndian(destination, (short)CheckRange(type, integer, short.MinValue, short.MaxValue));
                return;
            case PrimitiveKind.DInt:
                BinaryPrimitives.WriteInt32LittleEndian(destination, (int)CheckRange(type, integer, int.MinValue, int.MaxValue));
                return;
            case PrimitiveKind.LWord or PrimitiveKind.ULInt or PrimitiveKind.LInt or PrimitiveKind.LTime:
                BinaryPrimitives.WriteInt64LittleEndian(destination, integer);
                return;
            default:
                BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)CheckRange(type, integer, uint.MinValue, uint.MaxValue));
                return;
        }
    }

    private static long CheckRange(PrimitiveType type, long value, long minimum, long maximum)
    {
        if (value < minimum || value > maximum)
            throw new OverflowException($"Value {value} is out of range for {type.Name} [{minimum}..{maximum}].");
        return value;
    }

    private static long ToEnumNumber(EnumType type, object? value)
    {
        if (value is not string text || PlcValue.ParseLiteral(text) is not string)
            return PlcValue.ToInt64(value);

        string valueName = text.Contains('.') ? text[(text.LastIndexOf('.') + 1)..] : text;
        return type.Find(valueName)?.Value
            ?? throw new ArgumentException($"'{text}' is not a value of {type.Name}.");
    }

    private static string DecodeString(StringType type, ReadOnlySpan<byte> data)
    {
        if (type.IsWide)
        {
            string wide = Encoding.Unicode.GetString(data);
            int end = wide.IndexOf('\0');
            return end < 0 ? wide : wide[..end];
        }

        int terminator = data.IndexOf((byte)0);
        return Encoding.Latin1.GetString(terminator < 0 ? data : data[..terminator]);
    }

    private static void EncodeString(StringType type, object? value, Span<byte> destination)
    {
        string text = value is string s ? s : PlcValue.Format(value);
        if (text.Length > type.Length)
            throw new ArgumentException($"String of length {text.Length} does not fit {type.Name}.");

        destination.Clear();
        Encoding encoding = type.IsWide ? Encoding.Unicode : Encoding.Latin1;
        encoding.GetBytes(text, destination);
    }
}
