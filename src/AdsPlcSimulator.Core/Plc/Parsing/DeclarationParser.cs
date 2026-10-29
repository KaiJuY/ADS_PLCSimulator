using System.Globalization;
using System.Text.RegularExpressions;

namespace AdsPlcSimulator.Core.Plc.Parsing;

/// <summary>
/// Recursive-descent parser for the declaration part of IEC 61131-3 sources:
/// TYPE, FUNCTION_BLOCK, PROGRAM, INTERFACE and VAR_GLOBAL. Implementation code is skipped,
/// because the simulator replaces PLC logic with workflows.
/// </summary>
public sealed partial class DeclarationParser
{
    private readonly List<Token> _tokens;
    private readonly string _fileName;
    private readonly string _globalScope;
    private readonly DeclarationSet _result = new();
    private int _position;

    private DeclarationParser(string text, string fileName, string globalScope)
    {
        _tokens = Lexer.Tokenize(text);
        _fileName = fileName;
        _globalScope = globalScope;
    }

    /// <param name="globalScope">Name used for VAR_GLOBAL blocks, i.e. the GVL name.</param>
    public static DeclarationSet Parse(string text, string fileName, string globalScope)
    {
        var parser = new DeclarationParser(text, fileName, globalScope);
        parser.ParseUnit();
        return parser._result;
    }

    private Token Current => _tokens[_position];

    private Token Peek(int offset = 1) => _tokens[Math.Min(_position + offset, _tokens.Count - 1)];

    private void ParseUnit()
    {
        while (Current.Kind != TokenKind.End)
        {
            Token keyword = Current;
            if (keyword.IsKeyword("TYPE")) { ParseTypeBlock(); continue; }
            if (keyword.IsKeyword("FUNCTION_BLOCK")) { ParseFunctionBlock(); continue; }
            if (keyword.IsKeyword("PROGRAM")) { ParseProgram(); continue; }
            if (keyword.IsKeyword("INTERFACE")) { ParseInterface(); continue; }
            if (keyword.IsKeyword("VAR_GLOBAL")) { ParseGlobalSection(); continue; }

            string? skippedBlock = new[] { "FUNCTION", "METHOD", "PROPERTY", "ACTION" }
                .FirstOrDefault(keyword.IsKeyword);
            if (skippedBlock is not null)
            {
                SkipPast("END_" + skippedBlock.ToUpperInvariant());
                continue;
            }

            throw Error($"Unexpected {keyword}; expected TYPE, FUNCTION_BLOCK, PROGRAM, INTERFACE or VAR_GLOBAL.");
        }
    }

    private void ParseTypeBlock()
    {
        Expect("TYPE");
        while (!Current.IsKeyword("END_TYPE"))
        {
            SourceLocation location = Location();
            string name = ExpectIdentifier();
            string? baseName = null;
            if (Accept("EXTENDS"))
                baseName = ExpectQualifiedName();

            ExpectSymbol(":");
            _result.Types.Add(ParseTypeBody(name, baseName, location));
            AcceptSymbol(";");
        }

        Expect("END_TYPE");
    }

    private TypeDeclaration ParseTypeBody(string name, string? baseName, SourceLocation location)
    {
        if (Accept("STRUCT"))
        {
            var members = new List<VariableDeclaration>();
            while (!Current.IsKeyword("END_STRUCT"))
                members.AddRange(ParseVariableLine());
            Expect("END_STRUCT");
            return new StructDeclaration(name, baseName, IsFunctionBlock: false, members, location);
        }

        if (Current.IsKeyword("UNION"))
            throw Error($"UNION '{name}' is not supported.");

        if (Current.IsSymbol("("))
            return ParseEnum(name, location);

        TypeSpec target = ParseTypeSpec();
        if (AcceptSymbol(":="))
            ParseExpression();
        return new AliasDeclaration(name, target, location);
    }

    private EnumDeclaration ParseEnum(string name, SourceLocation location)
    {
        ExpectSymbol("(");
        var members = new List<EnumMemberDeclaration>();
        do
        {
            string memberName = ExpectIdentifier();
            Expression? value = AcceptSymbol(":=") ? ParseExpression() : null;
            members.Add(new EnumMemberDeclaration(memberName, value));
        }
        while (AcceptSymbol(","));
        ExpectSymbol(")");

        string? baseTypeName = Current.Kind == TokenKind.Identifier ? ExpectIdentifier() : null;
        if (AcceptSymbol(":="))
            ParseExpression();
        return new EnumDeclaration(name, members, baseTypeName, location);
    }

