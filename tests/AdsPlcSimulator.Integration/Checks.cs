namespace AdsPlcSimulator.Integration;

/// <summary>Collects the expected/actual pairs of one scenario.</summary>
internal sealed class Checks
{
    private readonly List<string> _expected = [];
    private readonly List<string> _actual = [];

    public bool Passed { get; private set; } = true;

    public string Expected => string.Join("; ", _expected);

    public string Actual => string.Join("; ", _actual);

    public void Equal<T>(string label, T expected, T actual)
    {
        bool same = EqualityComparer<T>.Default.Equals(expected, actual);
        Passed &= same;
        _expected.Add($"{label}={expected}");
        _actual.Add(same ? $"{label}={actual}" : $"{label}={actual} (WRONG)");
    }

    public void True(string label, bool condition, string detail = "")
    {
        Passed &= condition;
        _expected.Add(label);
        _actual.Add(condition ? $"{label} {detail}".TrimEnd() : $"NOT {label} {detail}".TrimEnd());
    }
}
