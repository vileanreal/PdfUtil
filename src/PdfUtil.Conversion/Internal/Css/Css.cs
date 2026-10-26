using System.Globalization;
using System.Text;
using PdfUtil.Conversion.Internal.Html;
using PdfUtil.Conversion.Internal.Model;

namespace PdfUtil.Conversion.Internal.Css;

/// <summary>A declaration: property, value and whether it is <c>!important</c>.</summary>
internal readonly record struct Declaration(string Property, string Value, bool Important);

/// <summary>A style rule: a selector and its declarations, with its position in the cascade.</summary>
internal sealed record StyleRule(Selector Selector, List<Declaration> Declarations, int Origin, int Order);

/// <summary>An <c>@font-face</c> rule: the family it defines, its style, and its sources.</summary>
internal sealed record FontFaceRule(string Family, bool Bold, bool Italic, List<string> Sources);

/// <summary>A parsed style sheet.</summary>
internal sealed class StyleSheet
{
    public List<StyleRule> Rules { get; } = new();

    public List<Declaration> PageDeclarations { get; } = new();

    public List<FontFaceRule> FontFaces { get; } = new();

    public List<string> Imports { get; } = new();
}

/// <summary>
/// Parses CSS: style rules, <c>@media</c> (print and all are used, screen-only and other media are skipped),
/// <c>@page</c> (size and margins), <c>@font-face</c> and <c>@import</c>. Other at-rules are skipped with a warning.
/// </summary>
internal static class CssParser
{
    public static StyleSheet Parse(string css, int origin, ref int order, Action<string> warn)
    {
        var sheet = new StyleSheet();
        Parse(StripComments(css), origin, ref order, warn, sheet, 0);
        return sheet;
    }

    private static string StripComments(string css)
    {
        var sb = new StringBuilder(css.Length);
        var i = 0;
        while (i < css.Length)
        {
            if (i + 1 < css.Length && css[i] == '/' && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? css.Length : end + 2;
                sb.Append(' ');
                continue;
            }

            if (css[i] is '"' or '\'')
            {
                var quote = css[i];
                var end = i + 1;
                while (end < css.Length && css[end] != quote && css[end] != '\n')
                {
                    end += css[end] == '\\' ? 2 : 1;
                }

                end = Math.Min(end + 1, css.Length);
                sb.Append(css, i, end - i);
                i = end;
                continue;
            }

            sb.Append(css[i]);
            i++;
        }

        return sb.ToString();
    }

