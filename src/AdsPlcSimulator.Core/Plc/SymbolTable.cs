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

/// <summary>One entry of a symbol tree level: a scope, a root, a struct member, an array element, an index prefix of a
/// multi-dimensional array, or a range of indices of a large array.</summary>
public sealed record SymbolChild(string Name, string Path, string? Type, bool IsScalar, int? From = null, int? To = null);

/// <summary>
/// Resolves ADS-style instance paths (<c>GVL.Var[1].Member</c>, case-insensitive) to typed memory locations.
/// </summary>
public sealed class SymbolTable
{
    public const int ChildPageSize = 50;

    private readonly Dictionary<string, RootSymbol> _rootsByPath;
    private readonly Dictionary<string, long> _enumValues;
    private readonly Dictionary<string, object> _constants;

    public SymbolTable(IReadOnlyList<RootSymbol> roots, int imageSize, IReadOnlyDictionary<string, long>? enumValues = null,
        IReadOnlyDictionary<string, object>? constants = null)
    {
        Roots = roots;
        ImageSize = imageSize;
        _rootsByPath = roots.ToDictionary(root => root.Path, StringComparer.OrdinalIgnoreCase);
        _enumValues = new Dictionary<string, long>(enumValues ?? new Dictionary<string, long>(), StringComparer.OrdinalIgnoreCase);
        _constants = new Dictionary<string, object>(constants ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RootSymbol> Roots { get; }

    public int ImageSize { get; }

    /// <summary>Value of a declared enum member, e.g. "E_PolarIIChamberServiceId.ShutterOpen"; case-insensitive.</summary>
    public bool TryGetEnumValue(string qualifiedName, out long value) => _enumValues.TryGetValue(qualifiedName, out value);

    /// <summary>Value of a VAR_GLOBAL CONSTANT, e.g. "Param_Config.ModuleHeartbeat_Timeout"; case-insensitive.
    /// The value has the natural type of the literal or expression: a T# literal is TimeSpan, an integer expression long, a real literal double.</summary>
    public bool TryGetConstant(string qualifiedName, out object value) => _constants.TryGetValue(qualifiedName, out value!);

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

    /// <summary>One level of the symbol tree under <paramref name="path"/>: scopes for null or blank, roots for a scope,
    /// members for a struct, elements or index ranges for an array. <paramref name="from"/> and <paramref name="to"/>
    /// are inclusive index values and select a page of an array level.</summary>
    public bool TryGetChildren(string? path, int? from, int? to, out IReadOnlyList<SymbolChild> children, out string? error)
    {
        children = [];
        error = null;
        try
        {
            children = ListChildren(path, from, to);
            return true;
        }
        catch (Exception exception) when (exception is SymbolResolutionException or ArgumentException or FormatException or OverflowException)
        {
            error = exception.Message;
            return false;
        }
    }

    private IReadOnlyList<SymbolChild> ListChildren(string? path, int? from, int? to)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            RequireNoRange(from, to);
            return Roots
                .Select(root => root.Path.Split('.')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(scope => new SymbolChild(scope, scope, null, false))
                .ToList();
        }

        List<PathSegment> segments = ParsePath(path);
        if (segments is [{ Member: { } scopeName }])
            return ListScope(path, scopeName, from, to);

        SymbolRef parent = default;
        IReadOnlyList<int> prefix = [];
        bool prefixMode = false;
        if (segments is [.., { Member: null, Indices: { } lastIndices }])
        {
            parent = ResolveSegments(path, segments.GetRange(0, segments.Count - 1));
            prefixMode = parent.Type.Unaliased is ArrayType parentArray && lastIndices.Count < parentArray.Dimensions.Count;
            if (prefixMode)
            {
                prefix = lastIndices;
                CheckPrefix((ArrayType)parent.Type.Unaliased, prefix);
            }
        }

        if (!prefixMode)
            parent = ResolveSegments(path, segments);

        switch (parent.Type.Unaliased)
        {
            case StructType structure:
                RequireNoRange(from, to);
                return structure.Members
                    .Select(member => ChildOf(MemberOf(parent, member), member.Name))
                    .ToList();
            case ArrayType array:
                return ListArray(parent, array, prefix, from, to);
            default:
                throw new SymbolResolutionException($"'{parent.Path}' is {parent.Type.Name} and has no children.");
        }
    }

    private IReadOnlyList<SymbolChild> ListScope(string path, string scopeName, int? from, int? to)
    {
        RequireNoRange(from, to);
        List<SymbolChild> roots = Roots
            .Where(root => root.Path.Split('.')[0].Equals(scopeName, StringComparison.OrdinalIgnoreCase))
            .Select(root => ChildOf(root.Path, root.Type, root.Path.Split('.')[1]))
            .ToList();
        if (roots.Count == 0)
            throw new SymbolResolutionException($"Scope '{path}' is not declared.");
        return roots;
    }

    private static IReadOnlyList<SymbolChild> ListArray(SymbolRef baseRef, ArrayType array, IReadOnlyList<int> prefix, int? from, int? to)
    {
        int level = prefix.Count;
        ArrayDimension dimension = array.Dimensions[level];
        (int first, int last) = PageBounds(dimension, from, to);
        if (!from.HasValue && dimension.Length > ChildPageSize)
            return ListRanges(level == 0 ? baseRef.Path : $"{baseRef.Path}[{string.Join(",", prefix)}]", dimension);

        bool isLastDimension = level + 1 == array.Dimensions.Count;
        var items = new List<SymbolChild>();
        for (int index = first; index <= last; index++)
        {
            int[] indices = [.. prefix, index];
            items.Add(isLastDimension
                ? ChildOf(ElementOf(baseRef, array, indices), $"[{index}]")
                : new SymbolChild($"[{index}]", $"{baseRef.Path}[{string.Join(",", indices)}]", null, false));
        }

        return items;
    }

    private static (int First, int Last) PageBounds(ArrayDimension dimension, int? from, int? to)
    {
        if (from.HasValue != to.HasValue)
            throw new SymbolResolutionException("from and to must be given together.");

        int first = from ?? dimension.LowerBound;
        int last = to ?? dimension.UpperBound;
        bool inside = first >= dimension.LowerBound && first <= last && last <= dimension.UpperBound;
        if (from.HasValue && !(inside && (long)last - first + 1 <= ChildPageSize))
            throw new SymbolResolutionException($"Range {first}..{last} is outside {dimension} or longer than {ChildPageSize}.");
        return (first, last);
    }

    private static void CheckPrefix(ArrayType array, IReadOnlyList<int> prefix)
    {
        for (int level = 0; level < prefix.Count; level++)
        {
            ArrayDimension dimension = array.Dimensions[level];
            if (prefix[level] < dimension.LowerBound || prefix[level] > dimension.UpperBound)
                throw new SymbolResolutionException($"Index {prefix[level]} is outside [{dimension}] of {array.Name}.");
        }
    }

    private static List<SymbolChild> ListRanges(string path, ArrayDimension dimension)
    {
        var ranges = new List<SymbolChild>();
        for (int first = dimension.LowerBound; first <= dimension.UpperBound; first += ChildPageSize)
        {
            int last = Math.Min(first + ChildPageSize - 1, dimension.UpperBound);
            ranges.Add(new SymbolChild($"[{first}..{last}]", path, null, false, first, last));
        }

        return ranges;
    }

    private static SymbolChild ChildOf(SymbolRef symbol, string name) =>
        ChildOf(symbol.Path, symbol.Type, name);

    private static SymbolChild ChildOf(string path, PlcType type, string name) =>
        new(name, path, type.Name, type.IsScalar);

    private static void RequireNoRange(int? from, int? to)
    {
        if (from.HasValue || to.HasValue)
            throw new SymbolResolutionException("from and to are only valid for an array.");
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
                    foreach (SymbolRef scalar in EnumerateScalars(MemberOf(symbol, member)))
                        yield return scalar;
                }

                yield break;
            case ArrayType array:
                foreach (int[] indices in EnumerateIndices(array.Dimensions))
                {
                    foreach (SymbolRef scalar in EnumerateScalars(ElementOf(symbol, array, indices)))
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
        return MemberOf(parent, member);
    }

    private static SymbolRef MemberOf(SymbolRef parent, StructMember member) =>
        new($"{parent.Path}.{member.Name}", member.Type, parent.Offset + member.Offset);

    private static SymbolRef ElementOf(SymbolRef parent, ArrayType array, IReadOnlyList<int> indices) =>
        new($"{parent.Path}[{string.Join(",", indices)}]", array.ElementType, parent.Offset + array.OffsetOf(indices));

    private static SymbolRef SelectElement(SymbolRef parent, IReadOnlyList<int> indices)
    {
        if (parent.Type.Unaliased is not ArrayType array)
            throw new SymbolResolutionException($"'{parent.Path}' is {parent.Type.Name}, not an array.");

        return ElementOf(parent, array, indices);
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
