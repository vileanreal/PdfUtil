using System.Text;
using PdfUtil.Conversion.Internal.Model;

namespace PdfUtil.Conversion.Internal.Layout;

/// <summary>Something drawn on a page. Coordinates are in points from the top-left of the page (y down) or of the slice holding it.</summary>
internal abstract record DrawOp
{
    public abstract DrawOp Offset(double dx, double dy);
}

/// <summary>Text at a baseline position.</summary>
internal sealed record TextOp(FontHandle Font, double Size, double X, double Y, string Text, string Color, bool FakeBold = false, bool FakeItalic = false) : DrawOp
{
    public override DrawOp Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };
}

/// <summary>A filled rectangle; Y is its top.</summary>
internal sealed record RectOp(double X, double Y, double Width, double Height, string Color) : DrawOp
{
    public override DrawOp Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };
}

/// <summary>An image; Y is its top.</summary>
internal sealed record ImageOp(ImageData Image, double X, double Y, double Width, double Height) : DrawOp
{
    public override DrawOp Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };
}

/// <summary>A link area; Y is its top.</summary>
internal sealed record LinkArea(double X, double Y, double Width, double Height, string? Uri, string? Anchor)
{
    public LinkArea Offset(double dx, double dy) => this with { X = X + dx, Y = Y + dy };
}

/// <summary>One laid-out line of a paragraph, in coordinates relative to its own top.</summary>
internal sealed class LineBox
{
    public double Height { get; set; }

    public double Baseline { get; set; }

    public List<DrawOp> Ops { get; } = new();

    public List<LinkArea> Links { get; } = new();

    public List<string> Anchors { get; } = new();
}

/// <summary>
/// Inline formatting: paragraph content becomes pieces (text in one font, collapsible spaces, images, breaks, anchors)
/// with break opportunities (a simplified UAX #14: after spaces, after hyphens and dashes between letters, between
/// CJK characters except before closing and after opening punctuation, at zero-width spaces), then lines are filled
/// greedily. Words longer than a line are broken between characters. Lines are aligned left, right, centred or
/// justified (extra space spread over the spaces), with half-leading line heights, super- and subscripts, text
/// decorations, inline backgrounds and link areas.
/// </summary>
internal sealed class LineLayout
{
    private readonly FontProvider _fonts;
    private readonly Func<FieldKind, string> _field;
    private readonly StringBuilder _shownText;

    public LineLayout(FontProvider fonts, Func<FieldKind, string> field, StringBuilder shownText)
    {
        _fonts = fonts;
        _field = field;
        _shownText = shownText;
    }

    private enum Kind
    {
        Text,
        Space,
        Image,
        Break,
        Anchor,
    }

    private sealed class Piece
    {
        public Kind Kind { get; init; }

        public string Text { get; set; } = "";

        public FontHandle? Font { get; init; }

        public RunStyle Style { get; init; } = RunStyle.Plain;

        public double Width { get; set; }

        public bool BreakAfter { get; set; }

        public LinkInline? Link { get; init; }

        public ImageInline? Image { get; init; }

        public string? AnchorName { get; init; }

        public double Size => Style.Size ?? 12;
    }

    // ------------------------------------------------------------------ pieces

