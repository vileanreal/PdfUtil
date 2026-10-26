using System.Buffers;
using System.Globalization;
using System.Text;

namespace PdfUtil.Internal.Parsing;

internal enum TokenType
{
    EndOfFile,
    Integer,
    Real,
    Name,
    LiteralString,
    HexString,
    ArrayStart,
    ArrayEnd,
    DictStart,
    DictEnd,
    Keyword,
}

internal readonly struct Token
{
    public Token(TokenType type, long position, long integer = 0, double real = 0, string? text = null, byte[]? bytes = null)
    {
        Type = type;
        Position = position;
        Integer = integer;
        Real = real;
        Text = text;
        Bytes = bytes;
    }

    public TokenType Type { get; }

    public long Position { get; }

    public long Integer { get; }

    public double Real { get; }

    /// <summary>Keyword text, name value (Latin-1, decoded), or the raw text of a real number.</summary>
    public string? Text { get; }

    /// <summary>Bytes of a literal or hex string.</summary>
    public byte[]? Bytes { get; }

    public bool IsKeyword(string keyword) => Type == TokenType.Keyword && string.Equals(Text, keyword, StringComparison.Ordinal);

    public override string ToString() => $"{Type} {Text ?? Integer.ToString(CultureInfo.InvariantCulture)} @{Position}";
}

/// <summary>Tokenizer for PDF syntax (ISO 32000-1 §7.2, §7.3).</summary>
internal sealed class PdfLexer : IDisposable
{
    private readonly PdfSource _source;
    private byte[] _scratch;
    private int _scratchLength;

    public PdfLexer(PdfSource source)
    {
        _source = source;
        _scratch = ArrayPool<byte>.Shared.Rent(256);
    }

    public PdfSource Source => _source;

    public long Position
    {
        get => _source.Position;
        set => _source.Position = value;
    }

    public static bool IsWhitespace(int b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    public static bool IsDelimiter(int b) => b is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

    public static bool IsRegular(int b) => b >= 0 && !IsWhitespace(b) && !IsDelimiter(b);

    public void SkipWhitespaceAndComments()
    {
        while (true)
        {
            var b = _source.Peek();
            if (b < 0)
            {
                return;
            }

            if (IsWhitespace(b))
            {
                _source.Position++;
                continue;
            }

            if (b == '%')
            {
                while (true)
                {
                    b = _source.Read();
                    if (b is < 0 or '\r' or '\n')
                    {
                        break;
                    }
                }

                continue;
            }

            return;
        }
    }

    public Token Next()
    {
        SkipWhitespaceAndComments();
        var start = _source.Position;
        var b = _source.Read();
        switch (b)
        {
            case < 0:
                return new Token(TokenType.EndOfFile, start);
            case '[':
                return new Token(TokenType.ArrayStart, start);
            case ']':
                return new Token(TokenType.ArrayEnd, start);
            case '{':
            case '}':
                // PostScript calculator braces only appear inside function streams; treat as keywords.
                return new Token(TokenType.Keyword, start, text: b == '{' ? "{" : "}");
            case '/':
                return ReadName(start);
            case '(':
                return ReadLiteralString(start);
            case '<':
                if (_source.Peek() == '<')
                {
                    _source.Position++;
                    return new Token(TokenType.DictStart, start);
                }

                return ReadHexString(start);
            case '>':
                if (_source.Peek() == '>')
                {
                    _source.Position++;
                    return new Token(TokenType.DictEnd, start);
                }

                // Stray '>' - report as keyword so the parser can skip it.
                return new Token(TokenType.Keyword, start, text: ">");
            case ')':
                return new Token(TokenType.Keyword, start, text: ")");
        }

        if (b is '+' or '-' or '.' || (b >= '0' && b <= '9'))
        {
            return ReadNumber(start, b);
        }

        _scratchLength = 0;
        Append((byte)b);
        while (IsRegular(_source.Peek()))
        {
            Append((byte)_source.Read());
        }

        return new Token(TokenType.Keyword, start, text: Encoding.Latin1.GetString(_scratch, 0, _scratchLength));
    }

    /// <summary>Reads a non-negative integer token at the current position, or returns null and restores the position.</summary>
    public long? TryReadInteger()
    {
        var saved = _source.Position;
        var t = Next();
        if (t.Type == TokenType.Integer)
        {
            return t.Integer;
        }

        _source.Position = saved;
        return null;
    }

    private Token ReadNumber(long start, int first)
    {
        _scratchLength = 0;
        Append((byte)first);
        while (true)
        {
            var c = _source.Peek();
            if ((c >= '0' && c <= '9') || c is '.' or '-' or '+' or 'e' or 'E')
            {
                // Exponents are not PDF syntax but appear in the wild; '-' or '+' mid-number are tolerated.
                if ((c is 'e' or 'E') && !NextIsExponent())
                {
                    break;
                }

                Append((byte)_source.Read());
                continue;
            }

            break;
        }

        // A number token immediately followed by regular characters (e.g. "12abc") is a keyword.
        if (IsRegular(_source.Peek()))
        {
            while (IsRegular(_source.Peek()))
            {
                Append((byte)_source.Read());
            }

            return new Token(TokenType.Keyword, start, text: Encoding.Latin1.GetString(_scratch, 0, _scratchLength));
        }

        var text = Encoding.ASCII.GetString(_scratch, 0, _scratchLength);
        var isInteger = true;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '.' or 'e' or 'E' || ((c is '-' or '+') && i > 0))
            {
                isInteger = false;
                break;
            }
        }

