using System.Globalization;
using System.Text;
using PdfUtil.Conversion.Internal.Model;

namespace PdfUtil.Conversion.Internal.Layout;

/// <summary>
/// A horizontal strip of laid-out content: a line of text, a table row group, an image, or space. Pagination places
/// whole slices; ops are positioned with absolute x and y relative to the slice top.
/// </summary>
internal sealed class Slice
{
    public double Height { get; set; }

    public List<DrawOp> Ops { get; } = new();

    public List<LinkArea> Links { get; } = new();

    public List<(string Name, double Y)> Anchors { get; } = new();

    /// <summary>Vertical margin: dropped at the top of a page.</summary>
    public bool IsSpacer { get; init; }

    public BreakRule BreakBefore { get; set; }

    public bool KeepWithNext { get; set; }

    /// <summary>Paragraph lines: the paragraph, this line's index and the line count, for widows and orphans.</summary>
    public int Paragraph { get; init; }

    public int Line { get; init; }

    public int Lines { get; init; }

    public int Widows { get; init; } = 2;

    public int Orphans { get; init; } = 2;

    /// <summary>Slices of a block with <c>break-inside: avoid</c> share a group.</summary>
    public int AvoidGroup { get; set; }

    /// <summary>The boxes (backgrounds and borders) this slice lies in, outermost first.</summary>
    public List<int> Containers { get; set; } = new();

    public int Opens { get; init; } = -1;

    public int Closes { get; init; } = -1;

    /// <summary>Table rows: the table and its header rows, repeated when the table continues on a new page.</summary>
    public int Table { get; init; }

    public bool IsTableHeader { get; init; }

    public List<Slice>? RepeatHeader { get; init; }

    /// <summary>The first baseline in the slice, for list markers.</summary>
    public double? Baseline { get; init; }
}

/// <summary>A box with a background or borders, drawn behind the slices it contains.</summary>
internal sealed record ContainerBox(int Id, BoxStyle Box, double Left, double Width);

/// <summary>A finished page: drawing operations, links and anchors in page coordinates (top-left origin, y down).</summary>
internal sealed class LaidOutPage
{
    public required double Width { get; init; }

    public required double Height { get; init; }

    public List<DrawOp> Ops { get; } = new();

    public List<LinkArea> Links { get; } = new();

    public List<(string Name, double Y)> Anchors { get; } = new();
}

/// <summary>
/// The layout engine: block formatting (CSS box model with collapsing sibling margins, borders, padding, backgrounds
/// and widths), lists with markers, tables (automatic or fixed column widths, column and row spans, repeated header
/// rows), images scaled to fit, and pagination with forced breaks, avoided breaks, keep-with-next, widows and
/// orphans. Headers and footers are laid out per page with page number fields.
/// </summary>
internal sealed class LayoutEngine
{
    private readonly FontProvider _fonts;
    private readonly Action<string> _warn;
    private readonly double _maxImageHeight;
    private readonly Dictionary<int, ContainerBox> _containers = new();
    private readonly StringBuilder _shownText = new();
    private int _nextId = 1;
    private int _pageNumber;
    private int _pageCount;

    public LayoutEngine(FontProvider fonts, Action<string> warn, double maxImageHeight)
    {
        _fonts = fonts;
        _warn = warn;
        _maxImageHeight = maxImageHeight;
    }

    /// <summary>The text shown in the body (for verification against extraction).</summary>
    public string ShownText => _shownText.ToString();

    private LineLayout Lines(StringBuilder? sink = null) => new(_fonts, Field, sink ?? _shownText);

    private string Field(FieldKind kind) => (kind == FieldKind.PageNumber ? _pageNumber : _pageCount).ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ flow

    /// <summary>Collects slices for a column, with pending (collapsing) margins, breaks and anchors.</summary>
    private sealed class Flow
    {
        public List<Slice> Slices { get; } = new();

        public List<int> Containers { get; } = new();

        public double PendingMargin { get; set; }

        public bool PendingBreak { get; set; }

        public List<string> PendingAnchors { get; } = new();

        public void Margin(double margin) => PendingMargin = Math.Max(PendingMargin, margin);

        /// <summary>Writes the pending margin now, outside any box that is about to open.</summary>
        public void FlushMargin()
        {
            if (PendingMargin > 0)
            {
                Slices.Add(new Slice { IsSpacer = true, Height = PendingMargin, Containers = Containers.ToList(), BreakBefore = PendingBreak ? BreakRule.Always : BreakRule.Auto });
                PendingMargin = 0;
                PendingBreak = false;
            }
        }

