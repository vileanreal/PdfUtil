using System.Globalization;

namespace PdfUtil.Internal.Fonts;

/// <summary>The base encodings of simple fonts (ISO 32000-1 Annex D), as byte → Unicode tables.</summary>
internal static class FontEncodings
{
    public static readonly char[] Standard = BuildStandard();

    public static readonly char[] WinAnsi = BuildWinAnsi();

    public static readonly char[] MacRoman = BuildMacRoman();

    public static readonly char[] Symbol = BuildSymbol();

    private static readonly Dictionary<char, byte> WinAnsiReverse = BuildReverse(WinAnsi);

    /// <summary>The table for an encoding name, or null.</summary>
    public static char[]? ByName(string? name) => name switch
    {
        "StandardEncoding" => Standard,
        "WinAnsiEncoding" => WinAnsi,
        "MacRomanEncoding" => MacRoman,
        "MacExpertEncoding" => Standard,
        "PDFDocEncoding" => BuildPdfDoc(),
        _ => null,
    };

    /// <summary>Encodes text as WinAnsi bytes; returns the characters that cannot be encoded.</summary>
    public static byte[] EncodeWinAnsi(string text, out List<char> unsupported)
    {
        unsupported = new List<char>();
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            if (WinAnsiReverse.TryGetValue(text[i], out var b))
            {
                bytes[i] = b;
            }
            else
            {
                unsupported.Add(text[i]);
            }
        }

        return bytes;
    }

    private static Dictionary<char, byte> BuildReverse(char[] table)
    {
        var result = new Dictionary<char, byte>();
        for (var i = 255; i >= 32; i--)
        {
            if (table[i] != '\0' && !(table[i] == '•' && i != 0x95))
            {
                result[table[i]] = (byte)i;
            }
        }

        return result;
    }

    private static char[] Ascii()
    {
        var t = new char[256];
        for (var i = 32; i < 127; i++)
        {
            t[i] = (char)i;
        }

        return t;
    }

    private static void Set(char[] t, int start, string chars)
    {
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] != '\0')
            {
                t[start + i] = chars[i];
            }
        }
    }

    private static char[] BuildStandard()
    {
        var t = Ascii();
        t[0x27] = '’';
        t[0x60] = '‘';
        Set(t, 0xA1, "¡¢£⁄¥ƒ§¤'“«‹›ﬁﬂ");
        Set(t, 0xB1, "–†‡·\0¶•‚„”»…‰\0¿");
        Set(t, 0xC1, "`´ˆ˜¯˘˙¨\0˚¸\0˝˛ˇ—");
        t[0xE1] = 'Æ';
        t[0xE3] = 'ª';
        Set(t, 0xE8, "ŁØŒº");
        t[0xF1] = 'æ';
        t[0xF5] = 'ı';
        Set(t, 0xF8, "łøœß");
        return t;
    }

    private static char[] BuildWinAnsi()
    {
        var t = Ascii();
        for (var i = 0xA0; i <= 0xFF; i++)
        {
            t[i] = (char)i;
        }

        Set(t, 0x80, "€•‚ƒ„…†‡ˆ‰Š‹Œ•Ž••‘’“”•–—˜™š›œ•žŸ");
        t[0x7F] = '•';
        return t;
    }

    private static char[] BuildMacRoman()
    {
        var t = Ascii();
        Set(t, 0x80, "ÄÅÇÉÑÖÜáàâäãåçéèêëíìîïñóòôöõúùûü†°¢£§•¶ß®©™´¨≠ÆØ∞±≤≥¥µ∂∑∏π∫ªºΩæø¿¡¬√ƒ≈∆«»… ÀÃÕŒœ–—“”‘’÷◊ÿŸ⁄¤‹›ﬁﬂ‡·‚„‰ÂÊÁËÈÍÎÏÌÓÔ\0ÒÚÛÙıˆ˜¯˘˙˚¸˝˛ˇ");
        return t;
    }

    private static char[] BuildSymbol()
    {
        var t = Ascii();
        Set(t, 0x22, "∀#∃%&∋()∗+,−./");
        Set(t, 0x40, "≅ΑΒΧΔΕΦΓΗΙϑΚΛΜΝΟΠΘΡΣΤΥςΩΞΨΖ[∴]⊥_‾αβχδεφγηιϕκλμνοπθρστυϖωξψζ{|}∼");
        Set(t, 0xA1, "ϒ′≤⁄∞ƒ♣♦♥♠↔←↑→↓°±″≥×∝∂•÷≠≡≈…");
        Set(t, 0xC4, "⊗⊕∅∩∪⊃⊇⊄⊂⊆∈∉∠∇");
        Set(t, 0xD5, "∏√⋅¬∧∨");
        t[0xE5] = '∑';
        t[0xF2] = '∫';
        return t;
    }

    private static char[] BuildPdfDoc()
    {
        var t = new char[256];
        for (var i = 0; i < 256; i++)
        {
            t[i] = Text.PdfDocEncoding.Decode((byte)i);
        }

        return t;
    }
}

