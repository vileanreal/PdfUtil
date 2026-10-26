using System.Globalization;
using System.Text;
using PdfUtil.Internal.Fonts;

namespace PdfUtil.Tests.Infrastructure;

/// <summary>
/// Builds documents with a known layout for conversion tests: wrapped paragraphs, headings, lists, columns, links,
/// images and ruling lines, in the standard fonts F1 Helvetica, F2 Helvetica-Bold, F3 Times-Roman, F4 Courier,
/// F5 Helvetica-Oblique and F6 Times-Bold (WinAnsi).
/// </summary>
internal sealed class LayoutPdf
{
    private static readonly (string Key, string BaseFont)[] Fonts =
    {
        ("F1", "Helvetica"), ("F2", "Helvetica-Bold"), ("F3", "Times-Roman"), ("F4", "Courier"), ("F5", "Helvetica-Oblique"), ("F6", "Times-Bold"),
    };

    private readonly List<PageBuilder> _pages = new();
    private readonly List<string> _images = new();

    public string? Title { get; set; }

    public string? Language { get; set; }

    /// <summary>Bookmarks: title and zero-based page index.</summary>
    public List<(string Title, int Page)> Bookmarks { get; } = new();

    public PageBuilder Page(double width = 612, double height = 792, int rotate = 0)
    {
        var page = new PageBuilder(this, width, height, rotate);
        _pages.Add(page);
        return page;
    }