    private static void Parse(string css, int origin, ref int order, Action<string> warn, StyleSheet sheet, int depth)
    {
        var i = 0;
        while (i < css.Length)
        {
            while (i < css.Length && (char.IsWhiteSpace(css[i]) || css[i] is ';' or '}'))
            {
                i++;
            }

            if (i >= css.Length)
            {
                break;
            }

            if (css[i] == '@')
            {
                var nameEnd = i + 1;
                while (nameEnd < css.Length && (char.IsAsciiLetterOrDigit(css[nameEnd]) || css[nameEnd] == '-'))
                {
                    nameEnd++;
                }

                var name = css[(i + 1)..nameEnd].ToLowerInvariant();
                var semicolon = IndexOutsideBlocks(css, ';', nameEnd);
                var brace = IndexOutsideBlocks(css, '{', nameEnd);
                if (brace < 0 || (semicolon >= 0 && semicolon < brace))
                {
                    // Statement at-rule: @import, @charset, @namespace.
                    var end = semicolon < 0 ? css.Length : semicolon;
                    var prelude = css[nameEnd..end].Trim();
                    if (name == "import" && Url(prelude) is { } url)
                    {
                        var media = prelude[(prelude.IndexOf(')', StringComparison.Ordinal) + 1)..].Trim();
                        if (MediaApplies(media.Length == 0 ? "all" : media.Trim('"', '\'')))
                        {
                            sheet.Imports.Add(url);
                        }
                    }
                    else if (name is not ("charset" or "namespace"))
                    {
                        warn($"The CSS at-rule @{name} is not supported and was ignored.");
                    }

                    i = end + 1;
                    continue;
                }

                var close = MatchingBrace(css, brace);
                var preludeText = css[nameEnd..brace].Trim();
                var body = css[(brace + 1)..close];
                switch (name)
                {
                    case "media" when depth < 8:
                        if (MediaApplies(preludeText))
                        {
                            Parse(body, origin, ref order, warn, sheet, depth + 1);
                        }

                        break;
                    case "page":
                        sheet.PageDeclarations.AddRange(Declarations(body));
                        break;
                    case "font-face":
                        var declarations = Declarations(body).ToDictionary(d => d.Property, d => d.Value);
                        if (declarations.TryGetValue("font-family", out var family) && declarations.TryGetValue("src", out var src))
                        {
                            var weight = declarations.GetValueOrDefault("font-weight", "normal");
                            var style = declarations.GetValueOrDefault("font-style", "normal");
                            var sources = SplitTopLevel(src, ',').Select(Url).OfType<string>().ToList();
                            sheet.FontFaces.Add(new FontFaceRule(Unquote(family.Trim()), weight is "bold" or "bolder" || (int.TryParse(weight, out var w) && w >= 600), style is "italic" or "oblique", sources));
                        }

                        break;
                    default:
                        warn($"The CSS at-rule @{name} is not supported and was ignored.");
                        break;
                }

                i = close + 1;
                continue;
            }

            var open = IndexOutsideBlocks(css, '{', i);
            if (open < 0)
            {
                break;
            }

            var closeBrace = MatchingBrace(css, open);
            var selectorText = css[i..open].Trim();
            var declarationsList = Declarations(css[(open + 1)..closeBrace]);
            foreach (var part in SplitTopLevel(selectorText, ','))
            {
                var selector = Selector.Parse(part.Trim(), out var unsupported);
                if (selector is null)
                {
                    warn($"CSS selectors with {unsupported ?? "this syntax"} are not supported; such rules were ignored.");
                    continue;
                }

                sheet.Rules.Add(new StyleRule(selector, declarationsList, origin, order++));
            }

            i = closeBrace + 1;
        }
    }

