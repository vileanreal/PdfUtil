using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PdfUtil.Internal.Objects;

namespace PdfUtil.Internal.Fonts;

/// <summary>
/// A TrueType font being embedded as a subset: glyphs get compact new ids in the order they are first used (0 stays
/// .notdef), so text can be encoded (Identity-H, two bytes per glyph) before the subset exists. <see cref="Build"/>
/// then writes the subset font program (composite glyph components included and renumbered), and the Type0 /
/// CIDFontType2 objects with widths and a <c>/ToUnicode</c> map, so copying text out of the PDF works.
/// </summary>
internal sealed class EmbeddedFont
{
    private readonly Dictionary<int, int> _newIds = new() { [0] = 0 };
    private readonly List<int> _oldIds = new() { 0 };
    private readonly Dictionary<int, string> _text = new();

    public EmbeddedFont(TrueTypeFont font)
    {
        if (!font.CanEmbed)
        {
            throw new NotSupportedException($"The license of font {font.FamilyName} does not allow embedding.");
        }

        Font = font;
    }

    public TrueTypeFont Font { get; }

    public bool IsUsed => _oldIds.Count > 1;

    /// <summary>Encodes text as two-byte glyph ids, recording what each glyph stands for. Unmapped characters become .notdef.</summary>
    public byte[] Encode(string text, out int missing)
    {
        missing = 0;
        var bytes = new List<byte>(text.Length * 2);
        foreach (var rune in text.EnumerateRunes())
        {
            var old = Font.GlyphFor(rune.Value);
            if (old == 0)
            {
                missing++;
            }

            var id = Use(old, rune.ToString());
            bytes.Add((byte)(id >> 8));
            bytes.Add((byte)id);
        }

        return bytes.ToArray();
    }

    /// <summary>The width of text in 1/1000 em.</summary>
    public double Width(string text) => text.EnumerateRunes().Sum(r => Font.Advance1000(Font.GlyphFor(r.Value)));

    private int Use(int oldGlyph, string text)
    {
        if (!_newIds.TryGetValue(oldGlyph, out var id))
        {
            id = _oldIds.Count;
            if (id > 0xFFFF)
            {
                throw new PdfLimitExceededException("Too many glyphs from one font.");
            }

            _newIds[oldGlyph] = id;
            _oldIds.Add(oldGlyph);
        }

        if (oldGlyph != 0)
        {
            _text.TryAdd(id, text);
        }

        return id;
    }

