using PdfUtil.Internal.Document;
using PdfUtil.Internal.Filters;
using PdfUtil.Internal.Objects;

namespace PdfUtil.Internal.Fonts;

/// <summary>One decoded glyph: its Unicode text (U+FFFD when unknown), its advance width in text space units, and whether it is the single-byte code 32 (to which word spacing applies).</summary>
internal readonly record struct DecodedGlyph(string Text, double Width, bool IsSpaceCode, bool IsUnknown);

/// <summary>
/// Decodes the strings of one font into glyphs (ISO 32000-1 §9.10): <c>/ToUnicode</c> first, then the encoding
/// (standard, WinAnsi, MacRoman, PDFDoc, Symbol, <c>/Differences</c> with glyph names) for simple fonts, or the CMap
/// (Identity, Unicode-based predefined CMaps, embedded CMaps) for Type0 fonts. Widths come from <c>/Widths</c>,
/// <c>/W</c> and <c>/DW</c>, or the standard 14 metrics.
/// </summary>
internal sealed class FontDecoder
{
    private readonly CMap? _toUnicode;
    private readonly CMap? _encodingCMap;
    private readonly char[]? _baseEncoding;
    private readonly Dictionary<int, string> _differences = new();
    private readonly Dictionary<int, double> _widths = new();
    private readonly Dictionary<uint, double> _cidWidths = new();
    private readonly double _defaultWidth;
    private readonly string? _standardFont;
    private readonly double _widthScale = 0.001;

    private FontDecoder(string name, bool isType0)
    {
        Name = name;
        IsType0 = isType0;
    }

    public string Name { get; }

    public bool IsType0 { get; }

    public bool IsBold { get; private set; }

    public bool IsItalic { get; private set; }

    /// <summary>Whether the font has serifs (from its name or descriptor flags).</summary>
    public bool IsSerif { get; private set; }

    /// <summary>Whether the font is fixed-pitch (from its name or descriptor flags).</summary>
    public bool IsMonospace { get; private set; }

    /// <summary>Ascent and descent in text space units (fractions of the font size).</summary>
    public double Ascent { get; private set; } = 0.75;

    public double Descent { get; private set; } = -0.25;

    /// <summary>Whether the font is written vertically (Identity-V and other -V CMaps); such text is laid out horizontally, with a warning.</summary>
    public bool IsVertical { get; private set; }

    public static FontDecoder Create(PdfDocument doc, PdfDictionary? font, int maxDepth)
    {
        var subtype = (doc.Resolve(font?["Subtype"]) as PdfName)?.Value;
        var baseFont = (doc.Resolve(font?["BaseFont"]) as PdfName)?.Value ?? "Unknown";
        var name = baseFont.Length > 7 && baseFont[6] == '+' ? baseFont[7..] : baseFont;
        if (font is null)
        {
            return new FontDecoder("Helvetica", false);
        }

        return subtype == "Type0" ? CreateType0(doc, font, name, maxDepth) : CreateSimple(doc, font, subtype, name, maxDepth);
    }

    private FontDecoder(string name, bool isType0, CMap? toUnicode, CMap? encodingCMap, char[]? baseEncoding, double defaultWidth, string? standardFont, double widthScale)
        : this(name, isType0)
    {
        _toUnicode = toUnicode;
        _encodingCMap = encodingCMap;
        _baseEncoding = baseEncoding;
        _defaultWidth = defaultWidth;
        _standardFont = standardFont;
        _widthScale = widthScale;
    }

    private static CMap? ReadCMap(PdfDocument doc, PdfObject? obj, int maxDepth)
    {
        switch (doc.Resolve(obj))
        {
            case PdfName n:
                return CMap.Predefined(n.Value);
            case PdfStream s when obj is PdfReference r:
                var data = doc.GetDecodedStreamData(s, new ObjectId(r.Id.Number, doc.GetGeneration(r.Id.Number)));
                if (data is null)
                {
                    return null;
                }

                var cmap = CMap.Parse(data, maxDepth);
                if (!cmap.HasCodeSpace && s.Dictionary["UseCMap"] is { } parent && ReadCMap(doc, parent, maxDepth) is { } p)
                {
                    return p;
                }

                return cmap;
            default:
                return null;
        }
    }

