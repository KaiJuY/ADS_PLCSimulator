using AdsPlcSimulator.Core.Plc.Parsing;

namespace AdsPlcSimulator.Core.Plc;

public sealed class PlcBuildException(string message) : Exception(message);

/// <summary>The simulated PLC: its symbol table and the initial content of its process image.</summary>
public sealed class PlcProgram(SymbolTable symbols, byte[] initialImage)
{
    public SymbolTable Symbols { get; } = symbols;

    public byte[] InitialImage { get; } = initialImage;

    public ProcessImage CreateProcessImage() => new(InitialImage);

    public static PlcProgram LoadDirectory(string directory) => PlcProgramBuilder.Build(PlcSourceReader.ReadDirectory(directory));
}

/// <summary>
/// Turns parsed declarations into a memory layout: resolves types, evaluates constants,
/// places every GVL/PROGRAM variable in the process image and writes the declared initial values.
/// </summary>
public sealed class PlcProgramBuilder
{
    private const int DefaultStringLength = 80;
    private const int FunctionBlockHeaderSize = PlcType.PointerSize;

    private readonly Dictionary<string, TypeDeclaration> _typeDeclarations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PlcType> _resolvedTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _typesInProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<StructType, List<(StructMember Member, Expression Value)>> _memberDefaults = [];
    private readonly Dictionary<PlcType, bool> _hasDefaults = [];
    private readonly Dictionary<string, GlobalVariableDeclaration> _constants = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _constantValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _constantsInProgress = new(StringComparer.OrdinalIgnoreCase);

    private PlcProgramBuilder(DeclarationSet declarations)
    {
        foreach (TypeDeclaration declaration in declarations.Types)
        {
            if (PrimitiveType.TryGet(declaration.Name, out _))
                throw new PlcBuildException($"{declaration.Location}: '{declaration.Name}' is a built-in type name.");
            if (!_typeDeclarations.TryAdd(declaration.Name, declaration))
                throw new PlcBuildException($"{declaration.Location}: type '{declaration.Name}' is declared twice (first at {_typeDeclarations[declaration.Name].Location}).");
        }

        foreach (GlobalVariableDeclaration constant in declarations.Globals.Where(global => global.IsConstant))
            _constants[$"{constant.Scope}.{constant.Variable.Name}"] = constant;
    }

    public static PlcProgram Build(DeclarationSet declarations)
    {
        var builder = new PlcProgramBuilder(declarations);
        return builder.BuildProgram(declarations.Globals.Where(global => !global.IsConstant).ToList());
    }

    private PlcProgram BuildProgram(List<GlobalVariableDeclaration> variables)
    {
        var roots = new List<RootSymbol>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int offset = 0;
        foreach (GlobalVariableDeclaration global in variables)
        {
            string path = $"{global.Scope}.{global.Variable.Name}";
            if (!paths.Add(path))
                throw new PlcBuildException($"{global.Variable.Location}: '{path}' is declared twice.");

            PlcType type = Resolve(global.Variable.Type, global.Variable.Location);
            offset = AlignUp(offset, type.Alignment);
            roots.Add(new RootSymbol(path, type, offset));
            offset += type.Size;
        }

        var image = new byte[AlignUp(offset, PlcType.PointerSize)];
        for (int index = 0; index < roots.Count; index++)
        {
            VariableDeclaration variable = variables[index].Variable;
            WriteInitialValue(image, roots[index].Type, roots[index].Offset, variable.InitialValue, variable.Location);
        }

        return new PlcProgram(new SymbolTable(roots, image.Length), image);
    }

    private PlcType Resolve(TypeSpec spec, SourceLocation location) => spec switch
    {
        NamedTypeSpec named => ResolveNamed(named.Name, location),
        StringTypeSpec text => new StringType(
            text.Length is null ? DefaultStringLength : EvaluateInt(text.Length, location),
            text.IsWide),
        ArrayTypeSpec array => new ArrayType(
            Resolve(array.ElementType, location),
            array.Dimensions.Select(bounds => CreateDimension(bounds, location)).ToList()),
        PointerTypeSpec pointer => new PointerType(
            (pointer.IsReference ? "REFERENCE TO " : "POINTER TO ") + DescribeSpec(pointer.Target),
            pointer.IsReference ? PointerKind.Reference : PointerKind.Pointer),
        _ => throw new PlcBuildException($"{location}: unsupported type specification {spec}."),
    };

