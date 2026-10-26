using System.Text;
using PdfUtil.Conversion.Internal.Model;
using PdfUtil.Internal.Fonts;

namespace PdfUtil.Conversion.Internal.Layout;

/// <summary>A font as the layout uses it: measuring, encoding and vertical metrics (per point of font size).</summary>
internal abstract class FontHandle
{
    public abstract string Key { get; }

    public abstract double Ascent { get; }

    public abstract double Descent { get; }

    public double UnderlinePosition { get; protected init; } = -0.1;

    public double UnderlineThickness { get; protected init; } = 0.05;

    /// <summary>Width of text in points at the given size.</summary>
    public abstract double Width(string text, double size);

    /// <summary>The bytes to show the text with this font.</summary>
    public abstract byte[] Encode(string text);

    /// <summary>The text a reader extracts after <see cref="Encode"/>: characters the font cannot show become '?' or U+FFFD.</summary>
    public abstract string Shown(string text);
}

/// <summary>One of the standard 14 fonts with WinAnsi encoding (not embedded).</summary>
internal sealed class StandardFontHandle : FontHandle
{
    private readonly double _ascent;
    private readonly double _descent;

    public StandardFontHandle(string name)
    {
        Name = name;
        var (ascent, descent) = StandardFonts.VerticalMetrics(name);
        _ascent = ascent / 1000.0;
        _descent = descent / 1000.0;
        UnderlinePosition = -0.1;
        UnderlineThickness = 0.05;
    }

    public string Name { get; }

    public override string Key => Name;

    public override double Ascent => _ascent;

    public override double Descent => _descent;

    public override double Width(string text, double size)
    {
        double total = 0;
        foreach (var c in text)
        {
            total += StandardFonts.Width(Name, c);
        }

        return total * size / 1000;
    }

    public override byte[] Encode(string text)
    {
        var bytes = FontEncodings.EncodeWinAnsi(text, out _);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0)
            {
                bytes[i] = (byte)'?';
            }
        }

        return bytes;
    }

    public override string Shown(string text) => new(text.Select(c => CanEncode(c) && c != '\t' ? c : '?').ToArray());

    public static bool CanEncode(char c) => c < 128 ? c >= 32 || c is '\t' : FontEncodings.EncodeWinAnsi(c.ToString(), out var unsupported).Length == 1 && unsupported.Count == 0;
}

/// <summary>An embedded TrueType font (subset, Type0 with Identity-H).</summary>
internal sealed class TrueTypeFontHandle : FontHandle
{
    public TrueTypeFontHandle(TrueTypeFont font, string key, string family)
    {
        Embedded = new EmbeddedFont(font);
        Key = key;
        Family = family;
        UnderlinePosition = font.UnderlinePosition / (double)font.UnitsPerEm;
        UnderlineThickness = Math.Max(0.02, font.UnderlineThickness / (double)font.UnitsPerEm);
    }

    public EmbeddedFont Embedded { get; }

    /// <summary>The CSS family the font was chosen for.</summary>
    public string Family { get; }

    public override string Shown(string text) => string.Concat(text.EnumerateRunes().Select(r => Embedded.Font.Covers(r.Value) ? r.ToString() : "\uFFFD"));

    public override string Key { get; }

    public override double Ascent => Embedded.Font.Ascent / (double)Embedded.Font.UnitsPerEm;

    public override double Descent => Embedded.Font.Descent / (double)Embedded.Font.UnitsPerEm;

    public override double Width(string text, double size) => Embedded.Width(text) * size / 1000;

    public override byte[] Encode(string text) => Embedded.Encode(text, out _);
}

/// <summary>A TrueType face available to the layout, with the family it answers to.</summary>
internal sealed record FontFace(string Family, TrueTypeFont Font);

