using System.Globalization;
using System.Xml.Linq;
using PdfUtil.Conversion.Internal.Html;
using PdfUtil.Conversion.Internal.Model;

namespace PdfUtil.Conversion.Internal.Docx;

/// <summary>
/// Reads a Word document (OOXML WordprocessingML) into the shared document model: styles (with <c>basedOn</c>
/// inheritance and document defaults), theme fonts, numbering (formats, levels, overrides and continued counts),
/// paragraphs and runs, tables (grid, widths, merged cells, borders, shading, header rows), inline images, hyperlinks,
/// bookmarks, PAGE and NUMPAGES fields, sections (page size, orientation, margins) with default, first and even
/// headers and footers, and the core properties. Content outside that scope (floating shapes, text boxes, SmartArt,
/// charts, equations, comments, other fields, columns) is simplified or left out, each kind with one warning.
/// </summary>
internal sealed class DocxReader
{
    private static readonly XNamespace W = Ooxml.W;
    private static readonly XNamespace R = Ooxml.R;
    private static readonly XNamespace WP = Ooxml.WP;
    private static readonly XNamespace A = Ooxml.A;
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";

    private readonly DocxPackage _package;
    private readonly Action<string> _warn;
    private readonly long _maxPixels;
    private readonly int _maxElements;
    private readonly int _maxDepth;
    private readonly Dictionary<string, Style> _styles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Numbering> _numbering = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Abstract, int Level), int> _counters = new();
    private readonly Dictionary<string, ImageData> _images = new(StringComparer.Ordinal);
    private string _main = "word/document.xml";
    private Dictionary<string, (string Type, string Target, bool External)> _relationships = new();
    private XElement? _defaultPPr;
    private XElement? _defaultRPr;
    private string? _minorFont;
    private string? _majorFont;
    private bool _evenAndOdd;
    private int _elements;
    private int _headingAnchor;

    private sealed record Style(string Id, string Type, string? Name, string? BasedOn, XElement? PPr, XElement? RPr, XElement? TblPr, XElement? TcPr);

    private sealed record Level(string Format, string Text, int Start, XElement? PPr, XElement? RPr);

    private sealed record Numbering(string AbstractId, Dictionary<int, Level> Levels);

    public DocxReader(DocxPackage package, Action<string> warn, long maxPixels, int maxElements, int maxDepth)
    {
        _package = package;
        _warn = warn;
        _maxPixels = maxPixels;
        _maxElements = maxElements;
        _maxDepth = maxDepth;
    }

    public DocModel Read()
    {
        var root = _package.Relationships("");
        _main = root.Values.Where(r => r.Type == Ooxml.RelOfficeDocument).Select(r => r.Target.TrimStart('/')).FirstOrDefault()
            ?? throw new PdfCorruptException("The package has no main document; it is not a Word document.");
        var document = _package.Xml(_main) ?? throw new PdfCorruptException("The main document part is missing.");
        _relationships = _package.Relationships(_main);
        ReadStyles();
        ReadNumbering();
        ReadTheme();
        _evenAndOdd = Part(Ooxml.RelSettings)?.Root?.Element(W + "evenAndOddHeaders") is { } even && On(even);

        var body = document.Root?.Element(W + "body") ?? throw new PdfCorruptException("The document has no body.");
        var model = new DocModel();
        ReadCoreProperties(model, root);

        // Sections: a paragraph's sectPr ends a section; the body's sectPr describes the last one.
        Section? previous = null;
        foreach (var (sectionBlocks, sectPr) in SplitSections(body))
        {
            var section = SectionOf(sectPr, previous, out var continuous);
            if (continuous && previous is not null)
            {
                previous.Blocks.AddRange(sectionBlocks);
                continue;
            }

            section.Blocks.AddRange(sectionBlocks);
            model.Sections.Add(section);
            previous = section;
        }

        if (model.Sections.Count == 0)
        {
            model.Sections.Add(new Section());
        }

        return model;
    }

    // ------------------------------------------------------------------ package parts

    private XDocument? Part(string relationshipType)
    {
        var target = _relationships.Values.Where(r => r.Type == relationshipType && !r.External).Select(r => DocxPackage.ResolveTarget(_main, r.Target)).FirstOrDefault();
        return target is null ? null : _package.Xml(target);
    }

    private void ReadStyles()
    {
        var styles = Part(Ooxml.RelStyles)?.Root;
        if (styles is null)
        {
            return;
        }

        _defaultPPr = styles.Element(W + "docDefaults")?.Element(W + "pPrDefault")?.Element(W + "pPr");
        _defaultRPr = styles.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr");
        foreach (var s in styles.Elements(W + "style"))
        {
            var id = (string?)s.Attribute(W + "styleId");
            if (id is null)
            {
                continue;
            }

            _styles[id] = new Style(id, (string?)s.Attribute(W + "type") ?? "paragraph", Val(s.Element(W + "name")), Val(s.Element(W + "basedOn")), s.Element(W + "pPr"), s.Element(W + "rPr"), s.Element(W + "tblPr"), s.Element(W + "tcPr"));
        }
    }

    private void ReadNumbering()
    {
        var numbering = Part(Ooxml.RelNumbering)?.Root;
        if (numbering is null)
        {
            return;
        }

        var abstracts = new Dictionary<string, Dictionary<int, Level>>(StringComparer.Ordinal);
        foreach (var a in numbering.Elements(W + "abstractNum"))
        {
            var levels = new Dictionary<int, Level>();
            foreach (var lvl in a.Elements(W + "lvl"))
            {
                levels[Int((string?)lvl.Attribute(W + "ilvl"), 0)] = LevelOf(lvl);
            }

            abstracts[(string?)a.Attribute(W + "abstractNumId") ?? ""] = levels;
        }

        foreach (var num in numbering.Elements(W + "num"))
        {
            var abstractId = Val(num.Element(W + "abstractNumId")) ?? "";
            var levels = abstracts.TryGetValue(abstractId, out var l) ? new Dictionary<int, Level>(l) : new Dictionary<int, Level>();
            foreach (var over in num.Elements(W + "lvlOverride"))
            {
                var ilvl = Int((string?)over.Attribute(W + "ilvl"), 0);
                if (over.Element(W + "lvl") is { } replacement)
                {
                    levels[ilvl] = LevelOf(replacement);
                }

                if (Val(over.Element(W + "startOverride")) is { } start && levels.TryGetValue(ilvl, out var existing))
                {
                    levels[ilvl] = existing with { Start = Int(start, 1) };
                }
            }

            // Lists with a start override count on their own; others share their abstract numbering's count.
            var key = num.Elements(W + "lvlOverride").Any(o => o.Element(W + "startOverride") is not null) ? "num:" + (string?)num.Attribute(W + "numId") : abstractId;
            _numbering[(string?)num.Attribute(W + "numId") ?? ""] = new Numbering(key, levels);
        }
    }

    private static Level LevelOf(XElement lvl) =>
        new(Val(lvl.Element(W + "numFmt")) ?? "decimal", Val(lvl.Element(W + "lvlText")) ?? "", Int(Val(lvl.Element(W + "start")), 1), lvl.Element(W + "pPr"), lvl.Element(W + "rPr"));

    private void ReadTheme()
    {
        var target = _relationships.Values.Where(r => r.Type.EndsWith("/theme", StringComparison.Ordinal)).Select(r => DocxPackage.ResolveTarget(_main, r.Target)).FirstOrDefault();
        var fonts = target is null ? null : _package.Xml(target)?.Descendants(A + "fontScheme").FirstOrDefault();
        _majorFont = (string?)fonts?.Element(A + "majorFont")?.Element(A + "latin")?.Attribute("typeface");
        _minorFont = (string?)fonts?.Element(A + "minorFont")?.Element(A + "latin")?.Attribute("typeface");
    }

    private void ReadCoreProperties(DocModel model, Dictionary<string, (string Type, string Target, bool External)> root)
    {
        var target = root.Values.Where(r => r.Type == Ooxml.RelCore).Select(r => r.Target.TrimStart('/')).FirstOrDefault();
        var core = target is null ? null : _package.Xml(target)?.Root;
        if (core is null)
        {
            return;
        }

        XNamespace dc = Ooxml.DC;
        XNamespace cp = Ooxml.CoreProperties;
        string? Text(XName name) => core.Element(name)?.Value is { Length: > 0 } v ? v.Trim() : null;
        model.Title = Text(dc + "title");
        model.Author = Text(dc + "creator");
        model.Subject = Text(dc + "subject") ?? Text(dc + "description");
        model.Keywords = Text(cp + "keywords");
        model.Language = Text(dc + "language");
    }

    // ------------------------------------------------------------------ sections

    private IEnumerable<(List<Block> Blocks, XElement? SectPr)> SplitSections(XElement body)
    {
        var current = new List<XElement>();
        foreach (var element in body.Elements())
        {
            if (element.Name == W + "sectPr")
            {
                continue;
            }

            current.Add(element);
            if (element.Name == W + "p" && element.Element(W + "pPr")?.Element(W + "sectPr") is { } sectPr)
            {
                yield return (Blocks(current, 0, header: false), sectPr);
                current = new List<XElement>();
            }
        }

        yield return (Blocks(current, 0, header: false), body.Element(W + "sectPr"));
    }

    private Section SectionOf(XElement? sectPr, Section? previous, out bool continuous)
    {
        var section = new Section();
        continuous = Val(sectPr?.Element(W + "type")) == "continuous";
        if (sectPr?.Element(W + "pgSz") is { } size)
        {
            section.PageWidth = Twips((string?)size.Attribute(W + "w"), 612);
            section.PageHeight = Twips((string?)size.Attribute(W + "h"), 792);
            if ((string?)size.Attribute(W + "orient") == "landscape" && section.PageWidth < section.PageHeight)
            {
                (section.PageWidth, section.PageHeight) = (section.PageHeight, section.PageWidth);
            }
        }

        section.PageWidth = Math.Clamp(section.PageWidth, 72, 14400);
        section.PageHeight = Math.Clamp(section.PageHeight, 72, 14400);
        if (sectPr?.Element(W + "pgMar") is { } margins)
        {
            section.Margins = new Margins(
                Math.Abs(Twips((string?)margins.Attribute(W + "top"), 72)),
                Twips((string?)margins.Attribute(W + "right") ?? (string?)margins.Attribute(W + "end"), 72),
                Math.Abs(Twips((string?)margins.Attribute(W + "bottom"), 72)),
                Twips((string?)margins.Attribute(W + "left") ?? (string?)margins.Attribute(W + "start"), 72));
            section.HeaderDistance = Twips((string?)margins.Attribute(W + "header"), 36);
            section.FooterDistance = Twips((string?)margins.Attribute(W + "footer"), 36);
        }

        var m = section.Margins;
        section.Margins = new Margins(Math.Clamp(m.Top, 0, section.PageHeight / 3), Math.Clamp(m.Right, 0, section.PageWidth / 3), Math.Clamp(m.Bottom, 0, section.PageHeight / 3), Math.Clamp(m.Left, 0, section.PageWidth / 3));
        if (Int((string?)sectPr?.Element(W + "cols")?.Attribute(W + "num"), 1) > 1)
        {
            _warn("Multi-column sections are laid out in one column.");
        }

        section.DifferentFirstPage = sectPr?.Element(W + "titlePg") is { } titlePg && On(titlePg);

        // Headers and footers not given are inherited from the previous section.
        section.Header.AddRange(HeaderFooter(sectPr, "headerReference", "default") ?? previous?.Header ?? new List<Block>());
        section.Footer.AddRange(HeaderFooter(sectPr, "footerReference", "default") ?? previous?.Footer ?? new List<Block>());
        section.FirstHeader.AddRange(HeaderFooter(sectPr, "headerReference", "first") ?? previous?.FirstHeader ?? new List<Block>());
        section.FirstFooter.AddRange(HeaderFooter(sectPr, "footerReference", "first") ?? previous?.FirstFooter ?? new List<Block>());
        if (_evenAndOdd)
        {
            section.EvenHeader = HeaderFooter(sectPr, "headerReference", "even") ?? previous?.EvenHeader ?? new List<Block>();
            section.EvenFooter = HeaderFooter(sectPr, "footerReference", "even") ?? previous?.EvenFooter ?? new List<Block>();
        }

        return section;
    }

    private List<Block>? HeaderFooter(XElement? sectPr, string kind, string type)
    {
        var reference = sectPr?.Elements(W + kind).FirstOrDefault(r => ((string?)r.Attribute(W + "type") ?? "default") == type);
        if (reference is null || (string?)reference.Attribute(R + "id") is not { } id || !_relationships.TryGetValue(id, out var rel) || rel.External)
        {
            return null;
        }

        var partName = DocxPackage.ResolveTarget(_main, rel.Target);
        var part = _package.Xml(partName)?.Root;
        if (part is null)
        {
            return null;
        }

        // Header parts have their own relationships (images, links).
        var saved = (_main, _relationships);
        (_main, _relationships) = (partName, _package.Relationships(partName));
        try
        {
            return Blocks(part.Elements(), 0, header: true);
        }
        finally
        {
            (_main, _relationships) = saved;
        }
    }

    // ------------------------------------------------------------------ blocks

    private void Count(int depth)
    {
        if (++_elements > _maxElements)
        {
            throw new PdfLimitExceededException($"The document has more elements than the configured maximum of {_maxElements}.");
        }

        if (depth > _maxDepth)
        {
            throw new PdfLimitExceededException($"The document is nested deeper than the configured maximum of {_maxDepth}.");
        }
    }

    /// <summary>Open lists while reading consecutive numbered paragraphs.</summary>
    private sealed class ListState
    {
        public List<(ListBlock List, int Level, string NumId, double TextLeft)> Stack { get; } = new();
    }

    private List<Block> Blocks(IEnumerable<XElement> elements, int depth, bool header)
    {
        var blocks = new List<Block>();
        var lists = new ListState();
        var pendingAnchors = new List<string>();
        foreach (var element in elements)
        {
            Count(depth);
            switch (element.Name.LocalName)
            {
                case "p" when element.Name.Namespace == W:
                    foreach (var block in ParagraphBlocks(element, header))
                    {
                        block.Anchors.InsertRange(0, pendingAnchors);
                        pendingAnchors.Clear();
                        if (block is Paragraph p && NumberingOf(element) is { } numbering)
                        {
                            AddListItem(blocks, lists, p, numbering);
                        }
                        else
                        {
                            lists.Stack.Clear();
                            blocks.Add(block);
                        }
                    }

                    break;
                case "tbl" when element.Name.Namespace == W:
                    lists.Stack.Clear();
                    var table = Table(element, depth + 1, header);
                    table.Anchors.InsertRange(0, pendingAnchors);
                    pendingAnchors.Clear();
                    blocks.Add(table);
                    break;
                case "sdt":
                    if (element.Element(W + "sdtContent") is { } content)
                    {
                        blocks.AddRange(Blocks(content.Elements(), depth + 1, header));
                    }

                    break;
                case "customXml":
                    blocks.AddRange(Blocks(element.Elements(), depth + 1, header));
                    break;
                case "bookmarkStart":
                    if ((string?)element.Attribute(W + "name") is { Length: > 0 } name)
                    {
                        pendingAnchors.Add(name);
                    }

                    break;
                case "AlternateContent":
                    _warn("Text boxes and shapes are not converted.");
                    break;
                case "oMathPara" or "oMath":
                    _warn("Equations are not converted.");
                    break;
            }
        }

        if (pendingAnchors.Count > 0)
        {
            var anchor = new AnchorBlock();
            anchor.Anchors.AddRange(pendingAnchors);
            blocks.Add(anchor);
        }

        return blocks;
    }

    // ------------------------------------------------------------------ style resolution

    private static string? Val(XElement? e) => (string?)e?.Attribute(W + "val");

    private static int Int(string? s, int fallback) => int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Twentieths of a point to points.</summary>
    private static double Twips(string? s, double fallback) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? Math.Clamp(v / 20, -100_000, 100_000) : fallback;

    /// <summary>A toggle property: present means on, unless its value says off.</summary>
    private static bool On(XElement e) => (string?)e.Attribute(W + "val") is not ("0" or "false" or "off" or "none");

    /// <summary>Merges property elements: later layers override attributes and child elements of earlier ones.</summary>
    private static XElement Merge(IEnumerable<XElement?> layers, XName name)
    {
        var result = new XElement(name);
        foreach (var layer in layers)
        {
            if (layer is null)
            {
                continue;
            }

            foreach (var attribute in layer.Attributes())
            {
                result.SetAttributeValue(attribute.Name, attribute.Value);
            }

            foreach (var child in layer.Elements())
            {
                if (child.Name == W + "rPr" || child.Name == W + "sectPr" || child.Name == W + "pPrChange" || child.Name == W + "rPrChange")
                {
                    continue;
                }

                var existing = result.Element(child.Name);
                if (existing is null || !child.HasAttributes || child.Name == W + "numPr" || child.Name == W + "tabs")
                {
                    existing?.Remove();
                    result.Add(new XElement(child));
                }
                else
                {
                    var merged = Merge(new[] { existing, child }, child.Name);
                    existing.ReplaceWith(merged);
                }
            }
        }

        return result;
    }

    private List<Style> Chain(string? styleId)
    {
        var chain = new List<Style>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (styleId is not null && seen.Add(styleId) && _styles.TryGetValue(styleId, out var style) && chain.Count < 32)
        {
            chain.Insert(0, style);
            styleId = style.BasedOn;
        }

        return chain;
    }

    private string? DefaultParagraphStyle => _styles.Values.FirstOrDefault(s => s.Type == "paragraph" && s.Id == "Normal")?.Id;

    private (XElement PPr, XElement MarkRPr, Style? Style) ParagraphProperties(XElement p)
    {
        var direct = p.Element(W + "pPr");
        var styleId = Val(direct?.Element(W + "pStyle")) ?? DefaultParagraphStyle;
        var chain = Chain(styleId);
        var pPr = Merge(new[] { _defaultPPr }.Concat(chain.Select(s => s.PPr)).Append(direct), W + "pPr");
        var rPr = Merge(new[] { _defaultRPr }.Concat(chain.Select(s => s.RPr)), W + "rPr");
        return (pPr, rPr, chain.LastOrDefault());
    }

    private (Numbering Numbering, Level Level, int Ilvl, string NumId)? NumberingOf(XElement p)
    {
        var (pPr, _, _) = ParagraphProperties(p);
        var numPr = pPr.Element(W + "numPr");
        var numId = Val(numPr?.Element(W + "numId"));
        if (numId is null or "0" || !_numbering.TryGetValue(numId, out var numbering))
        {
            return null;
        }

        var ilvl = Math.Clamp(Int(Val(numPr!.Element(W + "ilvl")), 0), 0, 8);
        return numbering.Levels.TryGetValue(ilvl, out var level) ? (numbering, level, ilvl, numId) : null;
    }

    private RunStyle RunStyleOf(XElement rPr)
    {
        string? family = null;
        if (rPr.Element(W + "rFonts") is { } fonts)
        {
            family = (string?)fonts.Attribute(W + "ascii") ?? (string?)fonts.Attribute(W + "hAnsi") ?? (string?)fonts.Attribute(W + "eastAsia") ?? (string?)fonts.Attribute(W + "cs");
            var theme = (string?)fonts.Attribute(W + "asciiTheme") ?? (string?)fonts.Attribute(W + "hAnsiTheme");
            if (theme is not null)
            {
                family = theme.StartsWith("major", StringComparison.Ordinal) ? _majorFont ?? family : _minorFont ?? family;
            }
        }

        // w:sz is in half-points.
        var size = double.TryParse(Val(rPr.Element(W + "sz")), NumberStyles.Float, CultureInfo.InvariantCulture, out var halfPoints) ? halfPoints / 2 : 10;
        string? color = Val(rPr.Element(W + "color")) is { Length: 6 } hex && hex.All(char.IsAsciiHexDigit) ? "#" + hex.ToUpperInvariant() : null;
        if (color == "#000000")
        {
            color = null;
        }

        string? background = Val(rPr.Element(W + "highlight")) switch
        {
            "yellow" => "#FFFF00",
            "green" => "#00FF00",
            "cyan" => "#00FFFF",
            "magenta" => "#FF00FF",
            "blue" => "#0000FF",
            "red" => "#FF0000",
            "darkBlue" => "#000080",
            "darkCyan" => "#008080",
            "darkGreen" => "#008000",
            "darkMagenta" => "#800080",
            "darkRed" => "#800000",
            "darkYellow" => "#808000",
            "darkGray" => "#808080",
            "lightGray" => "#C0C0C0",
            "black" => "#000000",
            "white" => "#FFFFFF",
            _ => null,
        };
        if (background is null && (string?)rPr.Element(W + "shd")?.Attribute(W + "fill") is { Length: 6 } fill && fill != "auto" && fill.All(char.IsAsciiHexDigit))
        {
            background = "#" + fill.ToUpperInvariant();
        }

        return new RunStyle
        {
            FontFamily = family,
            GenericFamily = family is null ? null : "sans-serif",
            Size = Math.Clamp(size <= 0 ? 10 : size, 1, 1638),
            Bold = rPr.Element(W + "b") is { } b && On(b),
            Italic = rPr.Element(W + "i") is { } i && On(i),
            Underline = rPr.Element(W + "u") is { } u && Val(u) is not ("none" or null) || rPr.Element(W + "u") is { } u2 && Val(u2) is null,
            Strike = (rPr.Element(W + "strike") is { } s && On(s)) || (rPr.Element(W + "dstrike") is { } ds && On(ds)),
            Color = color,
            Background = background,
            Position = Val(rPr.Element(W + "vertAlign")) switch { "superscript" => VerticalPosition.Superscript, "subscript" => VerticalPosition.Subscript, _ => VerticalPosition.Baseline },
        };
    }

    // ------------------------------------------------------------------ paragraphs

    private IEnumerable<Block> ParagraphBlocks(XElement p, bool header)
    {
        var (pPr, markRPr, style) = ParagraphProperties(p);
        var heading = HeadingLevel(pPr, style);
        Paragraph NewParagraph()
        {
            var paragraph = new Paragraph
            {
                HeadingLevel = heading,
                DefaultStyle = RunStyleOf(markRPr),
                Alignment = Val(pPr.Element(W + "jc")) switch
                {
                    "center" => TextAlignment.Center,
                    "right" or "end" => TextAlignment.Right,
                    "both" or "distribute" => TextAlignment.Justify,
                    _ => TextAlignment.Left,
                },
                KeepWithNext = pPr.Element(W + "keepNext") is { } keep && On(keep),
            };
            var widow = pPr.Element(W + "widowControl") is not { } widowControl || On(widowControl);
            (paragraph.Widows, paragraph.Orphans) = widow ? (2, 2) : (1, 1);
            if (pPr.Element(W + "spacing") is { } spacing)
            {
                var rule = (string?)spacing.Attribute(W + "lineRule") ?? "auto";
                if ((string?)spacing.Attribute(W + "line") is { } line)
                {
                    if (rule == "auto")
                    {
                        paragraph.LineHeight = Math.Clamp(Int(line, 240) / 240.0 * 1.17, 0.5, 10);
                    }
                    else
                    {
                        paragraph.LineHeightPoints = Math.Clamp(Twips(line, 12), 1, 1000);
                    }
                }
            }

            var ind = pPr.Element(W + "ind");
            var firstLine = Twips((string?)ind?.Attribute(W + "firstLine"), 0);
            var hanging = Twips((string?)ind?.Attribute(W + "hanging"), 0);
            paragraph.TextIndent = hanging > 0 ? -hanging : firstLine;
            paragraph.Box = BoxOf(pPr, ind);
            return paragraph;
        }

        var current = NewParagraph();
        if (heading > 0)
        {
            current.Anchors.Add("_heading-" + (++_headingAnchor).ToString(CultureInfo.InvariantCulture));
        }

        var first = true;
        var state = new FieldState();
        foreach (var item in Inlines(p.Elements(), markRPr, state, null, 0, header))
        {
            if (item is PageBreakBlock)
            {
                // A page break inside a paragraph splits it.
                yield return current;
                yield return new PageBreakBlock();
                current = NewParagraph();
                current.Box = current.Box! with { BreakBefore = BreakRule.Auto };
                first = false;
                continue;
            }

            current.Inlines.Add((Inline)item);
        }

        if (first && pPr.Element(W + "pageBreakBefore") is { } pb && On(pb))
        {
            current.Box = current.Box! with { BreakBefore = BreakRule.Always };
        }

        yield return current;
    }

    private int HeadingLevel(XElement pPr, Style? style)
    {
        if (Val(pPr.Element(W + "outlineLvl")) is { } outline && int.TryParse(outline, out var level) && level is >= 0 and < 9)
        {
            return Math.Min(6, level + 1);
        }

        var name = style?.Name?.ToLowerInvariant() ?? "";
        if (name.StartsWith("heading ", StringComparison.Ordinal) && int.TryParse(name[8..], out var n) && n is >= 1 and <= 9)
        {
            return Math.Min(6, n);
        }

        return name == "title" ? 1 : 0;
    }

    private BoxStyle BoxOf(XElement pPr, XElement? ind)
    {
        var spacing = pPr.Element(W + "spacing");
        var box = new BoxStyle
        {
            MarginTop = new Length(Math.Max(0, Twips((string?)spacing?.Attribute(W + "before"), 0))),
            MarginBottom = new Length(Math.Max(0, Twips((string?)spacing?.Attribute(W + "after"), 0))),
            MarginLeft = new Length(Twips((string?)ind?.Attribute(W + "left") ?? (string?)ind?.Attribute(W + "start"), 0)),
            MarginRight = new Length(Twips((string?)ind?.Attribute(W + "right") ?? (string?)ind?.Attribute(W + "end"), 0)),
            AvoidBreakInside = pPr.Element(W + "keepLines") is { } keepLines && On(keepLines),
        };
        if ((string?)pPr.Element(W + "shd")?.Attribute(W + "fill") is { Length: 6 } fill && fill != "auto" && fill.All(char.IsAsciiHexDigit))
        {
            box = box with { Background = "#" + fill.ToUpperInvariant() };
        }

        if (pPr.Element(W + "pBdr") is { } borders)
        {
            box = box with
            {
                BorderTop = Border(borders.Element(W + "top")),
                BorderRight = Border(borders.Element(W + "right") ?? borders.Element(W + "end")),
                BorderBottom = Border(borders.Element(W + "bottom")),
                BorderLeft = Border(borders.Element(W + "left") ?? borders.Element(W + "start")),
            };
        }

        return box;
    }

    private static BorderSide Border(XElement? border)
    {
        if (border is null || Val(border) is null or "nil" or "none")
        {
            return BorderSide.None;
        }

        var size = Int((string?)border.Attribute(W + "sz"), 4) / 8.0;
        var color = (string?)border.Attribute(W + "color") is { Length: 6 } c && c.All(char.IsAsciiHexDigit) ? "#" + c.ToUpperInvariant() : "#000000";
        return new BorderSide(Math.Clamp(size, 0.25, 12), color);
    }

    // ------------------------------------------------------------------ lists

    private void AddListItem(List<Block> blocks, ListState lists, Paragraph paragraph, (Numbering Numbering, Level Level, int Ilvl, string NumId) numbering)
    {
        var (num, level, ilvl, numId) = numbering;

        // Counting: deeper levels restart when a shallower item appears.
        var key = (num.AbstractId, ilvl);
        var count = _counters.TryGetValue(key, out var previous) ? previous + 1 : level.Start;
        _counters[key] = count;
        foreach (var deeper in _counters.Keys.Where(k => k.Abstract == num.AbstractId && k.Level > ilvl).ToList())
        {
            _counters.Remove(deeper);
        }

        var ind = Merge(new[] { level.PPr?.Element(W + "ind") }, W + "ind");
        var left = Twips((string?)ind.Attribute(W + "left") ?? (string?)ind.Attribute(W + "start"), 36 * (ilvl + 1));
        var hanging = Twips((string?)ind.Attribute(W + "hanging"), 18);
        if (paragraph.Box?.MarginLeft.Value is double direct && direct > 0)
        {
            left = direct;
        }

        // The item's paragraph is placed by the list: its own indentation goes.
        paragraph.Box = (paragraph.Box ?? BoxStyle.Empty) with { MarginLeft = new Length(0) };
        paragraph.TextIndent = 0;

        while (lists.Stack.Count > 0 && (lists.Stack[^1].Level > ilvl || (lists.Stack[^1].Level == ilvl && lists.Stack[^1].NumId != numId)))
        {
            lists.Stack.RemoveAt(lists.Stack.Count - 1);
        }

        if (lists.Stack.Count == 0 || lists.Stack[^1].Level < ilvl)
        {
            var parentLeft = lists.Stack.Count > 0 ? lists.Stack[^1].TextLeft : 0;
            var list = new ListBlock
            {
                Kind = level.Format switch
                {
                    "bullet" or "none" => ListKind.Bullet,
                    "lowerLetter" => ListKind.LowerAlpha,
                    "upperLetter" => ListKind.UpperAlpha,
                    "lowerRoman" => ListKind.LowerRoman,
                    "upperRoman" => ListKind.UpperRoman,
                    _ => ListKind.Decimal,
                },
                BulletText = level.Format == "none" ? "" : Bullet(level),
                Start = count,
                Indent = hanging,
                MarkerStyle = RunStyleOf(Merge(new[] { _defaultRPr, level.RPr }, W + "rPr")) with { FontFamily = null, Background = null, Underline = false },
                Box = new BoxStyle { MarginLeft = new Length(Math.Max(0, left - hanging - parentLeft)) },
            };
            if (level.Format is not ("bullet" or "none" or "decimal" or "decimalZero" or "lowerLetter" or "upperLetter" or "lowerRoman" or "upperRoman"))
            {
                _warn($"Numbering format '{level.Format}' is shown as decimal numbers.");
            }

            if (lists.Stack.Count > 0 && lists.Stack[^1].List.Items.Count > 0)
            {
                lists.Stack[^1].List.Items[^1].Blocks.Add(list);
            }
            else
            {
                lists.Stack.Clear();
                blocks.Add(list);
            }

            lists.Stack.Add((list, ilvl, numId, left));
        }

        var item = new ListItem();
        item.Blocks.Add(paragraph);
        lists.Stack[^1].List.Items.Add(item);
    }

    /// <summary>The bullet character: symbol-font bullets (private use code points) become standard ones.</summary>
    private static string Bullet(Level level)
    {
        var text = level.Text;
        if (text.Length == 0)
        {
            return "•";
        }

        var c = text[0];
        return c switch
        {
            '' or '' or '·' => "•",
            'o' => "◦",
            '' or '' or '' or '' or '§' => "▪",
            '-' or '–' => "–",
            _ when c >= '' && c <= '' => "•",
            _ => text,
        };
    }

    // ------------------------------------------------------------------ inlines

    /// <summary>Complex field state: inside a field's instructions or its result, and whether that result is replaced.</summary>
    private sealed class FieldState
    {
        public int Depth { get; set; }

        public bool InInstruction { get; set; }

        public string Instruction { get; set; } = "";

        public bool SkipResult { get; set; }
    }

    private IEnumerable<object> Inlines(IEnumerable<XElement> elements, XElement markRPr, FieldState field, LinkInline? link, int depth, bool header)
    {
        if (depth > 32)
        {
            yield break;
        }

        foreach (var e in elements)
        {
            Count(depth);
            switch (e.Name.LocalName)
            {
                case "r" when e.Name.Namespace == W:
                    foreach (var item in Run(e, markRPr, field, link, header))
                    {
                        yield return item;
                    }

                    break;
                case "hyperlink":
                    var target = (string?)e.Attribute(R + "id") is { } id && _relationships.TryGetValue(id, out var rel) ? UrlSafety.Check(rel.Target) : null;
                    var anchor = (string?)e.Attribute(W + "anchor");
                    LinkInline? created = target is not null ? new LinkInline { Uri = target } : anchor is { Length: > 0 } ? new LinkInline { Anchor = anchor } : null;
                    if ((string?)e.Attribute(R + "id") is not null && target is null)
                    {
                        _warn("A link with an unsafe or unsupported address was not converted.");
                    }

                    if (created is not null && link is null)
                    {
                        foreach (var child in Inlines(e.Elements(), markRPr, field, created, depth + 1, header))
                        {
                            created.Children.Add((Inline)child);
                        }

                        yield return created;
                    }
                    else
                    {
                        foreach (var child in Inlines(e.Elements(), markRPr, field, link, depth + 1, header))
                        {
                            yield return child;
                        }
                    }

                    break;
                case "fldSimple":
                    var instruction = ((string?)e.Attribute(W + "instr") ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant();
                    if (instruction is "PAGE" or "NUMPAGES" or "SECTIONPAGES")
                    {
                        var rPr = e.Descendants(W + "rPr").FirstOrDefault();
                        yield return new FieldInline { Kind = instruction == "PAGE" ? FieldKind.PageNumber : FieldKind.PageCount, Style = RunStyleOf(Merge(new[] { markRPr, rPr }, W + "rPr")) };
                    }
                    else
                    {
                        _warn("Fields other than PAGE and NUMPAGES show their last calculated result.");
                        foreach (var child in Inlines(e.Elements(), markRPr, field, link, depth + 1, header))
                        {
                            yield return child;
                        }
                    }

                    break;
                case "ins" or "smartTag" or "customXml" or "moveTo" or "dir" or "bdo":
                    foreach (var child in Inlines(e.Elements(), markRPr, field, link, depth + 1, header))
                    {
                        yield return child;
                    }

                    break;
                case "sdt":
                    if (e.Element(W + "sdtContent") is { } content)
                    {
                        foreach (var child in Inlines(content.Elements(), markRPr, field, link, depth + 1, header))
                        {
                            yield return child;
                        }
                    }

                    break;
                case "del" or "moveFrom":
                    _warn("Tracked changes are shown in their accepted state.");
                    break;
                case "AlternateContent":
                    var drawing = e.Element(Mc + "Choice")?.Descendants(W + "drawing").FirstOrDefault();
                    if (drawing is not null && Drawing(drawing) is { } image)
                    {
                        yield return image;
                    }
                    else
                    {
                        _warn("Text boxes and shapes are not converted.");
                    }

                    break;
                case "bookmarkStart":
                    if ((string?)e.Attribute(W + "name") is { Length: > 0 } name)
                    {
                        yield return new AnchorInline { Name = name };
                    }

                    break;
                case "commentRangeStart" or "commentRangeEnd":
                    _warn("Comments are not converted.");
                    break;
                case "oMath" or "oMathPara":
                    _warn("Equations are not converted.");
                    break;
            }
        }
    }

    private IEnumerable<object> Run(XElement r, XElement markRPr, FieldState field, LinkInline? link, bool header)
    {
        var direct = r.Element(W + "rPr");
        var characterStyle = Chain(Val(direct?.Element(W + "rStyle")));
        var rPr = Merge(new[] { markRPr }.Concat(characterStyle.Select(s => s.RPr)).Append(direct), W + "rPr");
        if (rPr.Element(W + "vanish") is { } vanish && On(vanish))
        {
            yield break;
        }

        var style = RunStyleOf(rPr);
        var caps = rPr.Element(W + "caps") is { } capsElement && On(capsElement);
        foreach (var e in r.Elements())
        {
            switch (e.Name.LocalName)
            {
                case "fldChar":
                    switch ((string?)e.Attribute(W + "fldCharType"))
                    {
                        case "begin":
                            field.Depth++;
                            if (field.Depth == 1)
                            {
                                field.InInstruction = true;
                                field.Instruction = "";
                                field.SkipResult = false;
                            }

                            break;
                        case "separate" when field.Depth == 1:
                            field.InInstruction = false;
                            var name = field.Instruction.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant();
                            if (name is "PAGE" or "NUMPAGES" or "SECTIONPAGES")
                            {
                                field.SkipResult = true;
                                yield return new FieldInline { Kind = name == "PAGE" ? FieldKind.PageNumber : FieldKind.PageCount, Style = style };
                            }
                            else if (name is not null)
                            {
                                _warn("Fields other than PAGE and NUMPAGES show their last calculated result.");
                            }

                            break;
                        case "end":
                            if (field.Depth == 1)
                            {
                                field.InInstruction = false;
                                field.SkipResult = false;
                            }

                            field.Depth = Math.Max(0, field.Depth - 1);
                            break;
                    }

                    break;
                case "instrText":
                    if (field.Depth == 1)
                    {
                        field.Instruction += e.Value;
                    }

                    break;
                default:
                    if (field.InInstruction || field.SkipResult)
                    {
                        break;
                    }

                    foreach (var item in RunContent(e, style, caps, header))
                    {
                        yield return item;
                    }

                    break;
            }
        }
    }

    private IEnumerable<object> RunContent(XElement e, RunStyle style, bool caps, bool header)
    {
        switch (e.Name.LocalName)
        {
            case "t":
                yield return new TextInline(caps ? e.Value.ToUpperInvariant() : e.Value, style);
                break;
            case "tab" or "ptab":
                yield return new TextInline("\t", style);
                break;
            case "br":
                if ((string?)e.Attribute(W + "type") == "page")
                {
                    yield return new PageBreakBlock();
                }
                else
                {
                    yield return new BreakInline();
                }

                break;
            case "cr":
                yield return new BreakInline();
                break;
            case "noBreakHyphen":
                yield return new TextInline("‑", style);
                break;
            case "sym":
                var code = Int((string?)e.Attribute(W + "char") is { } hex ? int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) : null, 0x2022);
                yield return new TextInline(code is >= 0xF000 and <= 0xF0FF ? "•" : char.ConvertFromUtf32(Math.Clamp(code, 0x20, 0x10FFFF)), style);
                break;
            case "drawing":
                if (Drawing(e) is { } image)
                {
                    yield return image;
                }

                break;
            case "AlternateContent":
                var picture = e.Element(Mc + "Choice")?.Descendants(W + "drawing").FirstOrDefault();
                if (picture is not null && Drawing(picture) is { } choiceImage)
                {
                    yield return choiceImage;
                }
                else
                {
                    _warn("Text boxes and shapes are not converted.");
                }

                break;
            case "pict" or "object":
                _warn("Legacy drawings (VML) and embedded objects are not converted.");
                break;
            case "footnoteReference" or "endnoteReference":
                _warn("Footnotes and endnotes are not converted.");
                break;
            case "commentReference":
                _warn("Comments are not converted.");
                break;
            case "delText" or "delInstrText":
                _warn("Tracked changes are shown in their accepted state.");
                break;
        }
    }

    private ImageInline? Drawing(XElement drawing)
    {
        var container = drawing.Element(WP + "inline") ?? drawing.Element(WP + "anchor");
        if (container is null)
        {
            return null;
        }

        if (container.Name == WP + "anchor")
        {
            _warn("Floating images are placed inline.");
        }

        var graphic = container.Descendants(A + "graphicData").FirstOrDefault();
        var uri = (string?)graphic?.Attribute("uri") ?? "";
        if (uri.EndsWith("/chart", StringComparison.Ordinal))
        {
            _warn("Charts are not converted.");
            return null;
        }

        if (uri.EndsWith("/diagram", StringComparison.Ordinal))
        {
            _warn("SmartArt is not converted.");
            return null;
        }

        if (container.Descendants().Any(d => d.Name.LocalName == "txbx"))
        {
            _warn("Text boxes and shapes are not converted.");
            return null;
        }

        var extent = container.Element(WP + "extent");
        var width = double.TryParse((string?)extent?.Attribute("cx"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cx) ? cx / 12700 : 0;
        var height = double.TryParse((string?)extent?.Attribute("cy"), NumberStyles.Float, CultureInfo.InvariantCulture, out var cy) ? cy / 12700 : 0;
        var embed = (string?)container.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed");
        if (embed is null || !_relationships.TryGetValue(embed, out var rel) || rel.External)
        {
            _warn(embed is null ? "Shapes other than pictures are not converted." : "An image refers to a missing or external file and was left out.");
            return null;
        }

        var part = DocxPackage.ResolveTarget(_main, rel.Target);
        if (!_images.TryGetValue(part, out var data))
        {
            var bytes = _package.Get(part);
            data = bytes is null ? new ImageData { Placeholder = "missing" } : HtmlModelBuilder.Describe(bytes, _maxPixels, _warn);
            _images[part] = data;
        }

        if (width <= 0 || height <= 0)
        {
            (width, height) = (data.PixelWidth * 0.75, data.PixelHeight * 0.75);
        }

        return new ImageInline { Image = data, Width = Math.Clamp(width, 1, 14400), Height = Math.Clamp(height, 1, 14400), AltText = (string?)container.Element(WP + "docPr")?.Attribute("descr") };
    }

    // ------------------------------------------------------------------ tables

    private TableBlock Table(XElement tbl, int depth, bool header)
    {
        var tblPr = tbl.Element(W + "tblPr");
        var styleChain = Chain(Val(tblPr?.Element(W + "tblStyle")));
        var props = Merge(styleChain.Select(s => s.TblPr).Append(tblPr), W + "tblPr");
        var table = new TableBlock { CollapseBorders = true };
        var grid = tbl.Element(W + "tblGrid")?.Elements(W + "gridCol").Select(g => Twips((string?)g.Attribute(W + "w"), 0)).ToList() ?? new List<double>();
        if (grid.Count > 0 && grid.All(g => g > 0))
        {
            table.ColumnSpecs = grid.Select(g => (Length?)new Length(g)).ToList();
        }

        if (props.Element(W + "tblW") is { } tblW)
        {
            var type = (string?)tblW.Attribute(W + "type");
            var w = (string?)tblW.Attribute(W + "w");
            table.Box = (table.Box ?? BoxStyle.Empty) with
            {
                Width = type switch
                {
                    "dxa" when Twips(w, 0) > 0 => new Length(Twips(w, 0)),
                    "pct" when double.TryParse(w?.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) && pct > 0 => new Length(w!.EndsWith('%') ? pct : pct / 50, IsPercent: true),
                    _ => null,
                },
            };
        }

        table.FixedLayout = (string?)props.Element(W + "tblLayout")?.Attribute(W + "type") == "fixed";
        if (Twips((string?)props.Element(W + "tblInd")?.Attribute(W + "w"), 0) is var indent and > 0)
        {
            table.Box = (table.Box ?? BoxStyle.Empty) with { MarginLeft = new Length(indent) };
        }

        var borders = props.Element(W + "tblBorders");
        var margins = props.Element(W + "tblCellMar");
        var cellLeft = Twips((string?)(margins?.Element(W + "left") ?? margins?.Element(W + "start"))?.Attribute(W + "w"), 5.4);
        var cellRight = Twips((string?)(margins?.Element(W + "right") ?? margins?.Element(W + "end"))?.Attribute(W + "w"), 5.4);
        var cellTop = Twips((string?)margins?.Element(W + "top")?.Attribute(W + "w"), 0);
        var cellBottom = Twips((string?)margins?.Element(W + "bottom")?.Attribute(W + "w"), 0);

        var rows = tbl.Elements(W + "tr").ToList();
        var columns = Math.Max(grid.Count, rows.Select(r => r.Elements(W + "tc").Sum(tc => Math.Max(1, Int(Val(tc.Element(W + "tcPr")?.Element(W + "gridSpan")), 1)))).DefaultIfEmpty(1).Max());
        var vertical = new Dictionary<int, TableCell>();
        for (var r = 0; r < rows.Count; r++)
        {
            Count(depth);
            var row = new TableRow();
            var trPr = rows[r].Element(W + "trPr");
            if (trPr?.Element(W + "tblHeader") is { } tblHeader && On(tblHeader) && table.HeaderRows == r)
            {
                table.HeaderRows++;
            }

            var column = Int(Val(trPr?.Element(W + "gridBefore")), 0);
            for (var c = 0; c < column; c++)
            {
                row.Cells.Add(new TableCell());
            }

            foreach (var tc in rows[r].Elements(W + "tc"))
            {
                var tcPr = tc.Element(W + "tcPr");
                var span = Math.Clamp(Int(Val(tcPr?.Element(W + "gridSpan")), 1), 1, 1000);
                var merge = tcPr?.Element(W + "vMerge");
                if (merge is not null && Val(merge) != "restart" && vertical.TryGetValue(column, out var above))
                {
                    above.RowSpan++;
                    column += span;
                    continue;
                }

                var cellBorders = tcPr?.Element(W + "tcBorders");
                BorderSide Side(string outer, string inner, bool edge, string own) =>
                    cellBorders?.Element(W + own) is { } specific ? Border(specific) : Border(edge ? borders?.Element(W + outer) : borders?.Element(W + inner));
                var mar = tcPr?.Element(W + "tcMar");
                var cell = new TableCell
                {
                    ColumnSpan = span,
                    Background = (string?)tcPr?.Element(W + "shd")?.Attribute(W + "fill") is { Length: 6 } fill && fill != "auto" && fill.All(char.IsAsciiHexDigit) ? "#" + fill.ToUpperInvariant() : null,
                    VerticalAlignment = Val(tcPr?.Element(W + "vAlign")) switch { "center" => CellAlignment.Middle, "bottom" => CellAlignment.Bottom, _ => CellAlignment.Top },
                    Box = new BoxStyle
                    {
                        PaddingLeft = new Length(Twips((string?)(mar?.Element(W + "left") ?? mar?.Element(W + "start"))?.Attribute(W + "w"), cellLeft)),
                        PaddingRight = new Length(Twips((string?)(mar?.Element(W + "right") ?? mar?.Element(W + "end"))?.Attribute(W + "w"), cellRight)),
                        PaddingTop = new Length(Twips((string?)mar?.Element(W + "top")?.Attribute(W + "w"), cellTop)),
                        PaddingBottom = new Length(Twips((string?)mar?.Element(W + "bottom")?.Attribute(W + "w"), cellBottom)),
                        BorderTop = Side("top", "insideH", r == 0, "top"),
                        BorderBottom = Side("bottom", "insideH", r == rows.Count - 1, "bottom"),
                        BorderLeft = Side("left", "insideV", column == 0, "left"),
                        BorderRight = Side("right", "insideV", column + span >= columns, "right"),
                    },
                };
                if (tcPr?.Element(W + "tcW") is { } tcW && (string?)tcW.Attribute(W + "type") == "dxa" && Twips((string?)tcW.Attribute(W + "w"), 0) > 0)
                {
                    cell.Width = new Length(Twips((string?)tcW.Attribute(W + "w"), 0));
                }

                cell.Blocks.AddRange(Blocks(tc.Elements(), depth + 1, header));
                row.Cells.Add(cell);
                if (merge is not null)
                {
                    vertical[column] = cell;
                }
                else
                {
                    vertical.Remove(column);
                }

                column += span;
            }

            table.Rows.Add(row);
        }

        return table;
    }
}
