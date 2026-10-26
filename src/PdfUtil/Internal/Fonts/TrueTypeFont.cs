using System.Buffers.Binary;
using System.Text;

namespace PdfUtil.Internal.Fonts;

/// <summary>
/// A TrueType font (glyf outlines) read for embedding: the tables PDF needs, character to glyph mapping (cmap formats
/// 4 and 12, and 0 and 6), advance widths, vertical metrics, names, style and embedding permissions. Fonts with CFF
/// outlines (<c>OTTO</c>) are refused. Collections (<c>.ttc</c>) are read face by face. Every offset is bounds-checked;
/// a damaged font throws <see cref="InvalidDataException"/>.
/// </summary>
internal sealed class TrueTypeFont
{
    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables;
    private readonly Dictionary<int, ushort> _cmap;
    private ushort[] _advances = Array.Empty<ushort>();

    private TrueTypeFont(byte[] data, Dictionary<string, (int Offset, int Length)> tables)
    {
        _data = data;
        _tables = tables;
        var head = Table("head");
        UnitsPerEm = U16(head, 18);
        if (UnitsPerEm is < 16 or > 16384)
        {
            throw new InvalidDataException("The font has an invalid unitsPerEm.");
        }

        MacStyle = U16(head, 44);
        BoundingBox = (S16(head, 36), S16(head, 38), S16(head, 40), S16(head, 42));
        IndexToLocFormat = S16(head, 50);
        GlyphCount = U16(Table("maxp"), 4);
        var hhea = Table("hhea");
        Ascent = S16(hhea, 4);
        Descent = S16(hhea, 6);
        LineGap = S16(hhea, 8);
        ReadAdvances(U16(hhea, 34));
        if (_tables.ContainsKey("OS/2"))
        {
            var os2 = Table("OS/2");
            WeightClass = U16(os2, 4);
            FsType = U16(os2, 8);
            FsSelection = U16(os2, 62);
            if (os2.Length >= 74)
            {
                var typoAscent = S16(os2, 68);
                var typoDescent = S16(os2, 70);
                if (typoAscent > 0 && (FsSelection & 0x80) != 0)
                {
                    (Ascent, Descent) = (typoAscent, typoDescent);
                }
            }

            CapHeight = os2.Length >= 90 ? S16(os2, 88) : (short)(Ascent * 0.7);
        }
        else
        {
            CapHeight = (short)(Ascent * 0.7);
        }

        if (_tables.ContainsKey("post"))
        {
            var post = Table("post");
            ItalicAngle = BinaryPrimitives.ReadInt32BigEndian(post.Slice(4, 4)) / 65536.0;
            UnderlinePosition = S16(post, 8);
            UnderlineThickness = S16(post, 10);
            IsFixedPitch = BinaryPrimitives.ReadUInt32BigEndian(post.Slice(12, 4)) != 0;
        }

        ReadNames();
        _cmap = ReadCmap();
    }

    public int UnitsPerEm { get; }

    public int GlyphCount { get; }

    public short Ascent { get; private set; }

    public short Descent { get; private set; }

    public short LineGap { get; }

    public short CapHeight { get; }

    public int MacStyle { get; }

    public int WeightClass { get; } = 400;

    public int FsSelection { get; }

    /// <summary>OS/2 embedding permissions; bit 1 (restricted) forbids embedding.</summary>
    public int FsType { get; }

    public double ItalicAngle { get; }

    public short UnderlinePosition { get; } = -100;

    public short UnderlineThickness { get; } = 50;

    public bool IsFixedPitch { get; }

    public (short XMin, short YMin, short XMax, short YMax) BoundingBox { get; }

    private short IndexToLocFormat { get; }

    public string FamilyName { get; private set; } = "";

    public string SubfamilyName { get; private set; } = "";

    public string PostScriptName { get; private set; } = "";

    public bool IsBold => WeightClass >= 600 || (MacStyle & 1) != 0 || (FsSelection & 0x20) != 0;