    private static bool MediaApplies(string media)
    {
        foreach (var query in media.ToLowerInvariant().Split(','))
        {
            var words = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || words.Contains("print") || words.Contains("all") || (words[0] == "only" && words.Length > 1 && words[1] is "print" or "all") || words[0].StartsWith('('))
            {
                if (!words.Contains("not"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int IndexOutsideBlocks(string css, char target, int start)
    {
        var depth = 0;
        for (var i = start; i < css.Length; i++)
        {
            var c = css[i];
            if (c is '"' or '\'')
            {
                var end = css.IndexOf(c, i + 1);
                i = end < 0 ? css.Length : end;
                continue;
            }

            if (c == target && depth == 0)
            {
                return i;
            }

            if (c is '(' or '[')
            {
                depth++;
            }
            else if (c is ')' or ']')
            {
                depth = Math.Max(0, depth - 1);
            }
        }

        return -1;
    }

    private static int MatchingBrace(string css, int open)
    {
        var depth = 0;
        for (var i = open; i < css.Length; i++)
        {
            if (css[i] is '"' or '\'')
            {
                var end = css.IndexOf(css[i], i + 1);
                i = end < 0 ? css.Length - 1 : end;
                continue;
            }

            if (css[i] == '{')
            {
                depth++;
            }
            else if (css[i] == '}' && --depth == 0)
            {
                return i;
            }
        }

        return css.Length;
    }

    public static List<Declaration> Declarations(string body)
    {
        var result = new List<Declaration>();
        foreach (var part in SplitTopLevel(body, ';'))
        {
            var colon = part.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var property = part[..colon].Trim().ToLowerInvariant();
            var value = part[(colon + 1)..].Trim();
            var important = false;
            var bang = value.LastIndexOf('!');
            if (bang >= 0 && value[(bang + 1)..].Trim().Equals("important", StringComparison.OrdinalIgnoreCase))
            {
                important = true;
                value = value[..bang].Trim();
            }

            if (property.Length > 0 && value.Length > 0)
            {
                result.Add(new Declaration(property, value, important));
            }
        }

        return result;
    }

    public static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        char quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth = Math.Max(0, depth - 1);
                    break;
                default:
                    if (c == separator && depth == 0)
                    {
                        parts.Add(text[start..i]);
                        start = i + 1;
                    }

                    break;
            }
        }

        parts.Add(text[start..]);
        return parts.Where(p => p.Trim().Length > 0).ToList();
    }

    /// <summary>The address in <c>url(…)</c> or a quoted string, or null.</summary>
    public static string? Url(string value)
    {
        var v = value.Trim();
        if (v.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            var close = v.IndexOf(')', StringComparison.Ordinal);
            return close < 0 ? null : Unquote(v[4..close].Trim());
        }

        return v.Length > 1 && v[0] is '"' or '\'' ? Unquote(v.Split(' ')[0]) : null;
    }

    public static string Unquote(string s) => s.Length >= 2 && s[0] is '"' or '\'' && s[^1] == s[0] ? s[1..^1] : s;
}

internal enum Combinator
{
    None,
    Descendant,
    Child,
}

/// <summary>A compound selector: type, id, classes, attribute tests and the structural pseudo-classes supported.</summary>
internal sealed class Compound
{
    public string? Tag { get; set; }

    public string? Id { get; set; }

    public List<string> Classes { get; } = new();

    public List<(string Name, string? Value, char Operator)> Attributes { get; } = new();

    public bool FirstChild { get; set; }

    public bool LastChild { get; set; }

    public bool Matches(Element e)
    {
        if (Tag is not null && Tag != "*" && Tag != e.Name)
        {
            return false;
        }

        if (Id is not null && e.Attr("id") != Id)
        {
            return false;
        }

        if (Classes.Count > 0)
        {
            var classes = e.Classes.ToHashSet(StringComparer.Ordinal);
            if (!Classes.All(classes.Contains))
            {
                return false;
            }
        }

        foreach (var (name, value, op) in Attributes)
        {
            var actual = e.Attr(name);
            if (actual is null)
            {
                return false;
            }

            var ok = op switch
            {
                '\0' => true,
                '=' => actual == value,
                '~' => actual.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(value),
                '^' => value!.Length > 0 && actual.StartsWith(value, StringComparison.Ordinal),
                '$' => value!.Length > 0 && actual.EndsWith(value, StringComparison.Ordinal),
                '*' => value!.Length > 0 && actual.Contains(value!, StringComparison.Ordinal),
                '|' => actual == value || actual.StartsWith(value + "-", StringComparison.Ordinal),
                _ => false,
            };
            if (!ok)
            {
                return false;
            }
        }

        if (FirstChild && e.Parent?.Children.OfType<Element>().FirstOrDefault() != e)
        {
            return false;
        }

        return !LastChild || e.Parent?.Children.OfType<Element>().LastOrDefault() == e;
    }
}

/// <summary>
/// A complex selector: compound selectors joined by descendant (space) or child (<c>&gt;</c>) combinators, with its
/// specificity. Supported: type, universal, class, id, attribute tests, <c>:first-child</c>, <c>:last-child</c> and
/// <c>:link</c>; anything else makes the selector unsupported.
/// </summary>
internal sealed class Selector
{
    private readonly List<(Combinator Combinator, Compound Compound)> _parts;

    private Selector(List<(Combinator, Compound)> parts, int specificity)
    {
        _parts = parts;
        Specificity = specificity;
    }

