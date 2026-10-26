using PdfUtil.Internal.Objects;

namespace PdfUtil.Internal.Parsing;

/// <summary>Builds <see cref="PdfObject"/> values from tokens (ISO 32000-1 §7.3).</summary>
internal sealed class PdfParser
{
    private readonly PdfLexer _lexer;
    private readonly int _maxDepth;

    public PdfParser(PdfLexer lexer, int maxDepth)
    {
        _lexer = lexer;
        _maxDepth = maxDepth;
    }

    public PdfLexer Lexer => _lexer;

    /// <summary>Parses one direct object. Stream bodies are handled by the caller.</summary>
    public PdfObject ParseObject() => ParseObject(_lexer.Next(), 0);

    /// <summary>Parses one direct object whose first token has already been read.</summary>
    public PdfObject ParseObject(Token first) => ParseObject(first, 0);

    /// <summary>
    /// Parses "<c>n g obj</c>" at the current position. Returns false (and restores the position) if the header
    /// is not there.
    /// </summary>
    public bool TryReadObjectHeader(out ObjectId id)
    {
        var saved = _lexer.Position;
        id = default;
        var t1 = _lexer.Next();
        if (t1.Type != TokenType.Integer || t1.Integer < 0 || t1.Integer > int.MaxValue)
        {
            _lexer.Position = saved;
            return false;
        }

        var t2 = _lexer.Next();
        if (t2.Type != TokenType.Integer || t2.Integer < 0 || t2.Integer > 65535)
        {
            _lexer.Position = saved;
            return false;
        }

        var t3 = _lexer.Next();
        if (!t3.IsKeyword("obj"))
        {
            _lexer.Position = saved;
            return false;
        }

        id = new ObjectId((int)t1.Integer, (int)t2.Integer);
        return true;
    }

    private PdfObject ParseObject(Token token, int depth)
    {
        if (depth > _maxDepth)
        {
            throw new PdfLimitExceededException("Maximum object nesting depth exceeded.");
        }

        switch (token.Type)
        {
            case TokenType.Integer:
                return ParseIntegerOrReference(token);
            case TokenType.Real:
                return new PdfReal(token.Real, token.Text);
            case TokenType.Name:
                return PdfName.Get(token.Text!);
            case TokenType.LiteralString:
                return new PdfString(token.Bytes!, isHex: false);
            case TokenType.HexString:
                return new PdfString(token.Bytes!, isHex: true);
            case TokenType.ArrayStart:
                return ParseArray(depth);
            case TokenType.DictStart:
                return ParseDictionary(depth);
            case TokenType.Keyword:
                switch (token.Text)
                {
                    case "true":
                        return PdfBoolean.True;
                    case "false":
                        return PdfBoolean.False;
                    case "null":
                        return PdfNull.Instance;
                }

                throw new PdfCorruptException($"Unexpected keyword at offset {token.Position}.");
            case TokenType.EndOfFile:
                throw new PdfCorruptException("Unexpected end of file while parsing an object.");
            default:
                throw new PdfCorruptException($"Unexpected token at offset {token.Position}.");
        }
    }

    private PdfObject ParseIntegerOrReference(Token first)
    {
        if (first.Integer < 0 || first.Integer > int.MaxValue)
        {
            return new PdfInteger(first.Integer);
        }

        var saved = _lexer.Position;
        var second = _lexer.Next();
        if (second.Type == TokenType.Integer && second.Integer is >= 0 and <= 65535)
        {
            var third = _lexer.Next();
            if (third.IsKeyword("R"))
            {
                return new PdfReference((int)first.Integer, (int)second.Integer);
            }
        }

        _lexer.Position = saved;
        return new PdfInteger(first.Integer);
    }

    private PdfArray ParseArray(int depth)
    {
        var array = new PdfArray();
        while (true)
        {
            var saved = _lexer.Position;
            var t = _lexer.Next();
            switch (t.Type)
            {
                case TokenType.ArrayEnd:
                    return array;
                case TokenType.EndOfFile:
                    throw new PdfCorruptException("Unterminated array.");
                case TokenType.DictEnd:
                    // Missing ']' - stop here and let the dictionary see its end token.
                    _lexer.Position = saved;
                    return array;
                case TokenType.Keyword when t.Text is "endobj" or "stream" or "endstream" or "obj":
                    _lexer.Position = saved;
                    return array;
                case TokenType.Keyword when t.Text is not ("true" or "false" or "null"):
                    continue; // junk inside an array is skipped
                default:
                    array.Add(ParseObject(t, depth + 1));
                    break;
            }
        }
    }

    private PdfDictionary ParseDictionary(int depth)
    {
        var dict = new PdfDictionary();
        while (true)
        {
            var saved = _lexer.Position;
            var t = _lexer.Next();
            if (t.Type == TokenType.DictEnd)
            {
                return dict;
            }

            if (t.Type == TokenType.EndOfFile)
            {
                throw new PdfCorruptException("Unterminated dictionary.");
            }

            if (t.Type == TokenType.Keyword && t.Text is "endobj" or "stream" or "endstream" or "obj")
            {
                _lexer.Position = saved; // missing '>>'
                return dict;
            }

            if (t.Type != TokenType.Name)
            {
                continue; // skip junk keys
            }

            var key = PdfName.Get(t.Text!);
            var valueStart = _lexer.Position;
            var v = _lexer.Next();
            if (v.Type == TokenType.DictEnd)
            {
                dict[key] = PdfNull.Instance;
                return dict;
            }

            if (v.Type == TokenType.Name || v.Type != TokenType.Keyword || v.Text is "true" or "false" or "null")
            {
                dict[key] = ParseObject(v, depth + 1);
            }
            else
            {
                // A keyword where a value should be (e.g. "endobj"): the value is missing.
                _lexer.Position = valueStart;
                dict[key] = PdfNull.Instance;
            }
        }
    }
}