    private void ParseFunctionBlock()
    {
        SourceLocation location = Location();
        Expect("FUNCTION_BLOCK");
        SkipModifiers();
        string name = ExpectIdentifier();
        string? baseName = Accept("EXTENDS") ? ExpectQualifiedName() : null;
        if (Accept("IMPLEMENTS"))
        {
            do ExpectQualifiedName();
            while (AcceptSymbol(","));
        }

        var members = new List<VariableDeclaration>();
        while (TryParseInstanceSection(members))
        {
        }

        _result.Types.Add(new StructDeclaration(name, baseName, IsFunctionBlock: true, members, location));
        SkipPast("END_FUNCTION_BLOCK");
    }

    private void ParseProgram()
    {
        Expect("PROGRAM");
        string name = ExpectIdentifier();
        var members = new List<VariableDeclaration>();
        while (TryParseInstanceSection(members))
        {
        }

        foreach (VariableDeclaration member in members)
            _result.Globals.Add(new GlobalVariableDeclaration(name, member, IsConstant: false));
        SkipPast("END_PROGRAM");
    }

    private void ParseInterface()
    {
        SourceLocation location = Location();
        Expect("INTERFACE");
        string name = ExpectIdentifier();
        _result.Types.Add(new InterfaceDeclaration(name, location));
        SkipPast("END_INTERFACE");
    }

    private void ParseGlobalSection()
    {
        Expect("VAR_GLOBAL");
        bool isConstant = ParseSectionModifiers();
        foreach (VariableDeclaration variable in ParseSectionBody())
            _result.Globals.Add(new GlobalVariableDeclaration(_globalScope, variable, isConstant));
    }

    /// <summary>Parses one VAR section of an FB or PROGRAM; returns false when no section follows.</summary>
    private bool TryParseInstanceSection(List<VariableDeclaration> members)
    {
        Token keyword = Current;
        bool isInOut = keyword.IsKeyword("VAR_IN_OUT");
        bool ownsInstanceMemory = keyword.IsKeyword("VAR") || keyword.IsKeyword("VAR_INPUT")
            || keyword.IsKeyword("VAR_OUTPUT") || isInOut;
        bool isOtherSection = keyword.IsKeyword("VAR_TEMP") || keyword.IsKeyword("VAR_STAT")
            || keyword.IsKeyword("VAR_INST") || keyword.IsKeyword("VAR_EXTERNAL");
        if (!ownsInstanceMemory && !isOtherSection)
            return false;

        _position++;
        bool isConstant = ParseSectionModifiers();
        List<VariableDeclaration> variables = ParseSectionBody();
        if (!ownsInstanceMemory || isConstant)
            return true;

        // VAR_IN_OUT is passed by reference, so the instance only stores a pointer.
        members.AddRange(isInOut
            ? variables.Select(variable => variable with { Type = new PointerTypeSpec(variable.Type, IsReference: true), InitialValue = null })
            : variables);
        return true;
    }

    private bool ParseSectionModifiers()
    {
        bool isConstant = false;
        while (true)
        {
            if (Accept("CONSTANT")) { isConstant = true; continue; }
            if (Accept("PERSISTENT") || Accept("RETAIN") || Accept("NON_RETAIN")) continue;
            return isConstant;
        }
    }

    private List<VariableDeclaration> ParseSectionBody()
    {
        var variables = new List<VariableDeclaration>();
        while (!Current.IsKeyword("END_VAR"))
            variables.AddRange(ParseVariableLine());
        Expect("END_VAR");
        return variables;
    }

    private List<VariableDeclaration> ParseVariableLine()
    {
        SourceLocation location = Location();
        var names = new List<string> { ExpectIdentifier() };
        while (AcceptSymbol(","))
            names.Add(ExpectIdentifier());

        if (Accept("AT"))
        {
            while (!Current.IsSymbol(":") && Current.Kind != TokenKind.End)
                _position++;
        }

        ExpectSymbol(":");
        TypeSpec type = ParseTypeSpec();
        Expression? initialValue = AcceptSymbol(":=") ? ParseExpression() : null;
        ExpectSymbol(";");
        return names.Select(name => new VariableDeclaration(name, type, initialValue, location)).ToList();
    }