    private ArrayDimension CreateDimension(ArrayBoundsSpec bounds, SourceLocation location)
    {
        var dimension = new ArrayDimension(EvaluateInt(bounds.Lower, location), EvaluateInt(bounds.Upper, location));
        if (dimension.Length <= 0)
            throw new PlcBuildException($"{location}: array bounds [{dimension}] are empty.");
        return dimension;
    }

    private static string DescribeSpec(TypeSpec spec) => spec switch
    {
        NamedTypeSpec named => named.Name,
        StringTypeSpec text => text.IsWide ? "WSTRING" : "STRING",
        ArrayTypeSpec array => "ARRAY OF " + DescribeSpec(array.ElementType),
        PointerTypeSpec pointer => (pointer.IsReference ? "REFERENCE TO " : "POINTER TO ") + DescribeSpec(pointer.Target),
        _ => spec.ToString(),
    };

    private PlcType ResolveNamed(string name, SourceLocation location)
    {
        if (PrimitiveType.TryGet(name, out PrimitiveType primitive))
            return primitive;
        if (name.Equals("STRING", StringComparison.OrdinalIgnoreCase) || name.Equals("WSTRING", StringComparison.OrdinalIgnoreCase))
            return new StringType(DefaultStringLength, name.Equals("WSTRING", StringComparison.OrdinalIgnoreCase));
        if (_resolvedTypes.TryGetValue(name, out PlcType? resolved))
            return resolved;

        if (!_typeDeclarations.TryGetValue(name, out TypeDeclaration? declaration))
        {
            // Library-qualified names such as Tc2_Standard.TON fall back to the plain type name.
            int separator = name.LastIndexOf('.');
            if (separator > 0)
                return ResolveNamed(name[(separator + 1)..], location);
            throw new PlcBuildException($"{location}: unknown type '{name}'. Declare it in a .st/.TcDUT file of the project.");
        }

        if (!_typesInProgress.Add(declaration.Name))
            throw new PlcBuildException($"{declaration.Location}: type '{declaration.Name}' contains itself.");

        PlcType type = declaration switch
        {
            StructDeclaration structure => BuildStruct(structure),
            EnumDeclaration enumeration => BuildEnum(enumeration),
            AliasDeclaration alias => new AliasType(alias.Name, Resolve(alias.Target, alias.Location)),
            InterfaceDeclaration => new PointerType(declaration.Name, PointerKind.Interface),
            _ => throw new PlcBuildException($"{declaration.Location}: unsupported declaration {declaration}."),
        };

        _typesInProgress.Remove(declaration.Name);
        _resolvedTypes[declaration.Name] = type;
        return type;
    }

    private StructType BuildStruct(StructDeclaration declaration)
    {
        var members = new List<StructMember>();
        var defaults = new List<(StructMember, Expression)>();
        int offset = 0;
        int alignment = 1;

        if (declaration.BaseName is not null)
        {
            if (ResolveNamed(declaration.BaseName, declaration.Location) is not StructType baseType)
                throw new PlcBuildException($"{declaration.Location}: '{declaration.Name}' extends '{declaration.BaseName}', which is not a STRUCT or FUNCTION_BLOCK.");
            members.AddRange(baseType.Members);
            defaults.AddRange(_memberDefaults[baseType]);
            offset = baseType.Size;
            alignment = baseType.Alignment;
        }
        else if (declaration.IsFunctionBlock)
        {
            // TwinCAT places a hidden virtual-table pointer at the start of every root function block.
            offset = FunctionBlockHeaderSize;
            alignment = PlcType.PointerSize;
        }

        foreach (VariableDeclaration variable in declaration.Members)
        {
            if (members.Any(member => member.Name.Equals(variable.Name, StringComparison.OrdinalIgnoreCase)))
                throw new PlcBuildException($"{variable.Location}: member '{variable.Name}' is declared twice in '{declaration.Name}'.");

            PlcType memberType = Resolve(variable.Type, variable.Location);
            offset = AlignUp(offset, memberType.Alignment);
            var member = new StructMember(variable.Name, memberType, offset);
            members.Add(member);
            if (variable.InitialValue is not null)
                defaults.Add((member, variable.InitialValue));

            offset += memberType.Size;
            alignment = Math.Max(alignment, memberType.Alignment);
        }

        // An empty STRUCT still occupies one byte, like any addressable variable.
        int size = Math.Max(1, AlignUp(offset, alignment));
        var type = new StructType(declaration.Name, declaration.IsFunctionBlock, members, size, alignment);
        _memberDefaults[type] = defaults;
        return type;
    }

