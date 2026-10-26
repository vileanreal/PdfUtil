using System.Globalization;
using PdfUtil.Conversion.Internal.Html;
using PdfUtil.Conversion.Internal.Model;

namespace PdfUtil.Conversion.Internal.Css;

/// <summary>The computed style of an element (the CSS subset PdfUtil lays out). Lengths are in points.</summary>
internal sealed class ComputedStyle
{
    public string Display { get; set; } = "inline";

    // Inherited.
    public double FontSize { get; set; } = 12;

    public List<string> FontFamily { get; set; } = new() { "serif" };

    public bool Bold { get; set; }

    public bool Italic { get; set; }

    public string Color { get; set; } = "#000000";

    public TextAlignment TextAlign { get; set; }

    public double? LineHeight { get; set; }

    public double? LineHeightPoints { get; set; }

    public WhiteSpaceMode WhiteSpace { get; set; }

    public string ListStyleType { get; set; } = "disc";

    public Length TextIndent { get; set; }

    public bool Hidden { get; set; }

    public int Widows { get; set; } = 2;

    public int Orphans { get; set; } = 2;

    public bool BorderCollapse { get; set; }

    public double BorderSpacing { get; set; } = 1.5;

    // Not inherited.
    public string? Background { get; set; }

    public bool Underline { get; set; }

    public bool LineThrough { get; set; }

    public string VerticalAlign { get; set; } = "baseline";

    public Length MarginTop { get; set; }

    public Length MarginRight { get; set; }

    public Length MarginBottom { get; set; }

    public Length MarginLeft { get; set; }

    public Length PaddingTop { get; set; }

    public Length PaddingRight { get; set; }

    public Length PaddingBottom { get; set; }

    public Length PaddingLeft { get; set; }

    public BorderSide BorderTop { get; set; } = BorderSide.None;

    public BorderSide BorderRight { get; set; } = BorderSide.None;

    public BorderSide BorderBottom { get; set; } = BorderSide.None;

    public BorderSide BorderLeft { get; set; } = BorderSide.None;

    public Length? Width { get; set; }

    public Length? Height { get; set; }

    public BreakRule BreakBefore { get; set; }

    public BreakRule BreakAfter { get; set; }

    public bool AvoidBreakInside { get; set; }

    public bool FixedTableLayout { get; set; }

    public ComputedStyle InheritFrom()
    {
        return new ComputedStyle
        {
            FontSize = FontSize,
            FontFamily = FontFamily,
            Bold = Bold,
            Italic = Italic,
            Color = Color,
            TextAlign = TextAlign,
            LineHeight = LineHeight,
            LineHeightPoints = LineHeightPoints,
            WhiteSpace = WhiteSpace,
            ListStyleType = ListStyleType,
            TextIndent = TextIndent,
            Hidden = Hidden,
            Widows = Widows,
            Orphans = Orphans,
            BorderCollapse = BorderCollapse,
            BorderSpacing = BorderSpacing,
        };
    }

    public BoxStyle ToBox() => new()
    {
        MarginTop = MarginTop,
        MarginRight = MarginRight,
        MarginBottom = MarginBottom,
        MarginLeft = MarginLeft,
        PaddingTop = PaddingTop,
        PaddingRight = PaddingRight,
        PaddingBottom = PaddingBottom,
        PaddingLeft = PaddingLeft,
        BorderTop = BorderTop,
        BorderRight = BorderRight,
        BorderBottom = BorderBottom,
        BorderLeft = BorderLeft,
        Background = Background,
        Width = Width,
        BreakBefore = BreakBefore,
        BreakAfter = BreakAfter,
        AvoidBreakInside = AvoidBreakInside,
    };
}

/// <summary>
/// Computes styles: matching rules from the user-agent and author style sheets are applied in cascade order
/// (importance, origin, specificity, source order), then the <c>style</c> attribute; inherited properties come from
/// the parent. Shorthands (<c>margin</c>, <c>padding</c>, <c>border*</c>, <c>font</c>, <c>background</c>,
/// <c>list-style</c>) are expanded. Unsupported properties are ignored with one warning each.
/// </summary>
internal sealed class Cascade
{
    /// <summary>Properties without a visible effect that need no warning.</summary>
    private static readonly HashSet<string> Silent = new(StringComparer.Ordinal) { "max-width", "min-width", "min-height", "max-height", "box-sizing", "cursor", "outline", "content", "quotes", "-webkit-text-size-adjust", "text-size-adjust", "speak", "font-variant", "font-feature-settings", "text-rendering", "-webkit-font-smoothing", "-moz-osx-font-smoothing", "orphans", "widows" };