    private TypeSpec ParseTypeSpec()
    {
        if (Accept("ARRAY"))
        {
            ExpectSymbol("[");
            var dimensions = new List<ArrayBoundsSpec>();
            do
            {
                if (Current.IsSymbol("*"))
                    throw Error("Variable-length arrays ARRAY[*] are not supported.");
                Expression lower = ParseExpression();
                ExpectSymbol("..");
                dimensions.Add(new ArrayBoundsSpec(lower, ParseExpression()));
            }
            while (AcceptSymbol(","));
            ExpectSymbol("]");
            Expect("OF");
            return new ArrayTypeSpec(dimensions, ParseTypeSpec());
        }

        if (Current.IsKeyword("STRING") || Current.IsKeyword("WSTRING"))
        {
            bool isWide = Current.IsKeyword("WSTRING");
            _position++;
            Expression? length = null;
            if (AcceptSymbol("("))
            {
                length = ParseExpression();
                ExpectSymbol(")");
            }
            else if (AcceptSymbol("["))
            {
                length = ParseExpression();
                ExpectSymbol("]");
            }

            return new StringTypeSpec(isWide, length);
        }

        if (Accept("POINTER"))
        {
            Expect("TO");
            return new PointerTypeSpec(ParseTypeSpec(), IsReference: false);
        }

        if (Accept("REFERENCE"))
        {
            Expect("TO");
            return new PointerTypeSpec(ParseTypeSpec(), IsReference: true);
        }

        return new NamedTypeSpec(ExpectQualifiedName());
    }

    private Expression ParseExpression() => ParseAdditive();

    private Expression ParseAdditive()
    {
        Expression left = ParseMultiplicative();
        while (Current.IsSymbol("+") || Current.IsSymbol("-"))
        {
            string op = Current.Text;
            _position++;
            left = new BinaryExpression(op, left, ParseMultiplicative());
        }

        return left;
    }

    private Expression ParseMultiplicative()
    {
        Expression left = ParseUnary();
        while (Current.IsSymbol("*") || Current.IsSymbol("/") || Current.IsKeyword("MOD"))
        {
            string op = Current.Text.ToUpperInvariant();
            _position++;
            left = new BinaryExpression(op, left, ParseUnary());
        }

        return left;
    }

    private Expression ParseUnary()
    {
        if (Current.IsSymbol("-") || Current.IsSymbol("+"))
        {
            string op = Current.Text;
            _position++;
            return new UnaryExpression(op, ParseUnary());
        }

        return ParsePrimary();
    }

    private Expression ParsePrimary()
    {
        Token token = Current;
        switch (token.Kind)
        {
            case TokenKind.Number:
                _position++;
                return new LiteralExpression(ParseNumber(token.Text));
            case TokenKind.String:
                _position++;
                return new LiteralExpression(token.Text);
            case TokenKind.TypedLiteral:
                _position++;
                return ParseTypedLiteral(token);
        }

        if (token.IsKeyword("TRUE") || token.IsKeyword("FALSE"))
        {
            _position++;
            return new LiteralExpression(token.IsKeyword("TRUE"));
        }

        if (token.Kind == TokenKind.Identifier)
            return new NameExpression(ExpectQualifiedName());

        if (token.IsSymbol("(") && Peek().Kind == TokenKind.Identifier && Peek(2).IsSymbol(":="))
            return ParseStructInit();

        if (AcceptSymbol("("))
        {
            Expression inner = ParseExpression();
            ExpectSymbol(")");
            return inner;
        }

        if (token.IsSymbol("["))
            return ParseArrayInit();

        throw Error($"Unexpected {token} in expression.");
    }

    private StructInitExpression ParseStructInit()
    {
        ExpectSymbol("(");
        var members = new List<MemberInit>();
        do
        {
            string name = ExpectIdentifier();
            ExpectSymbol(":=");
            members.Add(new MemberInit(name, ParseExpression()));
        }
        while (AcceptSymbol(","));
        ExpectSymbol(")");
        return new StructInitExpression(members);
    }

