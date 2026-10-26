using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfUtil.Conversion.Internal.Model;
using PdfUtil.Internal.Content;

namespace PdfUtil.Conversion.Internal.Pdf;

/// <summary>
/// Rebuilds document structure from positioned text (F19 reflow, reused by F20):
/// <list type="number">
/// <item>Runs on one baseline become lines; small raised or lowered runs join their line as superscripts or subscripts.</item>
/// <item>Lines and images are put in reading order by a recursive XY cut: the page is split at the widest empty
/// horizontal band or vertical gutter, so columns are read one after the other where they can be detected.</item>
/// <item>Consecutive lines with regular spacing, the same size and aligned left edges form paragraphs.</item>
/// <item>Paragraphs set larger than the body text (or short, bold, body-sized lines) become headings, one level per
/// distinct size; lines starting with a bullet or a number pattern become list items, nested by indentation.</item>
/// <item>Link areas become links on the text inside them, and ruling lines just under or through text become
/// underline or strike-through.</item>
/// </list>
/// </summary>
internal static class ReflowAnalyzer
{
    private const double SpaceGap = 0.15;
    private const int MaxCutDepth = 200;

    private static readonly Regex BulletPattern = new(@"^([•◦▪▫■□●○►▸‣⁃∙·–\-\*])\s+", RegexOptions.CultureInvariant);
    private static readonly Regex NumberPattern = new(@"^\(?(\d{1,3}|[a-z]|[A-Z]|[ivxlcdm]{1,7}|[IVXLCDM]{1,7})([.)])\s+", RegexOptions.CultureInvariant);

    private sealed class Line
    {
        public List<TextAssembler.Run> Runs { get; } = new();

        public double Left { get; set; }

        public double Right { get; set; }

        public double Top { get; set; }

        public double Bottom { get; set; }

        public double Baseline { get; set; }

        public double Size { get; set; }

        /// <summary>The right edge of the column this line is in: the widest line starting near its left edge.</summary>
        public double ColumnRight { get; set; }

        public bool AllBold => Runs.Where(r => r.Text.ToString().Trim().Length > 0).All(r => r.First.Font.IsBold);

        public bool Monospace => Runs.Where(r => r.Text.ToString().Trim().Length > 0).All(r => r.First.Font.IsMonospace);

        public string Text { get; set; } = "";

        public void Measure()
        {
            Runs.Sort((a, b) => a.First.X.CompareTo(b.First.X));
            var main = Runs.OrderByDescending(r => r.Text.Length * r.Size).First();
            Baseline = main.First.Y;
            Size = main.Size;
            Left = Runs.Min(r => r.First.X);
            Right = Runs.Max(r => r.EndX);
            Top = Runs.Max(r => r.First.Y + (Math.Clamp(r.First.Font.Ascent, 0.5, 1.1) * r.Size));
            Bottom = Runs.Min(r => r.First.Y + (Math.Clamp(r.First.Font.Descent, -0.4, -0.1) * r.Size));
            var sb = new StringBuilder();
            TextAssembler.Run? previous = null;
            foreach (var run in Runs)
            {
                if (previous is not null && NeedsSpace(previous, run, sb))
                {
                    sb.Append(' ');
                }

                sb.Append(run.Text);
                previous = run;
            }

            Text = sb.ToString();
        }
    }

    private static bool NeedsSpace(TextAssembler.Run previous, TextAssembler.Run run, StringBuilder text) =>
        run.First.X - previous.EndX > SpaceGap * Math.Max(run.Size, previous.Size) && text.Length > 0 && !char.IsWhiteSpace(text[^1]) && run.Text.Length > 0 && !char.IsWhiteSpace(run.Text[0]);

    private sealed class Item
    {
        public Line? Line { get; init; }

        public SourceImage? Image { get; init; }

        public TableLayout? Table { get; init; }

        public double Left { get; init; }

        public double Right { get; init; }

        public double Top { get; init; }

        public double Bottom { get; init; }
    }

    private sealed record Marker(ListKind Kind, int Value, int Length, string Text);

    private sealed class Para
    {
        public List<Line> Lines { get; } = new();

        public SourceImage? Image { get; init; }

        public TableLayout? Table { get; init; }

        public Marker? Marker { get; set; }

        public double FirstLeft { get; set; }

        public double BodyLeft { get; set; } = double.NaN;

        public double TextLeft { get; set; }

        public double LineGap { get; set; } = double.NaN;

        public bool Rotated { get; init; }

        public bool AllBold { get; set; }

        public bool Monospace { get; set; }

        public int Page { get; init; }

        public double Size => Lines.Count == 0 ? 0 : Lines.SelectMany(l => l.Runs).GroupBy(r => Math.Round(r.Size * 2) / 2).OrderByDescending(g => g.Sum(r => r.Text.Length)).First().Key;

        public string Text => string.Join(" ", Lines.Select(l => l.Text));

        public Paragraph? Built { get; set; }

        public int HeadingLevel { get; set; }
    }

    /// <summary>A detected table: its grid and the paragraphs in each cell.</summary>
    private sealed record TableLayout(RuledGrid Grid, List<(GridCell Cell, List<Para> Paragraphs)> Cells, bool BoldFirstRow);

    /// <summary>Context for one page: link areas and ruling lines.</summary>
    private sealed record PageContext(SourcePage Page, HashSet<int> ConvertedPages, double TextLeft, double TextRight);