    private readonly List<StyleRule> _rules;
    private readonly Action<string> _warn;
    private readonly double _rootFontSize;
    private readonly Dictionary<Element, ComputedStyle> _styles = new(ReferenceEqualityComparer.Instance);

    public Cascade(IEnumerable<StyleSheet> sheets, Action<string> warn, double rootFontSize)
    {
        _rules = sheets.SelectMany(s => s.Rules).ToList();
        _warn = warn;
        _rootFontSize = rootFontSize;
    }

    public ComputedStyle this[Element e] => _styles[e];

    public void Compute(Element root, ComputedStyle initial)
    {
        var stack = new Stack<(Element Element, ComputedStyle Parent)>();
        stack.Push((root, initial));
        while (stack.Count > 0)
        {
            var (element, parent) = stack.Pop();
            var style = ComputeOne(element, parent);
            _styles[element] = style;
            for (var i = element.Children.Count - 1; i >= 0; i--)
            {
                if (element.Children[i] is Element child)
                {
                    stack.Push((child, style));
                }
            }
        }
    }

    private ComputedStyle ComputeOne(Element element, ComputedStyle parent)
    {
        var matched = new List<(bool Important, int Origin, int Specificity, int Order, Declaration Declaration)>();
        foreach (var rule in _rules)
        {
            if (rule.Selector.Matches(element))
            {
                foreach (var d in rule.Declarations)
                {
                    matched.Add((d.Important, rule.Origin, rule.Selector.Specificity, rule.Order, d));
                }
            }
        }

        if (element.Attr("style") is { } inline)
        {
            foreach (var d in CssParser.Declarations(inline))
            {
                matched.Add((d.Important, 1, 1_000_000, int.MaxValue, d));
            }
        }

        // Later wins: important author declarations last.
        var ordered = matched.OrderBy(m => m.Important ? 1 : 0).ThenBy(m => m.Important ? -m.Origin : m.Origin).ThenBy(m => m.Specificity).ThenBy(m => m.Order).Select(m => m.Declaration);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in ordered)
        {
            foreach (var (property, value) in Expand(d.Property, d.Value))
            {
                values[property] = value;
            }
        }

        var style = parent.InheritFrom();

        // Font size first: other lengths in em depend on it.
        if (values.TryGetValue("font-size", out var fontSize))
        {
            style.FontSize = FontSize(fontSize, parent.FontSize);
        }

        foreach (var (property, value) in values)
        {
            if (property != "font-size" && !property.StartsWith("border-", StringComparison.Ordinal) || property is "border-collapse" or "border-spacing")
            {
                Apply(style, parent, property, value);
            }
        }

