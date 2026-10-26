namespace PdfUtil.Conversion.Internal.Model;

// The shared document model. Readers (PDF analysis, HTML, DOCX) produce it; writers (HTML, DOCX, the PDF layout
// engine) consume it. It describes structure and style, not positions: sections with a page size and margins,
// blocks (paragraphs, headings, lists, tables, images) and inlines (styled text, links, breaks, images).

/// <summary>A whole document.</summary>
internal sealed class DocModel
{
    public string? Title { get; set; }

    public string? Language { get; set; }

    public string? Author { get; set; }

    public string? Subject { get; set; }

    public string? Keywords { get; set; }

    public DateTimeOffset? Created { get; set; }

    public DateTimeOffset? Modified { get; set; }

    public List<Section> Sections { get; } = new();

    /// <summary>Navigation entries (from PDF bookmarks).</summary>
    public List<OutlineEntry> Outline { get; } = new();

    public IEnumerable<Block> AllBlocks() => Sections.SelectMany(s => Descendants(s.Blocks));

    public static IEnumerable<Block> Descendants(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            var children = block switch
            {
                ListBlock list => list.Items.SelectMany(i => i.Blocks),
                ContainerBlock container => container.Children,
                TableBlock table => table.Rows.SelectMany(r => r.Cells).SelectMany(c => c.Blocks),
                _ => Enumerable.Empty<Block>(),
            };
            foreach (var child in Descendants(children))
            {
                yield return child;
            }
        }
    }
}

/// <summary>A bookmark: a title, a target anchor and children.</summary>
internal sealed record OutlineEntry(string Title, string? Anchor, string? Uri, List<OutlineEntry> Children);

/// <summary>Page margins in points.</summary>
internal readonly record struct Margins(double Top, double Right, double Bottom, double Left)
{
    public static readonly Margins Default = new(72, 72, 72, 72);
}

/// <summary>A run of blocks sharing a page size and margins, with optional header and footer content.</summary>
internal sealed class Section
{
    public double PageWidth { get; set; } = 612;

    public double PageHeight { get; set; } = 792;

    public Margins Margins { get; set; } = Margins.Default;

    public List<Block> Blocks { get; } = new();

    /// <summary>Blocks repeated at the top of every page, inside the top margin (may hold page number fields).</summary>
    public List<Block> Header { get; } = new();

    /// <summary>Blocks repeated at the bottom of every page, inside the bottom margin.</summary>
    public List<Block> Footer { get; } = new();

    /// <summary>Header and footer of the section's first page when <see cref="DifferentFirstPage"/> is set.</summary>
    public List<Block> FirstHeader { get; } = new();

    public List<Block> FirstFooter { get; } = new();

    public bool DifferentFirstPage { get; set; }

    /// <summary>Header and footer of even pages, when set (null: the default ones).</summary>
    public List<Block>? EvenHeader { get; set; }

    public List<Block>? EvenFooter { get; set; }

    /// <summary>Distance from the top edge to the header, in points.</summary>
    public double HeaderDistance { get; set; } = 36;

    /// <summary>Distance from the bottom edge to the footer, in points; null for <see cref="HeaderDistance"/>.</summary>
    public double? FooterDistance { get; set; }
}

/// <summary>A length in points or a percentage of the containing width.</summary>
internal readonly record struct Length(double Value, bool IsPercent = false)
{
    public double Resolve(double containing) => IsPercent ? Value * containing / 100 : Value;
}

/// <summary>One border side: width in points (0 for none) and colour.</summary>
internal readonly record struct BorderSide(double Width, string Color)
{
    public static readonly BorderSide None = new(0, "#000000");
}

internal enum BreakRule
{
    Auto,
    Always,
    Avoid,
}

/// <summary>Box properties of a block (CSS box model): spacing, borders, background, width and page breaking.</summary>
internal sealed record BoxStyle
{
    public static readonly BoxStyle Empty = new();

    public Length MarginTop { get; init; }

    public Length MarginRight { get; init; }

    public Length MarginBottom { get; init; }

    public Length MarginLeft { get; init; }

    public Length PaddingTop { get; init; }

    public Length PaddingRight { get; init; }

    public Length PaddingBottom { get; init; }

    public Length PaddingLeft { get; init; }

    public BorderSide BorderTop { get; init; } = BorderSide.None;

