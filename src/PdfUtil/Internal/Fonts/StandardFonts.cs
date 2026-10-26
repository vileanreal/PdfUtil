using System.Globalization;
using System.Text;

namespace PdfUtil.Internal.Fonts;

/// <summary>
/// Metrics of the standard 14 fonts, for fonts without <c>/Widths</c> and for text stamps. Widths of printable ASCII
/// come from the Adobe font metrics; other Latin characters use the width of their base letter (é as e), which is how
/// these fonts are designed, and anything else a typical width.
/// </summary>
internal static class StandardFonts
{
    // Widths of the characters 32..126, in 1/1000 em.
    private const string HelveticaAscii = "278 278 355 556 556 889 667 191 333 333 389 584 278 333 278 278 556 556 556 556 556 556 556 556 556 556 278 278 584 584 584 556 1015 667 667 722 722 667 611 778 722 278 500 667 556 833 722 778 667 778 722 667 611 722 667 944 667 667 611 278 278 278 469 556 333 556 556 500 556 556 278 556 556 222 222 500 222 833 556 556 556 556 333 500 278 556 500 722 500 500 500 334 260 334 584";
    private const string HelveticaBoldAscii = "278 333 474 556 556 889 722 238 333 333 389 584 278 333 278 278 556 556 556 556 556 556 556 556 556 556 333 333 584 584 584 611 975 722 722 722 722 667 611 778 722 278 556 722 611 833 722 778 667 778 722 667 611 722 667 944 667 667 611 333 278 333 584 556 333 556 611 556 611 556 333 611 611 278 278 556 278 889 611 611 611 611 389 556 333 611 556 778 556 556 500 389 280 389 584";
    private const string TimesRomanAscii = "250 333 408 500 500 833 778 180 333 333 500 564 250 333 250 278 500 500 500 500 500 500 500 500 500 500 278 278 564 564 564 444 921 722 667 667 722 611 556 722 722 333 389 722 611 889 722 722 556 722 667 556 611 722 722 944 722 722 611 333 278 333 469 500 333 444 500 444 500 444 333 500 500 278 278 500 278 778 500 500 500 500 333 389 278 500 500 722 500 500 444 480 200 480 541";
    private const string TimesBoldAscii = "250 333 555 500 500 1000 833 278 333 333 500 570 250 333 250 278 500 500 500 500 500 500 500 500 500 500 333 333 570 570 570 500 930 722 667 722 722 667 611 778 778 389 500 778 667 944 722 778 611 778 722 556 667 722 722 1000 722 722 667 333 278 333 581 500 333 500 556 444 556 444 333 500 556 278 333 556 278 833 556 500 556 556 444 389 333 556 500 722 500 500 444 394 220 394 520";
    private const string TimesItalicAscii = "250 333 420 500 500 833 778 214 333 333 500 675 250 333 250 278 500 500 500 500 500 500 500 500 500 500 333 333 675 675 675 500 920 611 611 667 722 611 611 722 722 333 444 667 556 833 667 722 611 722 611 500 556 722 611 833 611 556 556 389 278 389 422 500 333 500 500 444 500 444 278 500 500 278 278 444 278 722 500 500 500 500 389 389 278 500 444 667 444 444 389 400 275 400 541";