    private List<Piece> Pieces(Paragraph p)
    {
        var pieces = new List<Piece>();
        var collapse = p.WhiteSpace is WhiteSpaceMode.Normal or WhiteSpaceMode.NoWrap or WhiteSpaceMode.PreLine;
        var wrap = p.WhiteSpace is WhiteSpaceMode.Normal or WhiteSpaceMode.PreWrap or WhiteSpaceMode.PreLine;
        void Walk(IEnumerable<Inline> inlines, LinkInline? link, int depth)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case TextInline t:
                        AddText(pieces, t.Text, t.Style, link, collapse, wrap);
                        break;
                    case FieldInline f:
                        AddText(pieces, _field(f.Kind), f.Style, link, collapse, wrap);
                        break;
                    case LinkInline l when depth < 16:
                        Walk(l.Children, l, depth + 1);
                        break;
                    case BreakInline:
                        pieces.Add(new Piece { Kind = Kind.Break, Style = p.DefaultStyle ?? RunStyle.Plain });
                        break;
                    case ImageInline image:
                        pieces.Add(new Piece { Kind = Kind.Image, Image = image, Width = image.Width, Link = link, BreakAfter = wrap, Style = p.DefaultStyle ?? RunStyle.Plain });
                        if (wrap && pieces.Count > 1 && pieces[^2].Kind == Kind.Text)
                        {
                            pieces[^2].BreakAfter = true;
                        }

                        break;
                    case AnchorInline a:
                        pieces.Add(new Piece { Kind = Kind.Anchor, AnchorName = a.Name });
                        break;
                }
            }
        }

        Walk(p.Inlines, null, 0);
        return pieces;
    }

    private void AddText(List<Piece> pieces, string text, RunStyle style, LinkInline? link, bool collapse, bool wrap)
    {
        var size = style.Position == VerticalPosition.Baseline ? style.Size ?? 12 : (style.Size ?? 12) * 0.75;
        var sized = style with { Size = size };
        var word = new StringBuilder();
        void FlushWord(bool breakAfter)
        {
            if (word.Length == 0)
            {
                return;
            }

            foreach (var (font, segment) in _fonts.Segments(sized, word.ToString()))
            {
                pieces.Add(new Piece { Kind = Kind.Text, Text = segment, Font = font, Style = sized, Width = font.Width(segment, size), Link = link });
            }

            pieces[^1].BreakAfter = breakAfter && wrap;
            word.Clear();
        }

        var runes = text.Replace("\t", "    ", StringComparison.Ordinal).EnumerateRunes().ToList();
        for (var i = 0; i < runes.Count; i++)
        {
            var r = runes[i].Value;
            if (r is ' ' or '\t' && (collapse || wrap))
            {
                FlushWord(false);
                var spaceText = r == '\t' && !collapse ? "    " : " ";
                var font = _fonts.Segments(sized, " ")[0].Font;
                pieces.Add(new Piece { Kind = Kind.Space, Text = spaceText, Font = font, Style = sized, Width = font.Width(spaceText, size), BreakAfter = wrap, Link = link });
                continue;
            }

            if (r == 0x200B)
            {
                FlushWord(true);
                continue;
            }

            word.Append(runes[i].ToString());
            if (i + 1 < runes.Count && BreakBetween(r, runes[i + 1].Value, i > 0 ? runes[i - 1].Value : 0))
            {
                FlushWord(true);
            }
        }

        FlushWord(false);
    }

    private static bool IsCjk(int c) =>
        c is (>= 0x2E80 and <= 0x9FFF) or (>= 0xAC00 and <= 0xD7AF) or (>= 0xF900 and <= 0xFAFF) or (>= 0xFF00 and <= 0xFFEF) or (>= 0x20000 and <= 0x2FFFF);

    private const string NoBreakBefore = "、。，．！？）」』】〉》〕｝ー々〜・：；…,.!?)]}%";
    private const string NoBreakAfter = "（「『【〈《〔｛([{";

    /// <summary>Whether a line may break between two characters (other than at spaces).</summary>
    internal static bool BreakBetween(int a, int b, int beforeA)
    {
        if (a is 0x00A0 or 0x2011 or 0x202F or 0x2060 || b is 0x00A0 or 0x2011 or 0x202F or 0x2060)
        {
            return false;
        }

        if ((IsCjk(a) || IsCjk(b)) && !NoBreakBefore.Contains((char)Math.Min(b, 0xFFFF)) && !NoBreakAfter.Contains((char)Math.Min(a, 0xFFFF)))
        {
            return b > 0xFFFF || !char.IsWhiteSpace((char)b);
        }

        // After a hyphen or dash between letters ("well-known").
        if (a is '-' or 0x2010 or 0x2013 or 0x2014 && char.IsLetterOrDigit((char)Math.Min(beforeA, 0xFFFF)) && b < 0x10000 && char.IsLetter((char)b))
        {
            return true;
        }

        return false;
    }

    // ------------------------------------------------------------------ intrinsic widths

    /// <summary>The widest unbreakable piece and the width of the longest forced line.</summary>
    public (double Min, double Max) Intrinsic(Paragraph p)
    {
        var pieces = Pieces(p);
        double min = 0, max = 0, word = 0, line = p.TextIndent;
        foreach (var piece in pieces)
        {
            if (piece.Kind == Kind.Break)
            {
                max = Math.Max(max, line);
                min = Math.Max(min, word);
                (line, word) = (0, 0);
                continue;
            }

            line += piece.Width;
            if (piece.Kind == Kind.Space)
            {
                min = Math.Max(min, word);
                word = 0;
                continue;
            }

            word += piece.Width;
            if (piece.BreakAfter)
            {
                min = Math.Max(min, word);
                word = 0;
            }
        }

        return (Math.Max(min, word), Math.Max(max, line));
    }

    // ------------------------------------------------------------------ lines

    public List<LineBox> Layout(Paragraph p, double x, double width)
    {
        var pieces = Pieces(p);
        var lines = new List<LineBox>();
        var strut = p.DefaultStyle ?? RunStyle.Plain;
        var strutFont = _fonts.Primary(strut);
        var collapse = p.WhiteSpace is WhiteSpaceMode.Normal or WhiteSpaceMode.NoWrap or WhiteSpaceMode.PreLine;
        var i = 0;
        var first = true;
        while (i < pieces.Count)
        {
            var indent = first ? p.TextIndent : 0;
            var available = Math.Max(1, width - indent);
            var line = new List<Piece>();
            double used = 0;
            var lastBreak = -1;
            var forced = false;

            // Spaces at the start of a line are dropped when white space collapses.
            while (collapse && i < pieces.Count && pieces[i].Kind == Kind.Space)
            {
                i++;
            }

            while (i < pieces.Count)
            {
                var piece = pieces[i];
                if (piece.Kind == Kind.Break)
                {
                    i++;
                    forced = true;
                    break;
                }

                if (piece.Kind != Kind.Space && used + piece.Width > available + 0.001 && line.Any(l => l.Kind is Kind.Text or Kind.Image))
                {
                    if (lastBreak >= 0)
                    {
                        // Back up to the last break opportunity.
                        var keep = lastBreak + 1;
                        i -= line.Count - keep;
                        line.RemoveRange(keep, line.Count - keep);
                        break;
                    }

                    if (piece.Kind == Kind.Text && p.WhiteSpace is not (WhiteSpaceMode.Pre or WhiteSpaceMode.NoWrap) && SplitToFit(pieces, i, available - used))
                    {
                        line.Add(pieces[i]);
                        i++;
                    }

                    break;
                }

                if (piece.Kind == Kind.Text && piece.Width > available && line.Count == 0 && p.WhiteSpace is not (WhiteSpaceMode.Pre or WhiteSpaceMode.NoWrap))
                {
                    // A word wider than the whole line is broken between characters.
                    SplitToFit(pieces, i, available);
                    piece = pieces[i];
                }

                line.Add(piece);
                used += piece.Width;
                if (piece.BreakAfter)
                {
                    lastBreak = line.Count - 1;
                }

                i++;
            }

            // Trailing spaces hang outside the line when white space collapses.
            while (collapse && line.Count > 0 && line[^1].Kind == Kind.Space)
            {
                line.RemoveAt(line.Count - 1);
            }

            var last = i >= pieces.Count || forced;
            lines.Add(Build(line, p, strut, strutFont, x + indent, available, last));
            first = false;
            if (i >= pieces.Count && forced)
            {
                // A break at the very end still makes an empty line.
                lines.Add(Build(new List<Piece>(), p, strut, strutFont, x, width, true));
            }
        }

        return lines;
    }

    /// <summary>Splits a text piece so that its first part fits <paramref name="width"/> (at least one character).</summary>
    private static bool SplitToFit(List<Piece> pieces, int index, double width)
    {
        var piece = pieces[index];
        var runes = piece.Text.EnumerateRunes().ToList();
        if (runes.Count < 2 || piece.Font is null)
        {
            return false;
        }

        var take = 1;
        while (take < runes.Count && piece.Font.Width(string.Concat(runes.Take(take + 1)), piece.Size) <= width)
        {
            take++;
        }

        if (take >= runes.Count)
        {
            return false;
        }

        var head = string.Concat(runes.Take(take));
        var tail = string.Concat(runes.Skip(take));
        var rest = new Piece { Kind = Kind.Text, Text = tail, Font = piece.Font, Style = piece.Style, Width = piece.Font.Width(tail, piece.Size), Link = piece.Link, BreakAfter = piece.BreakAfter };
        piece.Text = head;
        piece.Width = piece.Font.Width(head, piece.Size);
        piece.BreakAfter = true;
        pieces.Insert(index + 1, rest);
        return true;
    }

    private double LineHeightFor(Paragraph p, double size, FontHandle font) =>
        p.LineHeightPoints ?? (p.LineHeight is double m ? m * size : Math.Max(1.15, font.Ascent - font.Descent) * size);

    private LineBox Build(List<Piece> line, Paragraph p, RunStyle strut, FontHandle strutFont, double x, double width, bool endsParagraph)
    {
        // Vertical metrics: each piece's content area plus half-leading, and the paragraph's strut.
        var strutSize = strut.Size ?? 12;
        var strutHeight = LineHeightFor(p, strutSize, strutFont);
        var strutHalf = (strutHeight - ((strutFont.Ascent - strutFont.Descent) * strutSize)) / 2;
        double above = (strutFont.Ascent * strutSize) + strutHalf;
        double below = (-strutFont.Descent * strutSize) + strutHalf;
        foreach (var piece in line)
        {
            if (piece.Kind == Kind.Image)
            {
                above = Math.Max(above, piece.Image!.Height);
                continue;
            }

            if (piece.Font is null)
            {
                continue;
            }

            var size = piece.Size;
            var shift = Shift(piece);
            var height = LineHeightFor(p, size, piece.Font);
            var half = (height - ((piece.Font.Ascent - piece.Font.Descent) * size)) / 2;
            above = Math.Max(above, (piece.Font.Ascent * size) + half + shift);
            below = Math.Max(below, (-piece.Font.Descent * size) + half - shift);
        }

        var box = new LineBox { Height = above + below, Baseline = above };
        var used = line.Sum(l => l.Width);
        var offset = p.Alignment switch
        {
            TextAlignment.Center => Math.Max(0, (width - used) / 2),
            TextAlignment.Right => Math.Max(0, width - used),
            _ => 0,
        };
        var spaces = line.Count(l => l.Kind == Kind.Space);
        var extra = p.Alignment == TextAlignment.Justify && !endsParagraph && spaces > 0 && used < width ? (width - used) / spaces : 0;

        var cx = x + offset;
        TextOp? pending = null;
        void FlushText()
        {
            if (pending is not null)
            {
                box.Ops.Add(pending);
                _shownText.Append(pending.Text);
                pending = null;
            }
        }

        foreach (var piece in line)
        {
            var w = piece.Width + (piece.Kind == Kind.Space ? extra : 0);
            switch (piece.Kind)
            {
                case Kind.Anchor:
                    box.Anchors.Add(piece.AnchorName!);
                    continue;
                case Kind.Image:
                    FlushText();
                    var image = piece.Image!;
                    box.Ops.Add(new ImageOp(image.Image, cx, box.Baseline - image.Height, image.Width, image.Height));
                    break;
                case Kind.Text or Kind.Space:
                    var style = piece.Style;
                    var color = style.Color ?? "#000000";
                    var baseline = box.Baseline - Shift(piece);
                    if (style.Background is { } background)
                    {
                        box.Ops.Insert(0, new RectOp(cx, baseline - (piece.Font!.Ascent * piece.Size), w, (piece.Font.Ascent - piece.Font.Descent) * piece.Size, background));
                    }

                    if (piece.Kind == Kind.Text || extra == 0)
                    {
                        // A face without bold or italic is emboldened or slanted, as browsers do.
                        var fakeBold = style.Bold && piece.Font is TrueTypeFontHandle { Embedded.Font.IsBold: false };
                        var fakeItalic = style.Italic && piece.Font is TrueTypeFontHandle { Embedded.Font.IsItalic: false };
                        if (pending is not null && pending.Font == piece.Font && pending.Size == piece.Size && pending.Color == color && pending.FakeBold == fakeBold && pending.FakeItalic == fakeItalic && Math.Abs(pending.Y - baseline) < 0.001 && Math.Abs(pending.X + pending.Font.Width(pending.Text, pending.Size) - cx) < 0.01)
                        {
                            pending = pending with { Text = pending.Text + piece.Text };
                        }
                        else
                        {
                            FlushText();
                            pending = new TextOp(piece.Font!, piece.Size, cx, baseline, piece.Text, color, fakeBold, fakeItalic);
                        }
                    }
                    else
                    {
                        // Justified spaces are gaps between separately placed words.
                        FlushText();
                    }

                    if (style.Underline || style.Strike)
                    {
                        var thickness = Math.Max(0.4, piece.Font!.UnderlineThickness * piece.Size);
                        if (style.Underline)
                        {
                            box.Ops.Add(new RectOp(cx, baseline - (piece.Font.UnderlinePosition * piece.Size), w, thickness, color));
                        }

                        if (style.Strike)
                        {
                            box.Ops.Add(new RectOp(cx, baseline - (0.3 * piece.Size), w, thickness, color));
                        }
                    }

                    break;
            }

            if (piece.Link is { } link && piece.Kind != Kind.Anchor)
            {
                var top = 0.0;
                var previous = box.Links.Count > 0 ? box.Links[^1] : null;
                if (previous is not null && previous.Uri == link.Uri && previous.Anchor == link.Anchor && Math.Abs(previous.X + previous.Width - cx) < 0.01)
                {
                    box.Links[^1] = previous with { Width = previous.Width + w };
                }
                else
                {
                    box.Links.Add(new LinkArea(cx, top, w, box.Height, link.Uri, link.Anchor));
                }
            }

            cx += w;
        }

        FlushText();
        if (endsParagraph)
        {
            _shownText.Append(' ');
        }

        return box;
    }

    private static double Shift(Piece piece) => piece.Style.Position switch
    {
        VerticalPosition.Superscript => piece.Size * 0.45,
        VerticalPosition.Subscript => -piece.Size * 0.2,
        _ => 0,
    };
}