    public BorderSide BorderRight { get; init; } = BorderSide.None;

    public BorderSide BorderBottom { get; init; } = BorderSide.None;

    public BorderSide BorderLeft { get; init; } = BorderSide.None;

    public string? Background { get; init; }

    /// <summary>The content width, when set.</summary>
    public Length? Width { get; init; }

    public BreakRule BreakBefore { get; init; }

    public BreakRule BreakAfter { get; init; }

    public bool AvoidBreakInside { get; init; }
}

internal enum WhiteSpaceMode
{
    Normal,
    NoWrap,
    Pre,
    PreWrap,
    PreLine,
}

internal enum CellAlignment
{
    Top,
    Middle,
    Bottom,
}

/// <summary>A generic block container (div, section, blockquote…): its children laid out inside its box.</summary>
internal sealed class ContainerBlock : Block
{
    public List<Block> Children { get; } = new();
}

internal enum TextAlignment
{
    Left,
    Center,
    Right,
    Justify,
}

/// <summary>Base of all blocks. <see cref="Anchors"/> are link targets that point at the start of the block.</summary>
internal abstract class Block
{
    public List<string> Anchors { get; } = new();

    /// <summary>Box properties; null for none.</summary>
    public BoxStyle? Box { get; set; }
}

/// <summary>A paragraph or a heading (<see cref="HeadingLevel"/> 1–6).</summary>
internal sealed class Paragraph : Block
{
    public int HeadingLevel { get; set; }

    public TextAlignment Alignment { get; set; }

    /// <summary>Left indent in points.</summary>
    public double IndentLeft { get; set; }

    /// <summary>Preformatted (monospace) text: line breaks and spacing are kept.</summary>
    public bool IsPreformatted { get; set; }

    public bool PageBreakBefore { get; set; }

    public bool KeepWithNext { get; set; }

    public double? SpaceBefore { get; set; }

    public double? SpaceAfter { get; set; }

    /// <summary>First-line indent in points.</summary>
    public double TextIndent { get; set; }

    /// <summary>Line height as a multiple of the font size; null for the default (about 1.2).</summary>
    public double? LineHeight { get; set; }

    /// <summary>Line height in points, overriding <see cref="LineHeight"/>.</summary>
    public double? LineHeightPoints { get; set; }

    public WhiteSpaceMode WhiteSpace { get; set; }

    /// <summary>Style for text the paragraph creates itself (list markers, empty lines).</summary>
    public RunStyle? DefaultStyle { get; set; }

    public int Widows { get; set; } = 2;

    public int Orphans { get; set; } = 2;

    public List<Inline> Inlines { get; } = new();

    public string PlainText => string.Concat(Inline.Flatten(Inlines).Select(i => i switch
    {
        TextInline t => t.Text,
        BreakInline => "\n",
        _ => "",
    }));
}

internal enum ListKind
{
    Bullet,
    Decimal,
    LowerAlpha,
    UpperAlpha,
    LowerRoman,
    UpperRoman,
}

/// <summary>A bulleted or numbered list; items hold blocks, so lists nest.</summary>
internal sealed class ListBlock : Block
{
    public ListKind Kind { get; set; }

    public int Start { get; set; } = 1;

    /// <summary>The bullet character for bulleted lists; empty for none.</summary>
    public string BulletText { get; set; } = "\u2022";

    /// <summary>Indent of the item text from the list's left edge, in points.</summary>
    public double Indent { get; set; } = 30;

    public RunStyle? MarkerStyle { get; set; }

    public bool IsOrdered => Kind != ListKind.Bullet;

    public List<ListItem> Items { get; } = new();
}

internal sealed class ListItem
{
    public List<Block> Blocks { get; } = new();
}

internal sealed class TableBlock : Block
{
    /// <summary>Column widths in points, when known.</summary>
    public List<double>? ColumnWidths { get; set; }

    /// <summary>Leading rows that are header rows (repeated on each page).</summary>
    public int HeaderRows { get; set; }

    /// <summary>Column widths as given (points or percentages), when known; null entries are automatic.</summary>
    public List<Length?>? ColumnSpecs { get; set; }

    /// <summary>Fixed layout: widths from the first row and the column specs, not from the content.</summary>
    public bool FixedLayout { get; set; }

    /// <summary>Space between cells in points (0 for collapsed borders).</summary>
    public double CellSpacing { get; set; }

