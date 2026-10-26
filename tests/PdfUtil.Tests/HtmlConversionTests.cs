using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using PdfUtil.Conversion;
using PdfUtil.Extensions.DependencyInjection;
using PdfUtil.Internal.Images;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>F19: PDF to HTML.</summary>
public class HtmlConversionTests
{
    private static readonly XNamespace X = "http://www.w3.org/1999/xhtml";
    private static readonly PdfConverter Converter = new();

    internal static (string Html, PdfToHtmlResult Result) Convert(byte[] pdf, PdfToHtmlOptions? options = null, PdfConverter? converter = null)
    {
        using var output = new MemoryStream();
        var result = (converter ?? Converter).ConvertToHtmlAsync(new MemoryStream(pdf), output, options).GetAwaiter().GetResult();
        return (Encoding.UTF8.GetString(output.ToArray()), result);
    }

    internal static XDocument Parse(string html)
    {
        using var reader = XmlReader.Create(new StringReader(html), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        return XDocument.Load(reader);
    }

    /// <summary>The text of the document body (without navigation and image placeholders), whitespace removed.</summary>
    internal static string MainText(string html)
    {
        var main = Parse(html).Descendants(X + "main").Single();
        var text = main.DescendantNodes().OfType<XText>().Where(t => t.Parent?.Attribute("class")?.Value != "pdf-missing-image").Select(t => t.Value);
        return Strip(string.Concat(text));
    }

    private static string Strip(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static string Sorted(string s) => new(Strip(s).Order().ToArray());

    private static string Extract(byte[] pdf, PdfTextOrder order, string? password = null) =>
        new PdfTextExtractor().ExtractAsync(new MemoryStream(pdf), new PdfTextExtractionOptions { Order = order, Password = password }).GetAwaiter().GetResult().Text;

    /// <summary>Two pages with headings, paragraphs, lists, a superscript, links, columns, an image and bookmarks.</summary>
    internal static byte[] LayoutDocument()
    {
        var doc = new LayoutPdf { Title = "Layout test", Language = "en-GB" };
        doc.Bookmarks.Add(("Annual Report", 0));
        doc.Bookmarks.Add(("Two Columns", 1));
        var image = doc.GrayImage(4, 4, Enumerable.Range(0, 16).Select(i => (byte)(i * 16)).ToArray());
        var p1 = doc.Page();
        p1.Text(72, 720, 24, "Annual Report", "F2");
        var y = 690.0;
        p1.Paragraph(72, ref y, 468, 11, "This report describes the results of the year in some detail. It continues for long enough to wrap onto several lines, so that the paragraph detection has real work to do with regular line spacing.", "F3");
        p1.Paragraph(72, ref y, 468, 11, "A second paragraph follows after a little extra space, and it is also long enough to need more than one line of text on the page.", "F3", spaceAfter: 12);
        p1.Text(72, y, 16, "Highlights", "F2");
        y -= 26;
        foreach (var item in new[] { "First point about revenue growth", "Second point, which is long enough that it needs a second line of text to be written out in full here", "Third point" })
        {
            p1.Text(80, y, 11, "•", "F3");
            var itemY = y;
            p1.Paragraph(94, ref itemY, 440, 11, item, "F3", spaceAfter: 2);
            y = itemY;
        }

        p1.Text(100, y, 11, "–", "F3").Text(114, y, 11, "A nested point", "F3");
        y -= 22;
        p1.Text(72, y, 11, "Steps", "F6");
        y -= 18;
        for (var i = 1; i <= 3; i++)
        {
            p1.Text(80, y, 11, $"{i}.", "F3").Text(96, y, 11, $"Step number {i}", "F3");
            y -= 15;
        }

        y -= 8;
        p1.Text(72, y, 11, "Energy: E = mc", "F3");
        var w = LayoutPdf.TextWidth("Energy: E = mc", "Times-Roman", 11);
        p1.Text(72 + w, y + 4, 7, "2", "F3");
        p1.Text(72 + w + 6, y, 11, " holds.", "F3");
        y -= 20;
        p1.Text(72, y, 11, "Visit example.com or click here for more.", "F3");
        var visit = LayoutPdf.TextWidth("Visit ", "Times-Roman", 11);
        var domain = LayoutPdf.TextWidth("example.com", "Times-Roman", 11);
        p1.Link(72 + visit, y - 2, 72 + visit + domain, y + 9, "https://example.com/");
        var before = LayoutPdf.TextWidth("Visit example.com or click ", "Times-Roman", 11);
        p1.Link(72 + before, y - 2, 72 + before + 20, y + 9, "javascript:alert(1)");

        var p2 = doc.Page();
        p2.Text(72, 720, 16, "Two Columns", "F2");
        var left = 690.0;
        p2.Paragraph(72, ref left, 220, 10, "Left column first paragraph with enough words to wrap over a few lines in the narrow column.", "F3");
        p2.Paragraph(72, ref left, 220, 10, "Left column second paragraph, also wrapping over more than one line of the column.", "F3");
        var right = 690.0;
        p2.Paragraph(320, ref right, 220, 10, "Right column first paragraph with enough words to wrap over a few lines in the narrow column.", "F3");
        p2.Paragraph(320, ref right, 220, 10, "Right column second paragraph, which also wraps over more than one line.", "F3");
        p2.Text(72, 400, 10, "Back to the start", "F3");
        p2.LinkToPage(70, 397, 160, 410, 0);
        p2.Image(image, 72, 300, 72, 72);
        return doc.Build();
    }

    [Fact]
    public void Reflow_RebuildsStructure()
    {
        var (html, result) = Convert(LayoutDocument());
        var doc = Parse(html);
        string Text(XElement e) => Regex.Replace(e.Value, @"\s+", " ").Trim();

        Assert.Equal("en-GB", doc.Root!.Attribute("lang")!.Value);
        Assert.Equal("Layout test", doc.Descendants(X + "title").Single().Value);
        Assert.Equal("Annual Report", Text(doc.Descendants(X + "h1").Single()));
        Assert.Equal(new[] { "Highlights", "Two Columns" }, doc.Descendants(X + "h2").Select(Text));
        Assert.Equal("Steps", Text(doc.Descendants(X + "h3").Single()));

        var paragraphs = doc.Descendants(X + "p").Select(Text).ToList();
        Assert.Contains(paragraphs, p => p.StartsWith("This report describes", StringComparison.Ordinal) && p.EndsWith("regular line spacing.", StringComparison.Ordinal));
        Assert.Contains(paragraphs, p => p.StartsWith("A second paragraph", StringComparison.Ordinal) && p.EndsWith("on the page.", StringComparison.Ordinal));

        var bullets = doc.Descendants(X + "ul").First(u => u.Parent!.Name != X + "li" && u.Parent.Name != X + "nav");
        var items = bullets.Elements(X + "li").ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("First point about revenue growth", Text(items[0]));
        Assert.StartsWith("Second point, which is long enough", Text(items[1]), StringComparison.Ordinal);
        Assert.EndsWith("written out in full here", Text(items[1]), StringComparison.Ordinal);
        var nested = items[2].Elements(X + "ul").Single();
        Assert.Equal("A nested point", Text(nested.Elements(X + "li").Single()));

        var ordered = doc.Descendants(X + "ol").Single();
        Assert.Equal(new[] { "Step number 1", "Step number 2", "Step number 3" }, ordered.Elements(X + "li").Select(Text));
        Assert.Equal("2", doc.Descendants(X + "sup").Single().Value);
        Assert.Equal(3, result.ListCount);
        Assert.Equal(4, result.HeadingCount);
    }

    [Fact]
    public void Reflow_ReadsColumnsInOrder_AndConvertsLinksImagesAndBookmarks()
    {
        var (html, result) = Convert(LayoutDocument());
        var text = MainText(html);
        var leftSecond = text.IndexOf("Leftcolumnsecond", StringComparison.Ordinal);
        var rightFirst = text.IndexOf("Rightcolumnfirst", StringComparison.Ordinal);
        Assert.True(leftSecond > 0 && rightFirst > leftSecond, "the left column is read before the right column");

        var doc = Parse(html);
        var links = doc.Descendants(X + "main").Single().Descendants(X + "a").ToList();
        Assert.Contains(links, a => a.Attribute("href")!.Value == "https://example.com/" && a.Value == "example.com");
        Assert.Contains(links, a => a.Attribute("href")!.Value == "#page-1" && a.Value.Contains("Back to the start", StringComparison.Ordinal));
        Assert.DoesNotContain("javascript", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Warnings, w => w.Contains("unsafe", StringComparison.Ordinal));
        Assert.Equal(2, result.LinkCount);

        var nav = doc.Descendants(X + "nav").Single();
        Assert.Equal(new[] { "#page-1", "#page-2" }, nav.Descendants(X + "a").Select(a => a.Attribute("href")!.Value));
        Assert.NotNull(doc.Descendants().SingleOrDefault(e => e.Attribute("id")?.Value == "page-2"));

        var img = doc.Descendants(X + "img").Single();
        Assert.StartsWith("data:image/png;base64,", img.Attribute("src")!.Value, StringComparison.Ordinal);
        Assert.Equal(1, result.ImageCount);
        Assert.Equal(2, result.PageCount);
    }

    [Fact]
    public void Fixed_PositionsPagesTextImagesAndLinks()
    {
        var pdf = LayoutDocument();
        var (html, result) = Convert(pdf, new PdfToHtmlOptions { Layout = PdfHtmlLayout.Fixed });
        var doc = Parse(html);
        var pages = doc.Descendants(X + "div").Where(d => d.Attribute("class")?.Value == "pdf-page").ToList();
        Assert.Equal(2, pages.Count);
        Assert.Equal("width:612pt;height:792pt", pages[0].Attribute("style")!.Value);
        var title = pages[0].Elements(X + "span").First();
        Assert.Equal("Annual Report", title.Value);
        Assert.Contains("left:72pt;top:", title.Attribute("style")!.Value, StringComparison.Ordinal);
        Assert.Contains("font-size:24pt", title.Attribute("style")!.Value, StringComparison.Ordinal);
        Assert.Contains("font-weight:bold", title.Attribute("style")!.Value, StringComparison.Ordinal);
        Assert.Single(pages[1].Elements(X + "img"));
        var overlays = doc.Descendants(X + "a").Where(a => a.Attribute("class")?.Value == "pdf-link").ToList();
        Assert.Equal(new[] { "https://example.com/", "#page-1" }, overlays.Select(a => a.Attribute("href")!.Value));
        Assert.Equal(2, result.LinkCount);
        Assert.Equal(Strip(Extract(pdf, PdfTextOrder.Content)), MainText(html));
    }

    public static IEnumerable<object[]> TextFixtureNames() => TextFixtures.All().Select(f => new object[] { f.Name });

    [Theory]
    [MemberData(nameof(TextFixtureNames))]
    public void TextEqualsExtraction_EveryFontEncoding(string name)
    {
        var pdf = TextFixtures.All().Single(f => f.Name == name).Pdf;
        Assert.Equal(Strip(Extract(pdf, PdfTextOrder.Layout)), MainText(Convert(pdf).Html));
        Assert.Equal(Strip(Extract(pdf, PdfTextOrder.Content)), MainText(Convert(pdf, new PdfToHtmlOptions { Layout = PdfHtmlLayout.Fixed }).Html));
    }

    public static IEnumerable<object[]> Structures() => Enum.GetValues<XrefMode>().Select(m => new object[] { m });

    [Theory]
    [MemberData(nameof(Structures))]
    public void TextEqualsExtraction_EveryInputStructure(XrefMode mode)
    {
        foreach (var pdf in new[] { new TestPdfBuilder { Mode = mode }.Build(), new TestPdfBuilder { Mode = mode, Rich = true, PageCount = 3 }.Build() })
        {
            var expected = Sorted(Extract(pdf, PdfTextOrder.Content));
            Assert.Equal(expected, Sorted(MainText(Convert(pdf).Html)));
            Assert.Equal(expected, Sorted(MainText(Convert(pdf, new PdfToHtmlOptions { Layout = PdfHtmlLayout.Fixed }).Html)));
        }
    }

    [Fact]
    public void EncryptedInput_NeedsAPassword()
    {
        var pdf = new TestPdfBuilder { Encryption = (6, "user", "owner", true) }.Build();
        Assert.Throws<PdfInvalidPasswordException>(() => Convert(pdf));
        Assert.Throws<PdfInvalidPasswordException>(() => Convert(pdf, new PdfToHtmlOptions { Password = "wrong" }));
        var (html, _) = Convert(pdf, new PdfToHtmlOptions { Password = "user" });
        Assert.Equal(Sorted(Extract(pdf, PdfTextOrder.Content, "user")), Sorted(MainText(html)));
        Assert.Contains("<title>Test document</title>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Images_EveryColourSpace()
    {
        var doc = new LayoutPdf();
        var names = new[]
        {
            doc.AddImage(RawPdf.Stream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 4 /Filter /ASCIIHexDecode", "0F7F>")),
            doc.AddImage(RawPdf.Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 16 /Filter /ASCIIHexDecode", "FFFF80000000>")),
            doc.AddImage(RawPdf.Stream("/Type /XObject /Subtype /Image /Width 4 /Height 1 /ColorSpace [/Indexed /DeviceGray 3 <00557FFF>] /BitsPerComponent 2 /Filter /ASCIIHexDecode", "1B>")),
            doc.AddImage(RawPdf.Stream("/Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceCMYK /BitsPerComponent 8 /Filter [/ASCIIHexDecode /DCTDecode]", "FFD8FFD9>")),
            doc.AddImage(RawPdf.Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace [/Separation /Spot /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [1 0 0 0] /N 1 >>] /BitsPerComponent 8 /Filter /ASCIIHexDecode", "00FF>")),
            doc.AddImage(RawPdf.Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace [/CalRGB << /WhitePoint [0.95 1 1.09] >>] /BitsPerComponent 8 /Filter /ASCIIHexDecode", "102030>")),
        };
        var page = doc.Page().Text(72, 700, 12, "Images");
        for (var i = 0; i < names.Length; i++)
        {
            page.Image(names[i], 72 + (i * 60), 600, 50, 50);
        }

        var (html, result) = Convert(doc.Build());
        var sources = Parse(html).Descendants(X + "img").Select(i => i.Attribute("src")!.Value).ToList();
        Assert.Equal(6, sources.Count);
        Assert.Single(sources, s => s.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal));
        var pngs = sources.Where(s => s.StartsWith("data:image/png;base64,", StringComparison.Ordinal)).Select(s => ImageCodecs.DecodePng(System.Convert.FromBase64String(s.Split(',')[1]), 100)).ToList();
        Assert.Equal(5, pngs.Count);
        Assert.Equal(new byte[] { 0, 255, 119, 255 }, pngs[0].Samples);
        Assert.Equal(new byte[] { 255, 128, 0 }, pngs[1].Samples);
        Assert.Equal(new byte[] { 0, 0x55, 0x7F, 0xFF }, pngs[2].Samples.Select(i => pngs[2].Palette is null ? i : pngs[2].Palette![i * 3]).ToArray());
        Assert.Equal(new byte[] { 255, 0 }, pngs[3].Samples);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30 }, pngs[4].Samples);
        Assert.Equal(0, result.PlaceholderCount);
        Assert.Contains(result.Warnings, w => w.Contains("spot colour", StringComparison.Ordinal));
        Assert.Equal(6, result.ImageCount);
    }

    private static byte[] Png(string html) => System.Convert.FromBase64String(Parse(html).Descendants(X + "img").Single().Attribute("src")!.Value.Split(',')[1]);

    private static byte[] SingleImage(string imageObject, double width = 40, double height = 20)
    {
        var doc = new LayoutPdf();
        var name = doc.AddImage(imageObject);
        doc.Page().Text(72, 700, 12, "Image below").Image(name, 72, 600, width, height);
        return doc.Build();
    }

    [Fact]
    public void Images_PixelsRoundTrip()
    {
        // Gray, 8 bits.
        var gray = Convert(SingleImage(RawPdf.Stream("/Type /XObject /Subtype /Image /Width 3 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /ASCIIHexDecode", "00327D96C8FA>"))).Html;
        var decoded = ImageCodecs.DecodePng(Png(gray), 100);
        Assert.Equal(new byte[] { 0, 50, 125, 150, 200, 250 }, decoded.Samples);

        // Indexed, 1 bit, alternating palette entries; inverted gray with /Decode [1 0].
        var indexed = ImageCodecs.DecodePng(Png(Convert(SingleImage(RawPdf.Stream("/Type /XObject /Subtype /Image /Width 8 /Height 1 /ColorSpace [/Indexed /DeviceRGB 1 <FF00000000FF>] /BitsPerComponent 1 /Filter /ASCIIHexDecode", "AA>"))).Html), 100);
        var rgb = Enumerable.Range(0, 8).Select(i => indexed.Palette is null ? (indexed.Samples[i * 3], indexed.Samples[(i * 3) + 2]) : (indexed.Palette[indexed.Samples[i] * 3], indexed.Palette[(indexed.Samples[i] * 3) + 2])).ToList();
        Assert.Equal((0, 255), ((int)rgb[0].Item1, (int)rgb[0].Item2)); // bit 1: entry 1, blue
        Assert.Equal((255, 0), ((int)rgb[1].Item1, (int)rgb[1].Item2)); // bit 0: entry 0, red
        var inverted = ImageCodecs.DecodePng(Png(Convert(SingleImage(RawPdf.Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Decode [1 0] /Filter /ASCIIHexDecode", "00FF>"))).Html), 100);
        Assert.Equal(new byte[] { 255, 0 }, inverted.Samples);

        // CMYK becomes RGB; a stencil mask is painted in the fill colour with transparency.
        var cmyk = ImageCodecs.DecodePng(Png(Convert(SingleImage(RawPdf.Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceCMYK /BitsPerComponent 8 /Filter /ASCIIHexDecode", "00FFFF00>"))).Html), 100);
        Assert.Equal(new byte[] { 255, 0, 0 }, cmyk.Samples);
        var stencilDoc = new LayoutPdf();
        var stencil = stencilDoc.AddImage(RawPdf.Stream("/Type /XObject /Subtype /Image /Width 8 /Height 1 /ImageMask true /Filter /ASCIIHexDecode", "0F>"));
        stencilDoc.Page().Text(72, 700, 12, "Mask").Raw($"q 0 0 1 rg 80 0 0 10 72 600 cm /{stencil} Do Q");
        var mask = ImageCodecs.DecodePng(Png(Convert(stencilDoc.Build()).Html), 100);
        Assert.Equal(new byte[] { 0, 0, 255 }, mask.Samples.AsSpan(0, 3).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255, 0, 0, 0, 0 }, mask.Alpha);
    }

    [Fact]
    public void Images_SoftMaskBecomesAlpha()
    {
        var doc = new LayoutPdf();
        var smask = doc.AddImage(RawPdf.Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /ASCIIHexDecode", "00FF>"));
        // The soft mask is image 1 (object 9); the visible image refers to it.
        var visible = doc.AddImage(RawPdf.Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 9 0 R /Filter /ASCIIHexDecode", "FF000000FF00>"));
        doc.Page().Text(72, 700, 12, "Alpha").Image(visible, 72, 600, 40, 20);
        var png = ImageCodecs.DecodePng(Png(Convert(doc.Build()).Html), 100);
        Assert.Equal(new byte[] { 0, 255 }, png.Alpha);
        Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0 }, png.Samples);
    }

    [Fact]
    public void Images_UnsupportedCodecs_BecomePlaceholders()
    {
        var pdf = SingleImage(RawPdf.Stream("/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /JPXDecode", "not really jpx"));
        var (html, result) = Convert(pdf);
        Assert.Equal(1, result.PlaceholderCount);
        Assert.Contains(result.Warnings, w => w.Contains("JPX", StringComparison.Ordinal));
        Assert.Contains("pdf-missing-image", html, StringComparison.Ordinal);
        Assert.Empty(Parse(html).Descendants(X + "img"));
        var (fixedHtml, _) = Convert(pdf, new PdfToHtmlOptions { Layout = PdfHtmlLayout.Fixed });
        Assert.Contains("pdf-missing-image", fixedHtml, StringComparison.Ordinal);

        var tooLarge = Convert(SingleImage(RawPdf.Stream("/Type /XObject /Subtype /Image /Width 3 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /ASCIIHexDecode", "00327D96C8FA>")), converter: new PdfConverter(null, new PdfConversionLimits { MaxImagePixels = 4 }));
        Assert.Equal(1, tooLarge.Result.PlaceholderCount);
        Assert.Contains(tooLarge.Result.Warnings, w => w.Contains("maximum", StringComparison.Ordinal));

        var none = Convert(LayoutDocument(), new PdfToHtmlOptions { Images = PdfHtmlImageMode.None });
        Assert.Empty(Parse(none.Html).Descendants(X + "img"));
        Assert.Equal(0, none.Result.ImageCount);
    }

    [Fact]
    public void Xhtml_ParsesAsXml_AndTextIsEscaped()
    {
        var doc = new LayoutPdf { Title = "<b>Title</b> & \"more\"" };
        doc.Page().Text(72, 700, 12, "<script>alert(1)</script> & \"quoted\" > done");
        var pdf = doc.Build();
        var (html, _) = Convert(pdf, new PdfToHtmlOptions { Xhtml = true });
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", html, StringComparison.Ordinal);
        var parsed = Parse(html);
        Assert.Equal("en", parsed.Root!.Attribute("lang")?.Value ?? "en");
        Assert.Empty(parsed.Descendants(X + "script"));
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; \"quoted\" &gt; done", html, StringComparison.Ordinal);
        Assert.Equal("<b>Title</b> & \"more\"", parsed.Descendants(X + "title").Single().Value);

        // Characters XML does not allow are replaced.
        var control = TextFixtures.Page("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 500 >>] /ToUnicode 6 0 R >>",
            "BT /F1 12 Tf 72 700 Td <000100020003> Tj ET", new Dictionary<int, string> { [6] = TextFixtures.ToUnicodeCMap("3 beginbfchar <0001> <0001> <0002> <FFFF> <0003> <0041> endbfchar") });
        foreach (var layout in Enum.GetValues<PdfHtmlLayout>())
        {
            var text = Parse(Convert(control, new PdfToHtmlOptions { Layout = layout, Xhtml = true }).Html).Descendants(X + "main").Single().Value;
            Assert.Contains("��A", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task FileOutput_SeparateAssets()
    {
        var dir = Directory.CreateTempSubdirectory("pdfutil-html-");
        try
        {
            var input = Path.Combine(dir.FullName, "in.pdf");
            File.WriteAllBytes(input, LayoutDocument());
            var output = Path.Combine(dir.FullName, "out.html");
            var result = await Converter.ConvertToHtmlFileAsync(input, output, new PdfToHtmlOptions { Images = PdfHtmlImageMode.SeparateFiles });
            Assert.Equal(new[] { "out_files/image-1.png" }, result.AssetFiles);
            Assert.True(File.Exists(Path.Combine(dir.FullName, "out_files", "image-1.png")));
            var html = File.ReadAllText(output);
            Assert.Contains("src=\"out_files/image-1.png\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("data:image", html, StringComparison.Ordinal);
            ImageCodecs.DecodePng(File.ReadAllBytes(Path.Combine(dir.FullName, "out_files", "image-1.png")), 100);

            // Converting again replaces the folder; a custom folder name is used as given.
            File.WriteAllText(Path.Combine(dir.FullName, "out_files", "stale.txt"), "old");
            await Converter.ConvertToHtmlFileAsync(input, output, new PdfToHtmlOptions { Images = PdfHtmlImageMode.SeparateFiles });
            Assert.False(File.Exists(Path.Combine(dir.FullName, "out_files", "stale.txt")));
            await Converter.ConvertToHtmlFileAsync(input, output, new PdfToHtmlOptions { Images = PdfHtmlImageMode.SeparateFiles, AssetsFolderName = "media" });
            Assert.Contains("src=\"media/image-1.png\"", File.ReadAllText(output), StringComparison.Ordinal);

            // Embedded file output, and a failed conversion leaves the existing output alone.
            await Converter.ConvertToHtmlFileAsync(input, output);
            Assert.Contains("data:image/png", File.ReadAllText(output), StringComparison.Ordinal);
            File.WriteAllBytes(input, Encoding.ASCII.GetBytes("not a pdf"));
            var before = File.ReadAllText(output);
            await Assert.ThrowsAnyAsync<PdfUtilException>(() => Converter.ConvertToHtmlFileAsync(input, output));
            Assert.Equal(before, File.ReadAllText(output));
            Assert.Equal(new[] { "in.pdf", "media", "out.html", "out_files" }, dir.EnumerateFileSystemInfos().Select(f => f.Name).Order(StringComparer.Ordinal));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task InvalidOptions_LimitsAndFailures()
    {
        var pdf = LayoutDocument();
        await Assert.ThrowsAsync<ArgumentException>(() => Converter.ConvertToHtmlAsync(new MemoryStream(pdf), new MemoryStream(), new PdfToHtmlOptions { Images = PdfHtmlImageMode.SeparateFiles }));
        await Assert.ThrowsAsync<ArgumentException>(() => Converter.ConvertToHtmlFileAsync("in.pdf", "out.html", new PdfToHtmlOptions { Images = PdfHtmlImageMode.SeparateFiles, AssetsFolderName = "../up" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Converter.ConvertToHtmlAsync(new MemoryStream(pdf), new MemoryStream(), new PdfToHtmlOptions { Layout = (PdfHtmlLayout)7 }));
        Assert.Throws<ArgumentException>(() => new PdfConverter(null, new PdfConversionLimits { MaxOutputElements = 0 }));

        var output = new MemoryStream();
        await Assert.ThrowsAsync<PdfLimitExceededException>(() => new PdfConverter(null, new PdfConversionLimits { MaxOutputElements = 20 }).ConvertToHtmlAsync(new MemoryStream(pdf), output));
        Assert.Equal(0, output.Length);
        await Assert.ThrowsAnyAsync<PdfCorruptException>(() => Converter.ConvertToHtmlAsync(new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7 garbage")), output));
        Assert.Equal(0, output.Length);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Converter.ConvertToHtmlAsync(new MemoryStream(pdf), output, ct: cts.Token));

        var stages = new List<PdfProgress>();
        Convert(pdf, new PdfToHtmlOptions { Progress = new RepairTests.SyncProgress(stages.Add), Pages = PdfPageRange.Parse("2") });
        Assert.Equal(new PdfProgress(PdfProgressStage.Reading, 1, 1), stages[^1]);
    }

    [Fact]
    public void PageSelection_AndSeparators()
    {
        var (html, result) = Convert(LayoutDocument(), new PdfToHtmlOptions { Pages = PdfPageRange.Parse("2") });
        Assert.Equal(1, result.PageCount);
        Assert.DoesNotContain("Annual Report", MainText(html), StringComparison.Ordinal);

        // The link back to page 1 is dropped (its target was not converted); the bookmark to page 1 too.
        var doc = Parse(html);
        Assert.DoesNotContain(doc.Descendants(X + "a"), a => a.Attribute("href")?.Value == "#page-1");
        var (separated, _) = Convert(LayoutDocument(), new PdfToHtmlOptions { PageSeparators = true, IncludeNavigation = false });
        Assert.Single(Parse(separated).Descendants(X + "hr"));
        Assert.Empty(Parse(separated).Descendants(X + "nav"));
    }

    [Fact]
    public void Reflow_ParagraphsAcrossPagesAndShortLines()
    {
        var doc = new LayoutPdf();
        doc.Page().Text(72, 700, 11, "Copyright 2024 Example Inc.").Text(72, 687, 11, "Permission is granted to use this text without limits of any kind and").Text(72, 100, 11, "this paragraph goes on at the bottom of the page and continues on the")
            .Done().Page().Text(72, 720, 11, "next page, where it ends.");
        var (html, _) = Convert(doc.Build());
        var paragraphs = Parse(html).Descendants(X + "p").ToList();
        Assert.Equal("Copyright 2024 Example Inc.", paragraphs[0].Value);
        Assert.StartsWith("Permission is granted", paragraphs[1].Value, StringComparison.Ordinal);
        var joined = paragraphs.Single(p => p.Value.Contains("continues on the next page, where it ends.", StringComparison.Ordinal));
        Assert.NotNull(joined.Descendants(X + "span").SingleOrDefault(e => e.Attribute("id")?.Value == "page-2"));
    }

    [Fact]
    public void RotatedPage_UsesDisplayedOrientation()
    {
        var doc = new LayoutPdf();
        doc.Page(rotate: 90).Text(72, 500, 14, "Rotated page text");
        var (html, _) = Convert(doc.Build(), new PdfToHtmlOptions { Layout = PdfHtmlLayout.Fixed });
        var page = Parse(html).Descendants(X + "div").Single(d => d.Attribute("class")?.Value == "pdf-page");
        Assert.Equal("width:792pt;height:612pt", page.Attribute("style")!.Value);
        Assert.Contains("transform:rotate(", page.Elements(X + "span").Single().Attribute("style")!.Value, StringComparison.Ordinal);
        Assert.Contains("Rotatedpagetext", MainText(Convert(doc.Build()).Html), StringComparison.Ordinal);
    }

    [Fact]
    public void Di_RegistersTheConverter()
    {
        var services = new ServiceCollection().AddPdfUtilConversion(limits => limits.MaxImagePixels = 10).BuildServiceProvider();
        var converter = services.GetRequiredService<IPdfConverter>();
        Assert.Same(converter, services.GetRequiredService<IPdfConverter>());
        Assert.NotNull(services.GetRequiredService<IPdfAnalyzer>());
    }

    /// <summary>Renders the HTML in WebKit (macOS) and checks that something was drawn.</summary>
    [RequiresToolFact("swift")]
    public void WebKit_RendersOutput()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("pdfutil-webkit-");
        try
        {
            var files = new List<string>();
            foreach (var layout in Enum.GetValues<PdfHtmlLayout>())
            {
                var path = Path.Combine(dir.FullName, layout + ".html");
                File.WriteAllText(path, Convert(LayoutDocument(), new PdfToHtmlOptions { Layout = layout }).Html);
                files.Add(path);
            }

            var script = Path.Combine(dir.FullName, "render.swift");
            File.WriteAllText(script, WebKitScript);
            var (code, output) = TestHelpers.Run(TestHelpers.FindTool("swift")!, new[] { script }.Concat(files).ToArray());
            Assert.True(code == 0, "WebKit failures:\n" + output);
            Assert.Contains($"OK {files.Count}", output, StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    internal const string WebKitScript = """
        import AppKit
        import WebKit
        let files = Array(CommandLine.arguments.dropFirst())
        let app = NSApplication.shared
        var failures: [String] = []
        var index = 0
        class Loader: NSObject, WKNavigationDelegate {
            let view = WKWebView(frame: NSRect(x: 0, y: 0, width: 900, height: 1200))
            override init() { super.init(); view.navigationDelegate = self }
            func next() {
                if index >= files.count {
                    print(failures.isEmpty ? "OK \(files.count)" : failures.joined(separator: "\n"))
                    exit(failures.isEmpty ? 0 : 1)
                }
                let url = URL(fileURLWithPath: files[index])
                view.loadFileURL(url, allowingReadAccessTo: url.deletingLastPathComponent())
            }
            func webView(_ w: WKWebView, didFinish n: WKNavigation!) {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                    w.evaluateJavaScript("document.body.innerText.length") { r, _ in
                        let length = (r as? Int) ?? 0
                        w.takeSnapshot(with: nil) { img, _ in
                            var dark = 0
                            if let img = img, let rep = NSBitmapImageRep(data: img.tiffRepresentation!) {
                                for y in stride(from: 0, to: rep.pixelsHigh, by: 4) {
                                    for x in stride(from: 0, to: rep.pixelsWide, by: 4) {
                                        if let c = rep.colorAt(x: x, y: y), c.brightnessComponent < 0.5 { dark += 1 }
                                    }
                                }
                            }
                            if length < 100 || dark < 50 { failures.append("\(files[index]): text \(length), dark pixels \(dark)") }
                            index += 1
                            self.next()
                        }
                    }
                }
            }
            func webView(_ w: WKWebView, didFail n: WKNavigation!, withError e: Error) { failures.append("\(files[index]): \(e)"); index += 1; next() }
        }
        let loader = Loader()
        loader.next()
        app.run()
        """;
}