        public void Emit(Slice slice)
        {
            if (PendingMargin > 0)
            {
                var spacer = new Slice { IsSpacer = true, Height = PendingMargin, Containers = Containers.ToList(), BreakBefore = PendingBreak ? BreakRule.Always : BreakRule.Auto };
                Slices.Add(spacer);
                PendingMargin = 0;
                PendingBreak = false;
            }

            if (PendingBreak)
            {
                slice.BreakBefore = BreakRule.Always;
                PendingBreak = false;
            }

            foreach (var anchor in PendingAnchors)
            {
                slice.Anchors.Add((anchor, 0));
            }

            PendingAnchors.Clear();
            if (slice.Containers.Count == 0)
            {
                slice.Containers = Containers.ToList();
            }

            Slices.Add(slice);
        }
    }

    public List<Slice> LayoutBlocks(IEnumerable<Block> blocks, double x, double width)
    {
        var flow = new Flow();
        foreach (var block in blocks)
        {
            LayoutBlock(flow, block, x, width);
        }

        if (flow.PendingAnchors.Count > 0)
        {
            flow.Emit(new Slice());
        }

        return flow.Slices;
    }

    private static bool HasDecoration(BoxStyle box) =>
        box.Background is not null || box.BorderTop.Width > 0 || box.BorderRight.Width > 0 || box.BorderBottom.Width > 0 || box.BorderLeft.Width > 0;

    private void LayoutBlock(Flow flow, Block block, double x, double width)
    {
        if (block is PageBreakBlock)
        {
            flow.PendingBreak = true;
            return;
        }

        if (block is AnchorBlock)
        {
            flow.PendingAnchors.AddRange(block.Anchors);
            return;
        }

        var box = block.Box ?? BoxStyle.Empty;
        var ml = box.MarginLeft.Resolve(width);
        var mr = box.MarginRight.Resolve(width);
        var bl = box.BorderLeft.Width;
        var br = box.BorderRight.Width;
        var pl = box.PaddingLeft.Resolve(width);
        var pr = box.PaddingRight.Resolve(width);
        var pt = box.PaddingTop.Resolve(width);
        var pb = box.PaddingBottom.Resolve(width);
        var borderLeft = x + ml;
        var borderWidth = Math.Max(1, width - ml - mr);
        if (box.Width is { } specified && block is not TableBlock)
        {
            borderWidth = Math.Min(borderWidth, specified.Resolve(width) + bl + br + pl + pr);
        }

        var contentLeft = borderLeft + bl + pl;
        var contentWidth = Math.Max(1, borderWidth - bl - br - pl - pr);
        TableColumns? columns = null;
        if (block is TableBlock table)
        {
            columns = Columns(table, contentWidth, box.Width?.Resolve(width));
            contentWidth = columns.Width;
            borderWidth = contentWidth + bl + br + pl + pr;
        }

        flow.Margin(box.MarginTop.Resolve(width));
        if (box.BreakBefore == BreakRule.Always)
        {
            flow.PendingBreak = true;
        }

        flow.PendingAnchors.AddRange(block.Anchors);
        var first = flow.Slices.Count;
        var decorated = HasDecoration(box) || pt > 0 || pb > 0;
        var id = 0;
        if (decorated)
        {
            flow.FlushMargin();
            id = _nextId++;
            _containers[id] = new ContainerBox(id, box, borderLeft, borderWidth);
            flow.Containers.Add(id);
            flow.Emit(new Slice { Height = box.BorderTop.Width + pt, Opens = id });
        }

        var start = flow.Slices.Count;
        switch (block)
        {
            case Paragraph p:
                LayoutParagraph(flow, p, contentLeft, contentWidth);
                break;
            case ContainerBlock container:
                foreach (var child in container.Children)
                {
                    LayoutBlock(flow, child, contentLeft, contentWidth);
                }

                break;
            case ListBlock list:
                LayoutList(flow, list, contentLeft, contentWidth);
                break;
            case TableBlock t:
                LayoutTable(flow, t, columns!, contentLeft);
                break;
            case ImageBlock image:
                var (w, h) = Fit(image.Width, image.Height, contentWidth);
                var slice = new Slice { Height = h };
                slice.Ops.Add(new ImageOp(image.Image, contentLeft, 0, w, h));
                flow.Emit(slice);
                break;
        }

        if (decorated)
        {
            flow.Emit(new Slice { Height = pb + box.BorderBottom.Width, Closes = id });
            flow.Containers.Remove(id);
        }

        if (box.AvoidBreakInside && flow.Slices.Count - first > 1)
        {
            var group = _nextId++;
            foreach (var s in flow.Slices.Skip(first))
            {
                s.AvoidGroup = s.AvoidGroup == 0 ? group : s.AvoidGroup;
            }
        }

        if (block is Paragraph { KeepWithNext: true } or Paragraph { HeadingLevel: > 0 })
        {
            foreach (var s in flow.Slices.Skip(start))
            {
                s.KeepWithNext = true;
            }
        }

        flow.Margin(box.MarginBottom.Resolve(width));
        if (box.BreakAfter == BreakRule.Always)
        {
            flow.PendingBreak = true;
        }
    }

