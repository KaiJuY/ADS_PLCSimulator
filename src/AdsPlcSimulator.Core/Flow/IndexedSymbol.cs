using AdsPlcSimulator.Core.Plc;

namespace AdsPlcSimulator.Core.Flow;

/// <summary>A symbol path with at most one "[#]" array index that is filled in at run time.</summary>
public sealed class IndexedSymbol
{
    private const string Placeholder = "[#]";

    private readonly string _prefix;
    private readonly string _suffix;
    private readonly SymbolRef _fixed;

    private IndexedSymbol(string template, string prefix, string suffix, SymbolRef sample, long lowerBound, long upperBound, bool isIndexed)
    {
        Template = template;
        _prefix = prefix;
        _suffix = suffix;
        _fixed = sample;
        Type = sample.Type;
        LowerBound = lowerBound;
        UpperBound = upperBound;
        IsIndexed = isIndexed;
    }

    public string Template { get; }

    public bool IsIndexed { get; }

    public PlcType Type { get; }

    public long LowerBound { get; }

    public long UpperBound { get; }

    /// <exception cref="NodeConfigurationException">The path is not valid, has more than one "[#]", or is not scalar when required.</exception>
    public static IndexedSymbol Parse(string path, SymbolTable symbols, bool scalar)
    {
        int first = path.IndexOf(Placeholder, StringComparison.Ordinal);
        if (first < 0)
            return new IndexedSymbol(path, path, string.Empty, ResolveChecked(path, symbols, scalar), 0, 0, isIndexed: false);
        if (path.IndexOf(Placeholder, first + Placeholder.Length, StringComparison.Ordinal) >= 0)
            throw new NodeConfigurationException("Only one [#] is allowed.");

        string prefix = path[..first];
        string suffix = path[(first + Placeholder.Length)..];
        if (ResolveChecked(prefix, symbols, scalar: false).Type.Unaliased is not ArrayType { Dimensions.Count: 1 } array)
            throw new NodeConfigurationException($"'{prefix}' in '{path}' must be a one-dimensional array.");

        ArrayDimension bounds = array.Dimensions[0];
        SymbolRef sample = ResolveChecked($"{prefix}[{bounds.LowerBound}]{suffix}", symbols, scalar);
        return new IndexedSymbol(path, prefix, suffix, sample, bounds.LowerBound, bounds.UpperBound, isIndexed: true);
    }

    /// <exception cref="InvalidOperationException">The path needs an index and none was given, or the index is out of range.</exception>
    public SymbolRef Resolve(SymbolTable symbols, object? index)
    {
        if (!IsIndexed)
            return _fixed;
        if (index is null)
            throw new InvalidOperationException($"{Template} needs input 'index'.");

        long number = PlcValue.ToInt64(index);
        if (number < LowerBound || number > UpperBound)
            throw new InvalidOperationException($"Index {number} is outside {LowerBound}..{UpperBound} of {Template}.");
        return symbols.Resolve($"{_prefix}[{number}]{_suffix}");
    }

    public IReadOnlyList<(long? Index, SymbolRef Symbol)> ResolveAll(SymbolTable symbols)
    {
        if (!IsIndexed)
            return [(null, _fixed)];

        // Stryker disable once Checked: an array whose length overflows int cannot be declared in a process image
        return Enumerable.Range(0, checked((int)(UpperBound - LowerBound + 1)))
            .Select(offset => LowerBound + offset)
            .Select(index => ((long?)index, symbols.Resolve($"{_prefix}[{index}]{_suffix}")))
            .ToList();
    }

    private static SymbolRef ResolveChecked(string path, SymbolTable symbols, bool scalar)
    {
        if (string.IsNullOrEmpty(path))
            throw new NodeConfigurationException("A PLC symbol path is needed.");
        if (!symbols.TryResolve(path, out SymbolRef symbol, out string? error))
            throw new NodeConfigurationException(error!);
        if (scalar && !symbol.Type.IsScalar)
            throw new NodeConfigurationException($"'{symbol.Path}' is {symbol.Type.Name}; pick one of its scalar members.");
        return symbol;
    }
}
