using System.Text;
using AdsPlcSimulator.Core.Plc;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;
using AdsAliasType = TwinCAT.Ads.TypeSystem.AliasType;
using AdsArrayType = TwinCAT.Ads.TypeSystem.ArrayType;
using AdsPointerType = TwinCAT.Ads.TypeSystem.PointerType;
using AdsPrimitiveType = TwinCAT.Ads.TypeSystem.PrimitiveType;
using AdsStringType = TwinCAT.Ads.TypeSystem.StringType;
using AdsStructType = TwinCAT.Ads.TypeSystem.StructType;
using CoreAliasType = AdsPlcSimulator.Core.Plc.AliasType;
using CoreArrayType = AdsPlcSimulator.Core.Plc.ArrayType;
using CorePointerType = AdsPlcSimulator.Core.Plc.PointerType;
using CorePrimitiveType = AdsPlcSimulator.Core.Plc.PrimitiveType;
using CoreStringType = AdsPlcSimulator.Core.Plc.StringType;
using CoreStructType = AdsPlcSimulator.Core.Plc.StructType;

namespace AdsPlcSimulator.Host.Ads;

/// <summary>
/// Converts the simulator's resolved PLC types into Beckhoff type-system objects for the ADS symbol upload.
/// Layout (offsets and sizes) is copied from the Core types so both sides agree byte for byte.
/// </summary>
internal sealed class AdsTypeFactory
{
    private static readonly Encoding PlcStringEncoding = Encoding.Latin1;

    private readonly Dictionary<string, DataType> _created = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every created type, dependencies first, ready for <c>ServerSymbolFactory.AddType</c>.</summary>
    public List<DataType> CreatedTypes { get; } = [];

    public DataType GetOrCreate(PlcType type)
    {
        if (_created.TryGetValue(type.Name, out DataType? existing))
            return existing;

        DataType created = type switch
        {
            CorePrimitiveType primitive => new AdsPrimitiveType(primitive.Name, ManagedTypeOf(primitive)),
            CoreStringType { IsWide: true } text => new WStringType(text.Length),
            CoreStringType text => new AdsStringType(text.Length, PlcStringEncoding),
            EnumType enumeration => CreateEnum(enumeration),
            CoreAliasType alias => new AdsAliasType(alias.Name, GetOrCreate(alias.Target)),
            CorePointerType { Kind: PointerKind.Interface } pointer => new AdsAliasType(pointer.Name, GetOrCreate(CorePrimitiveType.FromName("PVOID"))),
            CorePointerType pointer => CreatePointer(pointer),
            CoreArrayType array => new AdsArrayType(
                GetOrCreate(array.ElementType),
                new DimensionCollection(array.Dimensions.Select(dimension => new Dimension(dimension.LowerBound, dimension.Length)))),
            CoreStructType structure => new ExplicitLayoutStructType(
                structure.Name,
                structure.Members.Select(member => (member.Name, GetOrCreate(member.Type), member.Offset)).ToList(),
                structure.Size),
            _ => throw new NotSupportedException($"No ADS mapping for {type.GetType().Name} '{type.Name}'."),
        };

        if (created.ByteSize != type.Size)
            throw new InvalidOperationException($"ADS type '{created.Name}' is {created.ByteSize} bytes but the simulator laid out {type.Size} bytes.");

        _created[type.Name] = created;
        CreatedTypes.Add(created);
        return created;
    }

    private DataType CreatePointer(CorePointerType pointer)
    {
        string target = pointer.Name[(pointer.Name.IndexOf(" TO ", StringComparison.Ordinal) + 4)..];
        return pointer.Kind == PointerKind.Reference
            ? new ReferenceType(pointer.Name, target, PlcType.PointerSize)
            : new AdsPointerType(pointer.Name, target, PlcType.PointerSize);
    }

    private DataType CreateEnum(EnumType enumeration)
    {
        DataType baseType = GetOrCreate(enumeration.BaseType);
        return enumeration.BaseType.Kind switch
        {
            PrimitiveKind.SInt => CreateEnum<sbyte>(enumeration, baseType),
            PrimitiveKind.Byte or PrimitiveKind.USInt => CreateEnum<byte>(enumeration, baseType),
            PrimitiveKind.Int => CreateEnum<short>(enumeration, baseType),
            PrimitiveKind.Word or PrimitiveKind.UInt => CreateEnum<ushort>(enumeration, baseType),
            PrimitiveKind.DInt => CreateEnum<int>(enumeration, baseType),
            PrimitiveKind.DWord or PrimitiveKind.UDInt => CreateEnum<uint>(enumeration, baseType),
            PrimitiveKind.LInt => CreateEnum<long>(enumeration, baseType),
            _ => CreateEnum<ulong>(enumeration, baseType),
        };
    }

    private static EnumType<T> CreateEnum<T>(EnumType enumeration, DataType baseType) where T : struct, IConvertible
    {
        var values = new EnumValueCollection<T>();
        foreach (EnumValue value in enumeration.Values)
            values.AddValue(value.Name, (T)Convert.ChangeType(value.Value, typeof(T)));
        return new EnumType<T>(enumeration.Name, baseType, values);
    }

    private static Type ManagedTypeOf(CorePrimitiveType primitive) => primitive.Kind switch
    {
        PrimitiveKind.Bool => typeof(bool),
        PrimitiveKind.Byte or PrimitiveKind.USInt => typeof(byte),
        PrimitiveKind.SInt => typeof(sbyte),
        PrimitiveKind.Word or PrimitiveKind.UInt => typeof(ushort),
        PrimitiveKind.Int => typeof(short),
        PrimitiveKind.DInt => typeof(int),
        PrimitiveKind.LInt or PrimitiveKind.LTime => typeof(long),
        PrimitiveKind.LWord or PrimitiveKind.ULInt => typeof(ulong),
        PrimitiveKind.Real => typeof(float),
        PrimitiveKind.LReal => typeof(double),
        _ => typeof(uint),
    };

    /// <summary>
    /// Beckhoff's StructType computes its own alignment; this subclass pins the offsets and the padded size
    /// computed by the simulator, which follow the TwinCAT compiler.
    /// </summary>
    private sealed class ExplicitLayoutStructType : AdsStructType
    {
        public ExplicitLayoutStructType(string name, IReadOnlyList<(string Name, DataType Type, int Offset)> members, int size)
            : base(name, null, CreateMembers(members))
        {
            foreach ((string memberName, _, int offset) in members)
                ((Member)Members[memberName]).SetOffset(offset);
            Size = size;
        }

        private static MemberCollection CreateMembers(IReadOnlyList<(string Name, DataType Type, int Offset)> members)
        {
            var collection = new MemberCollection();
            foreach ((string name, DataType type, _) in members)
                collection.Add(new Member(name, type));
            return collection;
        }
    }
}
