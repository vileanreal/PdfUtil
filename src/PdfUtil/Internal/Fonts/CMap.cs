using System.Text;
using PdfUtil.Internal.Parsing;

namespace PdfUtil.Internal.Fonts;

/// <summary>
/// A CMap (Adobe technical note 5014, ISO 32000-1 §9.10.3): code space ranges, which split a string into codes of
/// one to four bytes, and the mappings of codes to Unicode (<c>bfchar</c>, <c>bfrange</c>) or to CIDs (<c>cidchar</c>,
/// <c>cidrange</c>). Also models the predefined Identity and Unicode-based CMaps.
/// </summary>
internal sealed class CMap
{
    private const int MaxEntries = 1 << 20;

    private readonly List<(int Length, uint Low, uint High)> _codespaces = new();
    private readonly Dictionary<(int Length, uint Code), string> _unicode = new();
    private readonly List<(int Length, uint Low, uint High, string? Start, string[]? List)> _unicodeRanges = new();
    private readonly List<(int Length, uint Low, uint High, uint Cid)> _cidRanges = new();
    private int _entries;

    /// <summary>The kind of a predefined CMap whose codes are Unicode (for example UniGB-UCS2-H), or null.</summary>
    public UnicodeEncodingForm? UnicodeForm { get; private set; }

    /// <summary>Whether this is Identity-H or Identity-V (code = CID).</summary>
    public bool IsIdentity { get; private set; }

    /// <summary>Whether the CMap has any mapping to Unicode.</summary>
    public bool HasUnicode => _unicode.Count > 0 || _unicodeRanges.Count > 0;

    public enum UnicodeEncodingForm
    {
        Ucs2,
        Utf16,
        Utf8,
        Utf32,
    }

    /// <summary>A predefined CMap by name, or null when its mappings are not known (only code lengths are then guessed).</summary>
    public static CMap? Predefined(string name)
    {
        var cmap = new CMap();
        if (name is "Identity-H" or "Identity-V")
        {
            cmap.IsIdentity = true;
            cmap._codespaces.Add((2, 0, 0xFFFF));
            return cmap;
        }

        if (!name.StartsWith("Uni", StringComparison.Ordinal))
        {
            return null;
        }

        if (name.Contains("UCS2", StringComparison.Ordinal))
        {
            cmap.UnicodeForm = UnicodeEncodingForm.Ucs2;
            cmap._codespaces.Add((2, 0, 0xFFFF));
        }
        else if (name.Contains("UTF16", StringComparison.Ordinal))
        {
            cmap.UnicodeForm = UnicodeEncodingForm.Utf16;
            cmap._codespaces.Add((2, 0, 0xD7FF));
            cmap._codespaces.Add((4, 0xD800DC00, 0xDBFFDFFF));
            cmap._codespaces.Add((2, 0xE000, 0xFFFF));
        }
        else if (name.Contains("UTF8", StringComparison.Ordinal))
        {
            cmap.UnicodeForm = UnicodeEncodingForm.Utf8;
            cmap._codespaces.Add((1, 0, 0x7F));
            cmap._codespaces.Add((2, 0xC280, 0xDFBF));
            cmap._codespaces.Add((3, 0xE08080, 0xEFBFBF));
            cmap._codespaces.Add((4, 0xF0808080, 0xF48FBFBF));
        }
        else if (name.Contains("UTF32", StringComparison.Ordinal))
        {
            cmap.UnicodeForm = UnicodeEncodingForm.Utf32;
            cmap._codespaces.Add((4, 0, 0x10FFFF));
        }
        else
        {
            return null;
        }

        return cmap;
    }