    /// <summary>Specificity as a × 10000 + b × 100 + c.</summary>
    public int Specificity { get; }

    public static Selector? Parse(string text, out string? unsupported)
    {
        unsupported = null;
        var parts = new List<(Combinator, Compound)>();
        var combinator = Combinator.None;
        var i = 0;
        int ids = 0, classes = 0, types = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                if (parts.Count > 0 && combinator == Combinator.None)
                {
                    combinator = Combinator.Descendant;
                }

                continue;
            }

            if (text[i] == '>')
            {
                combinator = Combinator.Child;
                i++;
                continue;
            }

            if (text[i] is '+' or '~')
            {
                unsupported = $"the '{text[i]}' combinator";
                return null;
            }

            var compound = new Compound();
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('>' or '+' or '~'))
            {
                var c = text[i];
                if (c == '*')
                {
                    compound.Tag = "*";
                    i++;
                }
                else if (char.IsAsciiLetter(c) && i == start)
                {
                    compound.Tag = Ident(text, ref i).ToLowerInvariant();
                    types++;
                }
                else if (c == '#')
                {
                    i++;
                    compound.Id = Ident(text, ref i);
                    ids++;
                }
                else if (c == '.')
                {
                    i++;
                    compound.Classes.Add(Ident(text, ref i));
                    classes++;
                }
                else if (c == '[')
                {
                    var close = text.IndexOf(']', i);
                    if (close < 0)
                    {
                        unsupported = "an unclosed attribute selector";
                        return null;
                    }

                    var body = text[(i + 1)..close];
                    var eq = body.IndexOf('=', StringComparison.Ordinal);
                    if (eq < 0)
                    {
                        compound.Attributes.Add((body.Trim().ToLowerInvariant(), null, '\0'));
                    }
                    else
                    {
                        var op = eq > 0 && body[eq - 1] is '~' or '^' or '$' or '*' or '|' ? body[eq - 1] : '=';
                        var name = body[..(op == '=' ? eq : eq - 1)].Trim().ToLowerInvariant();
                        compound.Attributes.Add((name, CssParser.Unquote(body[(eq + 1)..].Trim()), op));
                    }

                    classes++;
                    i = close + 1;
                }
                else if (c == ':')
                {
                    i++;
                    if (i < text.Length && text[i] == ':')
                    {
                        unsupported = "pseudo-elements (::)";
                        return null;
                    }

                    var pseudo = Ident(text, ref i).ToLowerInvariant();
                    switch (pseudo)
                    {
                        case "first-child":
                            compound.FirstChild = true;
                            break;
                        case "last-child":
                            compound.LastChild = true;
                            break;
                        case "link" or "any-link":
                            compound.Attributes.Add(("href", null, '\0'));
                            break;
                        case "before" or "after" or "first-line" or "first-letter":
                            unsupported = "pseudo-elements";
                            return null;
                        default:
                            unsupported = $"the :{pseudo} pseudo-class";
                            return null;
                    }

                    classes++;
                }
                else
                {
                    unsupported = $"'{c}'";
                    return null;
                }
            }