/// <summary>
/// Chooses fonts for text. The CSS family list is tried in order: a family supplied as a TrueType font (option, font
/// folder, system fonts or <c>@font-face</c>) is embedded for all its text; generic and well-known families map to the
/// standard 14 fonts, which serve WinAnsi text. Characters a font cannot show fall back, character by character, to
/// the default family and then to any loaded TrueType font that has them.
/// </summary>
internal sealed class FontProvider
{
    private readonly List<FontFace> _faces;
    private readonly List<string> _defaultFamilies;
    private readonly Dictionary<string, FontHandle> _handles = new(StringComparer.Ordinal);
    private readonly Action<string> _warn;
    private readonly HashSet<int> _missing = new();

    public FontProvider(List<FontFace> faces, string defaultFamily, Action<string> warn)
    {
        _faces = faces;
        _defaultFamilies = defaultFamily.Split(',').Select(f => f.Trim().Trim('"', '\'')).Where(f => f.Length > 0).ToList();
        _warn = warn;
    }

    public IEnumerable<FontHandle> Handles => _handles.Values;

    public int MissingCharacters { get; private set; }

    /// <summary>Splits text into runs that one font can show.</summary>
    public List<(FontHandle Font, string Text)> Segments(RunStyle style, string text)
    {
        var families = (style.FontFamily ?? "").Split(',').Select(f => f.Trim().Trim('"', '\'')).Where(f => f.Length > 0).Concat(_defaultFamilies).Append("serif").ToList();
        text = Decompose(text);
        var result = new List<(FontHandle, string)>();
        FontHandle? current = null;
        var sb = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            var font = Choose(families, style.Bold, style.Italic, rune);
            if (current is not null && font != current)
            {
                result.Add((current, sb.ToString()));
                sb.Clear();
            }

            current = font;
            sb.Append(rune.ToString());
        }

        if (current is not null && sb.Length > 0)
        {
            result.Add((current, sb.ToString()));
        }