/// <summary>
/// Glyph name → Unicode (a subset of the Adobe Glyph List, plus the algorithmic <c>uniXXXX</c>, <c>uXXXX</c>,
/// <c>afii</c> Cyrillic, ligature (<c>f_i</c>) and suffix (<c>a.sc</c>) rules).
/// </summary>
internal static class GlyphNames
{
    private static readonly Dictionary<string, string> Table = Build();

    public static string? ToUnicode(string name)
    {
        if (Table.TryGetValue(name, out var direct))
        {
            return direct;
        }

        var dot = name.IndexOf('.');
        if (dot > 0)
        {
            return ToUnicode(name[..dot]);
        }

        if (name.Contains('_'))
        {
            var parts = name.Split('_').Select(ToUnicode).ToList();
            return parts.All(p => p is not null) ? string.Concat(parts) : null;
        }

        if (name.StartsWith("uni", StringComparison.Ordinal) && name.Length >= 7 && (name.Length - 3) % 4 == 0)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 3; i < name.Length; i += 4)
            {
                if (!int.TryParse(name.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cu) || cu is >= 0xD800 and <= 0xDFFF)
                {
                    return null;
                }

                sb.Append((char)cu);
            }

            return sb.ToString();
        }

        if (name.Length is >= 5 and <= 7 && name[0] == 'u' && int.TryParse(name.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp) && cp <= 0x10FFFF && cp is < 0xD800 or > 0xDFFF)
        {
            return char.ConvertFromUtf32(cp);
        }

        if (name.StartsWith("afii", StringComparison.Ordinal) && int.TryParse(name.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var afii))
        {
            return afii switch
            {
                >= 10017 and <= 10022 => ((char)(0x0410 + afii - 10017)).ToString(),
                10023 => "Ё",
                >= 10024 and <= 10049 => ((char)(0x0416 + afii - 10024)).ToString(),
                >= 10065 and <= 10070 => ((char)(0x0430 + afii - 10065)).ToString(),
                10071 => "ё",
                >= 10072 and <= 10097 => ((char)(0x0436 + afii - 10072)).ToString(),
                _ => null,
            };
        }

        return null;
    }

    private static Dictionary<string, string> Build()
    {
        var t = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var c = 'A'; c <= 'Z'; c++)
        {
            t[c.ToString()] = c.ToString();
            t[char.ToLowerInvariant(c).ToString()] = char.ToLowerInvariant(c).ToString();
        }

        string[] digits = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };
        for (var i = 0; i < 10; i++)
        {
            t[digits[i]] = ((char)('0' + i)).ToString();
        }

        const string Pairs =
            "space 0020 exclam 0021 quotedbl 0022 numbersign 0023 dollar 0024 percent 0025 ampersand 0026 quotesingle 0027 quoteright 2019 " +
            "parenleft 0028 parenright 0029 asterisk 002A plus 002B comma 002C hyphen 002D period 002E slash 002F colon 003A semicolon 003B " +
            "less 003C equal 003D greater 003E question 003F at 0040 bracketleft 005B backslash 005C bracketright 005D asciicircum 005E " +
            "underscore 005F grave 0060 quoteleft 2018 braceleft 007B bar 007C braceright 007D asciitilde 007E exclamdown 00A1 cent 00A2 " +
            "sterling 00A3 currency 00A4 yen 00A5 brokenbar 00A6 section 00A7 dieresis 00A8 copyright 00A9 ordfeminine 00AA guillemotleft 00AB " +
            "logicalnot 00AC registered 00AE macron 00AF degree 00B0 plusminus 00B1 twosuperior 00B2 threesuperior 00B3 acute 00B4 mu 00B5 " +
            "paragraph 00B6 periodcentered 00B7 cedilla 00B8 onesuperior 00B9 ordmasculine 00BA guillemotright 00BB onequarter 00BC onehalf 00BD " +
            "threequarters 00BE questiondown 00BF Agrave 00C0 Aacute 00C1 Acircumflex 00C2 Atilde 00C3 Adieresis 00C4 Aring 00C5 AE 00C6 " +
            "Ccedilla 00C7 Egrave 00C8 Eacute 00C9 Ecircumflex 00CA Edieresis 00CB Igrave 00CC Iacute 00CD Icircumflex 00CE Idieresis 00CF " +
            "Eth 00D0 Ntilde 00D1 Ograve 00D2 Oacute 00D3 Ocircumflex 00D4 Otilde 00D5 Odieresis 00D6 multiply 00D7 Oslash 00D8 Ugrave 00D9 " +
            "Uacute 00DA Ucircumflex 00DB Udieresis 00DC Yacute 00DD Thorn 00DE germandbls 00DF agrave 00E0 aacute 00E1 acircumflex 00E2 " +
            "atilde 00E3 adieresis 00E4 aring 00E5 ae 00E6 ccedilla 00E7 egrave 00E8 eacute 00E9 ecircumflex 00EA edieresis 00EB igrave 00EC " +
            "iacute 00ED icircumflex 00EE idieresis 00EF eth 00F0 ntilde 00F1 ograve 00F2 oacute 00F3 ocircumflex 00F4 otilde 00F5 odieresis 00F6 " +
            "divide 00F7 oslash 00F8 ugrave 00F9 uacute 00FA ucircumflex 00FB udieresis 00FC yacute 00FD thorn 00FE ydieresis 00FF " +
            "Euro 20AC quotesinglbase 201A florin 0192 quotedblbase 201E ellipsis 2026 dagger 2020 daggerdbl 2021 circumflex 02C6 " +
            "perthousand 2030 Scaron 0160 guilsinglleft 2039 OE 0152 Zcaron 017D quotedblleft 201C quotedblright 201D bullet 2022 endash 2013 " +
            "emdash 2014 tilde 02DC trademark 2122 scaron 0161 guilsinglright 203A oe 0153 zcaron 017E Ydieresis 0178 fi FB01 fl FB02 ff FB00 " +
            "ffi FB03 ffl FB04 dotlessi 0131 Lslash 0141 lslash 0142 ogonek 02DB caron 02C7 breve 02D8 dotaccent 02D9 hungarumlaut 02DD ring 02DA " +
            "fraction 2044 minus 2212 nbspace 00A0 sfthyphen 00AD Amacron 0100 amacron 0101 Abreve 0102 abreve 0103 Aogonek 0104 aogonek 0105 " +
            "Cacute 0106 cacute 0107 Ccaron 010C ccaron 010D Dcaron 010E dcaron 010F Dcroat 0110 dcroat 0111 Emacron 0112 emacron 0113 " +
            "Edotaccent 0116 edotaccent 0117 Eogonek 0118 eogonek 0119 Ecaron 011A ecaron 011B Gbreve 011E gbreve 011F Idotaccent 0130 " +
            "Imacron 012A imacron 012B Iogonek 012E iogonek 012F Lacute 0139 lacute 013A Lcaron 013D lcaron 013E Nacute 0143 nacute 0144 " +
            "Ncaron 0147 ncaron 0148 Omacron 014C omacron 014D Ohungarumlaut 0150 ohungarumlaut 0151 Racute 0154 racute 0155 Rcaron 0158 " +
            "rcaron 0159 Sacute 015A sacute 015B Scedilla 015E scedilla 015F Tcaron 0164 tcaron 0165 Uring 016E uring 016F Uhungarumlaut 0170 " +
            "uhungarumlaut 0171 Umacron 016A umacron 016B Uogonek 0172 uogonek 0173 Zacute 0179 zacute 017A Zdotaccent 017B zdotaccent 017C " +
            "Alpha 0391 Beta 0392 Gamma 0393 Delta 0394 Epsilon 0395 Zeta 0396 Eta 0397 Theta 0398 Iota 0399 Kappa 039A Lambda 039B Mu 039C " +
            "Nu 039D Xi 039E Omicron 039F Pi 03A0 Rho 03A1 Sigma 03A3 Tau 03A4 Upsilon 03A5 Phi 03A6 Chi 03A7 Psi 03A8 Omega 03A9 " +
            "alpha 03B1 beta 03B2 gamma 03B3 delta 03B4 epsilon 03B5 zeta 03B6 eta 03B7 theta 03B8 iota 03B9 kappa 03BA lambda 03BB " +
            "nu 03BD xi 03BE omicron 03BF pi 03C0 rho 03C1 sigma1 03C2 sigma 03C3 tau 03C4 upsilon 03C5 phi 03C6 chi 03C7 psi 03C8 omega 03C9 " +
            "theta1 03D1 phi1 03D5 omega1 03D6 Upsilon1 03D2 notequal 2260 lessequal 2264 greaterequal 2265 infinity 221E partialdiff 2202 " +
            "summation 2211 product 220F radical 221A integral 222B approxequal 2248 lozenge 25CA universal 2200 existential 2203 suchthat 220B " +
            "asteriskmath 2217 congruent 2245 therefore 2234 perpendicular 22A5 similar 223C minute 2032 second 2033 club 2663 diamond 2666 " +
            "heart 2665 spade 2660 arrowboth 2194 arrowleft 2190 arrowup 2191 arrowright 2192 arrowdown 2193 proportional 221D bullet 2022 " +
            "equivalence 2261 circlemultiply 2297 circleplus 2295 emptyset 2205 intersection 2229 union 222A propersuperset 2283 " +
            "reflexsuperset 2287 notsubset 2284 propersubset 2282 reflexsubset 2286 element 2208 notelement 2209 angle 2220 gradient 2207 " +
            "dotmath 22C5 logicaland 2227 logicalor 2228 radicalex 203E Ohorn 01A0 ohorn 01A1 Uhorn 01AF uhorn 01B0 dong 20AB " +
            "Gcommaaccent 0122 gcommaaccent 0123 Kcommaaccent 0136 kcommaaccent 0137 Lcommaaccent 013B lcommaaccent 013C " +
            "Ncommaaccent 0145 ncommaaccent 0146 Rcommaaccent 0156 rcommaaccent 0157 Scommaaccent 0218 scommaaccent 0219 " +
            "Tcommaaccent 021A tcommaaccent 021B Tcedilla 0162 tcedilla 0163 commaaccent 0326 onesuperior 00B9 Eng 014A eng 014B " +
            "napostrophe 0149 kgreenlandic 0138 Ldot 013F ldot 0140 Hbar 0126 hbar 0127 Tbar 0166 tbar 0167 IJ 0132 ij 0133 " +
            "Gcircumflex 011C gcircumflex 011D Hcircumflex 0124 hcircumflex 0125 Jcircumflex 0134 jcircumflex 0135 Ccircumflex 0108 " +
            "ccircumflex 0109 Scircumflex 015C scircumflex 015D Ubreve 016C ubreve 016D Wcircumflex 0174 wcircumflex 0175 Ycircumflex 0176 " +
            "ycircumflex 0177 Cdotaccent 010A cdotaccent 010B Gdotaccent 0120 gdotaccent 0121 Itilde 0128 itilde 0129 Utilde 0168 " +
            "utilde 0169 Ebreve 0114 ebreve 0115 Ibreve 012C ibreve 012D Obreve 014E obreve 014F";
        var parts = Pairs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            t[parts[i]] = ((char)int.Parse(parts[i + 1], NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString();
        }

        return t;
    }
}