        // Borders: a side is drawn when its style is visible; width defaults to medium and colour to the text colour.
        style.BorderTop = BorderOf(values, "top", style);
        style.BorderRight = BorderOf(values, "right", style);
        style.BorderBottom = BorderOf(values, "bottom", style);
        style.BorderLeft = BorderOf(values, "left", style);
        return style;
    }

    private void Warn(string property) => _warn($"The CSS property '{property}' is not supported and was ignored.");

    /// <summary>Expands shorthands into longhand properties.</summary>
    private IEnumerable<(string, string)> Expand(string property, string value)
    {
        var parts = CssParser.SplitTopLevel(value, ' ');
        switch (property)
        {
            case "margin" or "padding":
                var (t, r, b, l) = Box(parts);
                return new[] { (property + "-top", t), (property + "-right", r), (property + "-bottom", b), (property + "-left", l) };
            case "border" or "border-top" or "border-right" or "border-bottom" or "border-left":
                var sides = property == "border" ? new[] { "top", "right", "bottom", "left" } : new[] { property[7..] };
                var (width, style, color) = Border(parts);
                return sides.SelectMany(s => new[] { ($"border-{s}-width", width), ($"border-{s}-style", style), ($"border-{s}-color", color) });
            case "border-width" or "border-style" or "border-color":
                var (bt, br, bb, bl) = Box(parts);
                var kind = property[7..];
                return new[] { ($"border-top-{kind}", bt), ($"border-right-{kind}", br), ($"border-bottom-{kind}", bb), ($"border-left-{kind}", bl) };
            case "background":
                var colorPart = parts.FirstOrDefault(p => CssValues.TryParseColor(p, "#000000", out _)) ?? (parts.Any(p => p is "none" or "transparent") ? "transparent" : null);
                if (parts.Any(p => p.StartsWith("url(", StringComparison.OrdinalIgnoreCase) || p.Contains("gradient", StringComparison.OrdinalIgnoreCase)))
                {
                    _warn("CSS background images and gradients are not supported; only background colours are used.");
                }

                return colorPart is null ? Array.Empty<(string, string)>() : new[] { ("background-color", colorPart) };
            case "list-style":
                var type = parts.FirstOrDefault(p => !p.StartsWith("url(", StringComparison.OrdinalIgnoreCase) && p is not ("inside" or "outside"));
                return type is null ? Array.Empty<(string, string)>() : new[] { ("list-style-type", type) };
            case "font":
                return Font(value);
            case "text-decoration-line":
                return new[] { ("text-decoration", value) };
            case "page-break-before" or "page-break-after" or "page-break-inside":
                return new[] { ("break-" + property[11..], value) };
            default:
                return new[] { (property, value) };
        }
    }

    private static (string, string, string, string) Box(List<string> parts) => parts.Count switch
    {
        1 => (parts[0], parts[0], parts[0], parts[0]),
        2 => (parts[0], parts[1], parts[0], parts[1]),
        3 => (parts[0], parts[1], parts[2], parts[1]),
        _ => (parts[0], parts[1], parts[2], parts[3]),
    };

    private static (string Width, string Style, string Color) Border(List<string> parts)
    {
        string width = "medium", style = "none", color = "currentcolor";
        foreach (var p in parts)
        {
            var lower = p.ToLowerInvariant();
            if (lower is "none" or "hidden" or "solid" or "dashed" or "dotted" or "double" or "groove" or "ridge" or "inset" or "outset")
            {
                style = lower;
            }
            else if (lower is "thin" or "medium" or "thick" || char.IsAsciiDigit(lower[0]) || lower[0] == '.')
            {
                width = lower;
            }
            else
            {
                color = p;
            }
        }

        return (width, style, color);
    }

    private IEnumerable<(string, string)> Font(string value)
    {
        // [style] [variant] [weight] size[/line-height] family
        var parts = CssParser.SplitTopLevel(value, ' ');
        var result = new List<(string, string)> { ("font-style", "normal"), ("font-weight", "normal") };
        for (var i = 0; i < parts.Count; i++)
        {
            var p = parts[i].ToLowerInvariant();
            if (p is "italic" or "oblique")
            {
                result.Add(("font-style", p));
            }
            else if (p is "bold" or "bolder" or "lighter" or "normal" || (p.Length == 3 && p.All(char.IsAsciiDigit)))
            {
                result.Add(("font-weight", p));
            }
            else if (p is "small-caps")
            {
                continue;
            }
            else
            {
                var size = parts[i];
                var slash = size.IndexOf('/', StringComparison.Ordinal);
                if (slash > 0)
                {
                    result.Add(("line-height", size[(slash + 1)..]));
                    size = size[..slash];
                }
                else if (i + 1 < parts.Count && parts[i + 1].StartsWith('/'))
                {
                    result.Add(("line-height", parts[i + 1].TrimStart('/').Length > 0 ? parts[i + 1].TrimStart('/') : (i + 2 < parts.Count ? parts[i + 2] : "normal")));
                    i += parts[i + 1].Length == 1 ? 2 : 1;
                }

                result.Add(("font-size", size));
                result.Add(("font-family", string.Join(' ', parts.Skip(i + 1))));
                break;
            }
        }

        return result;
    }

    private double FontSize(string value, double parentSize)
    {
        var v = value.Trim().ToLowerInvariant();
        var px = v switch
        {
            "xx-small" => 9.0,
            "x-small" => 10.0,
            "small" => 13.0,
            "medium" => 16.0,
            "large" => 18.0,
            "x-large" => 24.0,
            "xx-large" => 32.0,
            "xxx-large" => 48.0,
            _ => 0,
        };
        if (px > 0)
        {
            return px * CssValues.PointsPerPixel;
        }

        if (v == "smaller")
        {
            return parentSize / 1.2;
        }

        if (v == "larger")
        {
            return parentSize * 1.2;
        }

        if (v is "inherit" or "unset")
        {
            return parentSize;
        }

        return CssValues.ParseLength(v, parentSize, _rootFontSize, parentSize) is { } length && length.Value > 0 ? Math.Min(length.Value, 1000) : parentSize;
    }

    private Length LengthOf(string value, ComputedStyle style, Length fallback) =>
        value.Trim().ToLowerInvariant() is "auto" or "initial" or "none" ? new Length(0) : CssValues.ParseLength(value, style.FontSize, _rootFontSize) ?? fallback;

    private BorderSide BorderOf(Dictionary<string, string> values, string side, ComputedStyle style)
    {
        var borderStyle = values.GetValueOrDefault($"border-{side}-style", "none").Trim().ToLowerInvariant();
        if (borderStyle is "none" or "hidden" or "initial" or "inherit")
        {
            return BorderSide.None;
        }

        if (borderStyle != "solid")
        {
            _warn("Only solid borders are supported; other border styles are drawn solid.");
        }

        var width = values.GetValueOrDefault($"border-{side}-width", "medium").Trim().ToLowerInvariant() switch
        {
            "thin" => 0.75,
            "medium" => 2.25,
            "thick" => 3.75,
            var v => CssValues.ParseLength(v, style.FontSize, _rootFontSize)?.Value ?? 2.25,
        };
        var color = CssValues.TryParseColor(values.GetValueOrDefault($"border-{side}-color", "currentcolor"), style.Color, out var c) ? c : style.Color;
        return width <= 0 || color is null ? BorderSide.None : new BorderSide(Math.Min(width, 200), color);
    }

    private void Apply(ComputedStyle s, ComputedStyle parent, string property, string value)
    {
        var v = value.Trim();
        var lower = v.ToLowerInvariant();
        if (lower is "inherit")
        {
            Inherit(s, parent, property);
            return;
        }

        switch (property)
        {
            case "display":
                s.Display = lower;
                if (lower is "flex" or "inline-flex" or "grid" or "inline-grid")
                {
                    _warn("Flexbox and grid layouts are not supported; such elements are laid out as blocks.");
                    s.Display = lower.StartsWith("inline", StringComparison.Ordinal) ? "inline" : "block";
                }

                break;
            case "color":
                if (CssValues.TryParseColor(v, parent.Color, out var color) && color is not null)
                {
                    s.Color = color;
                }

                break;
            case "background-color":
                if (CssValues.TryParseColor(v, s.Color, out var background))
                {
                    s.Background = background;
                }

                break;
            case "font-family":
                var families = CssParser.SplitTopLevel(v, ',').Select(f => CssParser.Unquote(f.Trim())).Where(f => f.Length > 0).ToList();
                if (families.Count > 0)
                {
                    s.FontFamily = families;
                }

                break;
            case "font-weight":
                s.Bold = lower switch
                {
                    "bold" or "bolder" => true,
                    "normal" or "lighter" => false,
                    _ => int.TryParse(lower, NumberStyles.None, CultureInfo.InvariantCulture, out var weight) ? weight >= 600 : s.Bold,
                };
                break;
            case "font-style":
                s.Italic = lower is "italic" or "oblique";
                break;
            case "text-align":
                s.TextAlign = lower switch
                {
                    "center" => TextAlignment.Center,
                    "right" or "end" => TextAlignment.Right,
                    "justify" => TextAlignment.Justify,
                    _ => TextAlignment.Left,
                };
                break;
            case "text-decoration":
                s.Underline = lower.Contains("underline", StringComparison.Ordinal);
                s.LineThrough = lower.Contains("line-through", StringComparison.Ordinal);
                break;
            case "line-height":
                if (lower == "normal")
                {
                    (s.LineHeight, s.LineHeightPoints) = (null, null);
                }
                else if (double.TryParse(lower, NumberStyles.Float, CultureInfo.InvariantCulture, out var multiplier))
                {
                    (s.LineHeight, s.LineHeightPoints) = (Math.Clamp(multiplier, 0, 10), null);
                }
                else if (lower.EndsWith('%') && double.TryParse(lower[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                {
                    (s.LineHeight, s.LineHeightPoints) = (Math.Clamp(percent / 100, 0, 10), null);
                }
                else if (CssValues.ParseLength(lower, s.FontSize, _rootFontSize) is { } height)
                {
                    (s.LineHeight, s.LineHeightPoints) = (null, Math.Clamp(height.Value, 0, 1000));
                }

                break;
            case "white-space":
                s.WhiteSpace = lower switch
                {
                    "pre" => WhiteSpaceMode.Pre,
                    "pre-wrap" or "break-spaces" => WhiteSpaceMode.PreWrap,
                    "pre-line" => WhiteSpaceMode.PreLine,
                    "nowrap" => WhiteSpaceMode.NoWrap,
                    _ => WhiteSpaceMode.Normal,
                };
                break;
            case "vertical-align":
                s.VerticalAlign = lower;
                break;
            case "list-style-type":
                s.ListStyleType = lower;
                break;
            case "text-indent":
                s.TextIndent = LengthOf(v, s, s.TextIndent);
                break;
            case "visibility":
                s.Hidden = lower is "hidden" or "collapse";
                break;
            case "margin-top":
                s.MarginTop = LengthOf(v, s, s.MarginTop);
                break;
            case "margin-right":
                s.MarginRight = LengthOf(v, s, s.MarginRight);
                break;
            case "margin-bottom":
                s.MarginBottom = LengthOf(v, s, s.MarginBottom);
                break;
            case "margin-left":
                s.MarginLeft = LengthOf(v, s, s.MarginLeft);
                break;
            case "padding-top":
                s.PaddingTop = LengthOf(v, s, s.PaddingTop);
                break;
            case "padding-right":
                s.PaddingRight = LengthOf(v, s, s.PaddingRight);
                break;
            case "padding-bottom":
                s.PaddingBottom = LengthOf(v, s, s.PaddingBottom);
                break;
            case "padding-left":
                s.PaddingLeft = LengthOf(v, s, s.PaddingLeft);
                break;
            case "width":
                s.Width = lower == "auto" ? null : CssValues.ParseLength(v, s.FontSize, _rootFontSize);
                break;
            case "height":
                s.Height = lower == "auto" ? null : CssValues.ParseLength(v, s.FontSize, _rootFontSize);
                break;
            case "break-before":
                s.BreakBefore = Break(lower);
                break;
            case "break-after":
                s.BreakAfter = Break(lower);
                break;
            case "break-inside":
                s.AvoidBreakInside = lower is "avoid" or "avoid-page";
                break;
            case "widows":
                s.Widows = int.TryParse(lower, out var widows) ? Math.Clamp(widows, 1, 20) : s.Widows;
                break;
            case "orphans":
                s.Orphans = int.TryParse(lower, out var orphans) ? Math.Clamp(orphans, 1, 20) : s.Orphans;
                break;
            case "border-collapse":
                s.BorderCollapse = lower == "collapse";
                break;
            case "border-spacing":
                s.BorderSpacing = CssValues.ParseLength(lower.Split(' ')[0], s.FontSize, _rootFontSize)?.Value ?? s.BorderSpacing;
                break;
            case "table-layout":
                s.FixedTableLayout = lower == "fixed";
                break;
            case "float":
                if (lower != "none")
                {
                    _warn("CSS floats are not supported; floated elements are laid out in the normal flow.");
                }

                break;
            case "position":
                if (lower is "absolute" or "fixed" or "sticky")
                {
                    _warn("CSS positioning is not supported; positioned elements are laid out in the normal flow.");
                }

                break;
            default:
                if (!Silent.Contains(property) && !property.StartsWith("--", StringComparison.Ordinal))
                {
                    Warn(property);
                }

                break;
        }
    }

    private static BreakRule Break(string value) => value switch
    {
        "always" or "page" or "left" or "right" or "recto" or "verso" => BreakRule.Always,
        "avoid" or "avoid-page" => BreakRule.Avoid,
        _ => BreakRule.Auto,
    };

    private static void Inherit(ComputedStyle s, ComputedStyle parent, string property)
    {
        switch (property)
        {
            case "background-color":
                s.Background = parent.Background;
                break;
            case "text-decoration":
                (s.Underline, s.LineThrough) = (parent.Underline, parent.LineThrough);
                break;
            case "display":
                s.Display = parent.Display;
                break;
            case "width":
                s.Width = parent.Width;
                break;
        }
    }
}
