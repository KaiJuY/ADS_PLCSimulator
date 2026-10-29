namespace AdsPlcSimulator.Core.Plc.Parsing;

public sealed class PlcParseException(string message) : Exception(message);

public sealed record SourceLocation(string File, int Line)
{
    public override string ToString() => $"{File}:{Line}";
}

public abstract record TypeSpec;

public sealed record NamedTypeSpec(string Name) : TypeSpec;

public sealed record StringTypeSpec(bool IsWide, Expression? Length) : TypeSpec;

public sealed record ArrayTypeSpec(IReadOnlyList<ArrayBoundsSpec> Dimensions, TypeSpec ElementType) : TypeSpec;

public sealed record ArrayBoundsSpec(Expression Lower, Expression Upper);

public sealed record PointerTypeSpec(TypeSpec Target, bool IsReference) : TypeSpec;

public abstract record Expression;

/// <summary>Value is a bool, long, double or string.</summary>
public sealed record LiteralExpression(object Value) : Expression;

public sealed record NameExpression(string QualifiedName) : Expression;

public sealed record UnaryExpression(string Operator, Expression Operand) : Expression;

public sealed record BinaryExpression(string Operator, Expression Left, Expression Right) : Expression;

public sealed record StructInitExpression(IReadOnlyList<MemberInit> Members) : Expression;

public sealed record MemberInit(string Name, Expression Value);

public sealed record ArrayInitExpression(IReadOnlyList<ArrayInitItem> Items) : Expression;

/// <summary>An array initializer item, optionally repeated: <c>3(0)</c> means three zeros.</summary>
public sealed record ArrayInitItem(Expression? RepeatCount, Expression Value);

public sealed record VariableDeclaration(string Name, TypeSpec Type, Expression? InitialValue, SourceLocation Location);

public abstract record TypeDeclaration(string Name, SourceLocation Location);

public sealed record StructDeclaration(
    string Name,
    string? BaseName,
    bool IsFunctionBlock,
    IReadOnlyList<VariableDeclaration> Members,
    SourceLocation Location) : TypeDeclaration(Name, Location);

public sealed record EnumDeclaration(
    string Name,
    IReadOnlyList<EnumMemberDeclaration> Members,
    string? BaseTypeName,
    SourceLocation Location) : TypeDeclaration(Name, Location);

public sealed record EnumMemberDeclaration(string Name, Expression? Value);

public sealed record AliasDeclaration(string Name, TypeSpec Target, SourceLocation Location) : TypeDeclaration(Name, Location);

public sealed record InterfaceDeclaration(string Name, SourceLocation Location) : TypeDeclaration(Name, Location);

/// <summary>
/// A variable that owns process-image memory (GVL or PROGRAM variable), or a global constant.
/// Its ADS instance path is <c>Scope.Name</c>.
/// </summary>
public sealed record GlobalVariableDeclaration(string Scope, VariableDeclaration Variable, bool IsConstant);

public sealed class DeclarationSet
{
    public List<TypeDeclaration> Types { get; } = [];

    public List<GlobalVariableDeclaration> Globals { get; } = [];

    public void Add(DeclarationSet other)
    {
        Types.AddRange(other.Types);
        Globals.AddRange(other.Globals);
    }
}
