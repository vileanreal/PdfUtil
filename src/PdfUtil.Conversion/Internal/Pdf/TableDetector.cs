using PdfUtil.Internal.Content;

namespace PdfUtil.Conversion.Internal.Pdf;

/// <summary>A ruled grid: column boundaries (left to right), row boundaries (top to bottom) and its cells.</summary>
internal sealed class RuledGrid
{
    public required double[] Xs { get; init; }

    public required double[] Ys { get; init; }

    public List<GridCell> Cells { get; } = new();

    public double Left => Xs[0];

    public double Right => Xs[^1];

    public double Top => Ys[0];

    public double Bottom => Ys[^1];
}

/// <summary>A cell of a <see cref="RuledGrid"/>; spans come from missing inner borders.</summary>
internal sealed record GridCell(int Row, int Column, int RowSpan, int ColumnSpan)
{
    public double Left(RuledGrid g) => g.Xs[Column];

    public double Right(RuledGrid g) => g.Xs[Column + ColumnSpan];

    public double Top(RuledGrid g) => g.Ys[Row];

    public double Bottom(RuledGrid g) => g.Ys[Row + RowSpan];
}

/// <summary>
/// Finds tables drawn with ruling lines: horizontal and vertical segments (stroked lines, stroked rectangles, thin
/// filled rectangles) that cross each other form connected groups; a group with at least two columns or rows of
/// cells is a grid. Cells whose inner border is missing are merged into column or row spans. Whether the text fits
/// the grid is decided by the caller.
/// </summary>
internal static class TableDetector
{
    private const double Tolerance = 2;
    private const int MaxSegments = 4000;

    private readonly record struct Segment(bool Horizontal, double At, double From, double To);

    public static List<RuledGrid> Detect(IReadOnlyList<RuleInfo> rules)
    {
        var segments = Segments(rules);
        var grids = new List<RuledGrid>();
        if (segments.Count < 4 || segments.Count > MaxSegments)
        {
            return grids;
        }

        // Union-find over crossing (or touching) horizontal and vertical segments.
        var parent = Enumerable.Range(0, segments.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (var i = 0; i < segments.Count; i++)
        {
            for (var j = i + 1; j < segments.Count; j++)
            {
                var (a, b) = (segments[i], segments[j]);
                if (a.Horizontal == b.Horizontal)
                {
                    continue;
                }

                var (h, v) = a.Horizontal ? (a, b) : (b, a);
                if (v.At >= h.From - Tolerance && v.At <= h.To + Tolerance && h.At >= v.From - Tolerance && h.At <= v.To + Tolerance)
                {
                    parent[Find(i)] = Find(j);
                }
            }
        }

        foreach (var group in Enumerable.Range(0, segments.Count).GroupBy(Find).Select(g => g.Select(i => segments[i]).ToList()))
        {
            var horizontal = group.Where(s => s.Horizontal).ToList();
            var vertical = group.Where(s => !s.Horizontal).ToList();
            var xs = Cluster(vertical.Select(s => s.At)).ToArray();
            var ys = Cluster(horizontal.Select(s => s.At)).OrderByDescending(y => y).ToArray();
            if (xs.Length < 2 || ys.Length < 2 || (xs.Length - 1) * (ys.Length - 1) < 2)
            {
                continue;
            }

            var grid = new RuledGrid { Xs = xs, Ys = ys };
            var rows = ys.Length - 1;
            var columns = xs.Length - 1;
            bool HasVertical(int boundary, int row) => vertical.Any(s => Math.Abs(s.At - xs[boundary]) <= Tolerance && s.From <= ((ys[row] + ys[row + 1]) / 2) && s.To >= ((ys[row] + ys[row + 1]) / 2));
            bool HasHorizontal(int boundary, int column) => horizontal.Any(s => Math.Abs(s.At - ys[boundary]) <= Tolerance && s.From <= ((xs[column] + xs[column + 1]) / 2) && s.To >= ((xs[column] + xs[column + 1]) / 2));
            var covered = new bool[rows, columns];
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < columns; c++)
                {
                    if (covered[r, c])
                    {
                        continue;
                    }

                    var colSpan = 1;
                    while (c + colSpan < columns && !HasVertical(c + colSpan, r))
                    {
                        colSpan++;
                    }

                    var rowSpan = 1;
                    while (r + rowSpan < rows && !HasHorizontal(r + rowSpan, c))
                    {
                        rowSpan++;
                    }

                    for (var rr = r; rr < r + rowSpan; rr++)
                    {
                        for (var cc = c; cc < c + colSpan; cc++)
                        {
                            covered[rr, cc] = true;
                        }
                    }

                    grid.Cells.Add(new GridCell(r, c, rowSpan, colSpan));
                }
            }

            if (grid.Cells.Count >= 2)
            {
                grids.Add(grid);
            }
        }

        return grids;
    }

    private static List<Segment> Segments(IReadOnlyList<RuleInfo> rules)
    {
        var segments = new List<Segment>();
        foreach (var r in rules)
        {
            var (x1, x2) = (Math.Min(r.X1, r.X2), Math.Max(r.X1, r.X2));
            var (y1, y2) = (Math.Min(r.Y1, r.Y2), Math.Max(r.Y1, r.Y2));
            var (w, h) = (x2 - x1, y2 - y1);
            if (r.IsRectangle && (!r.Filled || (w > 2.5 && h > 2.5)))
            {
                // A stroked rectangle is four borders; a large filled one is a background, not a rule.
                if (!r.Filled && w > 3 && h > 3)
                {
                    segments.Add(new Segment(true, y1, x1, x2));
                    segments.Add(new Segment(true, y2, x1, x2));
                    segments.Add(new Segment(false, x1, y1, y2));
                    segments.Add(new Segment(false, x2, y1, y2));
                }

                continue;
            }

            if (h <= 2.5 && w > 3)
            {
                segments.Add(new Segment(true, (y1 + y2) / 2, x1, x2));
            }
            else if (w <= 2.5 && h > 3)
            {
                segments.Add(new Segment(false, (x1 + x2) / 2, y1, y2));
            }
        }

        return segments;
    }

    /// <summary>Positions closer than the tolerance merge into their average; ascending.</summary>
    private static List<double> Cluster(IEnumerable<double> values)
    {
        var result = new List<(double Sum, int Count)>();
        foreach (var v in values.Order())
        {
            if (result.Count > 0 && v - (result[^1].Sum / result[^1].Count) <= Tolerance)
            {
                result[^1] = (result[^1].Sum + v, result[^1].Count + 1);
            }
            else
            {
                result.Add((v, 1));
            }
        }

        return result.Select(r => r.Sum / r.Count).ToList();
    }
}
