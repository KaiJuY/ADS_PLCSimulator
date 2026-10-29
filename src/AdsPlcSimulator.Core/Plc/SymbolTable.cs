using System.Globalization;
using System.Text;

namespace AdsPlcSimulator.Core.Plc;

/// <summary>A GVL or PROGRAM variable, e.g. <c>GVL_Module.Chamber1_Control</c>, placed in the process image.</summary>
public sealed record RootSymbol(string Path, PlcType Type, int Offset);

/// <summary>Any addressable variable, down to a single struct member or array element.</summary>
public readonly record struct SymbolRef(string Path, PlcType Type, int Offset)
{
    public int Size => Type.Size;

    public override string ToString() => $"{Path} : {Type.Name} @ {Offset}";
}

public sealed class SymbolResolutionException(string message) : Exception(message);

/// <summary>
/// Resolves ADS-style instance paths (<c>GVL.Var[1].Member</c>, case-insensitive) to typed memory locations.
/// </summary>
public sealed class SymbolTable
{
    private readonly Dictionary<string, RootSymbol> _rootsByPath;

    public SymbolTable(IReadOnlyList<RootSymbol> roots, int imageSize)
    {
        Roots = roots;
        ImageSize = imageSize;
        _rootsByPath = roots.ToDictionary(root => root.Path, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RootSymbol> Roots { get; }

    public int ImageSize { get; }

    public SymbolRef Resolve(string path) =>
        TryResolve(path, out SymbolRef symbol, out string? error) ? symbol : throw new SymbolResolutionException(error!);

    public bool TryResolve(string path, out SymbolRef symbol, out string? error)
    {
        symbol = default;
        error = null;
        try
        {
            symbol = ResolveSegments(path, ParsePath(path));
            return true;
        }
        catch (Exception exception) when (exception is SymbolResolutionException or ArgumentException or FormatException)
        {
            error = exception.Message;
            return false;
        }
    }

    /// <summary>Every scalar variable, depth first. Large arrays make this long; callers should limit it.</summary>
    public IEnumerable<SymbolRef> EnumerateScalars()
    {
        foreach (RootSymbol root in Roots)
        {
            foreach (SymbolRef scalar in EnumerateScalars(new SymbolRef(root.Path, root.Type, root.Offset)))
                yield return scalar;
        }
    }

    private static IEnumerable<SymbolRef> EnumerateScalars(SymbolRef symbol)
    {
        switch (symbol.Type.Unaliased)
        {
            case StructType structure:
                foreach (StructMember member in structure.Members)
                {
                    var child = new SymbolRef($"{symbol.Path}.{member.Name}", member.Type, symbol.Offset + member.Offset);
                    foreach (SymbolRef scalar in EnumerateScalars(child))
                        yield return scalar;
                }

                yield break;
            case ArrayType array:
                foreach (int[] indices in EnumerateIndices(array.Dimensions))
                {
                    var element = new SymbolRef(
                        $"{symbol.Path}[{string.Join(",", indices)}]",
                        array.ElementType,
                        symbol.Offset + array.OffsetOf(indices));
                    foreach (SymbolRef scalar in EnumerateScalars(element))
                        yield return scalar;
                }

                yield break;
            default:
                yield return symbol;
                yield break;
        }
    }

    private static IEnumerable<int[]> EnumerateIndices(IReadOnlyList<ArrayDimension> dimensions)
    {
        int[] current = dimensions.Select(dimension => dimension.LowerBound).ToArray();
        while (true)
        {
            yield return (int[])current.Clone();
            int position = dimensions.Count - 1;
            while (position >= 0 && current[position] == dimensions[position].UpperBound)
            {
                current[position] = dimensions[position].LowerBound;
                position--;
            }

            if (position < 0)
                yield break;
            current[position]++;
        }
    }

    private SymbolRef ResolveSegments(string path, List<PathSegment> segments)
    {
        if (segments.Count < 2 || segments[0].Member is null || segments[1].Member is null)
            throw new SymbolResolutionException($"'{path}' must start with a scope and a variable name, e.g. 'GVL_Main.Variable'.");

        string rootPath = $"{segments[0].Member}.{segments[1].Member}";
        if (!_rootsByPath.TryGetValue(rootPath, out RootSymbol? root))
            throw new SymbolResolutionException($"Symbol '{rootPath}' is not declared.");

        var current = new SymbolRef(root.Path, root.Type, root.Offset);
        foreach (PathSegment segment in segments.Skip(2))
            current = segment.Member is not null ? SelectMember(current, segment.Member) : SelectElement(current, segment.Indices!);
        return current;
    }

    private static SymbolRef SelectMember(SymbolRef parent, string memberName)
    {
        if (parent.Type.Unaliased is not StructType structure)
            throw new SymbolResolutionException($"'{parent.Path}' is {parent.Type.Name} and has no member '{memberName}'.");

        StructMember member = structure.Find(memberName)
            ?? throw new SymbolResolutionException($"{structure.Name} has no member '{memberName}' (in '{parent.Path}').");
        return new SymbolRef($"{parent.Path}.{member.Name}", member.Type, parent.Offset + member.Offset);
    }

    private static SymbolRef SelectElement(SymbolRef parent, IReadOnlyList<int> indices)
    {
        if (parent.Type.Unaliased is not ArrayType array)
            throw new SymbolResolutionException($"'{parent.Path}' is {parent.Type.Name}, not an array.");

        int offset = array.OffsetOf(indices);
        return new SymbolRef($"{parent.Path}[{string.Join(",", indices)}]", array.ElementType, parent.Offset + offset);
    }

    private sealed record PathSegment(string? Member, IReadOnlyList<int>? Indices);

    private static List<PathSegment> ParsePath(string path)
    {
        var segments = new List<PathSegment>();
        var name = new StringBuilder();
        int position = 0;

        void FlushName()
        {
            if (name.Length == 0)
                return;
            segments.Add(new PathSegment(name.ToString().Trim(), null));
            name.Clear();
        }

        while (position < path.Length)
        {
            char current = path[position];
            if (current == '.')
            {
                FlushName();
                position++;
                continue;
            }

            if (current == '[')
            {
                FlushName();
                int end = path.IndexOf(']', position);
                if (end < 0)
                    throw new SymbolResolutionException($"Missing ']' in '{path}'.");
                int[] indices = path[(position + 1)..end]
                    .Split(',')
                    .Select(index => int.Parse(index.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture))
                    .ToArray();
                segments.Add(new PathSegment(null, indices));
                position = end + 1;
                continue;
            }

            name.Append(current);
            position++;
        }

        FlushName();
        return segments;
    }
}