    public bool IsItalic => (MacStyle & 2) != 0 || (FsSelection & 1) != 0 || ItalicAngle != 0;

    /// <summary>Whether the license allows embedding (only "restricted license" forbids it).</summary>
    public bool CanEmbed => (FsType & 0x000F) != 0x0002;

    /// <summary>The code points the font maps.</summary>
    public IEnumerable<int> CodePoints => _cmap.Keys;

    /// <summary>Reads a font file: a single TrueType font, or every face of a collection.</summary>
    public static List<TrueTypeFont> Load(byte[] data)
    {
        if (data.Length < 12)
        {
            throw new InvalidDataException("The font file is too short.");
        }

        var tag = Encoding.ASCII.GetString(data, 0, 4);
        if (tag == "ttcf")
        {
            var count = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8)), 256);
            var fonts = new List<TrueTypeFont>();
            for (var i = 0; i < count; i++)
            {
                var offset = Check(data, 12 + (i * 4), 4);
                fonts.Add(Parse(data, (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset))));
            }

            return fonts;
        }

        return new List<TrueTypeFont> { Parse(data, 0) };
    }

    private static TrueTypeFont Parse(byte[] data, int start)
    {
        Check(data, start, 12);
        var version = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(start));
        if (version == 0x4F54544F)
        {
            throw new NotSupportedException("The font has PostScript (CFF) outlines; only TrueType outlines can be embedded.");
        }

        if (version is not (0x00010000 or 0x74727565))
        {
            throw new InvalidDataException("The file is not a TrueType font.");
        }

        var numTables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(start + 4));
        var tables = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        for (var i = 0; i < numTables; i++)
        {
            var entry = Check(data, start + 12 + (i * 16), 16);
            var name = Encoding.ASCII.GetString(data, entry, 4);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry + 8));
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entry + 12));
            if (offset > int.MaxValue || length > int.MaxValue || offset + length > (uint)data.Length)
            {
                throw new InvalidDataException($"The font table {name} lies outside the file.");
            }

            tables.TryAdd(name, ((int)offset, (int)length));
        }

        foreach (var required in new[] { "head", "hhea", "maxp", "hmtx", "loca", "glyf", "cmap" })
        {
            if (!tables.ContainsKey(required))
            {
                throw new InvalidDataException($"The font has no {required} table.");
            }
        }

        return new TrueTypeFont(data, tables);
    }

    private static int Check(byte[] data, int offset, int length) =>
        offset >= 0 && length >= 0 && (long)offset + length <= data.Length ? offset : throw new InvalidDataException("The font data is truncated.");

    internal ReadOnlySpan<byte> Table(string tag) => _tables.TryGetValue(tag, out var t) ? _data.AsSpan(t.Offset, t.Length) : ReadOnlySpan<byte>.Empty;

    internal bool HasTable(string tag) => _tables.ContainsKey(tag);

    private static ushort U16(ReadOnlySpan<byte> s, int offset) => offset + 2 <= s.Length ? BinaryPrimitives.ReadUInt16BigEndian(s[offset..]) : throw new InvalidDataException("A font table is truncated.");

    private static short S16(ReadOnlySpan<byte> s, int offset) => (short)U16(s, offset);

    private static uint U32(ReadOnlySpan<byte> s, int offset) => offset + 4 <= s.Length ? BinaryPrimitives.ReadUInt32BigEndian(s[offset..]) : throw new InvalidDataException("A font table is truncated.");

    private void ReadAdvances(int numberOfHMetrics)
    {
        var hmtx = Table("hmtx");
        numberOfHMetrics = Math.Clamp(numberOfHMetrics, 1, Math.Max(1, GlyphCount));
        _advances = new ushort[Math.Max(GlyphCount, 1)];
        for (var g = 0; g < _advances.Length; g++)
        {
            _advances[g] = U16(hmtx, Math.Min(g, numberOfHMetrics - 1) * 4);
        }
    }

    /// <summary>The advance width of a glyph in font units.</summary>
    public int Advance(int glyph) => glyph >= 0 && glyph < _advances.Length ? _advances[glyph] : _advances[0];

    /// <summary>The advance width in 1/1000 em, as PDF widths are given.</summary>
    public double Advance1000(int glyph) => Advance(glyph) * 1000.0 / UnitsPerEm;

    /// <summary>The glyph for a code point, or 0 (.notdef).</summary>
    public int GlyphFor(int codePoint) => _cmap.TryGetValue(codePoint, out var g) && g < GlyphCount ? g : 0;

    public bool Covers(int codePoint) => GlyphFor(codePoint) != 0;

    private void ReadNames()
    {
        var name = Table("name");
        if (name.Length < 6)
        {
            return;
        }

        var count = U16(name, 2);
        var storage = U16(name, 4);
        var found = new Dictionary<int, (int Rank, string Value)>();
        for (var i = 0; i < count; i++)
        {
            var record = 6 + (i * 12);
            if (record + 12 > name.Length)
            {
                break;
            }

            var platform = U16(name, record);
            var encoding = U16(name, record + 2);
            var language = U16(name, record + 4);
            var id = U16(name, record + 6);
            var length = U16(name, record + 8);
            var offset = storage + U16(name, record + 10);
            if (id is not (1 or 2 or 4 or 6 or 16 or 17) || offset + length > name.Length)
            {
                continue;
            }

            var bytes = name.Slice(offset, length);
            string? value = platform switch
            {
                3 when encoding is 0 or 1 or 10 => Encoding.BigEndianUnicode.GetString(bytes),
                0 => Encoding.BigEndianUnicode.GetString(bytes),
                1 when encoding == 0 => Encoding.Latin1.GetString(bytes),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            // Prefer Windows English, then any Unicode, then Macintosh.
            var rank = platform == 3 && language == 0x409 ? 0 : platform is 3 or 0 ? 1 : 2;
            if (!found.TryGetValue(id, out var existing) || rank < existing.Rank)
            {
                found[id] = (rank, value.Trim());
            }
        }

        FamilyName = found.TryGetValue(16, out var typo) ? typo.Value : found.TryGetValue(1, out var family) ? family.Value : "";
        SubfamilyName = found.TryGetValue(17, out var typoSub) ? typoSub.Value : found.TryGetValue(2, out var sub) ? sub.Value : "";
        PostScriptName = found.TryGetValue(6, out var ps) ? ps.Value : (FamilyName + "-" + SubfamilyName).Replace(" ", "", StringComparison.Ordinal);
    }

    private Dictionary<int, ushort> ReadCmap()
    {
        var cmap = Table("cmap");
        var count = U16(cmap, 2);
        var candidates = new List<(int Rank, int Offset)>();
        for (var i = 0; i < count; i++)
        {
            var record = 4 + (i * 8);
            var platform = U16(cmap, record);
            var encoding = U16(cmap, record + 2);
            var offset = (int)Math.Min(U32(cmap, record + 4), int.MaxValue);
            var rank = (platform, encoding) switch
            {
                (3, 10) => 0,
                (0, 4) or (0, 6) => 1,
                (3, 1) => 2,
                (0, _) => 3,
                (3, 0) => 4,
                (1, 0) => 5,
                _ => -1,
            };
            if (rank >= 0 && offset + 4 <= cmap.Length)
            {
                candidates.Add((rank, offset));
            }
        }

        foreach (var (_, offset) in candidates.OrderBy(c => c.Rank))
        {
            var map = ReadSubtable(cmap, offset);
            if (map is not null)
            {
                return map;
            }
        }

        throw new InvalidDataException("The font has no usable character map.");
    }

    private Dictionary<int, ushort>? ReadSubtable(ReadOnlySpan<byte> cmap, int offset)
    {
        var map = new Dictionary<int, ushort>();
        switch (U16(cmap, offset))
        {
            case 0:
                for (var c = 0; c < 256; c++)
                {
                    map[c] = cmap[offset + 6 + c];
                }

                return map;
            case 4:
                var segX2 = U16(cmap, offset + 6);
                var ends = offset + 14;
                var starts = ends + segX2 + 2;
                var deltas = starts + segX2;
                var ranges = deltas + segX2;
                for (var s = 0; s < segX2 / 2; s++)
                {
                    int end = U16(cmap, ends + (s * 2));
                    int start = U16(cmap, starts + (s * 2));
                    var delta = S16(cmap, deltas + (s * 2));
                    int rangeOffset = U16(cmap, ranges + (s * 2));
                    for (var c = start; c <= end && c != 0xFFFF; c++)
                    {
                        int glyph;
                        if (rangeOffset == 0)
                        {
                            glyph = (c + delta) & 0xFFFF;
                        }
                        else
                        {
                            var at = ranges + (s * 2) + rangeOffset + ((c - start) * 2);
                            glyph = at + 2 <= cmap.Length ? U16(cmap, at) : 0;
                            if (glyph != 0)
                            {
                                glyph = (glyph + delta) & 0xFFFF;
                            }
                        }

                        if (glyph != 0)
                        {
                            map[c] = (ushort)glyph;
                        }
                    }
                }

                return map;
            case 6:
                int first = U16(cmap, offset + 6);
                int entries = U16(cmap, offset + 8);
                for (var i = 0; i < entries; i++)
                {
                    map[first + i] = U16(cmap, offset + 10 + (i * 2));
                }

                return map;
            case 12:
                var groups = U32(cmap, offset + 12);
                if (groups > 1_000_000)
                {
                    return null;
                }

                for (var g = 0; g < groups; g++)
                {
                    var at = offset + 16 + (g * 12);
                    var startCode = U32(cmap, at);
                    var endCode = U32(cmap, at + 4);
                    var startGlyph = U32(cmap, at + 8);
                    if (endCode < startCode || endCode > 0x10FFFF || endCode - startCode > 0x10FFFF)
                    {
                        continue;
                    }

                    for (var c = startCode; c <= endCode; c++)
                    {
                        var glyph = startGlyph + (c - startCode);
                        if (glyph is > 0 and < 65536)
                        {
                            map[(int)c] = (ushort)glyph;
                        }
                    }
                }

                return map;
            default:
                return null;
        }
    }

    /// <summary>The bytes of a glyph's outline (empty for blank glyphs).</summary>
    internal ReadOnlySpan<byte> GlyphData(int glyph)
    {
        if (glyph < 0 || glyph >= GlyphCount)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        var loca = Table("loca");
        long start, end;
        if (IndexToLocFormat == 0)
        {
            start = U16(loca, glyph * 2) * 2L;
            end = U16(loca, (glyph + 1) * 2) * 2L;
        }
        else
        {
            start = U32(loca, glyph * 4);
            end = U32(loca, (glyph + 1) * 4);
        }

        var glyf = Table("glyf");
        if (end <= start || end > glyf.Length)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        return glyf[(int)start..(int)end];
    }

    /// <summary>The glyphs a composite glyph is made of (empty for simple glyphs).</summary>
    internal static List<(int Offset, int Glyph)> Components(ReadOnlySpan<byte> glyph)
    {
        var result = new List<(int, int)>();
        if (glyph.Length < 10 || S16(glyph, 0) >= 0)
        {
            return result;
        }

        var at = 10;
        while (at + 4 <= glyph.Length && result.Count < 256)
        {
            var flags = U16(glyph, at);
            result.Add((at + 2, U16(glyph, at + 2)));
            at += 4 + ((flags & 0x0001) != 0 ? 4 : 2);
            at += (flags & 0x0008) != 0 ? 2 : (flags & 0x0040) != 0 ? 4 : (flags & 0x0080) != 0 ? 8 : 0;
            if ((flags & 0x0020) == 0)
            {
                break;
            }
        }

        return result;
    }
}