            parts.Add((parts.Count == 0 ? Combinator.None : combinator == Combinator.None ? Combinator.Descendant : combinator, compound));
            combinator = Combinator.None;
        }

        return parts.Count == 0 ? null : new Selector(parts, (ids * 10000) + (classes * 100) + types);
    }

    private static string Ident(string text, ref int i)
    {
        var start = i;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '-' or '_' || text[i] > 127))
        {
            i++;
        }

        return text[start..i];
    }

    /// <summary>Matches right to left: the last compound against the element, earlier ones against ancestors.</summary>
    public bool Matches(Element element) => Match(element, _parts.Count - 1);

    private bool Match(Element element, int index)
    {
        var (combinator, compound) = _parts[index];
        if (!compound.Matches(element))
        {
            return false;
        }

        if (index == 0)
        {
            return true;
        }

        if (combinator == Combinator.Child)
        {
            return element.Parent is { } parent && Match(parent, index - 1);
        }

        for (var ancestor = element.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (Match(ancestor, index - 1))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>CSS value parsing: lengths, colours and font sizes.</summary>
internal static class CssValues
{
    public const double PointsPerPixel = 0.75;

    private static readonly Dictionary<string, string> NamedColors = BuildColors();

    /// <summary>A length in points; em and percent are relative to <paramref name="fontSize"/> and <paramref name="percentBase"/> (percent stays a percentage when the base is null).</summary>
    public static Length? ParseLength(string value, double fontSize, double rootFontSize, double? percentBase = null)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v is "0" or "0.0")
        {
            return new Length(0);
        }

        if (v.EndsWith('%'))
        {
            if (!double.TryParse(v[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            {
                return null;
            }

            return percentBase is double b ? new Length(percent * b / 100) : new Length(percent, IsPercent: true);
        }

        var unitStart = v.Length;
        while (unitStart > 0 && char.IsAsciiLetter(v[unitStart - 1]))
        {
            unitStart--;
        }

        if (!double.TryParse(v[..unitStart], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
        {
            return null;
        }

        var points = v[unitStart..] switch
        {
            "pt" => number,
            "px" or "" => number * PointsPerPixel,
            "pc" => number * 12,
            "in" => number * 72,
            "cm" => number * 72 / 2.54,
            "mm" => number * 72 / 25.4,
            "q" => number * 72 / 101.6,
            "em" => number * fontSize,
            "rem" => number * rootFontSize,
            "ex" or "ch" => number * fontSize / 2,
            _ => double.NaN,
        };
        return double.IsNaN(points) ? null : new Length(Math.Clamp(points, -100_000, 100_000));
    }

    /// <summary>A colour as <c>#RRGGBB</c> (alpha is blended over white), <c>transparent</c> as null, or null when invalid.</summary>
    public static bool TryParseColor(string value, string currentColor, out string? color)
    {
        var v = value.Trim().ToLowerInvariant();
        color = null;
        if (v == "transparent")
        {
            return true;
        }

        if (v == "currentcolor")
        {
            color = currentColor;
            return true;
        }

        if (NamedColors.TryGetValue(v, out var named))
        {
            color = named;
            return true;
        }

        if (v.StartsWith('#'))
        {
            var hex = v[1..];
            if (!hex.All(char.IsAsciiHexDigit))
            {
                return false;
            }

            hex = hex.Length switch
            {
                3 or 4 => string.Concat(hex.Take(3).Select(c => new string(c, 2))),
                6 or 8 => hex[..6],
                _ => "",
            };
            if (hex.Length != 6)
            {
                return false;
            }

            color = "#" + hex.ToUpperInvariant();
            return true;
        }

        if (v.StartsWith("rgb", StringComparison.Ordinal) && v.IndexOf('(') is var open and > 0 && v.EndsWith(')'))
        {
            var parts = v[(open + 1)..^1].Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                return false;
            }

            var channels = new double[3];
            for (var c = 0; c < 3; c++)
            {
                var p = parts[c];
                if (!double.TryParse(p.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                {
                    return false;
                }

                channels[c] = Math.Clamp(p.EndsWith('%') ? n * 2.55 : n, 0, 255);
            }

            var alpha = 1.0;
            if (parts.Length > 3 && double.TryParse(parts[3].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var a))
            {
                alpha = Math.Clamp(parts[3].EndsWith('%') ? a / 100 : a, 0, 1);
            }

            if (alpha == 0)
            {
                return true;
            }

            color = "#" + string.Concat(channels.Select(ch => ((int)Math.Round((ch * alpha) + (255 * (1 - alpha)))).ToString("X2", CultureInfo.InvariantCulture)));
            return true;
        }

        return false;
    }

    private static Dictionary<string, string> BuildColors()
    {
        const string table = "aliceblue F0F8FF antiquewhite FAEBD7 aqua 00FFFF aquamarine 7FFFD4 azure F0FFFF beige F5F5DC bisque FFE4C4 black 000000 blanchedalmond FFEBCD blue 0000FF blueviolet 8A2BE2 brown A52A2A burlywood DEB887 cadetblue 5F9EA0 chartreuse 7FFF00 chocolate D2691E coral FF7F50 cornflowerblue 6495ED cornsilk FFF8DC crimson DC143C cyan 00FFFF darkblue 00008B darkcyan 008B8B darkgoldenrod B8860B darkgray A9A9A9 darkgreen 006400 darkgrey A9A9A9 darkkhaki BDB76B darkmagenta 8B008B darkolivegreen 556B2F darkorange FF8C00 darkorchid 9932CC darkred 8B0000 darksalmon E9967A darkseagreen 8FBC8F darkslateblue 483D8B darkslategray 2F4F4F darkslategrey 2F4F4F darkturquoise 00CED1 darkviolet 9400D3 deeppink FF1493 deepskyblue 00BFFF dimgray 696969 dimgrey 696969 dodgerblue 1E90FF firebrick B22222 floralwhite FFFAF0 forestgreen 228B22 fuchsia FF00FF gainsboro DCDCDC ghostwhite F8F8FF gold FFD700 goldenrod DAA520 gray 808080 green 008000 greenyellow ADFF2F grey 808080 honeydew F0FFF0 hotpink FF69B4 indianred CD5C5C indigo 4B0082 ivory FFFFF0 khaki F0E68C lavender E6E6FA lavenderblush FFF0F5 lawngreen 7CFC00 lemonchiffon FFFACD lightblue ADD8E6 lightcoral F08080 lightcyan E0FFFF lightgoldenrodyellow FAFAD2 lightgray D3D3D3 lightgreen 90EE90 lightgrey D3D3D3 lightpink FFB6C1 lightsalmon FFA07A lightseagreen 20B2AA lightskyblue 87CEFA lightslategray 778899 lightslategrey 778899 lightsteelblue B0C4DE lightyellow FFFFE0 lime 00FF00 limegreen 32CD32 linen FAF0E6 magenta FF00FF maroon 800000 mediumaquamarine 66CDAA mediumblue 0000CD mediumorchid BA55D3 mediumpurple 9370DB mediumseagreen 3CB371 mediumslateblue 7B68EE mediumspringgreen 00FA9A mediumturquoise 48D1CC mediumvioletred C71585 midnightblue 191970 mintcream F5FFFA mistyrose FFE4E1 moccasin FFE4B5 navajowhite FFDEAD navy 000080 oldlace FDF5E6 olive 808000 olivedrab 6B8E23 orange FFA500 orangered FF4500 orchid DA70D6 palegoldenrod EEE8AA palegreen 98FB98 paleturquoise AFEEEE palevioletred DB7093 papayawhip FFEFD5 peachpuff FFDAB9 peru CD853F pink FFC0CB plum DDA0DD powderblue B0E0E6 purple 800080 rebeccapurple 663399 red FF0000 rosybrown BC8F8F royalblue 4169E1 saddlebrown 8B4513 salmon FA8072 sandybrown F4A460 seagreen 2E8B57 seashell FFF5EE sienna A0522D silver C0C0C0 skyblue 87CEEB slateblue 6A5ACD slategray 708090 slategrey 708090 snow FFFAFA springgreen 00FF7F steelblue 4682B4 tan D2B48C teal 008080 thistle D8BFD8 tomato FF6347 turquoise 40E0D0 violet EE82EE wheat F5DEB3 white FFFFFF whitesmoke F5F5F5 yellow FFFF00 yellowgreen 9ACD32";
        var parts = table.Split(' ');
        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            colors[parts[i]] = "#" + parts[i + 1];
        }

        return colors;
    }
}