    private ArrayInitExpression ParseArrayInit()
    {
        ExpectSymbol("[");
        var items = new List<ArrayInitItem>();
        do
        {
            Expression value = ParseExpression();
            if (AcceptSymbol("("))
            {
                Expression repeated = ParseExpression();
                ExpectSymbol(")");
                items.Add(new ArrayInitItem(value, repeated));
                continue;
            }

            items.Add(new ArrayInitItem(null, value));
        }
        while (AcceptSymbol(","));
        ExpectSymbol("]");
        return new ArrayInitExpression(items);
    }

    private Expression ParseTypedLiteral(Token token)
    {
        int separator = token.Text.IndexOf('#');
        string prefix = token.Text[..separator].ToUpperInvariant();
        string body = token.Text[(separator + 1)..].Replace("_", string.Empty);

        if (char.IsDigit(prefix[0]))
            return new LiteralExpression(Convert.ToInt64(body, int.Parse(prefix, CultureInfo.InvariantCulture)));

        if (prefix is "T" or "TIME" or "LT" or "LTIME")
            return new LiteralExpression(ParseDuration(body, token));

        if (prefix is "DATE" or "D" or "TOD" or "TIME_OF_DAY" or "DT" or "DATE_AND_TIME")
            throw Error($"Literal {token} is not supported; use a plain number instead.");

        if (body.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || body.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            return new LiteralExpression(body.Equals("TRUE", StringComparison.OrdinalIgnoreCase));

        if (body.Length > 0 && (char.IsDigit(body[0]) || body[0] is '-' or '+'))
            return new LiteralExpression(ParseNumber(body));

        // E_State#Idle is an enum value.
        return new NameExpression(token.Text[..separator] + "." + token.Text[(separator + 1)..]);
    }

    private TimeSpan ParseDuration(string body, Token token)
    {
        MatchCollection parts = DurationPart().Matches(body);
        if (parts.Count == 0 || parts.Sum(part => part.Length) != body.Length)
            throw Error($"Invalid duration literal {token}.");

        double milliseconds = 0;
        foreach (Match part in parts)
        {
            double amount = double.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture);
            milliseconds += part.Groups[2].Value.ToLowerInvariant() switch
            {
                "d" => amount * 86_400_000,
                "h" => amount * 3_600_000,
                "m" => amount * 60_000,
                "s" => amount * 1_000,
                "ms" => amount,
                "us" => amount / 1_000,
                _ => amount / 1_000_000,
            };
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static object ParseNumber(string text)
    {
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
            return integer;
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private void SkipModifiers()
    {
        while (Accept("ABSTRACT") || Accept("FINAL") || Accept("PUBLIC") || Accept("INTERNAL"))
        {
        }
    }

    /// <summary>Skips implementation code up to and including the terminator, or to end of input.</summary>
    private void SkipPast(string terminator)
    {
        while (Current.Kind != TokenKind.End && !Current.IsKeyword(terminator))
            _position++;
        if (Current.Kind != TokenKind.End)
            _position++;
    }

    private string ExpectQualifiedName()
    {
        string name = ExpectIdentifier();
        while (Current.IsSymbol(".") && Peek().Kind == TokenKind.Identifier)
        {
            _position++;
            name += "." + ExpectIdentifier();
        }

        return name;
    }

    private string ExpectIdentifier()
    {
        if (Current.Kind != TokenKind.Identifier)
            throw Error($"Expected an identifier but found {Current}.");
        return _tokens[_position++].Text;
    }

    private void Expect(string keyword)
    {
        if (!Accept(keyword))
            throw Error($"Expected '{keyword}' but found {Current}.");
    }

    private bool Accept(string keyword)
    {
        if (!Current.IsKeyword(keyword))
            return false;
        _position++;
        return true;
    }

    private void ExpectSymbol(string symbol)
    {
        if (!AcceptSymbol(symbol))
            throw Error($"Expected '{symbol}' but found {Current}.");
    }

    private bool AcceptSymbol(string symbol)
    {
        if (!Current.IsSymbol(symbol))
            return false;
        _position++;
        return true;
    }

    private SourceLocation Location() => new(_fileName, Current.Line);

    private PlcParseException Error(string message) => new($"{_fileName}:{Current.Line}: {message}");

    [GeneratedRegex(@"(\d+(?:\.\d+)?)(ms|us|ns|d|h|m|s)", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPart();
}