    public static DocModel Analyze(SourceDocument source, bool pageSeparators)
    {
        var model = new DocModel
        {
            Title = source.Title,
            Language = source.Info.Language,
            Author = source.Info.Info?.Author ?? (source.Info.Xmp?.Creators.Count > 0 ? string.Join(", ", source.Info.Xmp.Creators) : null),
            Subject = source.Info.Info?.Subject ?? source.Info.Xmp?.Description,
            Keywords = source.Info.Info?.Keywords ?? source.Info.Xmp?.Keywords,
            Created = source.Info.Info?.CreationDate ?? source.Info.Xmp?.CreateDate,
            Modified = source.Info.Info?.ModDate ?? source.Info.Xmp?.ModifyDate,
        };
        var converted = source.Pages.Select(p => p.Number).ToHashSet();
        model.Outline.AddRange(Outline(source.Info.Bookmarks, converted, 0));

        var section = new Section();
        if (source.Pages.Count > 0)
        {
            section.PageWidth = source.Pages[0].Width;
            section.PageHeight = source.Pages[0].Height;
            section.Margins = MarginsOf(source.Pages[0]);
        }

        model.Sections.Add(section);
        var bodySize = BodySize(source.Pages);
        var paragraphs = new List<(Para Para, PageContext Context)>();
        foreach (var page in source.Pages)
        {
            var horizontal = page.Runs.Where(IsHorizontal).ToList();
            var left = horizontal.Count > 0 ? horizontal.Min(r => r.First.X) : 0;
            var right = horizontal.Count > 0 ? horizontal.Max(r => r.EndX) : page.Width;
            var context = new PageContext(page, converted, left, right);
            foreach (var para in PageParagraphs(page, bodySize))
            {
                paragraphs.Add((para, context));
            }
        }

        AssignHeadings(paragraphs.Select(p => p.Para).ToList(), bodySize);
        ValidateMarkers(paragraphs.Select(p => p.Para).ToList());

        var lists = new ListBuilder();
        var currentPage = 0;
        Para? lastPara = null;
        Paragraph? lastParagraph = null;
        foreach (var (para, context) in paragraphs)
        {
            if (para.Page != currentPage)
            {
                lists.Close();
                var continues = currentPage != 0 && !pageSeparators && lastPara is not null && lastParagraph is not null && ContinuesOnNextPage(lastPara, para);
                if (currentPage != 0 && pageSeparators)
                {
                    section.Blocks.Add(new PageBreakBlock());
                }

                currentPage = para.Page;
                if (continues)
                {
                    // A paragraph broken by the page break: the rest joins it, with the page anchor where the page starts.
                    var rest = Build(para, context);
                    var style = Inline.Flatten(lastParagraph!.Inlines).OfType<TextInline>().LastOrDefault()?.Style ?? RunStyle.Plain;
                    if (lastParagraph.PlainText is var previousText && previousText.Length > 0 && previousText[^1] != '-' && !char.IsWhiteSpace(previousText[^1]))
                    {
                        lastParagraph.Inlines.Add(new TextInline(" ", style));
                    }

                    lastParagraph.Inlines.Add(new AnchorInline { Name = PageAnchor(para.Page) });
                    lastParagraph.Inlines.AddRange(rest.Inlines);
                    lastPara = para;
                    continue;
                }

                var anchor = new AnchorBlock();
                anchor.Anchors.Add(PageAnchor(para.Page));
                section.Blocks.Add(anchor);
            }

            if (para.Table is { } layout)
            {
                lists.Close();
                section.Blocks.Add(BuildTable(layout, context));
                (lastPara, lastParagraph) = (null, null);
                continue;
            }

            if (para.Image is { } image)
            {
                lists.Close();
                section.Blocks.Add(new ImageBlock { Image = image.Image, Width = image.Bounds.Width, Height = image.Bounds.Height, AltText = image.Image.Placeholder ?? "" });
                (lastPara, lastParagraph) = (null, null);
                continue;
            }

            var paragraph = Build(para, context);
            if (para.Marker is { } marker)
            {
                lists.Add(section.Blocks, marker, para.Lines[0].Left, para.Size, paragraph);
                (lastPara, lastParagraph) = (para, paragraph);
            }
            else if (lists.IsOpen && para.HeadingLevel == 0 && lists.Continues(para.Lines[0].Left, para.Size))
            {
                // A paragraph indented like the text of the open list item belongs to that item.
                lists.AddToItem(paragraph);
                (lastPara, lastParagraph) = (para, paragraph);
            }
            else
            {
                lists.Close();
                section.Blocks.Add(paragraph);
                (lastPara, lastParagraph) = (para, paragraph);
            }
        }

        // Pages without any content still get their anchor, so links and bookmarks to them work.
        foreach (var page in source.Pages.Where(p => paragraphs.All(x => x.Para.Page != p.Number)))
        {
            var anchor = new AnchorBlock();
            anchor.Anchors.Add(PageAnchor(page.Number));
            var index = section.Blocks.FindIndex(b => b.Anchors.Any(a => a.StartsWith("page-", StringComparison.Ordinal) && int.Parse(a[5..], CultureInfo.InvariantCulture) > page.Number));
            section.Blocks.Insert(index < 0 ? section.Blocks.Count : index, anchor);
        }

        return model;
    }

    private static bool ContinuesOnNextPage(Para last, Para next)
    {
        var text = last.Text.TrimEnd();
        var nextText = next.Text.TrimStart();
        // The last paragraph of a page (a list item too) continues when the next page starts mid-sentence.
        return last.HeadingLevel == 0 && !last.Monospace && !last.Rotated && last.Image is null
            && next.HeadingLevel == 0 && next.Marker is null && !next.Monospace && !next.Rotated && next.Image is null
            && text.Length > 0 && !".:!?".Contains(text[^1]) && nextText.Length > 0 && char.IsLower(nextText[0])
            && Math.Abs(last.Size - next.Size) < 0.12 * last.Size;
    }

    public static string PageAnchor(int page) => "page-" + page.ToString(CultureInfo.InvariantCulture);

