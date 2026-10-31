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

    private static readonly Dictionary<int, Func<string, bool>> StrictRules = new()
    {
        [String80] = value => value.Length <= 80,
        [Boolean] = value => value is "TRUE" or "FALSE",
        [Integer8] = value => IsStrictInteger(value, signed: true),
        [UnsignedInteger8] = value => IsStrictInteger(value, signed: false),
        [Integer16] = value => IsStrictInteger(value, signed: true),
        [UnsignedInteger16] = value => IsStrictInteger(value, signed: false),
        [Integer32] = value => IsStrictInteger(value, signed: true),
        [UnsignedInteger32] = value => IsStrictInteger(value, signed: false),
        [Float32] = IsStrictFloat,
        [Float64] = IsStrictFloat,
    };

    // FW: M_ValidateConfigParamBatch numeric branch
    public static bool IsNumeric(int configDataType) => TypeRanges.ContainsKey(configDataType) || configDataType == Float64;

    public static bool IsStrict(string value, int configDataType) =>
        StrictRules.TryGetValue(configDataType, out Func<string, bool>? rule) && rule(value);

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
            // Stryker disable once String: ToString("") on float formats like "R" (shortest round-trip text) in .NET Core 3.0 and later, so the output is the same
            Float32 => WithWholeNumberSuffix(((float)number).ToString("R", CultureInfo.InvariantCulture)),
            // Stryker disable once String: ToString("") on double formats like "R" (shortest round-trip text) in .NET Core 3.0 and later, so the output is the same
            Float64 => WithWholeNumberSuffix(number.ToString("R", CultureInfo.InvariantCulture)),
            _ => ((long)number).ToString(CultureInfo.InvariantCulture),
        };
    }

    // TwinCAT REAL_TO_STRING publishes a whole number as "2.0". Exponent form (e.g. 1E+20) was not measured, so it keeps the "R" text.
    private static string WithWholeNumberSuffix(string text) =>
        AllDigits(text, SignLength(text, signed: true)) ? text + ".0" : text;

    public static double ParseNumber(string value) =>
        double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    public static int? ToConfigDataType(long descriptorType) =>
        FromDescriptor.TryGetValue(descriptorType, out int configType) ? configType : null;

    private static bool IsStrictInteger(string value, bool signed)
    {
        int start = SignLength(value, signed);
        return start < value.Length && AllDigits(value, start);
    }

    private static int SignLength(string value, bool signed) =>
        signed && value.Length > 0 && value[0] is '+' or '-' ? 1 : 0;

    private static bool AllDigits(string value, int start) =>
        value.AsSpan(start).IndexOfAnyExceptInRange('0', '9') < 0;

    private static bool IsStrictFloat(string value)
    {
        int start = SignLength(value, signed: true);
        var state = new FloatState();
        for (int index = start; index < value.Length; index++)
        {
            if (!state.Accept(value, index))
                return false;
        }

        return state.Digit && (!state.Exponent || state.ExponentDigit);
    }

    private sealed class FloatState
    {
        private static readonly Dictionary<char, Func<FloatState, string, int, bool>> Rules = new()
        {
            ['.'] = (state, _, _) => state.AcceptDot(),
            ['e'] = (state, _, _) => state.AcceptExponent(),
            ['E'] = (state, _, _) => state.AcceptExponent(),
            ['+'] = (_, value, index) => value[index - 1] is 'e' or 'E',
            ['-'] = (_, value, index) => value[index - 1] is 'e' or 'E',
        };

        public bool Digit { get; private set; }

        public bool Dot { get; private set; }

        public bool Exponent { get; private set; }

        public bool ExponentDigit { get; private set; }

        // A sign is valid only right after e/E: that e/E was accepted, so it set Exponent and no exponent digit has followed yet.
        public bool Accept(string value, int index)
        {
            char c = value[index];
            if (char.IsAsciiDigit(c))
                return AcceptDigit();
            return Rules.TryGetValue(c, out Func<FloatState, string, int, bool>? rule) && rule(this, value, index);
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