    /// <summary>
    /// Writes the font objects through <paramref name="add"/> (which stores an object, with stream data when given,
    /// and returns its reference) and returns the Type0 font dictionary.
    /// </summary>
    public PdfDictionary Build(string subsetTag, Func<PdfObject, byte[]?, PdfReference> add)
    {
        // Composite glyphs need their components; they get ids after the used glyphs.
        for (var i = 0; i < _oldIds.Count; i++)
        {
            foreach (var (_, component) in TrueTypeFont.Components(Font.GlyphData(_oldIds[i])))
            {
                if (component < Font.GlyphCount && !_newIds.ContainsKey(component))
                {
                    _newIds[component] = _oldIds.Count;
                    _oldIds.Add(component);
                }
            }
        }

        var program = Subset();
        var scale = 1000.0 / Font.UnitsPerEm;
        var baseName = subsetTag + "+" + SafeName(Font.PostScriptName.Length > 0 ? Font.PostScriptName : Font.FamilyName);
        var flags = (Font.IsFixedPitch ? 1 : 0) | 4 | (Font.IsItalic ? 64 : 0);
        var descriptor = new PdfDictionary
        {
            ["Type"] = PdfName.Get("FontDescriptor"),
            ["FontName"] = PdfName.Get(baseName),
            ["Flags"] = new PdfInteger(flags),
            ["FontBBox"] = new PdfArray(new PdfObject[] { Num(Font.BoundingBox.XMin * scale), Num(Font.BoundingBox.YMin * scale), Num(Font.BoundingBox.XMax * scale), Num(Font.BoundingBox.YMax * scale) }),
            ["ItalicAngle"] = Num(Font.ItalicAngle),
            ["Ascent"] = Num(Font.Ascent * scale),
            ["Descent"] = Num(Font.Descent * scale),
            ["CapHeight"] = Num(Font.CapHeight * scale),
            ["StemV"] = new PdfInteger(Font.IsBold ? 120 : 80),
            ["FontFile2"] = add(new PdfDictionary { ["Length1"] = new PdfInteger(program.Length) }, program),
        };

        // Widths: runs of consecutive ids, [first [w1 w2 …]].
        var widths = new PdfArray();
        var run = new PdfArray();
        var runStart = -1;
        for (var id = 0; id < _oldIds.Count; id++)
        {
            if (runStart < 0)
            {
                runStart = id;
            }

            run.Add(Num(Math.Round(Font.Advance1000(_oldIds[id]), 2)));
            if (run.Count == 500 || id == _oldIds.Count - 1)
            {
                widths.Add(new PdfInteger(runStart));
                widths.Add(run);
                run = new PdfArray();
                runStart = -1;
            }
        }

        var cidFont = new PdfDictionary
        {
            ["Type"] = PdfName.Get("Font"),
            ["Subtype"] = PdfName.Get("CIDFontType2"),
            ["BaseFont"] = PdfName.Get(baseName),
            ["CIDSystemInfo"] = new PdfDictionary { ["Registry"] = new PdfString(Encoding.ASCII.GetBytes("Adobe")), ["Ordering"] = new PdfString(Encoding.ASCII.GetBytes("Identity")), ["Supplement"] = new PdfInteger(0) },
            ["FontDescriptor"] = add(descriptor, null),
            ["DW"] = new PdfInteger(1000),
            ["W"] = widths,
            ["CIDToGIDMap"] = PdfName.Get("Identity"),
        };
        return new PdfDictionary
        {
            ["Type"] = PdfName.Get("Font"),
            ["Subtype"] = PdfName.Get("Type0"),
            ["BaseFont"] = PdfName.Get(baseName),
            ["Encoding"] = PdfName.Get("Identity-H"),
            ["DescendantFonts"] = new PdfArray(new PdfObject[] { add(cidFont, null) }),
            ["ToUnicode"] = add(new PdfDictionary(), Encoding.ASCII.GetBytes(ToUnicode())),
        };
    }

    private static PdfObject Num(double v) => Math.Abs(v - Math.Round(v)) < 1e-9 ? new PdfInteger((long)Math.Round(v)) : new PdfReal(Math.Round(v, 3));

