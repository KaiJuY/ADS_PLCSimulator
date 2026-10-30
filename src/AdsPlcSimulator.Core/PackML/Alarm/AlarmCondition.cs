using System.Globalization;
using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.PackML.Alarm;

public static class AlarmCondition
{
    /// <summary>F_AlarmConditionMet: Equal uses |value - threshold| &lt;= |tolerance|; the others ignore tolerance.</summary>
    public static bool IsMet(double value, AlarmOperator op, double threshold, double tolerance) => op switch
    {
        AlarmOperator.GreaterThan => value > threshold,
        AlarmOperator.LessThan => value < threshold,
        AlarmOperator.Equal => Math.Abs(value - threshold) <= Math.Abs(tolerance),
        AlarmOperator.GreaterOrEqual => value >= threshold,
        AlarmOperator.LessOrEqual => value <= threshold,
        _ => false,
    };

    /// <summary>"gt", "lt", "eq", "ge", "le" (case-sensitive, as M_ParseOperator); null otherwise.</summary>
    public static AlarmOperator? ParseOperator(string? text) => text switch
    {
        "gt" => AlarmOperator.GreaterThan,
        "lt" => AlarmOperator.LessThan,
        "eq" => AlarmOperator.Equal,
        "ge" => AlarmOperator.GreaterOrEqual,
        "le" => AlarmOperator.LessOrEqual,
        _ => null,
    };

    /// <summary>M_ParseDataType names, case-sensitive as in the framework; null otherwise.</summary>
    public static AlarmSourceType? ParseDataType(string? text) =>
        text is not null && DataTypeNames.TryGetValue(text, out AlarmSourceType type) ? type : null;

    private static readonly Dictionary<string, AlarmSourceType> DataTypeNames = new(StringComparer.Ordinal)
    {
        ["BOOL"] = AlarmSourceType.Boolean, ["Bool"] = AlarmSourceType.Boolean, ["Boolean"] = AlarmSourceType.Boolean,
        ["DINT"] = AlarmSourceType.DInt, ["DInt"] = AlarmSourceType.DInt, ["Integer_32bit"] = AlarmSourceType.DInt,
        ["UDINT"] = AlarmSourceType.UDInt, ["UDInt"] = AlarmSourceType.UDInt, ["Unsigned_32bit"] = AlarmSourceType.UDInt,
        ["REAL"] = AlarmSourceType.Real, ["Real"] = AlarmSourceType.Real, ["Float_32bit"] = AlarmSourceType.Real,
        ["LREAL"] = AlarmSourceType.LReal, ["LReal"] = AlarmSourceType.LReal, ["Float_64bit"] = AlarmSourceType.LReal,
        ["SINT"] = AlarmSourceType.SInt,
        ["USINT"] = AlarmSourceType.USInt,
        ["INT"] = AlarmSourceType.Int,
        ["UINT"] = AlarmSourceType.UInt,
        ["STRING"] = AlarmSourceType.String80, ["STRING(80)"] = AlarmSourceType.String80,
        ["String80"] = AlarmSourceType.String80, ["String_80byte"] = AlarmSourceType.String80,
    };

    /// <summary>True when a symbol of this PLC type can be sampled as the given source type (BOOL, SINT … LREAL by kind; any STRING for String80).</summary>
    public static bool Matches(AlarmSourceType sourceType, PlcType symbolType)
    {
        PlcType type = symbolType.Unaliased;
        if (sourceType == AlarmSourceType.String80)
            return type is StringType { IsWide: false };

        return type is PrimitiveType primitive && primitive.Kind == ExpectedKind(sourceType);
    }

    private static PrimitiveKind ExpectedKind(AlarmSourceType sourceType) => sourceType switch
    {
        AlarmSourceType.Boolean => PrimitiveKind.Bool,
        AlarmSourceType.SInt => PrimitiveKind.SInt,
        AlarmSourceType.USInt => PrimitiveKind.USInt,
        AlarmSourceType.Int => PrimitiveKind.Int,
        AlarmSourceType.UInt => PrimitiveKind.UInt,
        AlarmSourceType.DInt => PrimitiveKind.DInt,
        AlarmSourceType.UDInt => PrimitiveKind.UDInt,
        AlarmSourceType.Real => PrimitiveKind.Real,
        _ => PrimitiveKind.LReal,
    };
}

public static class AlarmClock
{
    /// <summary>FB_UtcClock: "yyyyMMddHHmmssfff" in UTC, 17 digits.</summary>
    public static string Format(DateTimeOffset utc) => utc.UtcDateTime.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
}
