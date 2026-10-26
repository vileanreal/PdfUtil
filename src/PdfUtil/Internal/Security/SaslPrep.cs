using System.Globalization;
using System.Text;

namespace PdfUtil.Internal.Security;

/// <summary>
/// SASLprep profile of stringprep (RFC 4013 over RFC 3454), as required for AES-256 passwords
/// (ISO 32000-2 §7.6.4.3.3). Unassigned code points are allowed ("query" semantics), so passwords containing
/// characters added to Unicode after version 3.2 (for example emoji) are accepted.
/// </summary>
internal static class SaslPrep
{
    /// <summary>Applies SASLprep. Returns false when the string contains prohibited characters or violates the bidi rules.</summary>
    public static bool TryPrepare(string input, out string output)
    {
        output = string.Empty;

        // Step 1: mapping (RFC 4013 §2.1). Non-ASCII spaces (C.1.2) map to U+0020; B.1 maps to nothing.
        var mapped = new StringBuilder(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            var cp = NextCodePoint(input, ref i);
            if (cp < 0)
            {
                return false; // unpaired surrogate
            }

            if (StringPrepTables.InTable(StringPrepTables.C1_2_NonAsciiSpace, cp))
            {
                mapped.Append(' ');
            }
            else if (!StringPrepTables.InTable(StringPrepTables.B1_MappedToNothing, cp))
            {
                mapped.Append(char.ConvertFromUtf32(cp));
            }
        }

        // Step 2: normalisation form KC.
        string normalized;
        try
        {
            normalized = mapped.ToString().Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            return false;
        }

        // Step 3: prohibited output, and step 4: bidi (RFC 3454 §6).
        var hasRandAL = false;
        var hasL = false;
        var firstRandAL = false;
        var lastRandAL = false;
        var index = 0;
        for (var i = 0; i < normalized.Length; i++)
        {
            var cp = NextCodePoint(normalized, ref i);
            if (cp < 0 || StringPrepTables.IsProhibited(cp))
            {
                return false;
            }

            var isRandAL = StringPrepTables.InTable(StringPrepTables.D1_RandAL, cp);
            var isL = !isRandAL && StringPrepTables.IsLCat(cp);
            hasRandAL |= isRandAL;
            hasL |= isL;
            if (index == 0)
            {
                firstRandAL = isRandAL;
            }

            lastRandAL = isRandAL;
            index++;
        }

        if (hasRandAL && (hasL || !firstRandAL || !lastRandAL))
        {
            return false;
        }

        output = normalized;
        return true;
    }

    private static int NextCodePoint(string s, ref int i)
    {
        var c = s[i];
        if (char.IsHighSurrogate(c))
        {
            if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                var cp = char.ConvertToUtf32(c, s[i + 1]);
                i++;
                return cp;
            }

            return -1;
        }

        return char.IsLowSurrogate(c) ? -1 : c;
    }
}

/// <summary>Tables from RFC 3454 (stringprep) used by SASLprep, as sorted inclusive code point ranges.</summary>
internal static class StringPrepTables
{
    // B.1 Commonly mapped to nothing.
    internal static readonly int[] B1_MappedToNothing =
    {
        0x00AD, 0x00AD, 0x034F, 0x034F, 0x1806, 0x1806, 0x180B, 0x180D, 0x200B, 0x200D, 0x2060, 0x2060,
        0xFE00, 0xFE0F, 0xFEFF, 0xFEFF,
    };

    // C.1.2 Non-ASCII space characters.
    internal static readonly int[] C1_2_NonAsciiSpace =
    {
        0x00A0, 0x00A0, 0x1680, 0x1680, 0x2000, 0x200B, 0x202F, 0x202F, 0x205F, 0x205F, 0x3000, 0x3000,
    };

    // C.2.1 ASCII control characters.
    internal static readonly int[] C2_1_AsciiControl = { 0x0000, 0x001F, 0x007F, 0x007F };

    // C.2.2 Non-ASCII control characters.
    internal static readonly int[] C2_2_NonAsciiControl =
    {
        0x0080, 0x009F, 0x06DD, 0x06DD, 0x070F, 0x070F, 0x180E, 0x180E, 0x200C, 0x200D, 0x2028, 0x2029,
        0x2060, 0x2063, 0x206A, 0x206F, 0xFEFF, 0xFEFF, 0xFFF9, 0xFFFC, 0x1D173, 0x1D17A,
    };

    // C.3 Private use.
    internal static readonly int[] C3_PrivateUse = { 0xE000, 0xF8FF, 0xF0000, 0xFFFFD, 0x100000, 0x10FFFD };