        return result;
    }

    /// <summary>
    /// Characters no font has are replaced by their compatibility decomposition when every part of it can be shown
    /// (the ligature "ﬁ" becomes "fi", full-width letters become ordinary ones).
    /// </summary>
    private string Decompose(string text)
    {
        StringBuilder? sb = null;
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var original = rune.ToString();
            var replacement = original;
            if (!AnyFontHas(rune))
            {
                var decomposed = original.Normalize(System.Text.NormalizationForm.FormKC);
                if (decomposed != original && decomposed.EnumerateRunes().All(AnyFontHas))
                {
                    replacement = decomposed;
                }
            }

            if (replacement != original && sb is null)
            {
                sb = new StringBuilder(text, 0, index, text.Length + 8);
            }

            sb?.Append(replacement);
            index += original.Length;
        }

        return sb?.ToString() ?? text;
    }

    /// <summary>Whether some font can show every character of the text.</summary>
    public bool CanShow(string text) => text.EnumerateRunes().All(AnyFontHas);

    private bool AnyFontHas(Rune rune) => (rune.IsBmp && StandardFontHandle.CanEncode((char)rune.Value)) || _faces.Any(f => f.Font.Covers(rune.Value));

    /// <summary>The font for the vertical metrics of empty lines and markers.</summary>
    public FontHandle Primary(RunStyle style) => Segments(style, "x")[0].Font;

    private FontHandle Choose(List<string> families, bool bold, bool italic, Rune rune)
    {
        var c = rune.Value;
        var isControl = c < 32 || (c >= 0x7F && c < 0xA0);
        foreach (var family in families)
        {
            if (Face(family, bold, italic) is { } face && (isControl || face.Font.Covers(c)))
            {
                return Handle(face, bold, italic);
            }

            if (Standard(family, bold, italic) is { } standard && rune.IsBmp && (isControl || StandardFontHandle.CanEncode((char)c)))
            {
                return standard;
            }
        }

        // Any loaded font that has the character.
        foreach (var face in _faces)
        {
            if (face.Font.Covers(c))
            {
                return Handle(face, bold, italic);
            }
        }

        if (_missing.Add(c))
        {
            _warn($"No available font has the character U+{c:X4}; it is shown as a blank box or question mark. Supply a TrueType font that covers it.");
        }

        MissingCharacters++;
        return Standard(_defaultFamilies.FirstOrDefault() ?? "serif", bold, italic) ?? Standard("serif", bold, italic)!;
    }

    private FontFace? Face(string family, bool bold, bool italic)
    {
        var candidates = _faces.Where(f => f.Family.Equals(family, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates.OrderBy(f => (f.Font.IsBold == bold ? 0 : 2) + (f.Font.IsItalic == italic ? 0 : 1)).First();
    }

    private FontHandle Handle(FontFace face, bool bold, bool italic)
    {
        var key = "TT:" + face.Family + ":" + face.Font.PostScriptName + ":" + face.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!_handles.TryGetValue(key, out var handle))
        {
            handle = new TrueTypeFontHandle(face.Font, key, face.Family);
            _handles[key] = handle;
            if (bold && !face.Font.IsBold || italic && !face.Font.IsItalic)
            {
                _warn($"Font {face.Family} has no {(bold ? "bold " : "")}{(italic ? "italic " : "")}face; the regular face is emboldened or slanted instead.");
            }
        }

        return handle;
    }

    private FontHandle? Standard(string family, bool bold, bool italic)
    {
        var baseName = family.ToLowerInvariant() switch
        {
            "serif" or "times" or "times new roman" or "times-roman" or "georgia" or "cambria" or "garamond" or "book antiqua" or "palatino" or "palatino linotype" or "constantia" or "liberation serif" or "dejavu serif" or "noto serif" => "Times",
            "sans-serif" or "helvetica" or "arial" or "helvetica neue" or "verdana" or "tahoma" or "segoe ui" or "calibri" or "system-ui" or "-apple-system" or "liberation sans" or "dejavu sans" or "trebuchet ms" or "roboto" or "open sans" or "noto sans" or "calibri light" or "aptos" or "aptos display" or "arial narrow" or "century gothic" or "candara" or "corbel" or "franklin gothic" or "gill sans" or "lucida sans" or "carlito" => "Helvetica",
            "monospace" or "courier" or "courier new" or "consolas" or "menlo" or "monaco" or "lucida console" or "dejavu sans mono" or "liberation mono" or "ui-monospace" => "Courier",
            _ => null,
        };
        if (baseName is null)
        {
            return null;
        }

        var name = baseName switch
        {
            "Times" => bold && italic ? "Times-BoldItalic" : bold ? "Times-Bold" : italic ? "Times-Italic" : "Times-Roman",
            "Helvetica" => bold && italic ? "Helvetica-BoldOblique" : bold ? "Helvetica-Bold" : italic ? "Helvetica-Oblique" : "Helvetica",
            _ => bold && italic ? "Courier-BoldOblique" : bold ? "Courier-Bold" : italic ? "Courier-Oblique" : "Courier",
        };
        if (!_handles.TryGetValue(name, out var handle))
        {
            handle = new StandardFontHandle(name);
            _handles[name] = handle;
        }

        return handle;
    }

    /// <summary>Loads TrueType faces from files and data, skipping fonts that cannot be used (with a warning).</summary>
    public static List<FontFace> Load(IEnumerable<(string? Family, byte[] Data, string Source)> sources, Action<string> warn)
    {
        var faces = new List<FontFace>();
        foreach (var (family, data, source) in sources)
        {
            try
            {
                foreach (var font in TrueTypeFont.Load(data))
                {
                    if (!font.CanEmbed)
                    {
                        warn($"The license of font {font.FamilyName} ({source}) does not allow embedding; it is not used.");
                        continue;
                    }

                    faces.Add(new FontFace(family ?? font.FamilyName, font));
                }
            }
            catch (NotSupportedException ex)
            {
                warn($"Font {source} cannot be used: {ex.Message}");
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException)
            {
                warn($"Font {source} is damaged or not a TrueType font; it is not used.");
            }
        }

        return faces;
    }
}