    private static IEnumerable<OutlineEntry> Outline(IReadOnlyList<PdfBookmark> bookmarks, HashSet<int> converted, int depth)
    {
        foreach (var b in bookmarks)
        {
            var anchor = b.Page is int p && converted.Contains(p) ? PageAnchor(p) : null;
            var uri = UrlSafety.Check(b.Uri);
            var children = depth < 32 ? Outline(b.Children, converted, depth + 1).ToList() : new List<OutlineEntry>();
            if (anchor is not null || uri is not null || children.Count > 0)
            {
                yield return new OutlineEntry(b.Title, anchor, uri, children);
            }
        }
    }

    private static Margins MarginsOf(SourcePage page)
    {
        if (page.Runs.Count == 0)
        {
            return Margins.Default;
        }

        static double Clamp(double v) => Math.Round(Math.Clamp(v, 18, 144), 1);
        var left = page.Runs.Min(r => r.First.X);
        var right = page.Runs.Max(r => r.EndX);
        var top = page.Runs.Max(r => r.First.Y + (r.First.Font.Ascent * r.Size));
        var bottom = page.Runs.Min(r => r.First.Y + (r.First.Font.Descent * r.Size));
        // Ragged lines and short pages end before the real margin; pages are usually symmetric, so the right and bottom
        // margins are at most the left and top ones.
        var marginTop = Clamp(page.Height - top);
        var marginLeft = Clamp(left);
        return new Margins(marginTop, Math.Min(Clamp(page.Width - right), marginLeft), Math.Min(Clamp(bottom), Math.Max(marginTop, 36)), marginLeft);
    }

    private static bool IsHorizontal(TextAssembler.Run run) => Math.Abs(run.First.Angle) < 0.02;

    /// <summary>The most common text size, weighted by characters, rounded to half points.</summary>
    private static double BodySize(List<SourcePage> pages)
    {
        var sizes = pages.SelectMany(p => p.Runs).Where(IsHorizontal).GroupBy(r => Math.Round(r.Size * 2) / 2).Select(g => (Size: g.Key, Chars: g.Sum(r => r.Text.Length))).ToList();
        return sizes.Count == 0 ? 12 : sizes.OrderByDescending(s => s.Chars).ThenBy(s => s.Size).First().Size;
    }

    // ------------------------------------------------------------------ lines and reading order

    private static List<Line> BuildLines(List<TextAssembler.Run> runs)
    {
        var lines = new List<Line>();
        var groups = new List<List<TextAssembler.Run>>();
        foreach (var run in runs.Where(r => r.Text.ToString().Trim().Length > 0).OrderByDescending(r => r.Size))
        {
            // Join an existing line on the same baseline that this run touches; superscripts and subscripts (smaller
            // and shifted) join a line they sit next to.
            List<TextAssembler.Run>? target = null;
            foreach (var group in groups)
            {
                var main = group[0];
                var size = main.Size;
                var shift = run.First.Y - main.First.Y;
                var sameBaseline = Math.Abs(shift) < 0.2 * Math.Min(size, run.Size);
                var raisedOrLowered = run.Size < 0.9 * size && shift > -0.35 * size && shift < 0.6 * size;
                if (!sameBaseline && !raisedOrLowered)
                {
                    continue;
                }

                var left = group.Min(r => r.First.X);
                var right = group.Max(r => r.EndX);
                var gap = Math.Max(run.First.X - right, left - run.EndX);
                if (gap < 1.0 * Math.Max(size, run.Size) && (sameBaseline || gap < 0.3 * size))
                {
                    target = group;
                    break;
                }
            }

            if (target is null)
            {
                groups.Add(new List<TextAssembler.Run> { run });
            }
            else
            {
                target.Add(run);
            }
        }

        foreach (var group in groups)
        {
            var line = new Line();
            line.Runs.AddRange(group);
            line.Measure();
            lines.Add(line);
        }

        return lines;
    }

    private static IEnumerable<Para> PageParagraphs(SourcePage page, double bodySize)
    {
        var horizontal = page.Runs.Where(IsHorizontal).ToList();
        var tables = new List<Item>();
        foreach (var grid in TableDetector.Detect(page.Rules))
        {
            if (FitTable(grid, horizontal, page.Number, bodySize) is { } table)
            {
                tables.Add(new Item { Table = table, Left = grid.Left, Right = grid.Right, Top = grid.Top, Bottom = grid.Bottom });
            }
        }

        var lines = BuildLines(horizontal);
        foreach (var line in lines)
        {
            line.ColumnRight = lines.Where(l => Math.Abs(l.Left - line.Left) < 3 * line.Size && Math.Abs(l.Size - line.Size) < 0.2 * line.Size).Max(l => l.Right);
        }

        var items = lines.Select(l => new Item { Line = l, Left = l.Left, Right = l.Right, Top = l.Top, Bottom = l.Bottom }).Concat(tables).ToList();

        // Images behind text (backgrounds, scans under a text layer) come first and stay out of the reading-order
        // analysis; tiny images (decorations, rules drawn as images) are skipped.
        var background = new List<SourceImage>();
        foreach (var image in page.Images)
        {
            var b = image.Bounds;
            if (b.Width < 4 || b.Height < 4)
            {
                continue;
            }

            var covered = lines.Count(l => l.Left < b.UpperRightX && l.Right > b.LowerLeftX && l.Bottom < b.UpperRightY && l.Top > b.LowerLeftY);
            if (covered > 0)
            {
                background.Add(image);
            }
            else
            {
                items.Add(new Item { Image = image, Left = b.LowerLeftX, Right = b.UpperRightX, Top = b.UpperRightY, Bottom = b.LowerLeftY });
            }
        }

        foreach (var image in background)
        {
            yield return new Para { Image = image, Page = page.Number };
        }

        var ordered = new List<Item>();
        Cut(items, ordered, bodySize, 0);
        Para? current = null;
        foreach (var item in ordered)
        {
            if (item.Image is not null || item.Table is not null)
            {
                if (current is not null)
                {
                    yield return current;
                    current = null;
                }

                yield return new Para { Image = item.Image, Table = item.Table, Page = page.Number };
                continue;
            }

            var line = item.Line!;
            if (current is not null && TryContinue(current, line, bodySize))
            {
                continue;
            }

            if (current is not null)
            {
                yield return current;
            }

            current = Start(line, page.Number, bodySize);
        }

        if (current is not null)
        {
            yield return current;
        }

        // Rotated text (margin notes, stamps) follows the page's other text, one paragraph per direction change.
        Para? rotated = null;
        foreach (var run in page.Runs.Where(r => !IsHorizontal(r) && r.Text.ToString().Trim().Length > 0))
        {
            if (rotated is null || Math.Abs(rotated.Lines[^1].Runs[^1].First.Angle - run.First.Angle) > 0.02)
            {
                if (rotated is not null)
                {
                    yield return rotated;
                }

                rotated = new Para { Rotated = true, Page = page.Number };
            }

            var line = new Line();
            line.Runs.Add(run);
            line.Measure();
            rotated.Lines.Add(line);
        }

        if (rotated is not null)
        {
            yield return rotated;
        }
    }