    private static readonly Dictionary<string, (int[] Ascii, int Default, int Ascent, int Descent, bool Bold, bool Italic)> Fonts = new(StringComparer.Ordinal)
    {
        ["Helvetica"] = (Parse(HelveticaAscii), 556, 718, -207, false, false),
        ["Helvetica-Oblique"] = (Parse(HelveticaAscii), 556, 718, -207, false, true),
        ["Helvetica-Bold"] = (Parse(HelveticaBoldAscii), 611, 718, -207, true, false),
        ["Helvetica-BoldOblique"] = (Parse(HelveticaBoldAscii), 611, 718, -207, true, true),
        ["Times-Roman"] = (Parse(TimesRomanAscii), 500, 683, -217, false, false),
        ["Times-Italic"] = (Parse(TimesItalicAscii), 500, 683, -217, false, true),
        ["Times-Bold"] = (Parse(TimesBoldAscii), 500, 676, -205, true, false),
        ["Times-BoldItalic"] = (Parse(TimesBoldAscii), 500, 699, -205, true, true),
        ["Courier"] = (Enumerable.Repeat(600, 95).ToArray(), 600, 629, -157, false, false),
        ["Courier-Oblique"] = (Enumerable.Repeat(600, 95).ToArray(), 600, 629, -157, false, true),
        ["Courier-Bold"] = (Enumerable.Repeat(600, 95).ToArray(), 600, 629, -157, true, false),
        ["Courier-BoldOblique"] = (Enumerable.Repeat(600, 95).ToArray(), 600, 629, -157, true, true),
        ["Symbol"] = (Enumerable.Repeat(600, 95).ToArray(), 600, 1010, -293, false, false),
        ["ZapfDingbats"] = (Enumerable.Repeat(800, 95).ToArray(), 800, 820, -143, false, false),
    };

    private static int[] Parse(string widths) => widths.Split(' ').Select(w => int.Parse(w, CultureInfo.InvariantCulture)).ToArray();

    /// <summary>Maps a font name (including common aliases such as Arial and TimesNewRoman, with styles) to a standard 14 font name, or null.</summary>
    public static string? Resolve(string? baseFont)
    {
        if (baseFont is null)
        {
            return null;
        }

        var name = baseFont.Length > 7 && baseFont[6] == '+' ? baseFont[7..] : baseFont;
        if (Fonts.ContainsKey(name))
        {
            return name;
        }

        var lower = name.ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal).Replace("mt", "", StringComparison.Ordinal).Replace("ps", "", StringComparison.Ordinal);
        var bold = lower.Contains("bold") || lower.Contains("black") || lower.Contains("heavy");
        var italic = lower.Contains("italic") || lower.Contains("oblique");
        if (lower.StartsWith("arial") || lower.StartsWith("helvetica"))
        {
            return "Helvetica" + (bold ? italic ? "-BoldOblique" : "-Bold" : italic ? "-Oblique" : "");
        }

        if (lower.StartsWith("times"))
        {
            return "Times-" + (bold ? italic ? "BoldItalic" : "Bold" : italic ? "Italic" : "Roman");
        }

        if (lower.StartsWith("courier"))
        {
            return "Courier" + (bold ? italic ? "-BoldOblique" : "-Bold" : italic ? "-Oblique" : "");
        }

        return lower.StartsWith("symbol") ? "Symbol" : lower.StartsWith("zapfdingbats") ? "ZapfDingbats" : null;
    }

    /// <summary>The width of a character in 1/1000 em.</summary>
    public static int Width(string font, char c)
    {
        var metrics = Fonts[font];
        if (c >= 32 && c <= 126)
        {
            return metrics.Ascii[c - 32];
        }

        // Accented letters: the width of the base letter.
        var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        if (decomposed.Length > 1 && decomposed[0] >= 32 && decomposed[0] <= 126)
        {
            return metrics.Ascii[decomposed[0] - 32];
        }

        return c switch
        {
            ' ' => metrics.Ascii[0],
            '–' or '‐' => metrics.Default,
            '—' or '‰' or '™' or 'Œ' or 'Æ' => 1000,
            '’' or '‘' or '‚' => metrics.Ascii['\'' - 32] + 30,
            '“' or '”' or '„' => metrics.Ascii['"' - 32],
            '•' => 350,
            _ => metrics.Default,
        };
    }

    public static (int Ascent, int Descent) VerticalMetrics(string font) => (Fonts[font].Ascent, Fonts[font].Descent);

    public static bool IsBold(string font) => Fonts[font].Bold;

    public static bool IsItalic(string font) => Fonts[font].Italic;
}
