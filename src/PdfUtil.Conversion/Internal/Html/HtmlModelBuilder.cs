using System.Globalization;
using System.Text;
using PdfUtil.Conversion.Internal.Css;
using PdfUtil.Conversion.Internal.Model;
using PdfUtil.Internal.Images;

namespace PdfUtil.Conversion.Internal.Html;

/// <summary>
/// Turns a parsed and styled HTML document into the shared document model: block-level elements become paragraphs,
/// containers, lists, tables, images and rules (with their box styles); inline content becomes styled runs, links,
/// anchors, images and line breaks. White space is collapsed as CSS says. Scripts, forms, SVG and embedded objects
/// are left out with a warning.
/// </summary>
internal sealed class HtmlModelBuilder
{
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal) { "script", "style", "head", "title", "meta", "link", "template", "noscript", "base", "colgroup", "col", "param", "source", "track", "area", "map" };
    private static readonly HashSet<string> Forms = new(StringComparer.Ordinal) { "input", "select", "textarea", "button", "option", "optgroup", "datalist", "output", "progress", "meter" };
    private static readonly HashSet<string> Embedded = new(StringComparer.Ordinal) { "svg", "math", "canvas", "iframe", "object", "embed", "video", "audio", "picture" };

    private readonly Cascade _styles;
    private readonly Func<string, ImageData?> _image;
    private readonly Action<string> _warn;
    private readonly double _contentWidth;
    private readonly bool _fields;
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private int _headingAnchor;

    public HtmlModelBuilder(Cascade styles, Func<string, ImageData?> image, Action<string> warn, double contentWidth, bool fields = false)
    {
        _styles = styles;
        _image = image;
        _warn = warn;
        _contentWidth = contentWidth;
        _fields = fields;
    }

    /// <summary>The ids that exist in the document (link targets).</summary>
    public IReadOnlyCollection<string> Ids => _ids;

    public List<Block> Build(Element root) => BlockChildren(root);

    // ------------------------------------------------------------------ blocks

    private static bool IsBlock(ComputedStyle s) => s.Display is "block" or "list-item" or "table" or "table-caption" or "flow-root" or "table-row-group" or "table-header-group" or "table-footer-group" or "table-row" or "table-cell";

    private sealed class InlineContext
    {
        public Paragraph? Paragraph;
        public bool LastWasSpace = true;
    }

    private List<Block> BlockChildren(Element parent)
    {
        var blocks = new List<Block>();
        var context = new InlineContext();
        var parentStyle = _styles[parent];
        foreach (var node in parent.Children)
        {
            if (node is TextNode text)
            {
                AddText(context, parentStyle, parent, text.Text, Decorations.From(parentStyle), null, blocks);
                continue;
            }

            var element = (Element)node;
            if (!Include(element))
            {
                continue;
            }

            var style = _styles[element];
            if (style.Display == "none")
            {
                continue;
            }

            if (IsBlock(style) || element.Name is "ul" or "ol" or "hr" or "table")
            {
                Flush(context, blocks);
                foreach (var block in BuildBlock(element, style))
                {
                    blocks.Add(block);
                }
            }
            else
            {
                AddInline(context, parentStyle, parent, element, Decorations.From(parentStyle), null, blocks);
            }
        }

        Flush(context, blocks);
        return blocks;
    }

    private bool Include(Element e)
    {
        if (Skipped.Contains(e.Name))
        {
            return false;
        }

        if (Forms.Contains(e.Name))
        {
            _warn("Form controls are not converted.");
            return false;
        }

        if (Embedded.Contains(e.Name))
        {
            _warn($"<{e.Name}> content is not supported and was left out.");
            return false;
        }

        return true;
    }

    private void Flush(InlineContext context, List<Block> blocks)
    {
        if (context.Paragraph is { } p)
        {
            TrimTrailingSpace(p.Inlines);
            if (Inline.Flatten(p.Inlines).Any(i => i is not TextInline t || t.Text.Length > 0))
            {
                blocks.Add(p);
            }
        }

        context.Paragraph = null;
        context.LastWasSpace = true;
    }

    private static void TrimTrailingSpace(List<Inline> inlines)
    {
        for (var i = inlines.Count - 1; i >= 0; i--)
        {
            switch (inlines[i])
            {
                case TextInline t:
                    t.Text = t.Text.TrimEnd(' ');
                    if (t.Text.Length > 0)
                    {
                        return;
                    }

                    inlines.RemoveAt(i);
                    break;
                case LinkInline l:
                    TrimTrailingSpace(l.Children);
                    if (l.Children.Count > 0)
                    {
                        return;
                    }

                    break;
                default:
                    return;
            }
        }
    }

    private Paragraph NewParagraph(ComputedStyle style, Element? element)
    {
        var p = new Paragraph
        {
            Alignment = style.TextAlign,
            LineHeight = style.LineHeight,
            LineHeightPoints = style.LineHeightPoints,
            WhiteSpace = style.WhiteSpace,
            IsPreformatted = style.WhiteSpace is WhiteSpaceMode.Pre or WhiteSpaceMode.PreWrap,
            TextIndent = style.TextIndent.Resolve(_contentWidth),
            DefaultStyle = Run(style, Decorations.None, null),
            Widows = style.Widows,
            Orphans = style.Orphans,
        };
        if (element is not null && element.Name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
        {
            p.HeadingLevel = element.Name[1] - '0';
            p.KeepWithNext = true;
        }

        return p;
    }

    private IEnumerable<Block> BuildBlock(Element e, ComputedStyle style)
    {
        Block block;
        switch (e.Name)
        {
            case "ul" or "ol" or "menu" or "dir":
                block = BuildList(e, style);
                break;
            case "table":
                foreach (var b in BuildTable(e, style))
                {
                    yield return b;
                }

                yield break;
            case "hr":
                var box = style.ToBox();
                if (box.BorderTop.Width == 0 && box.BorderBottom.Width == 0)
                {
                    box = box with { BorderTop = new BorderSide(0.75, "#808080") };
                }

                block = new RuleBlock { Box = box };
                break;
            case "img":
                var image = Image(e, style);
                if (image is null)
                {
                    yield break;
                }

                block = new ImageBlock { Image = image.Image, Width = image.Width, Height = image.Height, AltText = image.AltText, Box = style.ToBox() };
                break;
            default:
                if (style.Display == "table")
                {
                    goto case "table";
                }

                var children = BlockChildren(e);
                if (children.Count == 1 && children[0] is Paragraph only && only.Box is null && only.HeadingLevel == 0 && !(e.Name is "li" or "td" or "th"))
                {
                    // A block with only inline content is one paragraph with the block's box and text settings.
                    var p = NewParagraph(style, e);
                    p.Inlines.AddRange(only.Inlines);
                    p.Anchors.AddRange(only.Anchors);
                    block = p;
                }
                else if (children.Count == 0 && e.Name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "p")
                {
                    // Empty paragraphs keep their margins in CSS; they produce no lines.
                    block = new ContainerBlock();
                }
                else
                {
                    var container = new ContainerBlock();
                    container.Children.AddRange(children);
                    block = container;
                }

                block.Box = style.ToBox();
                if (style.Background is not null && block.Box.Background is null)
                {
                    block.Box = block.Box with { Background = style.Background };
                }

                // Presentational attributes.
                if (e.Attr("align") is { } align && block is Paragraph para)
                {
                    para.Alignment = align.ToLowerInvariant() switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, "justify" => TextAlignment.Justify, _ => para.Alignment };
                }

                break;
        }

        if (e.Attr("id") is { Length: > 0 } id && _ids.Add(id))
        {
            block.Anchors.Insert(0, id);
        }

        if (block is Paragraph { HeadingLevel: > 0 } heading && heading.Anchors.Count == 0)
        {
            heading.Anchors.Add("_heading-" + (++_headingAnchor).ToString(CultureInfo.InvariantCulture));
        }

        yield return block;
    }

    private ListBlock BuildList(Element e, ComputedStyle style)
    {
        var type = style.ListStyleType;
        var list = new ListBlock
        {
            Kind = type switch
            {
                "decimal" or "decimal-leading-zero" or "arabic-indic" => ListKind.Decimal,
                "lower-alpha" or "lower-latin" or "lower-greek" => ListKind.LowerAlpha,
                "upper-alpha" or "upper-latin" => ListKind.UpperAlpha,
                "lower-roman" => ListKind.LowerRoman,
                "upper-roman" => ListKind.UpperRoman,
                _ => ListKind.Bullet,
            },
            BulletText = type switch { "circle" => "◦", "square" => "▪", "none" => "", "disc" => "•", _ => "•" },
            Start = int.TryParse(e.Attr("start"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var start) ? Math.Clamp(start, 0, 100_000) : 1,
            Box = style.ToBox() with { PaddingLeft = new Length(0) },
            Indent = style.PaddingLeft.Resolve(_contentWidth),
            MarkerStyle = Run(style, Decorations.None, null),
        };
        if (type == "none" && list.Kind == ListKind.Bullet)
        {
            list.BulletText = "";
        }

        ListItem? item = null;
        foreach (var node in e.Children)
        {
            if (node is Element child && child.Name == "li" && _styles[child].Display != "none")
            {
                item = new ListItem();
                list.Items.Add(item);
                var childStyle = _styles[child];
                var blocks = BlockChildren(child);
                if (blocks.Count == 0)
                {
                    blocks.Add(NewParagraph(childStyle, null));
                }

                if (child.Attr("id") is { Length: > 0 } id && _ids.Add(id))
                {
                    blocks[0].Anchors.Insert(0, id);
                }

                item.Blocks.AddRange(blocks);
            }
            else if (node is Element other && Include(other) && _styles[other].Display != "none")
            {
                // Content between items belongs to the previous item.
                item ??= AddItem(list);
                item.Blocks.AddRange(BuildBlock(other, _styles[other]));
            }
            else if (node is TextNode t && t.Text.Trim().Length > 0)
            {
                item ??= AddItem(list);
                var p = NewParagraph(style, null);
                p.Inlines.Add(new TextInline(Collapse(t.Text, style.WhiteSpace), Run(style, Decorations.From(style), null)));
                item.Blocks.Add(p);
            }
        }

        return list;
    }

    private static ListItem AddItem(ListBlock list)
    {
        var item = new ListItem();
        list.Items.Add(item);
        return item;
    }

    private IEnumerable<Block> BuildTable(Element e, ComputedStyle style)
    {
        var rows = new List<(Element Row, bool Header)>();
        var footer = new List<Element>();
        Paragraph? caption = null;
        void Collect(Element parent, bool header, bool foot)
        {
            foreach (var child in parent.Children.OfType<Element>())
            {
                var display = _styles[child].Display;
                if (display == "none")
                {
                    continue;
                }

                switch (child.Name)
                {
                    case "tr":
                        if (foot)
                        {
                            footer.Add(child);
                        }
                        else
                        {
                            rows.Add((child, header));
                        }

                        break;
                    case "thead":
                        Collect(child, header: rows.All(r => r.Header), foot: false);
                        break;
                    case "tfoot":
                        Collect(child, header: false, foot: true);
                        break;
                    case "tbody":
                        Collect(child, header: false, foot: false);
                        break;
                    case "caption":
                        var captionBlocks = BlockChildren(child);
                        caption = NewParagraph(_styles[child], child);
                        caption.Alignment = _styles[child].TextAlign == TextAlignment.Left ? TextAlignment.Center : _styles[child].TextAlign;
                        caption.Inlines.AddRange(captionBlocks.OfType<Paragraph>().SelectMany(p => p.Inlines));
                        break;
                }
            }
        }

        Collect(e, header: false, foot: false);
        var table = new TableBlock
        {
            Box = style.ToBox(),
            FixedLayout = style.FixedTableLayout,
            CollapseBorders = style.BorderCollapse,
            CellSpacing = style.BorderCollapse ? 0 : (e.Attr("cellspacing") is { } spacing && double.TryParse(spacing, NumberStyles.Float, CultureInfo.InvariantCulture, out var cs) ? cs * CssValues.PointsPerPixel : style.BorderSpacing),
        };
        if (style.Width is null && e.Attr("width") is { } width)
        {
            table.Box = table.Box with { Width = CssValues.ParseLength(width, style.FontSize, style.FontSize) };
        }

        if (style.Background is null && e.Attr("bgcolor") is { } bg && CssValues.TryParseColor(bg, "#000000", out var bgColor))
        {
            table.Box = table.Box with { Background = bgColor };
        }

        var border = double.TryParse(e.Attr("border"), NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? b * CssValues.PointsPerPixel : e.Attr("border") == "" ? 0.75 : 0;
        var padding = double.TryParse(e.Attr("cellpadding"), NumberStyles.Float, CultureInfo.InvariantCulture, out var pad) ? pad * CssValues.PointsPerPixel : (double?)null;
        if (border > 0 && table.Box.BorderTop.Width == 0)
        {
            var side = new BorderSide(border, "#808080");
            table.Box = table.Box with { BorderTop = side, BorderRight = side, BorderBottom = side, BorderLeft = side };
        }

        foreach (var (rowElement, header) in rows.Concat(footer.Select(f => (f, false))))
        {
            var row = new TableRow();
            var rowStyle = _styles[rowElement];
            foreach (var cellElement in rowElement.Children.OfType<Element>().Where(c => c.Name is "td" or "th"))
            {
                var cellStyle = _styles[cellElement];
                if (cellStyle.Display == "none")
                {
                    continue;
                }

                var cellBox = cellStyle.ToBox();
                if (padding is double p && cellBox.PaddingTop.Value == 0.75 && cellBox.PaddingLeft.Value == 0.75)
                {
                    cellBox = cellBox with { PaddingTop = new Length(p), PaddingRight = new Length(p), PaddingBottom = new Length(p), PaddingLeft = new Length(p) };
                }

                if (border > 0 && cellBox.BorderTop.Width == 0 && cellBox.BorderLeft.Width == 0)
                {
                    var side = new BorderSide(0.75, "#808080");
                    cellBox = cellBox with { BorderTop = side, BorderRight = side, BorderBottom = side, BorderLeft = side };
                }

                var cell = new TableCell
                {
                    ColumnSpan = Math.Clamp(int.TryParse(cellElement.Attr("colspan"), out var colspan) ? colspan : 1, 1, 1000),
                    RowSpan = Math.Clamp(int.TryParse(cellElement.Attr("rowspan"), out var rowspan) && rowspan > 0 ? rowspan : 1, 1, 65534),
                    Background = cellStyle.Background ?? rowStyle.Background ?? Color(cellElement.Attr("bgcolor")) ?? Color(rowElement.Attr("bgcolor")),
                    Box = cellBox,
                    VerticalAlignment = (cellStyle.VerticalAlign is "baseline" or "top" ? rowStyle.VerticalAlign : cellStyle.VerticalAlign) switch
                    {
                        "top" => CellAlignment.Top,
                        "bottom" => CellAlignment.Bottom,
                        "middle" => CellAlignment.Middle,
                        _ => (cellElement.Attr("valign") ?? rowElement.Attr("valign"))?.ToLowerInvariant() switch { "top" => CellAlignment.Top, "bottom" => CellAlignment.Bottom, _ => CellAlignment.Middle },
                    },
                    Width = cellStyle.Width ?? (cellElement.Attr("width") is { } cw ? CssValues.ParseLength(cw, cellStyle.FontSize, cellStyle.FontSize) : null),
                };
                var blocks = BlockChildren(cellElement);
                var align = cellElement.Attr("align")?.ToLowerInvariant() ?? rowElement.Attr("align")?.ToLowerInvariant();
                foreach (var para in blocks.OfType<Paragraph>())
                {
                    para.Alignment = align switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, _ => para.Alignment };
                }

                cell.Blocks.AddRange(blocks);
                if (cellElement.Attr("id") is { Length: > 0 } id && _ids.Add(id))
                {
                    cell.Blocks.Insert(0, new AnchorBlock { Anchors = { id } });
                }

                row.Cells.Add(cell);
            }

            if (row.Cells.Count > 0)
            {
                table.Rows.Add(row);
                if (header && table.Rows.Count == table.HeaderRows + 1)
                {
                    table.HeaderRows++;
                }
            }
        }

        // Column widths from <col> elements.
        var cols = e.Descendants().Where(c => c.Name == "col").ToList();
        if (cols.Count > 0)
        {
            table.ColumnSpecs = new List<Length?>();
            foreach (var col in cols)
            {
                var span = Math.Clamp(int.TryParse(col.Attr("span"), out var s) ? s : 1, 1, 1000);
                var spec = _styles[col].Width ?? (col.Attr("width") is { } w ? CssValues.ParseLength(w, 12, 12) : null);
                table.ColumnSpecs.AddRange(Enumerable.Repeat(spec, span));
            }
        }

        if (e.Attr("id") is { Length: > 0 } tableId && _ids.Add(tableId))
        {
            table.Anchors.Add(tableId);
        }

        if (caption is not null)
        {
            yield return caption;
        }

        if (table.Rows.Count > 0)
        {
            yield return table;
        }
    }

    private static string? Color(string? value) => value is not null && CssValues.TryParseColor(value, "#000000", out var c) ? c : null;

    // ------------------------------------------------------------------ inlines

    /// <summary>Text decorations in effect (they propagate to descendants), and an inline background.</summary>
    private readonly record struct Decorations(bool Underline, bool Strike, string? Background, VerticalPosition Position)
    {
        public static readonly Decorations None = new(false, false, null, VerticalPosition.Baseline);

        public static Decorations From(ComputedStyle s) => new(s.Underline, s.LineThrough, null, VerticalPosition.Baseline);
    }

    private RunStyle Run(ComputedStyle s, Decorations d, string? background) => new()
    {
        FontFamily = string.Join(",", s.FontFamily),
        GenericFamily = s.FontFamily.LastOrDefault(f => f is "serif" or "sans-serif" or "monospace"),
        Size = Math.Round(s.FontSize, 3),
        Bold = s.Bold,
        Italic = s.Italic,
        Underline = d.Underline,
        Strike = d.Strike,
        Color = s.Hidden ? "#FFFFFF" : s.Color == "#000000" ? null : s.Color,
        Background = background ?? d.Background,
        Position = d.Position,
    };

    private Paragraph Target(InlineContext context, ComputedStyle blockStyle, Element blockElement)
    {
        context.Paragraph ??= NewParagraph(blockStyle, null);
        return context.Paragraph;
    }

    private void AddText(InlineContext context, ComputedStyle style, Element blockElement, string text, Decorations decorations, LinkInline? link, List<Block> blocks)
    {
        var mode = style.WhiteSpace;
        if (mode is WhiteSpaceMode.Normal or WhiteSpaceMode.NoWrap or WhiteSpaceMode.PreLine)
        {
            if (context.Paragraph is null && text.Trim().Length == 0 && mode != WhiteSpaceMode.PreLine)
            {
                return;
            }
        }

        var paragraph = Target(context, _styles[blockElement], blockElement);
        var target = link?.Children ?? paragraph.Inlines;
        var runStyle = Run(style, decorations, null);
        if (mode is WhiteSpaceMode.Pre or WhiteSpaceMode.PreWrap)
        {
            // In pre, a newline right after the start tag is dropped.
            if (paragraph.Inlines.Count == 0 && blockElement.Name == "pre" && text.StartsWith('\n'))
            {
                text = text[1..];
            }

            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    target.Add(new BreakInline());
                }

                if (lines[i].Length > 0)
                {
                    AddRun(target, lines[i], runStyle);
                }
            }

            context.LastWasSpace = false;
            return;
        }

        if (mode == WhiteSpaceMode.PreLine)
        {
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    target.Add(new BreakInline());
                    context.LastWasSpace = true;
                }

                AddCollapsed(context, target, lines[i], runStyle);
            }

            return;
        }

        AddCollapsed(context, target, text, runStyle);
    }

    private void AddCollapsed(InlineContext context, List<Inline> target, string text, RunStyle style)
    {
        var collapsed = Collapse(text, WhiteSpaceMode.Normal);
        if (context.LastWasSpace)
        {
            collapsed = collapsed.TrimStart(' ');
        }

        if (collapsed.Length == 0)
        {
            return;
        }

        context.LastWasSpace = collapsed.EndsWith(' ');
        if (_fields)
        {
            AddWithFields(target, collapsed, style);
        }
        else
        {
            AddRun(target, collapsed, style);
        }
    }

    /// <summary>Header and footer text: <c>{page}</c> and <c>{pages}</c> become page number fields.</summary>
    private static void AddWithFields(List<Inline> target, string text, RunStyle style)
    {
        var i = 0;
        while (i < text.Length)
        {
            var page = text.IndexOf("{page}", i, StringComparison.Ordinal);
            var pages = text.IndexOf("{pages}", i, StringComparison.Ordinal);
            var next = page < 0 ? pages : pages < 0 ? page : Math.Min(page, pages);
            if (next < 0)
            {
                AddRun(target, text[i..], style);
                return;
            }

            if (next > i)
            {
                AddRun(target, text[i..next], style);
            }

            var isCount = next == pages;
            target.Add(new FieldInline { Kind = isCount ? FieldKind.PageCount : FieldKind.PageNumber, Style = style });
            i = next + (isCount ? 7 : 6);
        }
    }

    private static void AddRun(List<Inline> target, string text, RunStyle style)
    {
        if (target.Count > 0 && target[^1] is TextInline last && last.Style == style)
        {
            last.Text += text;
        }
        else
        {
            target.Add(new TextInline(text, style));
        }
    }

    private static string Collapse(string text, WhiteSpaceMode mode)
    {
        if (mode is WhiteSpaceMode.Pre or WhiteSpaceMode.PreWrap)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (c is ' ' or '\t' or '\n' or '\r' or '\f')
            {
                space = true;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
        }

        if (space)
        {
            sb.Append(' ');
        }

        return sb.ToString();
    }

    private void AddInline(InlineContext context, ComputedStyle parentStyle, Element blockElement, Element e, Decorations decorations, LinkInline? link, List<Block> blocks)
    {
        var style = _styles[e];
        if (style.Display == "none" || !Include(e))
        {
            return;
        }

        if (IsBlock(style) || e.Name is "ul" or "ol" or "table" or "hr")
        {
            // A block inside inline content ends the current paragraph.
            Flush(context, blocks);
            blocks.AddRange(BuildBlock(e, style));
            return;
        }

        var target = link?.Children;
        switch (e.Name)
        {
            case "br":
                (target ?? Target(context, _styles[blockElement], blockElement).Inlines).Add(new BreakInline());
                context.LastWasSpace = true;
                return;
            case "img":
                if (Image(e, style) is { } image)
                {
                    (target ?? Target(context, _styles[blockElement], blockElement).Inlines).Add(image);
                    context.LastWasSpace = false;
                }

                return;
            case "wbr":
                return;
        }

        if (e.Attr("id") is { Length: > 0 } id && _ids.Add(id))
        {
            (target ?? Target(context, _styles[blockElement], blockElement).Inlines).Add(new AnchorInline { Name = id });
        }

        if (_fields && e.Classes.Any(c => c is "pageNumber" or "totalPages"))
        {
            (target ?? Target(context, _styles[blockElement], blockElement).Inlines).Add(new FieldInline { Kind = e.Classes.Contains("totalPages") ? FieldKind.PageCount : FieldKind.PageNumber, Style = Run(style, decorations, null) });
            context.LastWasSpace = false;
            return;
        }

        var inner = decorations with
        {
            Underline = decorations.Underline || style.Underline,
            Strike = decorations.Strike || style.LineThrough,
            Background = style.Background ?? decorations.Background,
            Position = style.VerticalAlign switch { "super" => VerticalPosition.Superscript, "sub" => VerticalPosition.Subscript, _ => decorations.Position },
        };

        // The <font> element's presentational attributes.
        if (e.Name == "font" && e.Attr("color") is { } fontColor && CssValues.TryParseColor(fontColor, style.Color, out var fc) && fc is not null)
        {
            style.Color = fc;
        }

        var innerLink = link;
        if (e.Name == "a" && link is null && e.Attr("href") is { } href)
        {
            var created = Link(href);
            if (created is not null)
            {
                Target(context, _styles[blockElement], blockElement).Inlines.Add(created);
                innerLink = created;
            }
        }

        foreach (var node in e.Children)
        {
            if (node is TextNode text)
            {
                AddText(context, style, blockElement, text.Text, inner, innerLink, blocks);
            }
            else if (node is Element child)
            {
                AddInline(context, style, blockElement, child, inner, innerLink, blocks);
            }
        }
    }

    private LinkInline? Link(string href)
    {
        var value = href.Trim();
        if (value.StartsWith('#'))
        {
            return value.Length > 1 ? new LinkInline { Anchor = Uri.UnescapeDataString(value[1..]) } : null;
        }

        if (UrlSafety.Check(value) is { } safe)
        {
            return new LinkInline { Uri = BaseUri is not null && Uri.TryCreate(BaseUri, safe, out var absolute) && absolute.Scheme is "http" or "https" or "mailto" or "ftp" or "tel" ? absolute.ToString() : safe };
        }

        _warn("A link with an unsafe or unsupported address was not converted.");
        return null;
    }

    /// <summary>The base address for relative links.</summary>
    public Uri? BaseUri { get; init; }

    private ImageInline? Image(Element e, ComputedStyle style)
    {
        var src = e.Attr("src");
        if (string.IsNullOrWhiteSpace(src))
        {
            return null;
        }

        var data = _image(src.Trim());
        var alt = e.Attr("alt") ?? "";
        double? attrWidth = double.TryParse(e.Attr("width"), NumberStyles.Float, CultureInfo.InvariantCulture, out var aw) ? aw * CssValues.PointsPerPixel : null;
        double? attrHeight = double.TryParse(e.Attr("height"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ah) ? ah * CssValues.PointsPerPixel : null;
        double? width = style.Width?.Resolve(_contentWidth) ?? attrWidth;
        double? height = style.Height?.Resolve(_contentWidth) ?? attrHeight;
        if (data is null)
        {
            data = new ImageData { Placeholder = "The image could not be loaded." };
        }

        var naturalWidth = data.PixelWidth > 0 ? data.PixelWidth * CssValues.PointsPerPixel : 75;
        var naturalHeight = data.PixelHeight > 0 ? data.PixelHeight * CssValues.PointsPerPixel : 50;
        if (width is null && height is null)
        {
            (width, height) = (naturalWidth, naturalHeight);
        }
        else if (width is null)
        {
            width = height!.Value * naturalWidth / naturalHeight;
        }
        else if (height is null)
        {
            height = width.Value * naturalHeight / naturalWidth;
        }

        return new ImageInline { Image = data, Width = Math.Max(0.1, width.Value), Height = Math.Max(0.1, height!.Value), AltText = alt };
    }

    /// <summary>Reads an image's format and pixel size: PNG and JPEG are supported; anything else is a placeholder.</summary>
    public static ImageData Describe(byte[] data, long maxPixels, Action<string> warn)
    {
        try
        {
            if (ImageCodecs.IsJpeg(data))
            {
                var info = ImageCodecs.ReadJpeg(data);
                if ((long)info.Width * info.Height > maxPixels)
                {
                    warn("An image is larger than the configured maximum pixel count and was left out.");
                    return new ImageData { Placeholder = "too large", PixelWidth = info.Width, PixelHeight = info.Height };
                }

                return new ImageData { Data = data, MimeType = "image/jpeg", PixelWidth = info.Width, PixelHeight = info.Height };
            }

            if (ImageCodecs.IsPng(data))
            {
                var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16));
                var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20));
                if (width <= 0 || height <= 0 || (long)width * height > maxPixels)
                {
                    warn("An image is larger than the configured maximum pixel count (or has no size) and was left out.");
                    return new ImageData { Placeholder = "too large" };
                }

                return new ImageData { Data = data, MimeType = "image/png", PixelWidth = width, PixelHeight = height };
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            warn("An image is damaged and was left out.");
            return new ImageData { Placeholder = "damaged" };
        }

        var kind = data.Length > 4 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' ? "GIF" : data.Length > 4 && data[0] == '<' ? "SVG" : "this image format";
        warn($"{kind} images are not supported (only PNG and JPEG); a placeholder was drawn.");
        return new ImageData { Placeholder = kind + " is not supported" };
    }
}
