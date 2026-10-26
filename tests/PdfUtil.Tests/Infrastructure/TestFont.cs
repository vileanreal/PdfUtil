using System.Buffers.Binary;
using System.Text;

namespace PdfUtil.Tests.Infrastructure;

/// <summary>
/// Builds small, valid TrueType fonts for tests: every mapped character is a filled box whose width depends on the
/// character, with cmap formats 4 and 12 (characters above the BMP), and the last glyph is a composite of two others.
/// </summary>
internal static class TestFont
{
    /// <summary>ASCII, Latin-1, Greek, Cyrillic, some CJK and kana, and an emoji above the BMP.</summary>
    public static readonly int[] DefaultCharacters =
        Enumerable.Range(32, 95).Concat(Enumerable.Range(0xA0, 96)).Concat(Enumerable.Range(0x391, 57)).Concat(Enumerable.Range(0x410, 64))
            .Concat("日本語中文のテキスト。、".EnumerateRunes().Select(r => r.Value)).Concat(new[] { 0x2022, 0x1F600 }).Distinct().ToArray();

    public static byte[] Build(string family = "Test Sans", int[]? characters = null, bool bold = false, bool restricted = false, int composite = 0x00C5)
    {
        characters ??= DefaultCharacters;
        var chars = characters.Distinct().OrderBy(c => c).ToArray();
        var glyphCount = chars.Length + 1;
        int Advance(int c) => c == ' ' ? 250 : c >= 0x2E80 ? 1000 : 400 + (c % 7 * 50);

        // glyf: .notdef box, one box per character; the composite character is made of two other glyphs.
        using var glyf = new MemoryStream();
        var loca = new List<uint>();
        byte[] Box(int width)
        {
            var g = new byte[12 + 2 + 4 + 2 + 4 + 8 + 8];
            var s = g.AsSpan();
            BinaryPrimitives.WriteInt16BigEndian(s, 1);
            BinaryPrimitives.WriteInt16BigEndian(s[2..], 50);
            BinaryPrimitives.WriteInt16BigEndian(s[4..], 0);
            BinaryPrimitives.WriteInt16BigEndian(s[6..], (short)(width - 50));
            BinaryPrimitives.WriteInt16BigEndian(s[8..], 700);
            BinaryPrimitives.WriteUInt16BigEndian(s[10..], 3);
            BinaryPrimitives.WriteUInt16BigEndian(s[12..], 0);
            var at = 14;
            for (var i = 0; i < 4; i++)
            {
                g[at++] = 1;
            }

            short[] xs = { 50, (short)(width - 100), 0, (short)-(width - 100) };
            short[] ys = { 0, 0, 700, 0 };
            foreach (var x in xs)
            {
                BinaryPrimitives.WriteInt16BigEndian(s[at..], x);
                at += 2;
            }

            foreach (var y in ys)
            {
                BinaryPrimitives.WriteInt16BigEndian(s[at..], y);
                at += 2;
            }

            return g.AsSpan(0, at).ToArray();
        }

        void AddGlyph(byte[] data)
        {
            loca.Add((uint)glyf.Length);
            glyf.Write(data);
            while (glyf.Length % 4 != 0)
            {
                glyf.WriteByte(0);
            }
        }

        AddGlyph(Box(500));
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] == ' ')
            {
                AddGlyph(Array.Empty<byte>());
            }
            else if (chars[i] == composite && chars.Contains('A') && chars.Contains('.'))
            {
                var a = Array.IndexOf(chars, 'A') + 1;
                var dot = Array.IndexOf(chars, '.') + 1;
                // Two components of 6 bytes each: flags, glyph index, x and y offsets as bytes.
                var g = new byte[10 + 6 + 6];
                var s = g.AsSpan();
                BinaryPrimitives.WriteInt16BigEndian(s, -1);
                BinaryPrimitives.WriteInt16BigEndian(s[6..], 600);
                BinaryPrimitives.WriteInt16BigEndian(s[8..], 900);
                BinaryPrimitives.WriteUInt16BigEndian(s[10..], 0x0020 | 0x0002);
                BinaryPrimitives.WriteUInt16BigEndian(s[12..], (ushort)a);
                BinaryPrimitives.WriteUInt16BigEndian(s[16..], 0x0002);
                BinaryPrimitives.WriteUInt16BigEndian(s[18..], (ushort)dot);
                g[21] = 120;
                AddGlyph(g);
            }
            else
            {
                AddGlyph(Box(Advance(chars[i])));
            }
        }

        loca.Add((uint)glyf.Length);
        var locaBytes = new byte[loca.Count * 4];
        for (var i = 0; i < loca.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(locaBytes.AsSpan(i * 4), loca[i]);
        }

        var hmtx = new byte[glyphCount * 4];
        BinaryPrimitives.WriteUInt16BigEndian(hmtx, 500);
        for (var i = 0; i < chars.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan((i + 1) * 4), (ushort)Advance(chars[i]));
            BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(((i + 1) * 4) + 2), 50);
        }

        var head = new byte[54];
        BinaryPrimitives.WriteUInt32BigEndian(head, 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(12), 0x5F0F3CF5);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18), 1000);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(38), -200);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(40), 1000);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(42), 900);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(44), (ushort)(bold ? 1 : 0));
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);

        var hhea = new byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(hhea, 0x00010000);
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(4), 800);
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(6), -200);
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(10), 1000);
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)glyphCount);

        var maxp = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(maxp, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)glyphCount);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(6), 8);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(14), 1);

        var os2 = new byte[96];
        BinaryPrimitives.WriteUInt16BigEndian(os2, 4);
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(4), (ushort)(bold ? 700 : 400));
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(8), (ushort)(restricted ? 2 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(62), (ushort)(bold ? 0x20 : 0x40));
        BinaryPrimitives.WriteInt16BigEndian(os2.AsSpan(68), 800);
        BinaryPrimitives.WriteInt16BigEndian(os2.AsSpan(70), -200);
        BinaryPrimitives.WriteInt16BigEndian(os2.AsSpan(88), 700);

        var post = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(post, 0x00030000);
        BinaryPrimitives.WriteInt16BigEndian(post.AsSpan(8), -100);
        BinaryPrimitives.WriteInt16BigEndian(post.AsSpan(10), 50);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["cmap"] = Cmap(chars),
            ["glyf"] = glyf.ToArray(),
            ["head"] = head,
            ["hhea"] = hhea,
            ["hmtx"] = hmtx,
            ["loca"] = locaBytes,
            ["maxp"] = maxp,
            ["name"] = Name(family, bold ? "Bold" : "Regular"),
            ["OS/2"] = os2,
            ["post"] = post,
        };
        return Assemble(tables);
    }

    private static byte[] Cmap(int[] chars)
    {
        // Format 4 for the BMP (one segment per character), format 12 for everything.
        var bmp = chars.Select((c, i) => (Code: c, Glyph: i + 1)).Where(e => e.Code < 0xFFFF).ToList();
        var segCount = bmp.Count + 1;
        var f4 = new byte[16 + (segCount * 8)];
        var s = f4.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(s, 4);
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], (ushort)f4.Length);
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], (ushort)(segCount * 2));
        for (var i = 0; i < segCount; i++)
        {
            var (code, glyph) = i < bmp.Count ? bmp[i] : (0xFFFF, 0);
            BinaryPrimitives.WriteUInt16BigEndian(s[(14 + (i * 2))..], (ushort)code);
            BinaryPrimitives.WriteUInt16BigEndian(s[(16 + (segCount * 2) + (i * 2))..], (ushort)code);
            BinaryPrimitives.WriteInt16BigEndian(s[(16 + (segCount * 4) + (i * 2))..], (short)(i < bmp.Count ? glyph - code : 1));
        }

        var f12 = new byte[16 + (chars.Length * 12)];
        BinaryPrimitives.WriteUInt16BigEndian(f12, 12);
        BinaryPrimitives.WriteUInt32BigEndian(f12.AsSpan(4), (uint)f12.Length);
        BinaryPrimitives.WriteUInt32BigEndian(f12.AsSpan(12), (uint)chars.Length);
        for (var i = 0; i < chars.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(f12.AsSpan(16 + (i * 12)), (uint)chars[i]);
            BinaryPrimitives.WriteUInt32BigEndian(f12.AsSpan(20 + (i * 12)), (uint)chars[i]);
            BinaryPrimitives.WriteUInt32BigEndian(f12.AsSpan(24 + (i * 12)), (uint)(i + 1));
        }

        var table = new byte[4 + 16 + f4.Length + f12.Length];
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8), 20);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(14), 10);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(16), (uint)(20 + f4.Length));
        f4.CopyTo(table, 20);
        f12.CopyTo(table, 20 + f4.Length);
        return table;
    }

    private static byte[] Name(string family, string style)
    {
        var records = new[] { (1, family), (2, style), (4, family + " " + style), (6, family.Replace(" ", "", StringComparison.Ordinal) + "-" + style) };
        var strings = records.Select(r => Encoding.BigEndianUnicode.GetBytes(r.Item2)).ToList();
        var table = new byte[6 + (records.Length * 12) + strings.Sum(s => s.Length)];
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2), (ushort)records.Length);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4), (ushort)(6 + (records.Length * 12)));
        var offset = 0;
        for (var i = 0; i < records.Length; i++)
        {
            var r = table.AsSpan(6 + (i * 12));
            BinaryPrimitives.WriteUInt16BigEndian(r, 3);
            BinaryPrimitives.WriteUInt16BigEndian(r[2..], 1);
            BinaryPrimitives.WriteUInt16BigEndian(r[4..], 0x409);
            BinaryPrimitives.WriteUInt16BigEndian(r[6..], (ushort)records[i].Item1);
            BinaryPrimitives.WriteUInt16BigEndian(r[8..], (ushort)strings[i].Length);
            BinaryPrimitives.WriteUInt16BigEndian(r[10..], (ushort)offset);
            strings[i].CopyTo(table, 6 + (records.Length * 12) + offset);
            offset += strings[i].Length;
        }

        return table;
    }

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        var header = new byte[12 + (16 * tables.Count)];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)tables.Count);
        using var body = new MemoryStream();
        var i = 0;
        foreach (var (tag, data) in tables)
        {
            var e = header.AsSpan(12 + (i++ * 16));
            Encoding.ASCII.GetBytes(tag).CopyTo(e);
            BinaryPrimitives.WriteUInt32BigEndian(e[8..], (uint)(header.Length + body.Length));
            BinaryPrimitives.WriteUInt32BigEndian(e[12..], (uint)data.Length);
            body.Write(data);
            while (body.Length % 4 != 0)
            {
                body.WriteByte(0);
            }
        }

        return header.Concat(body.ToArray()).ToArray();
    }
}