    // C.4 Non-character code points.
    internal static readonly int[] C4_NonCharacter =
    {
        0xFDD0, 0xFDEF, 0xFFFE, 0xFFFF, 0x1FFFE, 0x1FFFF, 0x2FFFE, 0x2FFFF, 0x3FFFE, 0x3FFFF, 0x4FFFE, 0x4FFFF,
        0x5FFFE, 0x5FFFF, 0x6FFFE, 0x6FFFF, 0x7FFFE, 0x7FFFF, 0x8FFFE, 0x8FFFF, 0x9FFFE, 0x9FFFF, 0xAFFFE, 0xAFFFF,
        0xBFFFE, 0xBFFFF, 0xCFFFE, 0xCFFFF, 0xDFFFE, 0xDFFFF, 0xEFFFE, 0xEFFFF, 0xFFFFE, 0xFFFFF, 0x10FFFE, 0x10FFFF,
    };

    // C.5 Surrogate codes.
    internal static readonly int[] C5_Surrogate = { 0xD800, 0xDFFF };

    // C.6 Inappropriate for plain text.
    internal static readonly int[] C6_InappropriatePlainText = { 0xFFF9, 0xFFFD };

    // C.7 Inappropriate for canonical representation.
    internal static readonly int[] C7_InappropriateCanonical = { 0x2FF0, 0x2FFB };

    // C.8 Change display properties or are deprecated.
    internal static readonly int[] C8_DisplayProperties =
    {
        0x0340, 0x0341, 0x200E, 0x200F, 0x202A, 0x202E, 0x206A, 0x206F,
    };

    // C.9 Tagging characters.
    internal static readonly int[] C9_Tagging = { 0xE0001, 0xE0001, 0xE0020, 0xE007F };

    // D.1 Characters with bidirectional property "R" or "AL".
    internal static readonly int[] D1_RandAL =
    {
        0x05BE, 0x05BE, 0x05C0, 0x05C0, 0x05C3, 0x05C3, 0x05D0, 0x05EA, 0x05F0, 0x05F4, 0x061B, 0x061B,
        0x061F, 0x061F, 0x0621, 0x063A, 0x0640, 0x064A, 0x066D, 0x066F, 0x0671, 0x06D5, 0x06DD, 0x06DD,
        0x06E5, 0x06E6, 0x06FA, 0x06FE, 0x0700, 0x070D, 0x0710, 0x0710, 0x0712, 0x072C, 0x0780, 0x07A5,
        0x07B1, 0x07B1, 0x200F, 0x200F, 0xFB1D, 0xFB1D, 0xFB1F, 0xFB28, 0xFB2A, 0xFB36, 0xFB38, 0xFB3C,
        0xFB3E, 0xFB3E, 0xFB40, 0xFB41, 0xFB43, 0xFB44, 0xFB46, 0xFBB1, 0xFBD3, 0xFD3D, 0xFD50, 0xFD8F,
        0xFD92, 0xFDC7, 0xFDF0, 0xFDFC, 0xFE70, 0xFE74, 0xFE76, 0xFEFC,
    };

    private static readonly int[][] Prohibited =
    {
        C1_2_NonAsciiSpace, C2_1_AsciiControl, C2_2_NonAsciiControl, C3_PrivateUse, C4_NonCharacter,
        C5_Surrogate, C6_InappropriatePlainText, C7_InappropriateCanonical, C8_DisplayProperties, C9_Tagging,
    };

    public static bool IsProhibited(int cp)
    {
        foreach (var table in Prohibited)
        {
            if (InTable(table, cp))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// D.2 (bidirectional property "L"). The literal D.2 table has several hundred ranges; strong left-to-right
    /// characters are exactly the letters, spacing marks and letter numbers outside the right-to-left scripts, so
    /// the classification is derived from the BCL's Unicode general categories for those classes.
    /// </summary>
    public static bool IsLCat(int cp)
    {
        if (InTable(D1_RandAL, cp))
        {
            return false;
        }

        // Right-to-left and Arabic-letter blocks contain no L characters.
        if (cp is (>= 0x0590 and <= 0x08FF) or (>= 0xFB1D and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF) or (>= 0x10800 and <= 0x10FFF) or (>= 0x1E800 and <= 0x1EFFF))
        {
            return false;
        }

        var category = CharUnicodeInfo.GetUnicodeCategory(cp);
        return category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.LetterNumber;
    }

    public static bool InTable(int[] ranges, int cp)
    {
        int lo = 0, hi = (ranges.Length / 2) - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (cp < ranges[mid * 2])
            {
                hi = mid - 1;
            }
            else if (cp > ranges[(mid * 2) + 1])
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }
}