    private static CMap? SafeCMap(PdfDocument doc, PdfObject? obj, int maxDepth)
    {
        try
        {
            return ReadCMap(doc, obj, maxDepth);
        }
        catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException)
        {
            return null;
        }
    }

    private void ReadStyle(PdfDocument doc, PdfDictionary? descriptor)
    {
        var lower = Name.ToLowerInvariant();
        IsBold = lower.Contains("bold") || lower.Contains("black") || lower.Contains("heavy") || lower.Contains("semibold");
        IsItalic = lower.Contains("italic") || lower.Contains("oblique");
        IsMonospace = lower.Contains("courier") || lower.Contains("mono") || lower.Contains("consol") || lower.Contains("menlo");
        IsSerif = !IsMonospace && (lower.Contains("times") || lower.Contains("serif") && !lower.Contains("sans") || lower.Contains("roman") || lower.Contains("georgia") || lower.Contains("garamond") || lower.Contains("cambria") || lower.Contains("minion") || lower.Contains("palatino") || lower.Contains("book"));
        if (descriptor is null)
        {
            return;
        }

        var flags = doc.Resolve(descriptor["Flags"]) is PdfInteger f ? f.Value : 0;
        IsItalic |= (flags & (1 << 6)) != 0 || (doc.Resolve(descriptor["ItalicAngle"]) is PdfInteger { Value: not 0 } or PdfReal { Value: not 0 });
        IsBold |= (flags & (1 << 18)) != 0 || (doc.Resolve(descriptor["FontWeight"]) is PdfInteger { Value: >= 600 });
        IsMonospace |= (flags & 1) != 0;
        IsSerif |= !IsMonospace && (flags & 2) != 0;
        if (Number(doc, descriptor["Ascent"]) is double ascent && ascent > 0)
        {
            Ascent = Math.Min(ascent / 1000, 1.5);
        }

        if (Number(doc, descriptor["Descent"]) is double descent && descent < 0)
        {
            Descent = Math.Max(descent / 1000, -1);
        }
    }

    private static double? Number(PdfDocument doc, PdfObject? o) => doc.Resolve(o) switch
    {
        PdfInteger i => i.Value,
        PdfReal r when !double.IsNaN(r.Value) && !double.IsInfinity(r.Value) => r.Value,
        _ => null,
    };

    private static FontDecoder CreateSimple(PdfDocument doc, PdfDictionary font, string? subtype, string name, int maxDepth)
    {
        var descriptor = doc.ResolveDictionary(font["FontDescriptor"]);
        var standard = StandardFonts.Resolve(name);
        var symbolic = descriptor is not null && doc.Resolve(descriptor["Flags"]) is PdfInteger flags && (flags.Value & 4) != 0;

        // Base encoding: the named encoding, or the font's built-in one (approximated).
        char[]? baseEncoding = null;
        PdfArray? differences = null;
        switch (doc.Resolve(font["Encoding"]))
        {
            case PdfName n:
                baseEncoding = FontEncodings.ByName(n.Value);
                break;
            case PdfDictionary d:
                baseEncoding = FontEncodings.ByName((doc.Resolve(d["BaseEncoding"]) as PdfName)?.Value);
                differences = doc.Resolve(d["Differences"]) as PdfArray;
                break;
        }

        baseEncoding ??= standard switch
        {
            "Symbol" => FontEncodings.Symbol,
            "ZapfDingbats" => null,
            _ when subtype == "Type3" => null,
            _ when subtype == "TrueType" && !symbolic => FontEncodings.WinAnsi,
            _ => FontEncodings.Standard,
        };

        // Type 3 glyph widths are in glyph space, scaled by the font matrix.
        var scale = 0.001;
        if (subtype == "Type3" && doc.Resolve(font["FontMatrix"]) is PdfArray { Count: 6 } matrix && Number(doc, matrix[0]) is double m0 && m0 > 0)
        {
            scale = m0;
        }

        var missing = descriptor is not null ? Number(doc, descriptor["MissingWidth"]) ?? 0 : 0;
        var decoder = new FontDecoder(name, false, SafeCMap(doc, font["ToUnicode"], maxDepth), null, baseEncoding, missing, standard, scale);
        decoder.ReadStyle(doc, descriptor);
        if (standard is not null && descriptor is null)
        {
            decoder.IsBold |= StandardFonts.IsBold(standard);
            decoder.IsItalic |= StandardFonts.IsItalic(standard);
            var (ascent, descent) = StandardFonts.VerticalMetrics(standard);
            decoder.Ascent = ascent / 1000.0;
            decoder.Descent = descent / 1000.0;
        }

        if (differences is not null)
        {
            var code = 0;
            foreach (var item in differences.Items.Take(4096))
            {
                switch (doc.Resolve(item))
                {
                    case PdfInteger i:
                        code = (int)Math.Clamp(i.Value, 0, 255);
                        break;
                    case PdfName glyph when code <= 255:
                        decoder._differences[code++] = glyph.Value;
                        break;
                }
            }
        }

        if (doc.Resolve(font["Widths"]) is PdfArray widths)
        {
            var first = Number(doc, font["FirstChar"]) is double fc ? (int)Math.Clamp(fc, 0, 255) : 0;
            for (var i = 0; i < widths.Count && first + i <= 255; i++)
            {
                if (Number(doc, widths[i]) is double w)
                {
                    decoder._widths[first + i] = w;
                }
            }
        }

        return decoder;
    }

    private static FontDecoder CreateType0(PdfDocument doc, PdfDictionary font, string name, int maxDepth)
    {
        var encodingObj = font["Encoding"];
        var encoding = SafeCMap(doc, encodingObj, maxDepth);
        var encodingName = (doc.Resolve(encodingObj) as PdfName)?.Value;
        PdfDictionary? cidFont = doc.Resolve(font["DescendantFonts"]) is PdfArray { Count: > 0 } d ? doc.ResolveDictionary(d[0]) : null;
        var dw = Number(doc, cidFont?["DW"]) ?? 1000;
        var decoder = new FontDecoder(name, true, SafeCMap(doc, font["ToUnicode"], maxDepth), encoding ?? CMap.Predefined("Identity-H"), null, dw, null, 0.001)
        {
            IsVertical = encodingName?.EndsWith("-V", StringComparison.Ordinal) == true,
        };
        decoder.ReadStyle(doc, doc.ResolveDictionary(cidFont?["FontDescriptor"]));

        // /W: c [w1 w2 ...] or cfirst clast w.
        if (doc.Resolve(cidFont?["W"]) is PdfArray w)
        {
            var i = 0;
            var count = 0;
            while (i < w.Count && count < 1 << 20)
            {
                if (Number(doc, w[i]) is not double first)
                {
                    break;
                }

                if (i + 1 < w.Count && doc.Resolve(w[i + 1]) is PdfArray list)
                {
                    for (var k = 0; k < list.Count && count < 1 << 20; k++, count++)
                    {
                        if (Number(doc, list[k]) is double width)
                        {
                            decoder._cidWidths[(uint)first + (uint)k] = width;
                        }
                    }

                    i += 2;
                }
                else if (i + 2 < w.Count && Number(doc, w[i + 1]) is double last && Number(doc, w[i + 2]) is double width)
                {
                    for (var c = (uint)first; c <= (uint)Math.Min(last, first + 65535) && count < 1 << 20; c++, count++)
                    {
                        decoder._cidWidths[c] = width;
                    }

                    i += 3;
                }
                else
                {
                    break;
                }
            }
        }

        return decoder;
    }

    /// <summary>Decodes a string shown with this font.</summary>
    public List<DecodedGlyph> Decode(byte[] bytes)
    {
        var glyphs = new List<DecodedGlyph>(bytes.Length);
        if (IsType0)
        {
            var cmap = _encodingCMap!;
            var codeSource = cmap.HasCodeSpace ? cmap : _toUnicode ?? cmap;
            var pos = 0;
            while (pos < bytes.Length)
            {
                var length = codeSource.ReadCode(bytes, pos, out var code);
                var text = _toUnicode?.Lookup(code, length) ?? cmap.Lookup(code, length);
                var cid = cmap.Cid(code, length);
                var width = (_cidWidths.TryGetValue(cid, out var w) ? w : _defaultWidth) * _widthScale;
                glyphs.Add(new DecodedGlyph(text ?? "�", width, length == 1 && code == 32, text is null));
                pos += length;
            }

            return glyphs;
        }

        foreach (var b in bytes)
        {
            var text = _toUnicode?.Lookup(b, 1);
            if (text is null && _differences.TryGetValue(b, out var glyphName))
            {
                text = GlyphNames.ToUnicode(glyphName);
            }

            if (text is null && _baseEncoding is not null && _baseEncoding[b] != '\0')
            {
                text = _baseEncoding[b].ToString();
            }

            double width;
            if (_widths.TryGetValue(b, out var w))
            {
                width = w * _widthScale;
            }
            else if (_standardFont is not null)
            {
                width = StandardFonts.Width(_standardFont, text is { Length: 1 } ? text[0] : (char)b) / 1000.0;
            }
            else
            {
                width = (_defaultWidth > 0 ? _defaultWidth : 500) * _widthScale;
            }

            glyphs.Add(new DecodedGlyph(text ?? "�", width, b == 32, text is null));
        }

        return glyphs;
    }
}