    private (double Width, double Height) Fit(double width, double height, double maxWidth)
    {
        var scale = Math.Min(1, Math.Min(maxWidth / Math.Max(width, 0.01), _maxImageHeight / Math.Max(height, 0.01)));
        return (width * scale, height * scale);
    }

    private void LayoutParagraph(Flow flow, Paragraph p, double x, double width)
    {
        foreach (var image in Inline.Flatten(p.Inlines).OfType<ImageInline>())
        {
            (image.Width, image.Height) = Fit(image.Width, image.Height, width);
        }

        var lines = Lines().Layout(p, x, width);
        var id = _nextId++;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var slice = new Slice { Height = line.Height, Paragraph = id, Line = i, Lines = lines.Count, Widows = p.Widows, Orphans = p.Orphans, Baseline = line.Baseline };
            slice.Ops.AddRange(line.Ops);
            slice.Links.AddRange(line.Links);
            slice.Anchors.AddRange(line.Anchors.Select(a => (a, 0.0)));
            flow.Emit(slice);
        }
    }

    private void LayoutList(Flow flow, ListBlock list, double x, double width)
    {
        var number = list.Start;
        var indent = Math.Min(list.Indent, width / 2);
        foreach (var item in list.Items)
        {
            var before = flow.Slices.Count;
            foreach (var child in item.Blocks)
            {
                LayoutBlock(flow, child, x + indent, width - indent);
            }

            var marker = Marker(list, number++);
            if (marker.Length == 0)
            {
                continue;
            }

            // The marker sits on the first line of the item, right-aligned before the item's text.
            var target = flow.Slices.Skip(before).FirstOrDefault(s => s.Baseline is not null) ?? flow.Slices.Skip(before).FirstOrDefault(s => !s.IsSpacer);
            if (target is null)
            {
                var empty = new Slice { Height = (list.MarkerStyle?.Size ?? 12) * 1.2, Baseline = (list.MarkerStyle?.Size ?? 12) * 0.9 };
                flow.Emit(empty);
                target = empty;
            }

            var style = list.MarkerStyle ?? RunStyle.Plain;
            var size = style.Size ?? 12;
            var squareBullet = false;
            if (!_fonts.CanShow(marker))
            {
                // Bullets the fonts lack: a white bullet becomes "o" (as Word does), a black square is drawn.
                squareBullet = marker == "\u25AA";
                marker = marker == "\u25E6" ? "o" : "\u2022";
            }

            if (squareBullet)
            {
                var side = size * 0.3;
                var markerBaseline = target.Baseline ?? (size * 0.9);
                target.Ops.Insert(0, new RectOp(x + indent - (0.4 * size) - side, markerBaseline - (size * 0.4), side, side, style.Color ?? "#000000"));
                continue;
            }

            var segments = _fonts.Segments(style, marker);
            var markerWidth = segments.Sum(s => s.Font.Width(s.Text, size));
            var cx = x + indent - (0.4 * size) - markerWidth;
            var baseline = target.Baseline ?? (size * 0.9);
            var at = 0;
            foreach (var (font, text) in segments)
            {
                // Markers come first in the content, so text extraction reads them before the item.
                target.Ops.Insert(at++, new TextOp(font, size, cx, baseline, text, style.Color ?? "#000000"));
                _shownText.Append(text);
                cx += font.Width(text, size);
            }
        }
    }

    internal static string Marker(ListBlock list, int n) => list.Kind switch
    {
        ListKind.Bullet => list.BulletText,
        ListKind.Decimal => n.ToString(CultureInfo.InvariantCulture) + ".",
        ListKind.LowerAlpha => Alpha(n).ToLowerInvariant() + ".",
        ListKind.UpperAlpha => Alpha(n) + ".",
        ListKind.LowerRoman => Roman(n).ToLowerInvariant() + ".",
        _ => Roman(n) + ".",
    };

    private static string Alpha(int n)
    {
        var sb = new StringBuilder();
        n = Math.Max(1, n);
        while (n > 0)
        {
            n--;
            sb.Insert(0, (char)('A' + (n % 26)));
            n /= 26;
        }

        return sb.ToString();
    }

    private static string Roman(int n)
    {
        if (n is <= 0 or >= 4000)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }

        var values = new[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") };
        var sb = new StringBuilder();
        foreach (var (value, text) in values)
        {
            while (n >= value)
            {
                sb.Append(text);
                n -= value;
            }
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ intrinsic widths

    private (double Min, double Max) Intrinsic(IEnumerable<Block> blocks, int depth)
    {
        double min = 0, max = 0;
        foreach (var block in blocks)
        {
            var (bmin, bmax) = Intrinsic(block, depth);
            min = Math.Max(min, bmin);
            max = Math.Max(max, bmax);
        }

        return (min, max);
    }

    private (double Min, double Max) Intrinsic(Block block, int depth)
    {
        if (depth > 64)
        {
            return (0, 0);
        }

        var box = block.Box ?? BoxStyle.Empty;
        var horizontal = (box.MarginLeft.IsPercent ? 0 : box.MarginLeft.Value) + (box.MarginRight.IsPercent ? 0 : box.MarginRight.Value)
            + box.BorderLeft.Width + box.BorderRight.Width + (box.PaddingLeft.IsPercent ? 0 : box.PaddingLeft.Value) + (box.PaddingRight.IsPercent ? 0 : box.PaddingRight.Value);
        var (min, max) = block switch
        {
            Paragraph p => Lines(new StringBuilder()).Intrinsic(p),
            ContainerBlock c => Intrinsic(c.Children, depth + 1),
            ListBlock l => Intrinsic(l.Items.SelectMany(i => i.Blocks), depth + 1) is var (a, b) ? (a + l.Indent, b + l.Indent) : default,
            TableBlock t => TableIntrinsic(t, depth + 1),
            ImageBlock i => (i.Width, i.Width),
            _ => (0.0, 0.0),
        };
        if (box.Width is { IsPercent: false } fixedWidth)
        {
            (min, max) = (Math.Max(min, fixedWidth.Value), fixedWidth.Value);
        }

        return (min + horizontal, max + horizontal);
    }

    // ------------------------------------------------------------------ tables

    private sealed record PlacedCell(TableCell Cell, int Row, int Column, int RowSpan, int ColumnSpan);

    private sealed class TableColumns
    {
        public required List<PlacedCell> Cells { get; init; }

        public required double[] Widths { get; init; }

        public required int Rows { get; init; }

        public required double Spacing { get; init; }

        public double Width => Widths.Sum() + (Spacing * (Widths.Length + 1));

        public double Left(int column) => Spacing + Widths.Take(column).Sum() + (Spacing * column);

        public double SpanWidth(int column, int span) => Widths.Skip(column).Take(span).Sum() + (Spacing * (span - 1));
    }

    private static List<PlacedCell> Place(TableBlock table, out int columns)
    {
        var placed = new List<PlacedCell>();
        var occupied = new HashSet<(int, int)>();
        columns = 0;
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var c = 0;
            foreach (var cell in table.Rows[r].Cells)
            {
                while (occupied.Contains((r, c)))
                {
                    c++;
                }

                var rowSpan = Math.Min(cell.RowSpan, table.Rows.Count - r);
                var colSpan = Math.Min(cell.ColumnSpan, 1000);
                for (var rr = r; rr < r + rowSpan; rr++)
                {
                    for (var cc = c; cc < c + colSpan; cc++)
                    {
                        occupied.Add((rr, cc));
                    }
                }

                placed.Add(new PlacedCell(cell, r, c, rowSpan, colSpan));
                c += colSpan;
                columns = Math.Max(columns, c);
            }
        }

        if (columns > 1000)
        {
            throw new PdfLimitExceededException("A table has more than 1000 columns.");
        }

        return placed;
    }

    private static double CellHorizontal(TableCell cell, double width)
    {
        var box = cell.Box ?? BoxStyle.Empty;
        return box.PaddingLeft.Resolve(width) + box.PaddingRight.Resolve(width) + box.BorderLeft.Width + box.BorderRight.Width;
    }

    private (double Min, double Max) TableIntrinsic(TableBlock table, int depth)
    {
        var cells = Place(table, out var columns);
        var mins = new double[columns];
        var maxs = new double[columns];
        foreach (var placed in cells.Where(c => c.ColumnSpan == 1))
        {
            var (min, max) = Intrinsic(placed.Cell.Blocks, depth);
            var extra = CellHorizontal(placed.Cell, 0);
            mins[placed.Column] = Math.Max(mins[placed.Column], min + extra);
            maxs[placed.Column] = Math.Max(maxs[placed.Column], max + extra);
        }

        var spacing = table.CellSpacing * (columns + 1);
        return (mins.Sum() + spacing, maxs.Sum() + spacing);
    }

    private TableColumns Columns(TableBlock table, double available, double? specifiedWidth)
    {
        var cells = Place(table, out var columns);
        columns = Math.Max(columns, 1);
        var spacing = table.CellSpacing;
        var inner = Math.Max(1, (specifiedWidth ?? available) - (spacing * (columns + 1)));
        var mins = new double[columns];
        var maxs = new double[columns];
        var specs = new double?[columns];
        for (var c = 0; c < columns && table.ColumnSpecs is not null && c < table.ColumnSpecs.Count; c++)
        {
            specs[c] = table.ColumnSpecs[c]?.Resolve(inner);
        }

        foreach (var placed in cells.OrderBy(p => p.ColumnSpan))
        {
            var extra = CellHorizontal(placed.Cell, inner);
            var (min, max) = table.FixedLayout ? (extra, extra) : Intrinsic(placed.Cell.Blocks, 1);
            min += table.FixedLayout ? 0 : extra;
            max += table.FixedLayout ? 0 : extra;
            if (placed.Cell.Width is { } w && placed.ColumnSpan == 1)
            {
                specs[placed.Column] ??= w.Resolve(inner);
            }

            if (placed.ColumnSpan == 1)
            {
                mins[placed.Column] = Math.Max(mins[placed.Column], min);
                maxs[placed.Column] = Math.Max(maxs[placed.Column], max);
                continue;
            }

            // A spanning cell spreads what its columns lack evenly over them.
            var span = Enumerable.Range(placed.Column, placed.ColumnSpan).ToList();
            var gap = spacing * (placed.ColumnSpan - 1);
            var minLack = min - gap - span.Sum(c => mins[c]);
            var maxLack = max - gap - span.Sum(c => maxs[c]);
            foreach (var c in span)
            {
                if (minLack > 0)
                {
                    mins[c] += minLack / span.Count;
                }

                if (maxLack > 0)
                {
                    maxs[c] += maxLack / span.Count;
                }
            }
        }

        var widths = new double[columns];
        if (table.FixedLayout && (specifiedWidth is not null || specs.All(s => s is not null)))
        {
            var specified = specs.Where(s => s is not null).Sum(s => s!.Value);
            var free = specs.Count(s => s is null);
            for (var c = 0; c < columns; c++)
            {
                widths[c] = specs[c] ?? Math.Max(0, (inner - specified) / Math.Max(1, free));
            }
        }
        else
        {
            for (var c = 0; c < columns; c++)
            {
                if (specs[c] is double s)
                {
                    mins[c] = Math.Max(mins[c], Math.Min(s, inner));
                    maxs[c] = Math.Max(mins[c], s);
                }

                maxs[c] = Math.Max(maxs[c], mins[c]);
            }

            var sumMin = mins.Sum();
            var sumMax = maxs.Sum();
            var target = specifiedWidth is not null ? inner : Math.Min(inner, sumMax);
            if (sumMax <= target)
            {
                var grow = sumMax > 0 ? target / sumMax : 1;
                for (var c = 0; c < columns; c++)
                {
                    widths[c] = sumMax > 0 ? maxs[c] * grow : target / columns;
                }
            }
            else if (sumMin >= target)
            {
                for (var c = 0; c < columns; c++)
                {
                    widths[c] = sumMin > 0 ? mins[c] * target / sumMin : target / columns;
                }
            }
            else
            {
                var share = (target - sumMin) / (sumMax - sumMin);
                for (var c = 0; c < columns; c++)
                {
                    widths[c] = mins[c] + ((maxs[c] - mins[c]) * share);
                }
            }
        }

        // Rounding and floating error never make the table wider than the space it has.
        var total = widths.Sum();
        if (total > inner + 0.01)
        {
            for (var c = 0; c < columns; c++)
            {
                widths[c] *= inner / total;
            }
        }

        return new TableColumns { Cells = cells, Widths = widths, Rows = table.Rows.Count, Spacing = spacing };
    }

    private void LayoutTable(Flow flow, TableBlock table, TableColumns columns, double x)
    {
        var tableId = _nextId++;
        var spacing = columns.Spacing;

        // Lay out every cell's content once, at its width.
        var contents = new Dictionary<PlacedCell, (List<Slice> Slices, double Height)>();
        foreach (var placed in columns.Cells)
        {
            var box = placed.Cell.Box ?? BoxStyle.Empty;
            var width = columns.SpanWidth(placed.Column, placed.ColumnSpan);
            var left = x + columns.Left(placed.Column) + box.BorderLeft.Width + box.PaddingLeft.Resolve(width);
            var contentWidth = Math.Max(1, width - CellHorizontal(placed.Cell, width));
            var slices = LayoutBlocks(placed.Cell.Blocks, left, contentWidth);
            var height = slices.Sum(s => s.Height) + box.PaddingTop.Resolve(width) + box.PaddingBottom.Resolve(width) + box.BorderTop.Width + box.BorderBottom.Width;
            contents[placed] = (slices, height);
        }

        var heights = new double[columns.Rows];
        foreach (var placed in columns.Cells.Where(c => c.RowSpan == 1))
        {
            heights[placed.Row] = Math.Max(heights[placed.Row], contents[placed].Height);
        }

        foreach (var placed in columns.Cells.Where(c => c.RowSpan > 1))
        {
            var spanned = Enumerable.Range(placed.Row, placed.RowSpan).Sum(r => heights[r]) + (spacing * (placed.RowSpan - 1));
            if (spanned < contents[placed].Height)
            {
                heights[placed.Row + placed.RowSpan - 1] += contents[placed].Height - spanned;
            }
        }

        // Rows joined by row spans are kept together; the header rows form one group.
        var groups = new List<(int First, int Last)>();
        var row = 0;
        while (row < columns.Rows)
        {
            var last = row;
            if (row < table.HeaderRows)
            {
                last = Math.Max(last, table.HeaderRows - 1);
            }

            for (var r = row; r <= last; r++)
            {
                foreach (var placed in columns.Cells.Where(c => c.Row == r))
                {
                    last = Math.Max(last, placed.Row + placed.RowSpan - 1);
                }
            }

            groups.Add((row, last));
            row = last + 1;
        }

        List<Slice>? header = null;
        for (var g = 0; g < groups.Count; g++)
        {
            var (firstRow, lastRow) = groups[g];
            var isHeader = table.HeaderRows > 0 && firstRow == 0;
            var top = spacing;
            var slice = new Slice
            {
                Height = Enumerable.Range(firstRow, lastRow - firstRow + 1).Sum(r => heights[r] + spacing) + (g == groups.Count - 1 ? spacing : 0),
                Table = tableId,
                IsTableHeader = isHeader,
                RepeatHeader = isHeader ? null : header,
            };
            double RowTop(int r) => top + Enumerable.Range(firstRow, r - firstRow).Sum(rr => heights[rr] + spacing);
            foreach (var placed in columns.Cells.Where(c => c.Row >= firstRow && c.Row <= lastRow))
            {
                var box = placed.Cell.Box ?? BoxStyle.Empty;
                var cellX = x + columns.Left(placed.Column);
                var cellY = RowTop(placed.Row);
                var cellW = columns.SpanWidth(placed.Column, placed.ColumnSpan);
                var cellH = Enumerable.Range(placed.Row, placed.RowSpan).Sum(r => heights[r]) + (spacing * (placed.RowSpan - 1));
                if ((placed.Cell.Background ?? box.Background) is { } background)
                {
                    slice.Ops.Add(new RectOp(cellX, cellY, cellW, cellH, background));
                }

                var (contentSlices, contentHeight) = contents[placed];
                var free = cellH - contentHeight;
                var offset = cellY + box.BorderTop.Width + box.PaddingTop.Resolve(cellW) + placed.Cell.VerticalAlignment switch
                {
                    CellAlignment.Middle => free / 2,
                    CellAlignment.Bottom => free,
                    _ => 0,
                };
                foreach (var (op, link, anchor) in Render(contentSlices, offset))
                {
                    if (op is not null)
                    {
                        slice.Ops.Add(op);
                    }

                    if (link is not null)
                    {
                        slice.Links.Add(link);
                    }

                    if (anchor is { } a)
                    {
                        slice.Anchors.Add(a);
                    }
                }

                Borders(slice.Ops, box, cellX, cellY, cellW, cellH, true, true);
            }

            if (isHeader)
            {
                header = new List<Slice> { slice };
            }

            flow.Emit(slice);
        }
    }

    private static void Borders(List<DrawOp> ops, BoxStyle box, double x, double y, double w, double h, bool top, bool bottom)
    {
        if (top && box.BorderTop.Width > 0)
        {
            ops.Add(new RectOp(x, y, w, box.BorderTop.Width, box.BorderTop.Color));
        }

        if (bottom && box.BorderBottom.Width > 0)
        {
            ops.Add(new RectOp(x, y + h - box.BorderBottom.Width, w, box.BorderBottom.Width, box.BorderBottom.Color));
        }

        if (box.BorderLeft.Width > 0)
        {
            ops.Add(new RectOp(x, y, box.BorderLeft.Width, h, box.BorderLeft.Color));
        }

        if (box.BorderRight.Width > 0)
        {
            ops.Add(new RectOp(x + w - box.BorderRight.Width, y, box.BorderRight.Width, h, box.BorderRight.Color));
        }
    }

    // ------------------------------------------------------------------ rendering slices

    /// <summary>
    /// Places consecutive slices from <paramref name="top"/> without breaking them across pages, with their boxes'
    /// backgrounds and borders behind them.
    /// </summary>
    private IEnumerable<(DrawOp? Op, LinkArea? Link, (string, double)? Anchor)> Render(List<Slice> slices, double top)
    {
        var positions = new List<double>();
        var y = top;
        foreach (var slice in slices)
        {
            positions.Add(y);
            y += slice.Height;
        }

        foreach (var op in Decorations(slices, positions))
        {
            yield return (op, null, null);
        }

        for (var i = 0; i < slices.Count; i++)
        {
            foreach (var op in slices[i].Ops)
            {
                yield return (op.Offset(0, positions[i]), null, null);
            }

            foreach (var link in slices[i].Links)
            {
                yield return (null, link.Offset(0, positions[i]), null);
            }

            foreach (var (name, ay) in slices[i].Anchors)
            {
                yield return (null, null, (name, ay + positions[i]));
            }
        }
    }

    /// <summary>Backgrounds and borders of the boxes the placed slices lie in, outermost first.</summary>
    private List<DrawOp> Decorations(List<Slice> slices, List<double> positions)
    {
        var ops = new List<DrawOp>();
        var ids = slices.SelectMany(s => s.Containers).Distinct().Order().ToList();
        foreach (var id in ids)
        {
            var indices = Enumerable.Range(0, slices.Count).Where(i => slices[i].Containers.Contains(id)).ToList();
            var firstIndex = indices[0];
            var lastIndex = indices[^1];
            var top = positions[firstIndex];
            var bottom = positions[lastIndex] + slices[lastIndex].Height;
            var container = _containers[id];
            var opens = slices[firstIndex].Opens == id;
            var closes = slices[lastIndex].Closes == id;
            if (container.Box.Background is { } background)
            {
                ops.Add(new RectOp(container.Left, top, container.Width, bottom - top, background));
            }

            Borders(ops, container.Box, container.Left, top, container.Width, bottom - top, opens, closes);
        }

        return ops;
    }

    // ------------------------------------------------------------------ pagination

    public List<LaidOutPage> Paginate(List<Slice> slices, Section section)
    {
        var contentTop = section.Margins.Top;
        var available = Math.Max(10, section.PageHeight - section.Margins.Top - section.Margins.Bottom);
        var pages = new List<(List<Slice> Slices, List<double> Positions)>();
        var i = 0;
        var guard = 0;
        while (i < slices.Count)
        {
            if (++guard > slices.Count * 4 + 100)
            {
                throw new InvalidOperationException("Pagination did not make progress.");
            }

            var placed = new List<Slice>();
            var positions = new List<double>();
            double y = 0;

            // Margins are dropped at the top of a page.
            while (i < slices.Count && slices[i].IsSpacer && slices[i].BreakBefore != BreakRule.Always)
            {
                i++;
            }

            if (i < slices.Count && slices[i].IsSpacer)
            {
                i++;
            }

            // A table continuing from the previous page repeats its header rows.
            if (i < slices.Count && slices[i].RepeatHeader is { } header && pages.Count > 0 && pages[^1].Slices.Any(s => s.Table == slices[i].Table))
            {
                foreach (var h in header)
                {
                    placed.Add(h);
                    positions.Add(y);
                    y += h.Height;
                }
            }

            var firstContent = i;
            while (i < slices.Count)
            {
                var slice = slices[i];
                var hasContent = placed.Any(s => !s.IsSpacer && s.Height > 0) || i > firstContent && placed.Count > 0;
                if (slice.BreakBefore == BreakRule.Always && i > firstContent)
                {
                    break;
                }

                if (y + slice.Height > available + 0.01 && hasContent)
                {
                    var b = BreakPoint(slices, i, firstContent);
                    var remove = i - b;
                    if (remove > 0)
                    {
                        placed.RemoveRange(placed.Count - remove, remove);
                        positions.RemoveRange(positions.Count - remove, remove);
                        i = b;
                    }

                    break;
                }

                if (slice.Height > available + 0.01)
                {
                    _warn("Some content is taller than a page and was cut off at the bottom of the page.");
                }

                placed.Add(slice);
                positions.Add(y);
                y += slice.Height;
                i++;
            }

            pages.Add((placed, positions));
        }

        if (pages.Count == 0)
        {
            pages.Add((new List<Slice>(), new List<double>()));
        }

        var result = new List<LaidOutPage>();
        foreach (var (placed, positions) in pages)
        {
            var page = new LaidOutPage { Width = section.PageWidth, Height = section.PageHeight };
            var absolute = positions.Select(p => p + contentTop).ToList();
            page.Ops.AddRange(Decorations(placed, absolute));
            for (var k = 0; k < placed.Count; k++)
            {
                page.Ops.AddRange(placed[k].Ops.Select(o => o.Offset(0, absolute[k])));
                page.Links.AddRange(placed[k].Links.Select(l => l.Offset(0, absolute[k])));
                page.Anchors.AddRange(placed[k].Anchors.Select(a => (a.Name, a.Y + absolute[k])));
            }

            result.Add(page);
        }

        return result;
    }

    /// <summary>Numbers the pages of all sections and adds each page's header and footer.</summary>
    public void Finish(List<(LaidOutPage Page, Section Section, bool FirstOfSection)> pages)
    {
        _pageCount = pages.Count;
        for (var i = 0; i < pages.Count; i++)
        {
            _pageNumber = i + 1;
            var (page, section, first) = pages[i];
            var even = _pageNumber % 2 == 0;
            var header = first && section.DifferentFirstPage ? section.FirstHeader : even && section.EvenHeader is not null ? section.EvenHeader : section.Header;
            var footer = first && section.DifferentFirstPage ? section.FirstFooter : even && section.EvenFooter is not null ? section.EvenFooter : section.Footer;
            HeaderFooter(page, section, header, footer);
        }
    }

    /// <summary>
    /// Moves a page break at <paramref name="index"/> earlier: out of avoid-break groups, to keep at least the orphan
    /// count of a paragraph's lines at the bottom and the widow count at the top, and to keep headings with what
    /// follows. The break never moves to or before <paramref name="pageStart"/>.
    /// </summary>
    private static int BreakPoint(List<Slice> slices, int index, int pageStart)
    {
        var b = index;
        var next = slices[b];
        var previous = slices[b - 1];
        if (next.AvoidGroup != 0 && previous.AvoidGroup == next.AvoidGroup)
        {
            var groupStart = b;
            while (groupStart > pageStart && slices[groupStart - 1].AvoidGroup == next.AvoidGroup)
            {
                groupStart--;
            }

            if (groupStart > pageStart)
            {
                b = groupStart;
            }
        }

        // Widows and orphans.
        next = slices[b];
        if (next.Paragraph != 0 && b > pageStart && slices[b - 1].Paragraph == next.Paragraph)
        {
            var firstOnPage = b;
            while (firstOnPage > pageStart && slices[firstOnPage - 1].Paragraph == next.Paragraph)
            {
                firstOnPage--;
            }

            var placedLines = b - firstOnPage;
            var remaining = next.Lines - next.Line;
            if (remaining < next.Widows)
            {
                b -= Math.Min(next.Widows - remaining, placedLines);
            }

            if (b - firstOnPage < next.Orphans && b != firstOnPage && firstOnPage > pageStart)
            {
                b = firstOnPage;
            }
            else if (b - firstOnPage < next.Orphans && firstOnPage == pageStart)
            {
                // The paragraph starts the page: keep what fits rather than push it all.
                b = Math.Max(b, firstOnPage + 1);
            }
        }

        // Keep headings (and other keep-with-next blocks) with the content after them.
        while (b > pageStart + 1)
        {
            var before = b - 1;
            while (before > pageStart && slices[before].IsSpacer)
            {
                before--;
            }

            if (!slices[before].KeepWithNext || before <= pageStart)
            {
                break;
            }

            b = before;
            while (b > pageStart + 1 && slices[b - 1].KeepWithNext)
            {
                b--;
            }
        }

        return b > pageStart ? b : index;
    }

    private void HeaderFooter(LaidOutPage page, Section section, List<Block> header, List<Block> footer)
    {
        var width = section.PageWidth - section.Margins.Left - section.Margins.Right;
        foreach (var (blocks, isHeader) in new[] { (header, true), (footer, false) })
        {
            if (blocks.Count == 0)
            {
                continue;
            }

            var slices = LayoutBlocksQuiet(blocks, section.Margins.Left, width);
            var height = slices.Sum(s => s.Height);
            var top = isHeader ? section.HeaderDistance : section.PageHeight - (section.FooterDistance ?? section.HeaderDistance) - height;
            foreach (var (op, link, _) in Render(slices, top))
            {
                if (op is not null)
                {
                    page.Ops.Add(op);
                }

                if (link is not null)
                {
                    page.Links.Add(link);
                }
            }
        }
    }

    /// <summary>Lays out header and footer content without adding it to the body text.</summary>
    private List<Slice> LayoutBlocksQuiet(List<Block> blocks, double x, double width)
    {
        var length = _shownText.Length;
        var slices = LayoutBlocks(blocks, x, width);
        _shownText.Length = length;
        return slices;
    }
}
