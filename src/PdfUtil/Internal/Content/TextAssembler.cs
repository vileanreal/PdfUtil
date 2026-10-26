using System.Text;

namespace PdfUtil.Internal.Content;

/// <summary>
/// Turns glyphs into runs and page text. Glyphs join a run while font, size, colour, direction and baseline stay the
/// same and the gap to the previous glyph is small; a space is inserted for gaps wider than a fraction of the font
/// size (this covers <c>TJ</c> offsets used as spaces). Lines break where the baseline moves.
/// </summary>
internal static class TextAssembler
{
    private const double SpaceGap = 0.15;
    private const double RunGap = 0.6;

    internal sealed class Run
    {
        public required GlyphInfo First { get; init; }

        public List<GlyphInfo> Glyphs { get; } = new();

        public StringBuilder Text { get; } = new();

        /// <summary>For each glyph, where its text starts in <see cref="Text"/>.</summary>
        public List<int> Offsets { get; } = new();

        public double EndX { get; set; }

        public double EndY { get; set; }

        public double Size => First.Size;

        /// <summary>Signed distance from the run's start along its direction.</summary>
        public double Along(double x, double y) => ((x - First.X) * Math.Cos(First.Angle)) + ((y - First.Y) * Math.Sin(First.Angle));

        /// <summary>Distance from the run's baseline, perpendicular to its direction.</summary>
        public double Across(double x, double y) => (-(x - First.X) * Math.Sin(First.Angle)) + ((y - First.Y) * Math.Cos(First.Angle));
    }

    public static List<Run> BuildRuns(IReadOnlyList<GlyphInfo> glyphs)
    {
        var runs = new List<Run>();
        Run? run = null;
        foreach (var g in glyphs)
        {
            if (g.Text.Length == 0)
            {
                continue;
            }

            if (run is not null && Continues(run, g, out var gap))
            {
                if (gap > SpaceGap * run.Size && !char.IsWhiteSpace(run.Text[^1]) && !char.IsWhiteSpace(g.Text[0]))
                {
                    run.Text.Append(' ');
                }
            }
            else
            {
                run = new Run { First = g };
                runs.Add(run);
            }

            run.Glyphs.Add(g);
            run.Offsets.Add(run.Text.Length);
            run.Text.Append(g.Text);
            run.EndX = g.X + (Math.Cos(g.Angle) * Math.Max(g.Width, g.Advance));
            run.EndY = g.Y + (Math.Sin(g.Angle) * Math.Max(g.Width, g.Advance));
        }

        return runs;
    }

    private static bool Continues(Run run, GlyphInfo g, out double gap)
    {
        gap = run.Along(g.X, g.Y) - run.Along(run.EndX, run.EndY);
        var f = run.First;
        return ReferenceEquals(f.Font, g.Font)
            && Math.Abs(f.Size - g.Size) < 0.05 * f.Size
            && f.Color == g.Color
            && f.IsInvisible == g.IsInvisible
            && Math.Abs(NormalizeAngle(f.Angle - g.Angle)) < 0.02
            && Math.Abs(run.Across(g.X, g.Y)) < 0.2 * f.Size
            && gap > -0.3 * f.Size
            && gap < RunGap * f.Size;
    }

    private static double NormalizeAngle(double a)
    {
        while (a > Math.PI)
        {
            a -= 2 * Math.PI;
        }

        while (a < -Math.PI)
        {
            a += 2 * Math.PI;
        }

        return a;
    }

    /// <summary>The page text in content order: runs as shown, with line breaks where the baseline moves.</summary>
    public static string ContentOrder(List<Run> runs)
    {
        var sb = new StringBuilder();
        Run? previous = null;
        foreach (var run in runs)
        {
            if (previous is not null)
            {
                var across = previous.Across(run.First.X, run.First.Y);
                var gap = previous.Along(run.First.X, run.First.Y) - previous.Along(previous.EndX, previous.EndY);
                var size = Math.Max(previous.Size, run.Size);
                if (Math.Abs(across) > 0.5 * size || gap < -size || Math.Abs(NormalizeAngle(previous.First.Angle - run.First.Angle)) > 0.02)
                {
                    sb.Append('\n');
                }
                else if (gap > SpaceGap * size && sb.Length > 0 && !char.IsWhiteSpace(sb[^1]) && !char.IsWhiteSpace(run.Text[0]))
                {
                    sb.Append(' ');
                }
            }

            sb.Append(run.Text);
            previous = run;
        }

        return sb.ToString();
    }

    /// <summary>The page text in layout order: lines from top to bottom, runs from left to right within a line.</summary>
    public static string LayoutOrder(List<Run> runs)
    {
        var lines = new List<List<Run>>();
        foreach (var run in runs.OrderByDescending(r => r.First.Y))
        {
            var line = lines.LastOrDefault();
            if (line is not null && Math.Abs(line[0].First.Y - run.First.Y) < 0.5 * Math.Max(line[0].Size, run.Size))
            {
                line.Add(run);
            }
            else
            {
                lines.Add(new List<Run> { run });
            }
        }

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            Run? previous = null;
            foreach (var run in line.OrderBy(r => r.First.X))
            {
                if (previous is not null && run.First.X - previous.EndX > SpaceGap * run.Size && !char.IsWhiteSpace(sb[^1]) && !char.IsWhiteSpace(run.Text[0]))
                {
                    sb.Append(' ');
                }

                sb.Append(run.Text);
                previous = run;
            }
        }

        return sb.ToString();
    }
}