    /// <summary>Adds an image XObject body (a stream object) and returns its resource name.</summary>
    public string AddImage(string streamObject)
    {
        _images.Add(streamObject);
        return "Im" + _images.Count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>An uncompressed DeviceGray image with the given 8-bit pixels.</summary>
    public string GrayImage(int width, int height, byte[] pixels) =>
        AddImage(RawPdf.Stream($"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /ASCIIHexDecode", Convert.ToHexString(pixels) + ">"));

    public static double TextWidth(string text, string font, double size) => text.Sum(c => StandardFonts.Width(font, c)) * size / 1000;

    public static string BaseFont(string key) => Fonts.First(f => f.Key == key).BaseFont;

    public byte[] Build()
    {
        var objects = new Dictionary<int, string>();
        var fontObjects = new StringBuilder();
        for (var i = 0; i < Fonts.Length; i++)
        {
            objects[3 + i] = $"<< /Type /Font /Subtype /Type1 /BaseFont /{Fonts[i].BaseFont} /Encoding /WinAnsiEncoding >>";
            fontObjects.Append(CultureInfo.InvariantCulture, $"/{Fonts[i].Key} {3 + i} 0 R ");
        }

        var next = 3 + Fonts.Length;
        var imageObjects = new StringBuilder();
        for (var i = 0; i < _images.Count; i++)
        {
            objects[next] = _images[i];
            imageObjects.Append(CultureInfo.InvariantCulture, $"/Im{i + 1} {next} 0 R ");
            next++;
        }

        var pageNumbers = new List<int>();
        for (var i = 0; i < _pages.Count; i++)
        {
            pageNumbers.Add(next);
            next += 2;
        }

        for (var i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];
            var annots = new List<int>();
            foreach (var (rect, target) in page.Links)
            {
                var action = target.StartsWith("#page", StringComparison.Ordinal)
                    ? $"/Dest [{pageNumbers[int.Parse(target[5..], CultureInfo.InvariantCulture)]} 0 R /XYZ 0 792 0]"
                    : $"/A << /S /URI /URI ({Escape(target)}) >>";
                objects[next] = $"<< /Type /Annot /Subtype /Link /Rect [{rect}] /Border [0 0 0] {action} >>";
                annots.Add(next++);
            }

            var annotsEntry = annots.Count > 0 ? "/Annots [" + string.Join(" ", annots.Select(a => $"{a} 0 R")) + "]" : "";
            objects[pageNumbers[i]] = $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(page.Width)} {N(page.Height)}] /Rotate {page.Rotate} /Resources << /Font << {fontObjects}>> /XObject << {imageObjects}>> >> /Contents {pageNumbers[i] + 1} 0 R {annotsEntry} >>";
            objects[pageNumbers[i] + 1] = RawPdf.Stream("", page.Content.ToString());
        }

        objects[2] = $"<< /Type /Pages /Kids [{string.Join(" ", pageNumbers.Select(p => $"{p} 0 R"))}] /Count {_pages.Count} >>";
        var catalog = new StringBuilder("<< /Type /Catalog /Pages 2 0 R ");
        if (Language is not null)
        {
            catalog.Append(CultureInfo.InvariantCulture, $"/Lang ({Language}) ");
        }

        if (Bookmarks.Count > 0)
        {
            var root = next++;
            var items = Bookmarks.Select(_ => next++).ToList();
            objects[root] = $"<< /Type /Outlines /First {items[0]} 0 R /Last {items[^1]} 0 R /Count {items.Count} >>";
            for (var i = 0; i < items.Count; i++)
            {
                var links = (i > 0 ? $"/Prev {items[i - 1]} 0 R " : "") + (i + 1 < items.Count ? $"/Next {items[i + 1]} 0 R " : "");
                objects[items[i]] = $"<< /Title ({Escape(Bookmarks[i].Title)}) /Parent {root} 0 R {links}/Dest [{pageNumbers[Bookmarks[i].Page]} 0 R /Fit] >>";
            }

            catalog.Append(CultureInfo.InvariantCulture, $"/Outlines {root} 0 R ");
        }

        objects[1] = catalog.Append(">>").ToString();
        var trailer = "/Root 1 0 R";
        if (Title is not null)
        {
            objects[next] = $"<< /Title ({Escape(Title)}) /Author (Test Author) >>";
            trailer += $" /Info {next} 0 R";
        }

        return RawPdf.Build(objects, trailer);
    }

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A PDF literal string body in WinAnsi (Latin-1 for the characters tests use).</summary>
    public static string Escape(string text)
    {
        var sb = new StringBuilder();
        foreach (var b in FontEncodings.EncodeWinAnsi(text, out _))
        {
            var ch = (char)b;
            if (ch is '(' or ')' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    internal sealed class PageBuilder
    {
        private readonly LayoutPdf _owner;

        public PageBuilder(LayoutPdf owner, double width, double height, int rotate)
        {
            _owner = owner;
            Width = width;
            Height = height;
            Rotate = rotate;
        }

        public double Width { get; }

        public double Height { get; }

        public int Rotate { get; }

        public StringBuilder Content { get; } = new();

        public List<(string Rect, string Target)> Links { get; } = new();

        public PageBuilder Text(double x, double y, double size, string text, string font = "F1", string? rgb = null)
        {
            var color = rgb is null ? "0 g" : rgb + " rg";
            Content.Append(CultureInfo.InvariantCulture, $"BT {color} /{font} {N(size)} Tf {N(x)} {N(y)} Td ({Escape(text)}) Tj ET\n");
            return this;
        }

        /// <summary>Writes <paramref name="text"/> wrapped to <paramref name="width"/>, and moves <paramref name="y"/> below it (plus <paramref name="spaceAfter"/>).</summary>
        public PageBuilder Paragraph(double x, ref double y, double width, double size, string text, string font = "F1", double leading = 1.2, double firstIndent = 0, double spaceAfter = 6)
        {
            var baseFont = BaseFont(font);
            var line = new StringBuilder();
            var first = true;
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                var indent = first ? firstIndent : 0;
                if (line.Length > 0 && TextWidth(candidate, baseFont, size) > width - indent)
                {
                    Text(x + indent, y, size, line.ToString(), font);
                    y -= size * leading;
                    first = false;
                    line.Clear().Append(word);
                }
                else
                {
                    line.Clear().Append(candidate);
                }
            }

            if (line.Length > 0)
            {
                Text(x + (first ? firstIndent : 0), y, size, line.ToString(), font);
                y -= size * leading;
            }

            y -= spaceAfter;
            return this;
        }

        public PageBuilder Link(double x1, double y1, double x2, double y2, string target)
        {
            Links.Add(($"{N(x1)} {N(y1)} {N(x2)} {N(y2)}", target));
            return this;
        }

        /// <summary>A link to the page at zero-based <paramref name="pageIndex"/>.</summary>
        public PageBuilder LinkToPage(double x1, double y1, double x2, double y2, int pageIndex) => Link(x1, y1, x2, y2, "#page" + pageIndex.ToString(CultureInfo.InvariantCulture));

        public PageBuilder Image(string name, double x, double y, double width, double height)
        {
            Content.Append(CultureInfo.InvariantCulture, $"q {N(width)} 0 0 {N(height)} {N(x)} {N(y)} cm /{name} Do Q\n");
            return this;
        }

        public PageBuilder Line(double x1, double y1, double x2, double y2, double lineWidth = 0.5)
        {
            Content.Append(CultureInfo.InvariantCulture, $"q {N(lineWidth)} w {N(x1)} {N(y1)} m {N(x2)} {N(y2)} l S Q\n");
            return this;
        }

        public PageBuilder Raw(string content)
        {
            Content.Append(content).Append('\n');
            return this;
        }

        public LayoutPdf Done() => _owner;
    }
}