    private EnumType BuildEnum(EnumDeclaration declaration)
    {
        PrimitiveType baseType = PrimitiveType.FromName("INT");
        if (declaration.BaseTypeName is not null && !PrimitiveType.TryGet(declaration.BaseTypeName, out baseType))
            throw new PlcBuildException($"{declaration.Location}: enum base type '{declaration.BaseTypeName}' must be an integer type.");

        var values = new List<EnumValue>();
        long next = 0;
        foreach (EnumMemberDeclaration member in declaration.Members)
        {
            long value = member.Value is null ? next : EvaluateLong(member.Value, declaration.Location);
            values.Add(new EnumValue(member.Name, value));
            next = value + 1;
        }

        return new EnumType(declaration.Name, baseType, values);
    }

    private void WriteInitialValue(byte[] image, PlcType type, int offset, Expression? value, SourceLocation location)
    {
        PlcType layout = type.Unaliased;
        if (value is null)
        {
            WriteTypeDefaults(image, layout, offset, location);
            return;
        }

        switch (value)
        {
            case StructInitExpression structInit when layout is StructType structure:
                WriteTypeDefaults(image, structure, offset, location);
                foreach (MemberInit init in structInit.Members)
                {
                    StructMember member = structure.Find(init.Name)
                        ?? throw new PlcBuildException($"{location}: {structure.Name} has no member '{init.Name}'.");
                    WriteInitialValue(image, member.Type, offset + member.Offset, init.Value, location);
                }

                return;
            case ArrayInitExpression arrayInit when layout is ArrayType array:
                WriteTypeDefaults(image, array, offset, location);
                int index = 0;
                foreach (ArrayInitItem item in arrayInit.Items)
                {
                    int repeat = item.RepeatCount is null ? 1 : EvaluateInt(item.RepeatCount, location);
                    for (int copy = 0; copy < repeat; copy++, index++)
                    {
                        if (index >= array.ElementCount)
                            throw new PlcBuildException($"{location}: too many initial values for {array.Name}.");
                        WriteInitialValue(image, array.ElementType, offset + index * array.ElementType.Size, item.Value, location);
                    }
                }

                return;
            case StructInitExpression or ArrayInitExpression:
                throw new PlcBuildException($"{location}: initializer does not match type {type.Name}.");
        }

        object scalar = layout is EnumType enumeration && value is NameExpression name && enumeration.Find(name.QualifiedName) is { } enumValue
            ? enumValue.Value
            : Evaluate(value, location);
        try
        {
            ValueCodec.Encode(type, scalar, image.AsSpan(offset, type.Size));
        }
        catch (Exception exception) when (exception is InvalidCastException or OverflowException or ArgumentException or InvalidOperationException)
        {
            throw new PlcBuildException($"{location}: initial value is invalid for {type.Name}: {exception.Message}");
        }
    }

    private void WriteTypeDefaults(byte[] image, PlcType type, int offset, SourceLocation location)
    {
        if (!HasDefaults(type))
            return;

        switch (type.Unaliased)
        {
            case StructType structure:
                foreach (StructMember member in structure.Members)
                    WriteTypeDefaults(image, member.Type, offset + member.Offset, location);
                foreach ((StructMember member, Expression value) in _memberDefaults[structure])
                    WriteInitialValue(image, member.Type, offset + member.Offset, value, location);
                return;
            case ArrayType array:
                for (int index = 0; index < array.ElementCount; index++)
                    WriteTypeDefaults(image, array.ElementType, offset + index * array.ElementType.Size, location);
                return;
            case EnumType enumeration:
                ValueCodec.Encode(enumeration, enumeration.Values[0].Value, image.AsSpan(offset, enumeration.Size));
                return;
        }
    }

    /// <summary>Lets large arrays of plain structs skip the per-element default pass.</summary>
    private bool HasDefaults(PlcType type)
    {
        PlcType layout = type.Unaliased;
        if (_hasDefaults.TryGetValue(layout, out bool cached))
            return cached;

        bool result = layout switch
        {
            StructType structure => _memberDefaults[structure].Count > 0 || structure.Members.Any(member => HasDefaults(member.Type)),
            ArrayType array => HasDefaults(array.ElementType),
            EnumType enumeration => enumeration.Values.Count > 0 && enumeration.Values[0].Value != 0,
            _ => false,
        };
        _hasDefaults[layout] = result;
        return result;
    }