    /// <summary>Parses a CMap stream. Unknown or malformed parts are skipped.</summary>
    public static CMap Parse(byte[] data, int maxDepth)
    {
        var cmap = new CMap();
        using var source = PdfSource.FromBytes(data);
        using var lexer = new PdfLexer(source);
        var parser = new PdfParser(lexer, maxDepth);
        var operands = new List<Token>();
        while (true)
        {
            Token token;
            try
            {
                token = lexer.Next();
            }
            catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException)
            {
                break;
            }

            if (token.Type == TokenType.EndOfFile)
            {
                break;
            }

            if (token.Type != TokenType.Keyword)
            {
                operands.Add(token);
                if (operands.Count > 4)
                {
                    operands.RemoveAt(0);
                }

                continue;
            }

            switch (token.Text)
            {
                case "begincodespacerange":
                    cmap.ReadPairs(lexer, "endcodespacerange", 2, items => cmap._codespaces.Add((items[0].Bytes!.Length, ToUInt(items[0].Bytes!), ToUInt(items[1].Bytes!))));
                    break;
                case "beginbfchar":
                    cmap.ReadPairs(lexer, "endbfchar", 2, items =>
                    {
                        if (items[0].Bytes is { } src && Decode(items[1]) is { } dst)
                        {
                            cmap._unicode[(src.Length, ToUInt(src))] = dst;
                        }
                    });
                    break;
                case "beginbfrange":
                    cmap.ReadBfRanges(lexer, parser);
                    break;
                case "begincidchar":
                    cmap.ReadPairs(lexer, "endcidchar", 2, items =>
                    {
                        if (items[0].Bytes is { } src && items[1].Type == TokenType.Integer)
                        {
                            cmap._cidRanges.Add((src.Length, ToUInt(src), ToUInt(src), (uint)Math.Clamp(items[1].Integer, 0, uint.MaxValue)));
                        }
                    });
                    break;
                case "begincidrange":
                    cmap.ReadPairs(lexer, "endcidrange", 3, items =>
                    {
                        if (items[0].Bytes is { } lo && items[1].Bytes is { } hi && items[2].Type == TokenType.Integer)
                        {
                            cmap._cidRanges.Add((lo.Length, ToUInt(lo), ToUInt(hi), (uint)Math.Clamp(items[2].Integer, 0, uint.MaxValue)));
                        }
                    });
                    break;
                case "usecmap":
                    if (operands.Count > 0 && operands[^1].Type == TokenType.Name && Predefined(operands[^1].Text!) is { } parent)
                    {
                        cmap.IsIdentity |= parent.IsIdentity;
                        cmap.UnicodeForm ??= parent.UnicodeForm;
                        cmap._codespaces.AddRange(parent._codespaces);
                    }

                    break;
            }

            operands.Clear();
        }