    /// <summary>
    /// Assigns the runs inside a ruled grid to its cells and builds each cell's paragraphs. The grid is accepted only
    /// when it holds text and no run crosses a cell border; its runs are then removed from <paramref name="runs"/>.
    /// </summary>
    private static TableLayout? FitTable(RuledGrid grid, List<TextAssembler.Run> runs, int page, double bodySize)
    {
        var inside = runs.Where(r => Center(r) is var (x, y) && x > grid.Left && x < grid.Right && y < grid.Top && y > grid.Bottom).ToList();
        if (inside.Count == 0)
        {
            return null;
        }

        var byCell = grid.Cells.ToDictionary(c => c, _ => new List<TextAssembler.Run>());
        foreach (var run in inside)
        {
            var (x, y) = Center(run);
            var cell = grid.Cells.FirstOrDefault(c => x >= c.Left(grid) && x <= c.Right(grid) && y <= c.Top(grid) && y >= c.Bottom(grid));
            if (cell is null || run.First.X < cell.Left(grid) - 2 || run.EndX > cell.Right(grid) + 2)
            {
                return null;
            }

            byCell[cell].Add(run);
        }

        var cells = new List<(GridCell, List<Para>)>();
        foreach (var cell in grid.Cells)
        {
            var paragraphs = new List<Para>();
            Para? current = null;
            var cellLines = BuildLines(byCell[cell]);
            foreach (var line in cellLines)
            {
                line.ColumnRight = cell.Right(grid);
            }

            foreach (var line in cellLines.OrderByDescending(l => l.Baseline).ThenBy(l => l.Left))
            {
                if (current is not null && TryContinue(current, line, bodySize))
                {
                    continue;
                }

                current = Start(line, page, bodySize);
                current.Marker = null;
                paragraphs.Add(current);
            }

            cells.Add((cell, paragraphs));
        }

        runs.RemoveAll(inside.Contains);
        var firstRow = grid.Cells.Where(c => c.Row == 0).SelectMany(c => byCell[c]).ToList();
        return new TableLayout(grid, cells, grid.Ys.Length > 2 && firstRow.Count > 0 && firstRow.All(r => r.First.Font.IsBold));
    }

    private static (double X, double Y) Center(TextAssembler.Run run) => ((run.First.X + run.EndX) / 2, run.First.Y + (0.3 * run.Size));

    /// <summary>
    /// Recursive XY cut. At each level the items are split at the widest gap: an empty horizontal band (top to
    /// bottom order) or a vertical gutter (left to right) whose sides both look like text columns.
    /// </summary>
    private static void Cut(List<Item> items, List<Item> output, double bodySize, int depth)
    {
        if (items.Count <= 1 || depth > MaxCutDepth)
        {
            output.AddRange(items.OrderByDescending(i => i.Top).ThenBy(i => i.Left));
            return;
        }

        var horizontal = HorizontalGaps(items);
        var vertical = items.Count <= 2000 ? VerticalGap(items, bodySize) : null;
        var bestH = horizontal.Count > 0 ? horizontal.Max(g => g.Size) : 0;
        if (vertical is { } v && v.Size >= bestH)
        {
            var leftSide = items.Where(i => i.Right <= v.Position).ToList();
            var rightSide = items.Where(i => i.Right > v.Position).ToList();
            Cut(leftSide, output, bodySize, depth + 1);
            Cut(rightSide, output, bodySize, depth + 1);
            return;
        }

        if (horizontal.Count == 0)
        {
            output.AddRange(items.OrderByDescending(i => i.Top).ThenBy(i => i.Left));
            return;
        }

        // Cut at every gap nearly as wide as the widest, top to bottom.
        var cuts = horizontal.Where(g => g.Size >= 0.75 * bestH).Select(g => g.Position).OrderByDescending(p => p).ToList();
        var remaining = items;
        foreach (var cut in cuts)
        {
            var above = remaining.Where(i => i.Bottom >= cut).ToList();
            remaining = remaining.Where(i => i.Bottom < cut).ToList();
            Cut(above, output, bodySize, depth + 1);
        }

        Cut(remaining, output, bodySize, depth + 1);
    }

    /// <summary>Empty horizontal bands between items: their size and the y in their middle.</summary>
    private static List<(double Size, double Position)> HorizontalGaps(List<Item> items)
    {
        var gaps = new List<(double, double)>();
        var sorted = items.OrderByDescending(i => i.Top).ToList();
        var bottom = sorted[0].Bottom;
        for (var k = 1; k < sorted.Count; k++)
        {
            var item = sorted[k];
            if (item.Top < bottom)
            {
                gaps.Add((bottom - item.Top, (bottom + item.Top) / 2));
            }

            bottom = Math.Min(bottom, item.Bottom);
        }

        return gaps;
    }

