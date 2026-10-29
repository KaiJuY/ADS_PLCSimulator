using System.Text;

namespace AdsPlcSimulator.Core.Plc.Parsing;

public enum TokenKind
{
    Identifier,
    Number,
    TypedLiteral,
    String,
    Symbol,
    End,
}

public readonly record struct Token(TokenKind Kind, string Text, int Line)
{
    public bool IsKeyword(string keyword) =>
        Kind == TokenKind.Identifier && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);

    public bool IsSymbol(string symbol) => Kind == TokenKind.Symbol && Text == symbol;

    public override string ToString() => Kind == TokenKind.End ? "end of file" : $"'{Text}'";
}

/// <summary>
/// Tokenizer for IEC 61131-3 declaration text. Comments and pragmas are dropped because
/// the simulator only needs the data layout, never attributes or documentation.
/// </summary>
public static class Lexer
{
    private static readonly string[] TwoCharSymbols = [":=", "..", "=>", "<=", ">=", "<>", "**"];

    public static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        int position = 0;
        int line = 1;

        while (position < text.Length)
        {
            char current = text[position];

            if (current == '\n')
            {
                line++;
                position++;
                continue;
            }

            if (char.IsWhiteSpace(current))
            {
                position++;
                continue;
            }

            if (StartsWith(text, position, "//"))
            {
                while (position < text.Length && text[position] != '\n')
                    position++;
                continue;
            }

            if (StartsWith(text, position, "(*"))
            {
                position = SkipBlockComment(text, position, "(*", "*)", ref line);
                continue;
            }

            if (StartsWith(text, position, "/*"))
            {
                position = SkipBlockComment(text, position, "/*", "*/", ref line);
                continue;
            }

            if (current == '{')
            {
                position = SkipUntil(text, position, '}', ref line);
                continue;
            }

            if (current is '\'' or '"')
            {
                tokens.Add(new Token(TokenKind.String, ReadString(text, ref position, current, line), line));
                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                string word = ReadWhile(text, ref position, IsIdentifierChar);
                if (position < text.Length && text[position] == '#')
                {
                    position++;
                    string literal = ReadWhile(text, ref position, IsLiteralBodyChar);
                    tokens.Add(new Token(TokenKind.TypedLiteral, word + "#" + literal, line));
                    continue;
                }

                tokens.Add(new Token(TokenKind.Identifier, word, line));
                continue;
            }

            if (char.IsDigit(current))
            {
                tokens.Add(ReadNumber(text, ref position, line));
                continue;
            }

            string? symbol = TwoCharSymbols.FirstOrDefault(candidate => StartsWith(text, position, candidate));
            if (symbol is not null)
            {
                tokens.Add(new Token(TokenKind.Symbol, symbol, line));
                position += symbol.Length;
                continue;
            }

            tokens.Add(new Token(TokenKind.Symbol, current.ToString(), line));
            position++;
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, line));
        return tokens;
    }

    private static Token ReadNumber(string text, ref int position, int line)
    {
        string digits = ReadWhile(text, ref position, character => char.IsDigit(character) || character == '_');

        // Radix literal such as 16#FF or 2#1010.
        if (position < text.Length && text[position] == '#')
        {
            position++;
            string body = ReadWhile(text, ref position, IsLiteralBodyChar);
            return new Token(TokenKind.TypedLiteral, digits + "#" + body, line);
        }

        var number = new StringBuilder(digits);
        bool hasFraction = position + 1 < text.Length && text[position] == '.' && char.IsDigit(text[position + 1]);
        if (hasFraction)
        {
            position++;
            number.Append('.').Append(ReadWhile(text, ref position, character => char.IsDigit(character) || character == '_'));
        }

        bool hasExponent = position < text.Length && (text[position] == 'e' || text[position] == 'E');
        if (hasExponent)
        {
            number.Append('e');
            position++;
            if (position < text.Length && (text[position] == '+' || text[position] == '-'))
                number.Append(text[position++]);
            number.Append(ReadWhile(text, ref position, char.IsDigit));
        }

        return new Token(TokenKind.Number, number.ToString().Replace("_", string.Empty), line);
    }

    private static string ReadString(string text, ref int position, char quote, int line)
    {
        var value = new StringBuilder();
        position++;
        while (position < text.Length && text[position] != quote)
        {
            char current = text[position];
            if (current == '$' && position + 1 < text.Length)
            {
                value.Append(DecodeEscape(text[position + 1]));
                position += 2;
                continue;
            }

            value.Append(current);
            position++;
        }

        if (position >= text.Length)
            throw new PlcParseException($"Unterminated string literal starting on line {line}.");

        position++;
        return value.ToString();
    }

    private static string DecodeEscape(char escaped) => char.ToUpperInvariant(escaped) switch
    {
        'N' or 'L' => "\n",
        'R' => "\r",
        'T' => "\t",
        _ => escaped.ToString(),
    };

    private static int SkipBlockComment(string text, int position, string open, string close, ref int line)
    {
        int depth = 0;
        while (position < text.Length)
        {
            if (StartsWith(text, position, open))
            {
                depth++;
                position += open.Length;
                continue;
            }

            if (StartsWith(text, position, close))
            {
                depth--;
                position += close.Length;
                if (depth == 0)
                    return position;
                continue;
            }

            if (text[position] == '\n')
                line++;
            position++;
        }

        return position;
    }

    private static int SkipUntil(string text, int position, char terminator, ref int line)
    {
        while (position < text.Length && text[position] != terminator)
        {
            if (text[position] == '\n')
                line++;
            position++;
        }

        return Math.Min(position + 1, text.Length);
    }

    private static string ReadWhile(string text, ref int position, Func<char, bool> predicate)
    {
        int start = position;
        while (position < text.Length && predicate(text[position]))
            position++;
        return text[start..position];
    }

    private static bool StartsWith(string text, int position, string value) =>
        string.CompareOrdinal(text, position, value, 0, value.Length) == 0;

    private static bool IsIdentifierChar(char character) => char.IsLetterOrDigit(character) || character == '_';

    // Covers T#1h2m3s4ms, INT#-5, 16#FF_FF, LREAL#1.5e3 and E_State#Idle.
    private static bool IsLiteralBodyChar(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '.' or '-' or '+' or ':';
}