    public bool CollapseBorders { get; set; } = true;

    public List<TableRow> Rows { get; } = new();
}

internal sealed class TableRow
{
    public List<TableCell> Cells { get; } = new();
}

internal sealed class TableCell
{
    public int ColumnSpan { get; set; } = 1;

    public int RowSpan { get; set; } = 1;

    public string? Background { get; set; }

    /// <summary>Cell box: padding and borders (background is <see cref="Background"/>).</summary>
    public BoxStyle? Box { get; set; }

    public CellAlignment VerticalAlignment { get; set; }

    /// <summary>The width the cell asks for, when given.</summary>
    public Length? Width { get; set; }

    public List<Block> Blocks { get; } = new();
}

/// <summary>An image on its own line.</summary>
internal sealed class ImageBlock : Block
{
    public required ImageData Image { get; init; }

    /// <summary>Display width in points.</summary>
    public double Width { get; set; }

    /// <summary>Display height in points.</summary>
    public double Height { get; set; }

    public string? AltText { get; set; }
}

/// <summary>A horizontal rule.</summary>
internal sealed class RuleBlock : Block
{
}

/// <summary>A forced page break.</summary>
internal sealed class PageBreakBlock : Block
{
}

/// <summary>An empty block that only carries anchors (for example the start of a PDF page).</summary>
internal sealed class AnchorBlock : Block
{
}

internal enum VerticalPosition
{
    Baseline,
    Superscript,
    Subscript,
}

/// <summary>Character formatting. Null members inherit from the surrounding block.</summary>
internal sealed record RunStyle
{
    public static readonly RunStyle Plain = new();

    public string? FontFamily { get; init; }

    /// <summary>Generic family: <c>serif</c>, <c>sans-serif</c> or <c>monospace</c>.</summary>
    public string? GenericFamily { get; init; }

    /// <summary>Size in points.</summary>
    public double? Size { get; init; }

    public bool Bold { get; init; }

    public bool Italic { get; init; }

    public bool Underline { get; init; }

    public bool Strike { get; init; }

    /// <summary>Text colour as <c>#RRGGBB</c>; null for the default (black).</summary>
    public string? Color { get; init; }

    public string? Background { get; init; }

    public VerticalPosition Position { get; init; }
}

internal abstract class Inline
{
    public static IEnumerable<Inline> Flatten(IEnumerable<Inline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is LinkInline link)
            {
                foreach (var child in Flatten(link.Children))
                {
                    yield return child;
                }
            }
            else
            {
                yield return inline;
            }
        }
    }
}

internal sealed class TextInline : Inline
{
    public TextInline(string text, RunStyle style)
    {
        Text = text;
        Style = style;
    }

    public string Text { get; set; }

    public RunStyle Style { get; set; }
}

/// <summary>A hyperlink: an external URI or an anchor in the document.</summary>
internal sealed class LinkInline : Inline
{
    public string? Uri { get; init; }

    public string? Anchor { get; init; }

    public List<Inline> Children { get; } = new();
}

internal sealed class BreakInline : Inline
{
}

internal sealed class ImageInline : Inline
{
    public required ImageData Image { get; init; }

    public double Width { get; set; }

    public double Height { get; set; }

    public string? AltText { get; set; }
}

internal enum FieldKind
{
    PageNumber,
    PageCount,
}

/// <summary>A page number or page count, filled in at layout (headers and footers).</summary>
internal sealed class FieldInline : Inline
{
    public required FieldKind Kind { get; init; }

    public required RunStyle Style { get; init; }
}

/// <summary>An anchor at a position inside a paragraph.</summary>
internal sealed class AnchorInline : Inline
{
    public required string Name { get; init; }
}

/// <summary>
/// Encoded image data (PNG or JPEG), or a placeholder for an image that could not be converted (<see cref="Data"/>
/// null, with <see cref="Placeholder"/> saying why).
/// </summary>
internal sealed class ImageData
{
    public byte[]? Data { get; init; }

    /// <summary><c>image/png</c> or <c>image/jpeg</c>.</summary>
    public string MimeType { get; init; } = "image/png";

    public int PixelWidth { get; init; }

    public int PixelHeight { get; init; }

    public string? Placeholder { get; init; }

    public bool IsPlaceholder => Data is null;

    public string Extension => MimeType == "image/jpeg" ? "jpg" : "png";
}