    /// <summary>The widest vertical gutter whose two sides both look like text columns (or hold images), or null.</summary>
    private static (double Size, double Position)? VerticalGap(List<Item> items, double bodySize)
    {
        (double, double)? best = null;
        var sorted = items.OrderBy(i => i.Left).ToList();
        var right = sorted[0].Right;
        for (var k = 1; k < sorted.Count; k++)
        {
            var item = sorted[k];
            var gap = item.Left - right;
            if (gap >= Math.Max(0.8 * bodySize, 6) && (best is null || gap > best.Value.Item1))
            {
                var position = (right + item.Left) / 2;
                if (LooksLikeColumn(items.Where(i => i.Right <= position).ToList(), bodySize) && LooksLikeColumn(items.Where(i => i.Left >= position).ToList(), bodySize))
                {
                    best = (gap, position);
                }
            }

            right = Math.Max(right, item.Right);
        }

        return best;
    }

    private static bool LooksLikeColumn(List<Item> side, double bodySize)
    {
        if (side.Any(i => i.Image is not null))
        {
            return true;
        }

        var widths = side.Where(i => i.Line is not null).Select(i => i.Right - i.Left).OrderBy(w => w).ToList();
        return widths.Count >= 2 && widths[widths.Count / 2] >= 8 * bodySize;
    }

    // ------------------------------------------------------------------ paragraphs

    private static Para Start(Line line, int page, double bodySize)
    {
        var para = new Para { Page = page, FirstLeft = line.Left, AllBold = line.AllBold, Monospace = line.Monospace };
        para.Lines.Add(line);
        para.TextLeft = line.Left;
        // A bold numbered line ("1. General.") is a section title, not a list item.
        if (line.Size < bodySize * 1.15 && !line.AllBold && ParseMarker(line.Text) is { } marker)
        {
            para.Marker = marker;
            para.TextLeft = TextStart(line, marker);
        }

        return para;
    }

    private static bool TryContinue(Para para, Line line, double bodySize)
    {
        var previous = para.Lines[^1];
        var size = Math.Max(previous.Size, line.Size);

        // More text on the same baseline (tab-separated parts of one line).
        if (Math.Abs(line.Baseline - previous.Baseline) < 0.3 * size && line.Left >= previous.Right - 1)
        {
            previous.Runs.AddRange(line.Runs);
            previous.Measure();
            return true;
        }

        var delta = previous.Baseline - line.Baseline;
        if (delta <= 0 || para.Rotated)
        {
            return false;
        }

        // A short line that ends a sentence ends its paragraph, even without extra spacing after it.
        var previousText = previous.Text.TrimEnd();
        if (previousText.Length > 0 && ".:!?".Contains(previousText[^1]) && previous.Right < previous.ColumnRight - (3 * size))
        {
            return false;
        }

        var limit = double.IsNaN(para.LineGap) ? 1.5 * size : (para.LineGap * 1.3) + 0.5;
        if (delta > limit
            || Math.Abs(line.Size - previous.Size) > 0.12 * size
            || line.AllBold != para.AllBold
            || line.Monospace != para.Monospace
            || (line.Size < bodySize * 1.15 && !line.AllBold && ParseMarker(line.Text) is not null))
        {
            return false;
        }

        var centered = Math.Abs(((line.Left + line.Right) / 2) - ((previous.Left + previous.Right) / 2)) < Math.Max(1.0, 0.1 * size)
            && Math.Abs(line.Left - previous.Left) > 0.6 * size;
        if (!centered)
        {
            if (para.Lines.Count == 1 && para.Marker is null)
            {
                // The second line sets the paragraph's left edge; the first line may be indented.
                if (line.Left < para.FirstLeft - (3 * size) || line.Left > para.FirstLeft + (0.6 * size))
                {
                    return false;
                }
            }
            else
            {
                // List items continue under their text or, in some layouts, back at the marker.
                var reference = para.Lines.Count == 1 ? para.TextLeft : para.BodyLeft;
                var markerLeft = para.Marker is not null && para.Lines.Count == 1 && Math.Abs(line.Left - para.FirstLeft) <= 0.6 * size;
                if (Math.Abs(line.Left - reference) > 0.6 * size && !markerLeft)
                {
                    return false;
                }
            }
        }

        if (para.Lines.Count == 1)
        {
            para.BodyLeft = line.Left;
            para.LineGap = delta;
        }

        para.Lines.Add(line);
        return true;
    }

    private static Marker? ParseMarker(string text)
    {
        var bullet = BulletPattern.Match(text);
        if (bullet.Success && bullet.Length < text.Length)
        {
            return new Marker(ListKind.Bullet, 0, bullet.Length, bullet.Groups[1].Value);
        }

        var number = NumberPattern.Match(text);
        if (!number.Success || number.Length >= text.Length)
        {
            return null;
        }

        var value = number.Groups[1].Value;
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            return new Marker(ListKind.Decimal, n, number.Length, number.Value.Trim());
        }

        if (Roman(value) is int roman && (value.Length > 1 || value is "i" or "I"))
        {
            return new Marker(char.IsLower(value[0]) ? ListKind.LowerRoman : ListKind.UpperRoman, roman, number.Length, number.Value.Trim());
        }

