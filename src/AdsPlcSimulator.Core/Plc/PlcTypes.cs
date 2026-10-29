namespace AdsPlcSimulator.Core.Plc;

/// <summary>
/// Resolved PLC data type with its TwinCAT x64 memory layout (natural alignment, capped at 8 bytes).
/// </summary>
public abstract class PlcType(string name, int size, int alignment)
{
    public const int PointerSize = 8;

    public string Name { get; } = name;

    public int Size { get; } = size;

    public int Alignment { get; } = alignment;

    /// <summary>Follows alias chains to the type that actually defines the layout.</summary>
    public PlcType Unaliased => this is AliasType alias ? alias.Target.Unaliased : this;

    /// <summary>True when the value is read and written as one scalar.</summary>
    public bool IsScalar => Unaliased is PrimitiveType or StringType or EnumType or PointerType;

    public override string ToString() => Name;
}

public enum PrimitiveKind
{
    Bool,
    Byte,
    Word,
    DWord,
    LWord,
    SInt,
    USInt,
    Int,
    UInt,
    DInt,
    UDInt,
    LInt,
    ULInt,
    Real,
    LReal,
    Time,
    LTime,
    Date,
    TimeOfDay,
    DateAndTime,
}

public sealed class PrimitiveType : PlcType
{
    private static readonly Dictionary<string, PrimitiveType> ByName = new(StringComparer.OrdinalIgnoreCase);

    private PrimitiveType(string name, PrimitiveKind kind, int size) : base(name, size, size)
    {
        Kind = kind;
    }

    static PrimitiveType()
    {
        Register("BOOL", PrimitiveKind.Bool, 1);
        Register("BYTE", PrimitiveKind.Byte, 1);
        Register("WORD", PrimitiveKind.Word, 2);
        Register("DWORD", PrimitiveKind.DWord, 4);
        Register("LWORD", PrimitiveKind.LWord, 8);
        Register("SINT", PrimitiveKind.SInt, 1);
        Register("USINT", PrimitiveKind.USInt, 1);
        Register("INT", PrimitiveKind.Int, 2);
        Register("UINT", PrimitiveKind.UInt, 2);
        Register("DINT", PrimitiveKind.DInt, 4);
        Register("UDINT", PrimitiveKind.UDInt, 4);
        Register("LINT", PrimitiveKind.LInt, 8);
        Register("ULINT", PrimitiveKind.ULInt, 8);
        Register("REAL", PrimitiveKind.Real, 4);
        Register("LREAL", PrimitiveKind.LReal, 8);
        Register("TIME", PrimitiveKind.Time, 4);
        Register("LTIME", PrimitiveKind.LTime, 8);
        Register("DATE", PrimitiveKind.Date, 4);
        Register("TIME_OF_DAY", PrimitiveKind.TimeOfDay, 4);
        Register("DATE_AND_TIME", PrimitiveKind.DateAndTime, 4);
        ByName["TOD"] = ByName["TIME_OF_DAY"];
        ByName["DT"] = ByName["DATE_AND_TIME"];
        ByName["UXINT"] = ByName["ULINT"];
        ByName["XINT"] = ByName["LINT"];
        ByName["XWORD"] = ByName["LWORD"];
        ByName["PVOID"] = ByName["ULINT"];
    }

    public PrimitiveKind Kind { get; }

    public bool IsFloatingPoint => Kind is PrimitiveKind.Real or PrimitiveKind.LReal;

    public bool IsSigned => Kind is PrimitiveKind.SInt or PrimitiveKind.Int or PrimitiveKind.DInt or PrimitiveKind.LInt
        or PrimitiveKind.Time or PrimitiveKind.LTime;

    public static PrimitiveType Bool => ByName["BOOL"];

    public static PrimitiveType FromName(string name) => ByName[name];

    public static bool TryGet(string name, out PrimitiveType type) => ByName.TryGetValue(name, out type!);

    private static void Register(string name, PrimitiveKind kind, int size) => ByName[name] = new PrimitiveType(name, kind, size);
}

public sealed class StringType(int length, bool isWide) : PlcType(
    isWide ? $"WSTRING({length})" : $"STRING({length})",
    (length + 1) * (isWide ? 2 : 1),
    isWide ? 2 : 1)
{
    public int Length { get; } = length;

    public bool IsWide { get; } = isWide;
}

public sealed record EnumValue(string Name, long Value);

public sealed class EnumType(string name, PrimitiveType baseType, IReadOnlyList<EnumValue> values)
    : PlcType(name, baseType.Size, baseType.Alignment)
{
    public PrimitiveType BaseType { get; } = baseType;

    public IReadOnlyList<EnumValue> Values { get; } = values;

    public EnumValue? Find(string valueName) =>
        Values.FirstOrDefault(value => value.Name.Equals(valueName, StringComparison.OrdinalIgnoreCase));
}

public sealed class AliasType(string name, PlcType target) : PlcType(name, target.Size, target.Alignment)
{
    public PlcType Target { get; } = target;
}

public enum PointerKind
{
    Pointer,
    Reference,
    Interface,
}

/// <summary>POINTER TO, REFERENCE TO and interface variables. The simulator stores the raw address only.</summary>
public sealed class PointerType(string name, PointerKind kind) : PlcType(name, PointerSize, PointerSize)
{
    public PointerKind Kind { get; } = kind;
}

public sealed record ArrayDimension(int LowerBound, int UpperBound)
{
    public int Length => UpperBound - LowerBound + 1;

    public override string ToString() => $"{LowerBound}..{UpperBound}";
}

public sealed class ArrayType(PlcType elementType, IReadOnlyList<ArrayDimension> dimensions) : PlcType(
    $"ARRAY [{string.Join(",", dimensions)}] OF {elementType.Name}",
    elementType.Size * dimensions.Aggregate(1, (count, dimension) => count * dimension.Length),
    elementType.Alignment)
{
    public PlcType ElementType { get; } = elementType;

    public IReadOnlyList<ArrayDimension> Dimensions { get; } = dimensions;

    public int ElementCount => Dimensions.Aggregate(1, (count, dimension) => count * dimension.Length);

    /// <summary>Row-major element offset, matching TwinCAT multi-dimensional array layout.</summary>
    public int OffsetOf(IReadOnlyList<int> indices)
    {
        if (indices.Count != Dimensions.Count)
            throw new ArgumentException($"{Name} needs {Dimensions.Count} index(es) but got {indices.Count}.");

        int linearIndex = 0;
        for (int dimension = 0; dimension < Dimensions.Count; dimension++)
        {
            ArrayDimension bounds = Dimensions[dimension];
            int index = indices[dimension];
            if (index < bounds.LowerBound || index > bounds.UpperBound)
                throw new ArgumentOutOfRangeException(nameof(indices), $"Index {index} is outside [{bounds}] of {Name}.");
            linearIndex = linearIndex * bounds.Length + (index - bounds.LowerBound);
        }

        return linearIndex * ElementType.Size;
    }
}

public sealed record StructMember(string Name, PlcType Type, int Offset);

/// <summary>
/// STRUCT or FUNCTION_BLOCK. Members are flattened: inherited members come first, as in PLC memory.
/// </summary>
public sealed class StructType(string name, bool isFunctionBlock, IReadOnlyList<StructMember> members, int size, int alignment)
    : PlcType(name, size, alignment)
{
    public bool IsFunctionBlock { get; } = isFunctionBlock;

    public IReadOnlyList<StructMember> Members { get; } = members;

    public StructMember? Find(string memberName) =>
        Members.FirstOrDefault(member => member.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase));
}