    private static string SafeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (c > ' ' && c < 127 && c is not ('/' or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '%' or '#'))
            {
                sb.Append(c);
            }
        }

        return sb.Length == 0 ? "Font" : sb.ToString();
    }

    private string ToUnicode()
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var entries = _text.OrderBy(e => e.Key).ToList();
        for (var i = 0; i < entries.Count; i += 100)
        {
            var chunk = entries.Skip(i).Take(100).ToList();
            sb.Append(chunk.Count.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
            foreach (var (id, text) in chunk)
            {
                sb.Append('<').Append(id.ToString("X4", CultureInfo.InvariantCulture)).Append("> <");
                foreach (var b in Encoding.BigEndianUnicode.GetBytes(text))
                {
                    sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }

                sb.Append(">\n");
            }

            sb.Append("endbfchar\n");
        }

        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }

    /// <summary>The subset font program: the used glyphs renumbered, with the tables PDF requires.</summary>
    private byte[] Subset()
    {
        var count = _oldIds.Count;
        using var glyf = new MemoryStream();
        var loca = new byte[(count + 1) * 4];
        for (var id = 0; id < count; id++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(id * 4), (uint)glyf.Length);
            var data = Font.GlyphData(_oldIds[id]).ToArray();
            foreach (var (offset, component) in TrueTypeFont.Components(data))
            {
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), (ushort)(_newIds.TryGetValue(component, out var mapped) ? mapped : 0));
            }

            glyf.Write(data);
            while (glyf.Length % 4 != 0)
            {
                glyf.WriteByte(0);
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(count * 4), (uint)glyf.Length);

        var hmtx = new byte[count * 4];
        for (var id = 0; id < count; id++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(id * 4), (ushort)Font.Advance(_oldIds[id]));
            var data = Font.GlyphData(_oldIds[id]);
            BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan((id * 4) + 2), data.Length >= 4 ? BinaryPrimitives.ReadInt16BigEndian(data[2..]) : (short)0);
        }

        var head = Font.Table("head").ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), 0);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);
        var hhea = Font.Table("hhea").ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)count);
        var maxp = Font.Table("maxp").ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)count);
        var post = new byte[32];
        Font.Table("post")[..Math.Min(32, Font.Table("post").Length)].CopyTo(post);
        BinaryPrimitives.WriteUInt32BigEndian(post, 0x00030000);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["cmap"] = Cmap(),
            ["glyf"] = glyf.ToArray(),
            ["head"] = head,
            ["hhea"] = hhea,
            ["hmtx"] = hmtx,
            ["loca"] = loca,
            ["maxp"] = maxp,
            ["post"] = post,
        };
        foreach (var keep in new[] { "cvt ", "fpgm", "prep", "OS/2", "name" })
        {
            if (Font.HasTable(keep))
            {
                tables[keep] = Font.Table(keep).ToArray();
            }
        }

        return Assemble(tables);
    }

    /// <summary>A (3,1) format 4 character map for the BMP characters the subset shows.</summary>
    private byte[] Cmap()
    {
        var map = _text.Where(e => e.Value.Length == 1 && !char.IsSurrogate(e.Value[0]) && e.Value[0] != 0xFFFF).Select(e => (Code: (int)e.Value[0], Id: e.Key)).GroupBy(e => e.Code).Select(g => g.First()).OrderBy(e => e.Code).ToList();
        var segments = map.Select(m => (Start: m.Code, End: m.Code, Delta: (short)(m.Id - m.Code))).ToList();
        segments.Add((0xFFFF, 0xFFFF, 1));
        var segX2 = segments.Count * 2;
        var length = 16 + (segments.Count * 8);
        var table = new byte[12 + length];
        BinaryPrimitives.WriteUInt16BigEndian(table, 0);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8), 12);
        var s = table.AsSpan(12);
        BinaryPrimitives.WriteUInt16BigEndian(s, 4);
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], (ushort)length);
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], (ushort)segX2);
        var searchRange = 2;
        var entrySelector = 0;
        while (searchRange * 2 <= segX2)
        {
            searchRange *= 2;
            entrySelector++;
        }

        BinaryPrimitives.WriteUInt16BigEndian(s[8..], (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(s[10..], (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(s[12..], (ushort)(segX2 - searchRange));
        for (var i = 0; i < segments.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(s[(14 + (i * 2))..], (ushort)segments[i].End);
            BinaryPrimitives.WriteUInt16BigEndian(s[(16 + segX2 + (i * 2))..], (ushort)segments[i].Start);
            BinaryPrimitives.WriteInt16BigEndian(s[(16 + (2 * segX2) + (i * 2))..], segments[i].Delta);
        }

        return table;
    }

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        var count = tables.Count;
        var header = new byte[12 + (16 * count)];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)count);
        var searchRange = 1;
        var entrySelector = 0;
        while (searchRange * 2 <= count)
        {
            searchRange *= 2;
            entrySelector++;
        }

        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)(searchRange * 16));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)((count * 16) - (searchRange * 16)));
        using var output = new MemoryStream();
        output.Write(header);
        var index = 0;
        var headOffset = -1;
        foreach (var (tag, data) in tables)
        {
            var entry = header.AsSpan(12 + (index * 16));
            Encoding.ASCII.GetBytes(tag).CopyTo(entry);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], Checksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)output.Length);
            BinaryPrimitives.WriteUInt32BigEndian(entry[12..], (uint)data.Length);
            if (tag == "head")
            {
                headOffset = (int)output.Length;
            }

            output.Write(data);
            while (output.Length % 4 != 0)
            {
                output.WriteByte(0);
            }

            index++;
        }

        var font = output.ToArray();
        header.CopyTo(font, 0);
        if (headOffset >= 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(headOffset + 8), 0xB1B0AFBA - Checksum(font));
        }

        return font;
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (var k = 0; k < 4; k++)
            {
                word = (word << 8) | (i + k < data.Length ? data[i + k] : 0u);
            }

            sum += word;
        }

        return sum;
    }
}