    private int EvaluateInt(Expression expression, SourceLocation location) =>
        checked((int)EvaluateLong(expression, location));

    private long EvaluateLong(Expression expression, SourceLocation location)
    {
        object value = Evaluate(expression, location);
        try
        {
            return PlcValue.ToInt64(value);
        }
        catch (InvalidCastException exception)
        {
            throw new PlcBuildException($"{location}: expected an integer constant: {exception.Message}");
        }
    }

    private object Evaluate(Expression expression, SourceLocation location)
    {
        switch (expression)
        {
            case LiteralExpression literal:
                return literal.Value;
            case NameExpression name:
                return EvaluateName(name.QualifiedName, location);
            case UnaryExpression { Operator: "-" } unary:
                object operand = Evaluate(unary.Operand, location);
                return operand is double floating ? -floating : -PlcValue.ToInt64(operand);
            case UnaryExpression unary:
                return Evaluate(unary.Operand, location);
            case BinaryExpression binary:
                return EvaluateBinary(binary, location);
            default:
                throw new PlcBuildException($"{location}: {expression.GetType().Name} is not a constant value.");
        }
    }

    private object EvaluateBinary(BinaryExpression binary, SourceLocation location)
    {
        object left = Evaluate(binary.Left, location);
        object right = Evaluate(binary.Right, location);
        if (left is double || right is double)
        {
            double a = PlcValue.ToDouble(left);
            double b = PlcValue.ToDouble(right);
            return binary.Operator switch
            {
                "+" => a + b,
                "-" => a - b,
                "*" => a * b,
                "/" => a / b,
                _ => a % b,
            };
        }

        long x = PlcValue.ToInt64(left);
        long y = PlcValue.ToInt64(right);
        if (y == 0 && binary.Operator is "/" or "MOD")
            throw new PlcBuildException($"{location}: division by zero in constant expression.");
        return binary.Operator switch
        {
            "+" => x + y,
            "-" => x - y,
            "*" => x * y,
            "/" => x / y,
            _ => x % y,
        };
    }

    private object EvaluateName(string qualifiedName, SourceLocation location)
    {
        GlobalVariableDeclaration? constant = FindConstant(qualifiedName, location);
        if (constant is not null)
            return EvaluateConstant(constant);

        int separator = qualifiedName.LastIndexOf('.');
        if (separator > 0 && _typeDeclarations.ContainsKey(qualifiedName[..separator])
            && ResolveNamed(qualifiedName[..separator], location) is EnumType enumeration
            && enumeration.Find(qualifiedName[(separator + 1)..]) is { } enumValue)
        {
            return enumValue.Value;
        }

        throw new PlcBuildException($"{location}: '{qualifiedName}' is not a known constant or enum value.");
    }

    private GlobalVariableDeclaration? FindConstant(string name, SourceLocation location)
    {
        if (_constants.TryGetValue(name, out GlobalVariableDeclaration? qualified))
            return qualified;
        if (name.Contains('.'))
            return null;

        var matches = _constants.Values
            .Where(constant => constant.Variable.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count > 1)
            throw new PlcBuildException($"{location}: constant '{name}' is ambiguous; qualify it with its GVL name.");
        return matches.SingleOrDefault();
    }

    private object EvaluateConstant(GlobalVariableDeclaration constant)
    {
        string key = $"{constant.Scope}.{constant.Variable.Name}";
        if (_constantValues.TryGetValue(key, out object? cached))
            return cached;
        if (constant.Variable.InitialValue is null)
            throw new PlcBuildException($"{constant.Variable.Location}: constant '{key}' has no value.");
        if (!_constantsInProgress.Add(key))
            throw new PlcBuildException($"{constant.Variable.Location}: constant '{key}' depends on itself.");

        object value = Evaluate(constant.Variable.InitialValue, constant.Variable.Location);
        _constantsInProgress.Remove(key);
        _constantValues[key] = value;
        return value;
    }

    private static int AlignUp(int offset, int alignment) => (offset + alignment - 1) / alignment * alignment;
}