        if (isInteger && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
        {
            return new Token(TokenType.Integer, start, integer: l, text: text);
        }

        return new Token(TokenType.Real, start, real: ParseLenientReal(text), text: text);
    }

    private bool NextIsExponent()
    {
        var c = _source.PeekAt(_source.Position + 1);
        return (c >= '0' && c <= '9') || c is '-' or '+';
    }

    internal static double ParseLenientReal(string text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return d;
        }

        // Handle forms such as "--5", "5-", "0.0.1", "-.002", "4.-2".
        var sign = 1.0;
        var sb = new StringBuilder();
        var seenDot = false;
        var seenDigit = false;
        foreach (var c in text)
        {
            if (c == '-' && !seenDigit && !seenDot)
            {
                sign = -1;
            }
            else if (c >= '0' && c <= '9')
            {
                sb.Append(c);
                seenDigit = true;
            }
            else if (c == '.' && !seenDot)
            {
                sb.Append(c);
                seenDot = true;
            }
        }

        if (sb.Length == 0 || sb.ToString() == ".")
        {
            return 0;
        }

        return double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? sign * d : 0;
    }

    private Token ReadName(long start)
    {
        _scratchLength = 0;
        while (true)
        {
            var c = _source.Peek();
            if (!IsRegular(c))
            {
                break;
            }

            _source.Position++;
            if (c == '#')
            {
                var h1 = HexValue(_source.Peek());
                var h2 = HexValue(_source.PeekAt(_source.Position + 1));
                if (h1 >= 0 && h2 >= 0)
                {
                    _source.Position += 2;
                    Append((byte)((h1 << 4) | h2));
                    continue;
                }

                // Pre-PDF-1.2 names: '#' is an ordinary character.
            }

            Append((byte)c);
        }

        return new Token(TokenType.Name, start, text: Encoding.Latin1.GetString(_scratch, 0, _scratchLength));
    }

    private Token ReadLiteralString(long start)
    {
        _scratchLength = 0;
        var depth = 1;
        while (true)
        {
            var c = _source.Read();
            if (c < 0)
            {
                break; // unterminated string: keep what we have
            }

            if (c == '(')
            {
                depth++;
                Append((byte)c);
                continue;
            }

            if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }

                Append((byte)c);
                continue;
            }

            if (c == '\r')
            {
                // An unescaped end-of-line (CR, LF or CRLF) is read as a single LF (§7.3.4.2).
                if (_source.Peek() == '\n')
                {
                    _source.Position++;
                }

                Append((byte)'\n');
                continue;
            }

            if (c != '\\')
            {
                Append((byte)c);
                continue;
            }

            var e = _source.Read();
            switch (e)
            {
                case < 0:
                    break;
                case 'n':
                    Append((byte)'\n');
                    break;
                case 'r':
                    Append((byte)'\r');
                    break;
                case 't':
                    Append((byte)'\t');
                    break;
                case 'b':
                    Append((byte)'\b');
                    break;
                case 'f':
                    Append((byte)'\f');
                    break;
                case '(':
                case ')':
                case '\\':
                    Append((byte)e);
                    break;
                case '\r':
                    // Line continuation: backslash + EOL is ignored.
                    if (_source.Peek() == '\n')
                    {
                        _source.Position++;
                    }

                    break;
                case '\n':
                    break;
                default:
                    if (e >= '0' && e <= '7')
                    {
                        var value = e - '0';
                        for (var i = 0; i < 2; i++)
                        {
                            var d = _source.Peek();
                            if (d < '0' || d > '7')
                            {
                                break;
                            }

                            _source.Position++;
                            value = (value << 3) + (d - '0');
                        }

                        Append((byte)(value & 0xFF));
                    }
                    else
                    {
                        // Unknown escape: the backslash is ignored.
                        Append((byte)e);
                    }

                    break;
            }
        }

        return new Token(TokenType.LiteralString, start, bytes: _scratch.AsSpan(0, _scratchLength).ToArray());
    }

    private Token ReadHexString(long start)
    {
        _scratchLength = 0;
        var high = -1;
        while (true)
        {
            var c = _source.Read();
            if (c is < 0 or '>')
            {
                break;
            }

            var v = HexValue(c);
            if (v < 0)
            {
                continue; // whitespace and junk are ignored
            }

            if (high < 0)
            {
                high = v;
            }
            else
            {
                Append((byte)((high << 4) | v));
                high = -1;
            }
        }

        if (high >= 0)
        {
            // Odd number of digits: the final digit is followed by an implied 0 (§7.3.4.3).
            Append((byte)(high << 4));
        }

        return new Token(TokenType.HexString, start, bytes: _scratch.AsSpan(0, _scratchLength).ToArray());
    }

    private static int HexValue(int c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private void Append(byte b)
    {
        if (_scratchLength == _scratch.Length)
        {
            var bigger = ArrayPool<byte>.Shared.Rent(_scratch.Length * 2);
            _scratch.AsSpan(0, _scratchLength).CopyTo(bigger);
            ArrayPool<byte>.Shared.Return(_scratch);
            _scratch = bigger;
        }

        _scratch[_scratchLength++] = b;
    }

    public void Dispose()
    {
        if (_scratch.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_scratch);
            _scratch = Array.Empty<byte>();
        }
    }
}
