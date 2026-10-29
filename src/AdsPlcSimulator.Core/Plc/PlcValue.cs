using System.Globalization;

namespace AdsPlcSimulator.Core.Plc;

/// <summary>
/// Conversions for the loosely typed values that flow through workflows:
/// <see cref="bool"/>, <see cref="long"/>, <see cref="double"/> and <see cref="string"/>.
/// </summary>
public static class PlcValue
{
    public static bool IsNumeric(object? value) => value is long or double or int or float or short or byte;

    public static bool ToBoolean(object? value) => value switch
    {
        bool boolean => boolean,
        long or int or short or byte => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
        double or float => Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0,
        string text when bool.TryParse(text, out bool parsed) => parsed,
        string text when TryParseNumber(text, out object? number) => ToBoolean(number),
        _ => throw new InvalidCastException($"Cannot convert {Describe(value)} to BOOL."),
    };

    public static double ToDouble(object? value) => value switch
    {
        bool boolean => boolean ? 1 : 0,
        long or int or short or byte or double or float => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        TimeSpan duration => duration.TotalMilliseconds,
        string text when TryParseNumber(text, out object? number) => ToDouble(number),
        _ => throw new InvalidCastException($"Cannot convert {Describe(value)} to a number."),
    };

    public static long ToInt64(object? value)
    {
        switch (value)
        {
            case bool boolean:
                return boolean ? 1 : 0;
            case long or int or short or byte:
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            case TimeSpan duration:
                return (long)duration.TotalMilliseconds;
            case string text when TryParseNumber(text, out object? number):
                return ToInt64(number);
        }

        double floating = ToDouble(value);
        if (Math.Abs(floating % 1) > double.Epsilon || double.IsNaN(floating))
            throw new InvalidCastException($"{floating.ToString(CultureInfo.InvariantCulture)} is not an integer.");
        return (long)floating;
    }

    /// <summary>Parses user-entered text: TRUE/FALSE, integers, reals; anything else stays a string.</summary>
    public static object ParseLiteral(string text)
    {
        string trimmed = text.Trim();
        if (bool.TryParse(trimmed, out bool boolean))
            return boolean;
        return TryParseNumber(trimmed, out object? number) ? number! : text;
    }

    public static string Format(object? value) => value switch
    {
        null => "null",
        bool boolean => boolean ? "TRUE" : "FALSE",
        double floating => floating.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static bool TryParseNumber(string text, out object? number)
    {
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
        {
            number = integer;
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double floating))
        {
            number = floating;
            return true;
        }

        number = null;
        return false;
    }

    private static string Describe(object? value) => value is null ? "null" : $"'{Format(value)}' ({value.GetType().Name})";
}
