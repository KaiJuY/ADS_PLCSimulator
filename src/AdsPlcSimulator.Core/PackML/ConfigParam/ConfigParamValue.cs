using System.Globalization;

namespace AdsPlcSimulator.Core.PackML.ConfigParam;

/// <summary>Strict value text and number rules (F_IsStrictConfigParamValue, the numeric part of M_ValidateConfigParamBatch, and the *_TO_STRING output).</summary>
internal static class ConfigParamValue
{
    // E_ConfigDataType
    private const int Boolean = 1;
    private const int Integer32 = 2;
    private const int UnsignedInteger32 = 3;
    private const int Float32 = 4;
    private const int Float64 = 5;
    private const int String80 = 6;
    private const int Integer8 = 7;
    private const int UnsignedInteger8 = 8;
    private const int Integer16 = 9;
    private const int UnsignedInteger16 = 10;

    private static readonly Dictionary<int, (double Lower, double Upper)> TypeRanges = new()
    {
        [Integer8] = (-128, 127),
        [UnsignedInteger8] = (0, 255),
        [Integer16] = (-32768, 32767),
        [UnsignedInteger16] = (0, 65535),
        [Integer32] = (-2147483648.0, 2147483647.0),
        [UnsignedInteger32] = (0, 4294967295.0),
        [Float32] = (-3.402823466E38, 3.402823466E38),
    };

    // F_ConfigDataTypeToDescriptor, reversed: E_DescriptorType value -> E_ConfigDataType value
    private static readonly Dictionary<long, int> FromDescriptor = new()
    {
        [1] = Integer32,
        [2] = Float32,
        [3] = String80,
        [5] = Boolean,
        [6] = UnsignedInteger32,
        [7] = Float64,
        [8] = Integer8,
        [9] = UnsignedInteger8,
        [10] = Integer16,
        [11] = UnsignedInteger16,
    };

    // FW: M_ValidateConfigParamBatch numeric branch
    public static bool IsNumeric(int configDataType) => TypeRanges.ContainsKey(configDataType) || configDataType == Float64;

    public static bool IsStrict(string value, int configDataType)
    {
        if (configDataType == String80)
            return value.Length <= 80;
        if (configDataType == Boolean)
            return value is "TRUE" or "FALSE";
        if (value.Length == 0)
            return false;

        int start = 0;
        if (IsSigned(configDataType) && value[0] is ('+' or '-'))
        {
            start = 1;
            if (value.Length == 1)
                return false;
        }

        if (configDataType is Integer8 or UnsignedInteger8 or Integer16 or UnsignedInteger16 or Integer32 or UnsignedInteger32)
            return AllDigits(value, start);
        if (configDataType is not (Float32 or Float64))
            return false;
        return IsStrictFloat(value, start);
    }

    public static bool IsInTypeRange(double value, int configDataType) =>
        !TypeRanges.TryGetValue(configDataType, out (double Lower, double Upper) range)
        || (value >= range.Lower && value <= range.Upper);

    public static string Format(string value, int configDataType)
    {
        if (!IsNumeric(configDataType))
            return value;

        double number = ParseNumber(value);
        return configDataType switch
        {
            Float32 => ((float)number).ToString("R", CultureInfo.InvariantCulture),
            Float64 => number.ToString("R", CultureInfo.InvariantCulture),
            _ => ((long)number).ToString(CultureInfo.InvariantCulture),
        };
    }

    public static double ParseNumber(string value) =>
        double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    public static int? ToConfigDataType(long descriptorType) =>
        FromDescriptor.TryGetValue(descriptorType, out int configType) ? configType : null;

    private static bool IsSigned(int configDataType) =>
        configDataType is Integer8 or Integer16 or Integer32 or Float32 or Float64;

    private static bool AllDigits(string value, int start)
    {
        for (int index = start; index < value.Length; index++)
        {
            if (!IsDigit(value[index]))
                return false;
        }

        return true;
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static bool IsStrictFloat(string value, int start)
    {
        var state = new FloatState();
        for (int index = start; index < value.Length; index++)
        {
            if (!state.Accept(value, index))
                return false;
        }

        return state.Digit && (!state.Exponent || state.ExponentDigit);
    }

    private struct FloatState
    {
        public bool Digit;
        public bool Dot;
        public bool Exponent;
        public bool ExponentDigit;

        public bool Accept(string value, int index)
        {
            char c = value[index];
            if (IsDigit(c))
                return AcceptDigit();
            if (c == '.')
                return AcceptDot();
            if (c is 'e' or 'E')
                return AcceptExponent();
            if (c is '+' or '-')
                return Exponent && !ExponentDigit && index > 0 && value[index - 1] is ('e' or 'E');
            return false;
        }

        private bool AcceptDigit()
        {
            if (Exponent)
                ExponentDigit = true;
            else
                Digit = true;
            return true;
        }

        private bool AcceptDot()
        {
            if (Dot || Exponent)
                return false;
            Dot = true;
            return true;
        }

        private bool AcceptExponent()
        {
            if (!Digit || Exponent)
                return false;
            Exponent = true;
            return true;
        }
    }
}