        return cmap;
    }

    private void ReadPairs(PdfLexer lexer, string end, int arity, Action<Token[]> handle)
    {
        var items = new List<Token>();
        while (true)
        {
            var token = lexer.Next();
            if (token.Type == TokenType.EndOfFile || token.IsKeyword(end))
            {
                return;
            }

            items.Add(token);
            if (items.Count == arity)
            {
                if (++_entries > MaxEntries)
                {
                    throw new PdfLimitExceededException("A CMap has too many entries.");
                }

                if (items[0].Type is TokenType.HexString or TokenType.LiteralString && items[0].Bytes is { Length: >= 1 and <= 4 })
                {
                    handle(items.ToArray());
                }

                items.Clear();
            }
        }
    }

    private void ReadBfRanges(PdfLexer lexer, PdfParser parser)
    {
        while (true)
        {
            var lo = lexer.Next();
            if (lo.Type == TokenType.EndOfFile || lo.IsKeyword("endbfrange"))
            {
                return;
            }

            var hi = lexer.Next();
            var dst = lexer.Next();
            if (++_entries > MaxEntries)
            {
                throw new PdfLimitExceededException("A CMap has too many entries.");
            }

            if (lo.Bytes is not { Length: >= 1 and <= 4 } low || hi.Bytes is not { } high || high.Length != low.Length)
            {
                continue;
            }

            var a = ToUInt(low);
            var b = Math.Min(ToUInt(high), a + 0xFFFF);
            if (dst.Type == TokenType.ArrayStart)
            {
                var list = new List<string>();
                while (true)
                {
                    var item = lexer.Next();
                    if (item.Type is TokenType.ArrayEnd or TokenType.EndOfFile)
                    {
                        break;
                    }

                    list.Add(Decode(item) ?? "�");
                }

                _unicodeRanges.Add((low.Length, a, b, null, list.ToArray()));
            }
            else if (Decode(dst) is { } start)
            {
                _unicodeRanges.Add((low.Length, a, b, start, null));
            }
        }
    }

    private static string? Decode(Token token) => token.Type switch
    {
        TokenType.HexString or TokenType.LiteralString when token.Bytes is { Length: > 0 } b => DecodeUtf16(b),
        TokenType.Name => GlyphNames.ToUnicode(token.Text!),
        _ => null,
    };

    private static string DecodeUtf16(byte[] bytes)
    {
        if (bytes.Length == 1)
        {
            return ((char)bytes[0]).ToString();
        }

        return Encoding.BigEndianUnicode.GetString(bytes, 0, bytes.Length & ~1);
    }

    private static uint ToUInt(ReadOnlySpan<byte> bytes)
    {
        uint v = 0;
        foreach (var b in bytes)
        {
            v = (v << 8) | b;
        }

        return v;
    }

    /// <summary>
    /// Reads the next code at <paramref name="pos"/>: the shortest code space range that matches, else the length of
    /// the first range (or one byte).
    /// </summary>
    public int ReadCode(ReadOnlySpan<byte> bytes, int pos, out uint code)
    {
        for (var length = 1; length <= 4 && pos + length <= bytes.Length; length++)
        {
            var value = ToUInt(bytes.Slice(pos, length));
            foreach (var (l, low, high) in _codespaces)
            {
                if (l == length && value >= low && value <= high && InRangeBytewise(bytes.Slice(pos, length), low, high))
                {
                    code = value;
                    return length;
                }
            }
        }

        var fallback = Math.Min(_codespaces.Count > 0 ? _codespaces[0].Length : DefaultLength, bytes.Length - pos);
        code = ToUInt(bytes.Slice(pos, Math.Max(1, fallback)));
        return Math.Max(1, fallback);
    }

    /// <summary>Code space ranges compare each byte against the range's byte bounds (Adobe TN 5014 §4.1).</summary>
    private static bool InRangeBytewise(ReadOnlySpan<byte> code, uint low, uint high)
    {
        for (var i = 0; i < code.Length; i++)
        {
            var shift = 8 * (code.Length - 1 - i);
            var lo = (byte)(low >> shift);
            var hi = (byte)(high >> shift);
            if (code[i] < lo || code[i] > hi)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Length used when the CMap declares no code space (for example a ToUnicode CMap with only bfchar): the length of its mappings.</summary>
    private int DefaultLength => _unicode.Keys.Select(k => k.Length).Concat(_unicodeRanges.Select(r => r.Length)).DefaultIfEmpty(1).Min();

    /// <summary>Whether the CMap defines code lengths (a code space, or mappings to infer them from).</summary>
    public bool HasCodeSpace => _codespaces.Count > 0 || HasUnicode;

    /// <summary>The Unicode text of a code, or null.</summary>
    public string? Lookup(uint code, int length)
    {
        if (_unicode.TryGetValue((length, code), out var s))
        {
            return s;
        }

        foreach (var (l, low, high, start, list) in _unicodeRanges)
        {
            if (l != length || code < low || code > high)
            {
                continue;
            }

            var offset = (int)(code - low);
            if (list is not null)
            {
                return offset < list.Length ? list[offset] : null;
            }

            // The last code unit of the destination is incremented.
            var chars = start!.ToCharArray();
            chars[^1] = (char)(chars[^1] + offset);
            return new string(chars);
        }

        if (UnicodeForm is { } form)
        {
            var bytes = new byte[length];
            for (var i = 0; i < length; i++)
            {
                bytes[i] = (byte)(code >> (8 * (length - 1 - i)));
            }

            return form switch
            {
                UnicodeEncodingForm.Utf8 => Encoding.UTF8.GetString(bytes),
                UnicodeEncodingForm.Utf32 => code <= 0x10FFFF && code is < 0xD800 or > 0xDFFF ? char.ConvertFromUtf32((int)code) : null,
                _ => Encoding.BigEndianUnicode.GetString(bytes),
            };
        }

        return null;
    }

    /// <summary>The CID of a code: Identity maps code to CID; otherwise the cidchar/cidrange mappings, else the code itself.</summary>
    public uint Cid(uint code, int length)
    {
        foreach (var (l, low, high, cid) in _cidRanges)
        {
            if (l == length && code >= low && code <= high)
            {
                return cid + (code - low);
            }
        }

        return code;
    }
}
