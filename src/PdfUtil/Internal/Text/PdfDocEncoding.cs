namespace PdfUtil.Internal.Text;

/// <summary>PDFDocEncoding (ISO 32000-1 Annex D, Table D.2).</summary>
internal static class PdfDocEncoding
{
    /// <summary>Code point for each byte. Undefined codes decode to the same code point (lenient), as viewers do.</summary>
    private static readonly char[] ToUnicode = BuildTable();

    private static readonly Dictionary<char, byte> FromUnicode = BuildReverse();

    private static char[] BuildTable()
    {
        var t = new char[256];
        for (var i = 0; i < 256; i++)
        {
            t[i] = (char)i;
        }

        // 0x18-0x1F: spacing diacritics.
        t[0x18] = '˘'; // breve
        t[0x19] = 'ˇ'; // caron
        t[0x1A] = 'ˆ'; // circumflex
        t[0x1B] = '˙'; // dot above
        t[0x1C] = '˝'; // double acute
        t[0x1D] = '˛'; // ogonek
        t[0x1E] = '˚'; // ring above
        t[0x1F] = '˜'; // small tilde

        // 0x80-0x9E: typographic characters.
        t[0x80] = '•'; // bullet
        t[0x81] = '†'; // dagger
        t[0x82] = '‡'; // double dagger
        t[0x83] = '…'; // ellipsis
        t[0x84] = '—'; // em dash
        t[0x85] = '–'; // en dash
        t[0x86] = 'ƒ'; // florin
        t[0x87] = '⁄'; // fraction slash
        t[0x88] = '‹'; // single left angle quote
        t[0x89] = '›'; // single right angle quote
        t[0x8A] = '−'; // minus
        t[0x8B] = '‰'; // per mille
        t[0x8C] = '„'; // double low-9 quote
        t[0x8D] = '“'; // left double quote
        t[0x8E] = '”'; // right double quote
        t[0x8F] = '‘'; // left single quote
        t[0x90] = '’'; // right single quote
        t[0x91] = '‚'; // single low-9 quote
        t[0x92] = '™'; // trade mark
        t[0x93] = 'ﬁ'; // fi ligature
        t[0x94] = 'ﬂ'; // fl ligature
        t[0x95] = 'Ł'; // L with stroke
        t[0x96] = 'Œ'; // OE
        t[0x97] = 'Š'; // S caron
        t[0x98] = 'Ÿ'; // Y diaeresis
        t[0x99] = 'Ž'; // Z caron
        t[0x9A] = 'ı'; // dotless i
        t[0x9B] = 'ł'; // l with stroke
        t[0x9C] = 'œ'; // oe
        t[0x9D] = 'š'; // s caron
        t[0x9E] = 'ž'; // z caron
        t[0xA0] = '€'; // euro
        return t;
    }

    private static Dictionary<char, byte> BuildReverse()
    {
        var d = new Dictionary<char, byte>(256);
        for (var i = 0; i < 256; i++)
        {
            d.TryAdd(ToUnicode[i], (byte)i);
        }

        return d;
    }

    public static char Decode(byte b) => ToUnicode[b];

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        return string.Create(bytes.Length, bytes.ToArray(), static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                span[i] = ToUnicode[src[i]];
            }
        });
    }

    /// <summary>Encodes <paramref name="text"/>; returns false if any character has no PDFDocEncoding representation.</summary>
    public static bool TryEncode(string text, out byte[] bytes)
    {
        bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            if (!FromUnicode.TryGetValue(text[i], out var b))
            {
                bytes = Array.Empty<byte>();
                return false;
            }

            bytes[i] = b;
        }

        return true;
    }

    public static bool CanEncode(string text)
    {
        foreach (var c in text)
        {
            if (!FromUnicode.ContainsKey(c))
            {
                return false;
            }
        }

        return true;
    }
}