        return value.Length == 1
            ? new Marker(char.IsLower(value[0]) ? ListKind.LowerAlpha : ListKind.UpperAlpha, char.ToLowerInvariant(value[0]) - 'a' + 1, number.Length, number.Value.Trim())
            : null;
    }

    private static int? Roman(string s)
    {
        var values = new Dictionary<char, int> { ['i'] = 1, ['v'] = 5, ['x'] = 10, ['l'] = 50, ['c'] = 100, ['d'] = 500, ['m'] = 1000 };
        var total = 0;
        var lower = s.ToLowerInvariant();
        for (var i = 0; i < lower.Length; i++)
        {
            if (!values.TryGetValue(lower[i], out var v))
            {
                return null;
            }

            total += i + 1 < lower.Length && values.TryGetValue(lower[i + 1], out var next) && next > v ? -v : v;
        }

        return total > 0 && total < 4000 ? total : null;
    }

    /// <summary>Where the text after a list marker starts.</summary>
    private static double TextStart(Line line, Marker marker)
    {
        var markerChars = marker.Text.Count(c => !char.IsWhiteSpace(c));
        var seen = 0;
        foreach (var run in line.Runs)
        {
            foreach (var g in run.Glyphs)
            {
                if (seen >= markerChars && g.Text.Trim().Length > 0)
                {
                    return g.X;
                }

                seen += g.Text.Count(c => !char.IsWhiteSpace(c));
            }
        }

        return line.Left;
    }

    private static void AssignHeadings(List<Para> paragraphs, double bodySize)
    {
        static bool HasLetters(string s) => s.Any(char.IsLetter);
        var bySize = paragraphs.Where(p => p.Image is null && !p.Rotated && p.Marker is null && !p.Monospace && p.Lines.Count <= 4 && p.Size >= bodySize * 1.15 && p.Text.Length <= 200 && HasLetters(p.Text)).ToList();
        var levels = bySize.Select(p => Math.Round(p.Size * 2) / 2).Distinct().OrderByDescending(s => s).ToList();
        foreach (var para in bySize)
        {
            para.HeadingLevel = Math.Min(6, levels.IndexOf(Math.Round(para.Size * 2) / 2) + 1);
        }

        var boldLevel = Math.Min(6, levels.Count + 1);
        foreach (var para in paragraphs.Where(p => p.HeadingLevel == 0 && p.Image is null && !p.Rotated && p.Marker is null && p.AllBold && p.Lines.Count == 1 && Math.Abs(p.Size - bodySize) <= 0.12 * bodySize))
        {
            var text = para.Text.Trim();
            // Short titles may end with a period ("1. General."); sentences may not.
            var shortTitle = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 8;
            if (text.Length is > 0 and <= 120 && HasLetters(text) && !":;,".Contains(text[^1]) && (text[^1] != '.' || shortTitle))
            {
                para.HeadingLevel = boldLevel;
            }
        }
    }

    /// <summary>
    /// Letters and roman numerals start list items only when a neighbouring item continues the sequence, so that a
    /// paragraph such as "A. Smith wrote…" stays a paragraph.
    /// </summary>
    private static void ValidateMarkers(List<Para> paragraphs)
    {
        var demote = new List<Para>();
        for (var i = 0; i < paragraphs.Count; i++)
        {
            var para = paragraphs[i];
            if (para.Marker is not { Kind: not (ListKind.Bullet or ListKind.Decimal) } marker)
            {
                continue;
            }

            bool Neighbour(Para other, int expected) => other.Marker is { } m && m.Value == expected && m.Kind is not (ListKind.Bullet or ListKind.Decimal) && char.IsLower(m.Text.TrimStart('(')[0]) == char.IsLower(marker.Text.TrimStart('(')[0]) && Math.Abs(other.Lines[0].Left - para.Lines[0].Left) <= 0.6 * para.Size;
            var hasNeighbour = paragraphs.Skip(i + 1).Take(30).Any(o => Neighbour(o, marker.Value + 1)) || paragraphs.Skip(Math.Max(0, i - 30)).Take(Math.Min(i, 30)).Any(o => Neighbour(o, marker.Value - 1));
            if (!hasNeighbour)
            {
                demote.Add(para);
            }
        }

        foreach (var para in demote)
        {
            para.Marker = null;
            para.TextLeft = para.FirstLeft;
        }
    }

    // ------------------------------------------------------------------ inlines

    private static Paragraph Build(Para para, PageContext context)
    {
        var paragraph = new Paragraph { HeadingLevel = para.HeadingLevel, IsPreformatted = para.Monospace && para.HeadingLevel == 0 && para.Lines.Count > 1 };
        var segments = new List<(string Text, RunStyle Style, SourceLink? Link)>();
        foreach (var line in para.Lines)
        {
            if (segments.Count > 0)
            {
                var last = segments[^1];
                var endsWithHyphen = last.Text.Length > 1 && last.Text[^1] == '-' && char.IsLetter(last.Text[^2]);
                if (paragraph.IsPreformatted)
                {
                    segments.Add(("\n", last.Style, null));
                }
                else if (!endsWithHyphen && !char.IsWhiteSpace(last.Text[^1]))
                {
                    segments.Add((" ", last.Style, last.Link));
                }
            }

            AddLine(line, context, segments);
        }

        if (para.Marker is { } marker)
        {
            RemovePrefix(segments, marker.Length);
        }

        // Whitespace at the start or end of a link stays outside it.
        for (var i = 0; i < segments.Count; i++)
        {
            var (text, style, target) = segments[i];
            if (target is null)
            {
                continue;
            }

            var startsLink = i == 0 || !ReferenceEquals(segments[i - 1].Link, target);
            var endsLink = i + 1 >= segments.Count || !ReferenceEquals(segments[i + 1].Link, target);
            var lead = startsLink ? text.Length - text.TrimStart().Length : 0;
            var trail = endsLink ? text.Length - text.TrimEnd().Length : 0;
            if (lead + trail == 0)
            {
                continue;
            }

            if (lead + trail >= text.Length)
            {
                segments[i] = (text, style, null);
                continue;
            }

            segments.RemoveAt(i);
            var parts = new List<(string, RunStyle, SourceLink?)>();
            if (lead > 0)
            {
                parts.Add((text[..lead], style, null));
            }

            parts.Add((text[lead..^trail], style, target));
            if (trail > 0)
            {
                parts.Add((text[^trail..], style, null));
            }

            segments.InsertRange(i, parts);
            i += parts.Count - 1;
        }

        // Group into inlines: neighbouring segments with the same style merge, and segments with the same link share
        // one link element.
        LinkInline? link = null;
        SourceLink? linkSource = null;
        foreach (var (text, style, target) in segments)
        {
            var list = paragraph.Inlines;
            if (target is not null && ReferenceEquals(target, linkSource) && link is not null)
            {
                list = link.Children;
            }
            else if (target is not null && Target(target, context) is { } created)
            {
                link = created;
                linkSource = target;
                paragraph.Inlines.Add(link);
                list = link.Children;
            }
            else
            {
                link = null;
                linkSource = null;
            }

            if (text == "\n")
            {
                list.Add(new BreakInline());
            }
            else if (list.Count > 0 && list[^1] is TextInline previous && previous.Style == style)
            {
                previous.Text += text;
            }
            else
            {
                list.Add(new TextInline(text, style));
            }
        }

        var lineCenters = para.Lines.Select(l => (l.Left + l.Right) / 2).ToList();
        var textCenter = (context.TextLeft + context.TextRight) / 2;
        var size = para.Size;
        if (para.Marker is null && !para.Rotated && para.Lines.All(l => l.Left > context.TextLeft + (2 * size) && l.Right < context.TextRight - (2 * size)) && lineCenters.All(c => Math.Abs(c - textCenter) < Math.Max(2, 0.03 * (context.TextRight - context.TextLeft))))
        {
            paragraph.Alignment = TextAlignment.Center;
        }
        else if (para.Marker is null && !para.Rotated && para.Lines.All(l => Math.Abs(l.Right - context.TextRight) < 2 && l.Left > context.TextLeft + (0.3 * (context.TextRight - context.TextLeft))))
        {
            paragraph.Alignment = TextAlignment.Right;
        }

        para.Built = paragraph;
        return paragraph;
    }

    private static TableBlock BuildTable(TableLayout layout, PageContext context)
    {
        var grid = layout.Grid;
        var table = new TableBlock
        {
            ColumnWidths = grid.Xs.Zip(grid.Xs.Skip(1), (a, b) => Math.Round(b - a, 2)).ToList(),
            HeaderRows = layout.BoldFirstRow ? 1 : 0,
        };
        for (var r = 0; r < grid.Ys.Length - 1; r++)
        {
            var row = new TableRow();
            foreach (var (cell, paragraphs) in layout.Cells.Where(c => c.Cell.Row == r).OrderBy(c => c.Cell.Column))
            {
                var tableCell = new TableCell { ColumnSpan = cell.ColumnSpan, RowSpan = cell.RowSpan };
                tableCell.Blocks.AddRange(paragraphs.Select(p => Build(p, context)));
                row.Cells.Add(tableCell);
            }

            table.Rows.Add(row);
        }

        return table;
    }

    private static LinkInline? Target(SourceLink link, PageContext context) =>
        link.Uri is not null ? new LinkInline { Uri = link.Uri }
        : link.Page is int p && context.ConvertedPages.Contains(p) ? new LinkInline { Anchor = PageAnchor(p) }
        : null;

    private static void AddLine(Line line, PageContext context, List<(string, RunStyle, SourceLink?)> segments)
    {
        var text = new StringBuilder();
        TextAssembler.Run? previous = null;
        foreach (var run in line.Runs)
        {
            var style = StyleOf(run, line, context.Page.Rules);
            if (previous is not null && NeedsSpace(previous, run, text))
            {
                var linkBefore = LinkAt(context.Page, previous.Glyphs[^1], previous.Size);
                var linkAfter = LinkAt(context.Page, run.Glyphs[0], run.Size);
                segments.Add((" ", StyleOf(previous, line, context.Page.Rules), ReferenceEquals(linkBefore, linkAfter) ? linkBefore : null));
                text.Append(' ');
            }

            // Split the run where the link under its glyphs changes.
            var runText = run.Text.ToString();
            var start = 0;
            var current = run.Glyphs.Count > 0 ? LinkAt(context.Page, run.Glyphs[0], run.Size) : null;
            for (var g = 1; g <= run.Glyphs.Count; g++)
            {
                var next = g < run.Glyphs.Count ? LinkAt(context.Page, run.Glyphs[g], run.Size) : null;
                if (g == run.Glyphs.Count || !ReferenceEquals(next, current))
                {
                    var end = g < run.Glyphs.Count ? run.Offsets[g] : runText.Length;
                    if (end > start)
                    {
                        segments.Add((runText[start..end], style, current));
                    }

                    start = end;
                    current = next;
                }
            }

            text.Append(runText);
            previous = run;
        }
    }

    private static SourceLink? LinkAt(SourcePage page, GlyphInfo g, double size)
    {
        if (page.Links.Count == 0)
        {
            return null;
        }

        var x = g.X + (Math.Cos(g.Angle) * g.Width / 2) - (Math.Sin(g.Angle) * size * 0.3);
        var y = g.Y + (Math.Sin(g.Angle) * g.Width / 2) + (Math.Cos(g.Angle) * size * 0.3);
        return page.Links.LastOrDefault(l => x >= l.Bounds.LowerLeftX - 1 && x <= l.Bounds.UpperRightX + 1 && y >= l.Bounds.LowerLeftY - 1 && y <= l.Bounds.UpperRightY + 1);
    }

    private static RunStyle StyleOf(TextAssembler.Run run, Line line, List<RuleInfo> rules)
    {
        var f = run.First.Font;
        var shift = run.First.Y - line.Baseline;
        var position = run.Size < 0.9 * line.Size && shift > 0.15 * line.Size ? VerticalPosition.Superscript
            : run.Size < 0.9 * line.Size && shift < -0.08 * line.Size ? VerticalPosition.Subscript
            : VerticalPosition.Baseline;
        var underline = false;
        var strike = false;
        if (IsHorizontal(run))
        {
            var width = run.EndX - run.First.X;
            foreach (var rule in rules)
            {
                var thin = Math.Abs(rule.Y2 - rule.Y1) < Math.Max(1.5, 0.12 * run.Size);
                var overlap = Math.Min(run.EndX, Math.Max(rule.X1, rule.X2)) - Math.Max(run.First.X, Math.Min(rule.X1, rule.X2));
                var length = Math.Abs(rule.X2 - rule.X1);
                if (!thin || width <= 0 || overlap < 0.8 * width || length > (1.3 * width) + (2 * run.Size))
                {
                    continue;
                }

                var y = (rule.Y1 + rule.Y2) / 2 - run.First.Y;
                underline |= y < -0.02 * run.Size && y > -0.35 * run.Size;
                strike |= y > 0.15 * run.Size && y < 0.45 * run.Size;
            }
        }

        return new RunStyle
        {
            FontFamily = FamilyName(f.Name),
            GenericFamily = f.IsMonospace ? "monospace" : f.IsSerif ? "serif" : "sans-serif",
            Size = Math.Round(run.Size, 1),
            Bold = f.IsBold,
            Italic = f.IsItalic,
            Underline = underline,
            Strike = strike,
            Color = run.First.Color == "#000000" ? null : run.First.Color,
            Position = position,
        };
    }

    /// <summary>A CSS-friendly family from a PDF font name: "TimesNewRomanPS-BoldItalicMT" becomes "Times New Roman".</summary>
    internal static string FamilyName(string name)
    {
        var baseName = name;
        var dash = baseName.IndexOfAny(new[] { '-', ',' });
        if (dash > 0)
        {
            baseName = baseName[..dash];
        }

        foreach (var suffix in new[] { "PSMT", "PS", "MT", "Std", "Pro" })
        {
            if (baseName.Length > suffix.Length + 2 && baseName.EndsWith(suffix, StringComparison.Ordinal))
            {
                baseName = baseName[..^suffix.Length];
                break;
            }
        }

        var sb = new StringBuilder();
        for (var i = 0; i < baseName.Length; i++)
        {
            var c = baseName[i];
            if (i > 0 && char.IsUpper(c) && char.IsLower(baseName[i - 1]))
            {
                sb.Append(' ');
            }

            if (char.IsLetterOrDigit(c) || c == ' ')
            {
                sb.Append(c);
            }
        }

        return sb.Length == 0 ? "serif" : sb.ToString();
    }

    private static void RemovePrefix(List<(string Text, RunStyle Style, SourceLink? Link)> segments, int count)
    {
        while (count > 0 && segments.Count > 0)
        {
            var (text, style, link) = segments[0];
            if (text.Length <= count)
            {
                count -= text.Length;
                segments.RemoveAt(0);
            }
            else
            {
                segments[0] = (text[count..], style, link);
                count = 0;
            }
        }

        while (segments.Count > 0 && segments[0].Text.TrimStart().Length == 0)
        {
            segments.RemoveAt(0);
        }

        if (segments.Count > 0)
        {
            segments[0] = (segments[0].Text.TrimStart(), segments[0].Style, segments[0].Link);
        }
    }

    // ------------------------------------------------------------------ lists

    /// <summary>Builds nested lists from list-item paragraphs; indentation decides the nesting level.</summary>
    private sealed class ListBuilder
    {
        private readonly List<(ListBlock List, double Left, double TextLeft)> _stack = new();

        public bool IsOpen => _stack.Count > 0;

        public void Close() => _stack.Clear();

        public bool Continues(double left, double size) => _stack.Count > 0 && left > _stack[^1].Left + (0.6 * size);

        public void AddToItem(Paragraph paragraph) => _stack[^1].List.Items[^1].Blocks.Add(paragraph);

        public void Add(List<Block> blocks, Marker marker, double left, double size, Paragraph paragraph)
        {
            while (_stack.Count > 0 && left < _stack[^1].Left - (0.6 * size))
            {
                _stack.RemoveAt(_stack.Count - 1);
            }

            ListBlock? list = null;
            if (_stack.Count > 0 && Math.Abs(left - _stack[^1].Left) <= 0.6 * size)
            {
                var top = _stack[^1].List;
                list = SameKind(top.Kind, marker.Kind) && (top.Kind == ListKind.Bullet || marker.Value == top.Start + top.Items.Count) ? top : null;
                if (list is null)
                {
                    _stack.RemoveAt(_stack.Count - 1);
                }
            }

            if (list is null)
            {
                list = new ListBlock { Kind = marker.Kind, Start = marker.Kind == ListKind.Bullet ? 1 : Math.Max(1, marker.Value) };
                if (_stack.Count > 0 && left > _stack[^1].Left)
                {
                    _stack[^1].List.Items[^1].Blocks.Add(list);
                }
                else
                {
                    _stack.Clear();
                    blocks.Add(list);
                }

                _stack.Add((list, left, left));
            }

            var item = new ListItem();
            item.Blocks.Add(paragraph);
            list.Items.Add(item);
        }

        private static bool SameKind(ListKind a, ListKind b) => a == b
            || (a == ListKind.LowerAlpha && b == ListKind.LowerRoman) || (a == ListKind.LowerRoman && b == ListKind.LowerAlpha)
            || (a == ListKind.UpperAlpha && b == ListKind.UpperRoman) || (a == ListKind.UpperRoman && b == ListKind.UpperAlpha);
    }
}
