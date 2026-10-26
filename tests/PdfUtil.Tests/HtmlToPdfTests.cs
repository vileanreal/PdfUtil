using System.Text;
using PdfUtil.Conversion;
using PdfUtil.Conversion.Internal;
using PdfUtil.Conversion.Internal.Css;
using PdfUtil.Conversion.Internal.Html;
using PdfUtil.Internal.Fonts;
using PdfUtil.Internal.Images;
using PdfUtil.Internal.Objects;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>F21: HTML to PDF, and the HTML parser, CSS cascade, fonts and layout behind it.</summary>
public class HtmlToPdfTests
{
    private static readonly PdfConverter Converter = new();

    internal static (byte[] Pdf, HtmlToPdfResult Result) Convert(string html, HtmlToPdfOptions? options = null, PdfConverter? converter = null)
    {
        using var output = new MemoryStream();
        var result = (converter ?? Converter).ConvertFromHtmlStringAsync(html, output, options).GetAwaiter().GetResult();
        return (output.ToArray(), result);
    }

    internal static PdfTextExtractionResult Extract(byte[] pdf, string? password = null) =>
        new PdfTextExtractor().ExtractAsync(new MemoryStream(pdf), new PdfTextExtractionOptions { Password = password }).GetAwaiter().GetResult();

    private static string Strip(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    internal static HtmlToPdfOptions WithTestFont(HtmlToPdfOptions? options = null)
    {
        options ??= new HtmlToPdfOptions();
        options.Fonts.Add(new PdfFontSource(TestFont.Build()));
        return options;
    }

    /// <summary>The corpus documents for pdf.js and PDFKit: two pages, "Hello page 1" on the first.</summary>
    internal static IEnumerable<(string Name, byte[] Pdf)> InteropSamples()
    {
        var png = ImageCodecs.EncodePng(2, 2, 4, new byte[] { 255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 255, 255, 255, 0, 0 });
        var html = $"""
            <html lang="en"><head><title>Test document</title><style>td {"{"} border: 1px solid #888; padding: 3pt {"}"} .u {"{"} font-family: 'Test Sans' {"}"}</style></head><body>
            <h1>Hello page 1</h1><p>Plain <b>bold</b> <i>italic</i> <a href="https://example.com/">link</a> <a href="#end">jump</a>.</p>
            <p class="u">Привет 日本語 😀</p>
            <ul><li>One</li><li>Two</li></ul>
            <table><tr><th>A</th><th>B</th></tr><tr><td colspan="2">Spanning</td></tr></table>
            <p><img src="data:image/png;base64,{System.Convert.ToBase64String(png)}" width="40" height="40" alt="dots"></p>
            <h2 id="end" style="page-break-before: always">Second page</h2>
            </body></html>
            """;
        yield return ("html-basic", Convert(html, WithTestFont()).Pdf);
        yield return ("html-footer", Convert(html, WithTestFont(new HtmlToPdfOptions { PageSize = PdfPageSize.Letter.Landscape, FooterHtml = "<p style='text-align:center'>{page} / {pages}</p>" })).Pdf);
    }

    // ------------------------------------------------------------------ parser

    [Fact]
    public void Parser_ImpliesStructureAndRecovers()
    {
        var dom = HtmlParser.Parse("<title>T</title><p>One<p>Two<ul><li>A<li>B</ul><table><tr><td>1<td>2</table>text<b><i>x</b>y</i>", 1000, 64);
        Assert.Equal("T", dom.Head.Descendants().Single(e => e.Name == "title").TextContent);
        var body = dom.Body;
        Assert.Equal(new[] { "p", "p", "ul", "table" }, body.Children.OfType<Element>().Take(4).Select(e => e.Name));
        Assert.Equal(new[] { "One", "Two" }, body.Children.OfType<Element>().Where(e => e.Name == "p").Select(e => e.TextContent));
        Assert.Equal(new[] { "A", "B" }, body.Descendants().Where(e => e.Name == "li").Select(e => e.TextContent));
        var table = body.Descendants().Single(e => e.Name == "table");
        Assert.Equal("tbody", table.Children.OfType<Element>().Single().Name);
        Assert.Equal(new[] { "1", "2" }, table.Descendants().Where(e => e.Name == "td").Select(e => e.TextContent));

        // Misnested formatting: y is still italic after </b>.
        var italics = body.Descendants().Where(e => e.Name == "i").ToList();
        Assert.Equal("xy", string.Concat(italics.Select(i => i.TextContent)));

        // Stray text in a table goes before it.
        var fostered = HtmlParser.Parse("<table>oops<tr><td>cell</td></tr></table>", 1000, 64).Body;
        Assert.Equal("oops", ((TextNode)fostered.Children[0]).Text);
    }

    [Fact]
    public void Parser_CharacterReferencesAndRawText()
    {
        Assert.Equal("a & b © ∀ ≠ 😀 € � x", HtmlParser.DecodeText("a &amp; b &copy &forall; &ne; &#x1F600; &#128; &#0; x", attribute: false));
        Assert.Equal("&copy=1", HtmlParser.DecodeText("&copy=1", attribute: true));
        Assert.Equal("©x", HtmlParser.DecodeText("&copyx", attribute: false));
        Assert.Equal("&nosuch;", HtmlParser.DecodeText("&nosuch;", attribute: false));
        var dom = HtmlParser.Parse("<p title='a&amp;b'>x</p><script>if (a < b) { document.write('<p>no</p>') }</script><!-- <p>no</p> --><style>p { color: red }</style>", 1000, 64);
        Assert.Single(dom.Body.Descendants(), e => e.Name == "p");
        Assert.Equal("a&b", dom.Body.Descendants().Single(e => e.Name == "p").Attr("title"));
        Assert.Contains("a < b", dom.Root.Descendants().Single(e => e.Name == "script").TextContent, StringComparison.Ordinal);
        Assert.Equal("Grüße – “x”", HtmlParser.Decode(Encoding.Latin1.GetBytes("<meta charset=windows-1252>Grüße \u0096 \u0093x\u0094")).Split('>')[1]);
        Assert.Equal("é", HtmlParser.Decode(new byte[] { 0xEF, 0xBB, 0xBF, 0xC3, 0xA9 }));
    }

    [Fact]
    public void Parser_Limits()
    {
        Assert.Throws<PdfLimitExceededException>(() => HtmlParser.Parse(string.Concat(Enumerable.Repeat("<div>", 300)), 100_000, 256));
        Assert.Throws<PdfLimitExceededException>(() => HtmlParser.Parse(string.Concat(Enumerable.Repeat("<p>x</p>", 2000)), 1000, 256));
        Assert.Throws<PdfLimitExceededException>(() => Convert(string.Concat(Enumerable.Repeat("<span>", 300)), converter: new PdfConverter(null, new PdfConversionLimits { MaxNestingDepth = 100 })));
        Assert.Throws<PdfLimitExceededException>(() => Convert(new string('x', 2000), converter: new PdfConverter(null, new PdfConversionLimits { MaxInputBytes = 1000 })));
    }

    // ------------------------------------------------------------------ CSS

    private static Cascade Styles(string html, out HtmlDom dom, List<string>? warnings = null)
    {
        dom = HtmlParser.Parse(html, 1000, 64);
        var order = 0;
        var sheets = new List<StyleSheet> { CssParser.Parse(HtmlToPdfEngine.UserAgentCss, 0, ref order, _ => { }) };
        foreach (var style in dom.Root.Descendants().Where(e => e.Name == "style"))
        {
            sheets.Add(CssParser.Parse(style.TextContent, 1, ref order, w => warnings?.Add(w)));
        }

        var cascade = new Cascade(sheets, w => warnings?.Add(w), 12);
        cascade.Compute(dom.Root, new ComputedStyle { Display = "block" });
        return cascade;
    }

    [Fact]
    public void Css_CascadeSpecificityAndInheritance()
    {
        var warnings = new List<string>();
        var styles = Styles("""
            <style>
            p { color: red; font-size: 10pt }
            .note { color: green }
            #main p { color: blue }
            div > p.note { margin: 1em 2em }
            p { color: purple !important }
            em { font-weight: bold }
            .x::before, a:hover { color: red }
            p { transform: rotate(1deg); transition: all 1s }
            </style>
            <div id="main"><p class="note" style="font-size: 2em; padding: 3px 4px">Text <em>here</em></p></div>
            <p style="color: orange; border: 2px solid #0f0">Other</p>
            """, out var dom, warnings);
        var p = dom.Body.Descendants().First(e => e.Name == "p");
        var s = styles[p];
        Assert.Equal("#800080", s.Color);
        Assert.Equal(24, s.FontSize, 3);
        Assert.Equal(24, s.MarginTop.Value, 3);
        Assert.Equal(48, s.MarginLeft.Value, 3);
        Assert.Equal(2.25, s.PaddingTop.Value, 3);
        Assert.Equal(3, s.PaddingRight.Value, 3);
        var em = styles[dom.Body.Descendants().First(e => e.Name == "em")];
        Assert.True(em.Bold);
        Assert.True(em.Italic);
        Assert.Equal(24, em.FontSize, 3);
        Assert.Equal("#800080", em.Color);
        var other = styles[dom.Body.Descendants().Last(e => e.Name == "p")];
        Assert.Equal("#800080", other.Color);
        Assert.Equal(new Conversion.Internal.Model.BorderSide(1.5, "#00FF00"), other.BorderTop);
        warnings = warnings.Distinct().ToList();
        Assert.Single(warnings, w => w.Contains("'transform'", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("pseudo-elements", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains(":hover", StringComparison.Ordinal));
        Assert.Equal(2, warnings.Count(w => w.Contains("'transform'", StringComparison.Ordinal) || w.Contains("'transition'", StringComparison.Ordinal)));
    }

    [Fact]
    public void Css_ValuesAndShorthands()
    {
        Assert.Equal(72, CssValues.ParseLength("1in", 12, 12)!.Value.Value, 3);
        Assert.Equal(28.346, CssValues.ParseLength("1cm", 12, 12)!.Value.Value, 2);
        Assert.Equal(7.5, CssValues.ParseLength("10px", 12, 12)!.Value.Value, 3);
        Assert.Equal(18, CssValues.ParseLength("1.5em", 12, 12)!.Value.Value, 3);
        Assert.True(CssValues.ParseLength("50%", 12, 12)!.Value.IsPercent);
        Assert.Null(CssValues.ParseLength("calc(1px)", 12, 12));
        foreach (var (input, expected) in new[] { ("#abc", "#AABBCC"), ("rgb(255, 0, 0)", "#FF0000"), ("rgba(0,0,0,0.5)", "#808080"), ("navy", "#000080"), ("#11223344", "#112233") })
        {
            Assert.True(CssValues.TryParseColor(input, "#000000", out var color));
            Assert.Equal(expected, color);
        }

        Assert.True(CssValues.TryParseColor("transparent", "#000000", out var none) && none is null);
        Assert.False(CssValues.TryParseColor("notacolor", "#000000", out _));
        var styles = Styles("<style>p { font: italic bold 15px/2 Georgia, serif; border-bottom: thick dashed blue; list-style: square inside; background: #eee url(x.png) }</style><p>x</p>", out var dom);
        var s = styles[dom.Body.Descendants().Single(e => e.Name == "p")];
        Assert.True(s.Bold && s.Italic);
        Assert.Equal(11.25, s.FontSize, 3);
        Assert.Equal(2, s.LineHeight);
        Assert.Equal(new[] { "Georgia", "serif" }, s.FontFamily);
        Assert.Equal(3.75, s.BorderBottom.Width);
        Assert.Equal("#0000FF", s.BorderBottom.Color);
        Assert.Equal(0, s.BorderTop.Width);
        Assert.Equal("#EEEEEE", s.Background);
    }

    // ------------------------------------------------------------------ conversion

    [Fact]
    public void Text_RoundTripsThroughExtraction()
    {
        var html = "<h1>Title</h1><p>First paragraph with <b>bold</b>, <i>italic</i> and <code>code</code>.</p><ul><li>Apple</li><li>Pear</li></ul><ol start=3><li>Third</li></ol><table><tr><td>Cell A</td><td>Cell B</td></tr></table><pre>  pre  text</pre>";
        var (pdf, result) = Convert(html);
        Assert.Equal(1, result.PageCount);
        var text = Extract(pdf).Text;
        Assert.Equal(Strip("Title First paragraph with bold, italic and code. •Apple •Pear 3.Third Cell A Cell B pre text"), Strip(text));
        Assert.Empty(result.EmbeddedFonts);
        Assert.Equal(0, result.MissingCharacters);
    }

    [Fact]
    public void Layout_PositionsAndSizes()
    {
        var options = new HtmlToPdfOptions { PageSize = PdfPageSize.Letter, Margins = PdfMargins.All(72) };
        var (pdf, _) = Convert("<h1 style='margin-top:0'>Heading</h1><p style='margin:0'>Body text</p><p style='text-align:right;margin:0'>Right</p><p style='text-align:center;margin:0;padding-left:0'>Middle</p>", options);
        var runs = Extract(pdf).Pages[0].Runs;
        var heading = runs.Single(r => r.Text == "Heading");
        Assert.Equal(24, heading.FontSize, 1);
        Assert.True(heading.IsBold);
        Assert.Equal(72, heading.X, 1);
        var body = runs.Single(r => r.Text == "Body text");
        Assert.Equal(12, body.FontSize, 1);
        Assert.Equal(72, body.X, 1);
        Assert.True(body.Y < heading.Y - 20);
        var right = runs.Single(r => r.Text == "Right");
        Assert.Equal(612 - 72, right.X + right.Width, 0);
        var middle = runs.Single(r => r.Text == "Middle");
        Assert.Equal(306, middle.X + (middle.Width / 2), 0);
    }

    [Fact]
    public void Pagination_BreaksWidowsAndRepeatedHeaders()
    {
        var paragraphs = string.Concat(Enumerable.Range(1, 80).Select(i => $"<p>Paragraph {i} with some words in it.</p>"));
        var (pdf, result) = Convert(paragraphs);
        Assert.True(result.PageCount >= 3);
        var pages = Extract(pdf).Pages;
        Assert.All(Enumerable.Range(1, 80), i => Assert.Single(pages, p => p.Text.Contains($"Paragraph {i} ", StringComparison.Ordinal)));

        // A forced break, and an avoided one.
        var (forced, forcedResult) = Convert("<p>One</p><p style='break-before: page'>Two</p><p style='page-break-after: always'>Three</p><p>Four</p>");
        Assert.Equal(3, forcedResult.PageCount);
        Assert.Equal(new[] { "One", "Two Three", "Four" }, Extract(forced).Pages.Select(p => p.Text.Replace("\n", " ", StringComparison.Ordinal)));

        // Widows and orphans: a paragraph never leaves a single line at the bottom or the top of a page.
        var lines = string.Concat(Enumerable.Range(1, 12).Select(i => $"Line {i:00}<br>"));
        var filler = "<div style='height:1px'></div>" + string.Concat(Enumerable.Range(1, 40).Select(i => $"<p style='margin:0'>Filler {i}</p>"));
        var (widow, _) = Convert(filler + "<p style='margin:0'>" + lines + "</p>", new HtmlToPdfOptions { PageSize = PdfPageSize.A5 });
        foreach (var page in Extract(widow).Pages)
        {
            var count = page.Text.Split('\n').Count(l => l.StartsWith("Line", StringComparison.Ordinal));
            Assert.True(count is 0 or >= 2, $"a page has {count} line(s) of the paragraph");
        }

        // A long table repeats its header row on every page, and keeps a heading with what follows.
        var rows = string.Concat(Enumerable.Range(1, 120).Select(i => $"<tr><td>Row {i}</td><td>{i * 3}</td></tr>"));
        var (table, tableResult) = Convert($"<h2>Data</h2><table><thead><tr><th>Name</th><th>Value</th></tr></thead><tbody>{rows}</tbody></table>");
        Assert.True(tableResult.PageCount >= 2);
        var tablePages = Extract(table).Pages;
        Assert.All(tablePages, p => Assert.StartsWith(p.Number == 1 ? "Data" : "Name", p.Text, StringComparison.Ordinal));
        Assert.All(tablePages, p => Assert.Contains("NameValue", Strip(p.Text), StringComparison.Ordinal));
    }

    [Fact]
    public void Tables_WidthsSpansAndBorders()
    {
        var (pdf, _) = Convert("""
            <table style="width: 300pt; border-collapse: collapse; table-layout: fixed">
            <col width="100"><col>
            <tr><td style="border: 1px solid black">Narrow</td><td rowspan="2" style="border: 1px solid black; vertical-align: bottom">Tall</td></tr>
            <tr><td>Second</td></tr>
            <tr><td colspan="2" style="text-align: center">Across both</td></tr>
            </table>
            """, new HtmlToPdfOptions { Margins = PdfMargins.All(50) });
        var runs = Extract(pdf).Pages[0].Runs;
        var narrow = runs.Single(r => r.Text == "Narrow");
        var tall = runs.Single(r => r.Text == "Tall");
        var second = runs.Single(r => r.Text == "Second");
        var across = runs.Single(r => r.Text == "Across both");
        // Text starts after the cell's border and padding (0.75 pt each); the first column is 100 px = 75 pt.
        Assert.Equal(51.5, narrow.X, 1);
        Assert.Equal(50 + 75 + 1.5, tall.X, 1);
        Assert.True(Math.Abs(tall.Y - second.Y) < 3, "the row-spanning cell is aligned to the bottom");
        Assert.Equal(50 + 150, across.X + (across.Width / 2), 0);
    }

    [Fact]
    public void Fonts_EmbeddedSubsetWithToUnicode()
    {
        var html = "<p style=\"font-family: 'Test Sans'\">Hello Привет Ελλάδα 日本語 😀 Å</p><p>Standard WinAnsi text</p>";
        var (pdf, result) = Convert(html, WithTestFont());
        Assert.Equal(new[] { "Test Sans" }, result.EmbeddedFonts);
        Assert.Equal(Strip("Hello Привет Ελλάδα 日本語 😀 Å Standard WinAnsi text"), Strip(Extract(pdf).Text));

        // The font program is a subset: only the used glyphs (and the composite's components) are kept.
        using var doc = TestHelpers.Open(pdf, strict: true);
        var fontNumber = doc.Xref.Keys.Single(n => doc.GetObject(n) is PdfStream { } st && st.Dictionary["Length1"] is not null);
        var program = doc.GetDecodedStreamData((PdfStream)doc.GetObject(fontNumber)!, new ObjectId(fontNumber, 0))!;
        var subset = TrueTypeFont.Load(program).Single();
        var original = TrueTypeFont.Load(TestFont.Build()).Single();
        Assert.True(subset.GlyphCount < 40 && subset.GlyphCount < original.GlyphCount / 4, $"subset has {subset.GlyphCount} glyphs");
        Assert.True(TrueTypeFont.Components(subset.GlyphData(subset.GlyphFor('Å'))).Count == 2);
        Assert.All(TrueTypeFont.Components(subset.GlyphData(subset.GlyphFor('Å'))), c => Assert.True(c.Glyph < subset.GlyphCount && subset.GlyphData(c.Glyph).Length > 0));

        // Bold falls back to the regular face with a warning when there is no bold face.
        var (_, boldResult) = Convert("<p style=\"font-family: 'Test Sans'; font-weight: bold\">Жирный</p>", WithTestFont());
        Assert.Contains(boldResult.Warnings, w => w.Contains("no bold", StringComparison.Ordinal));
        var (boldPdf, _) = Convert("<p style=\"font-family: 'Test Sans'\"><b>Жирный</b> <i>Курсив</i></p>", WithTestFont());
        using var boldDoc = TestHelpers.Open(boldPdf, strict: true);
        var content = System.Text.Encoding.Latin1.GetString(boldDoc.GetFullyDecodedStreamData((PdfStream)boldDoc.GetObject(boldDoc.Xref.Keys.First(n => boldDoc.GetObject(n) is PdfStream s && s.Dictionary["Length1"] is null && s.Dictionary["Subtype"] is null && s.Dictionary["Type"] is null))!, new ObjectId(boldDoc.Xref.Keys.First(n => boldDoc.GetObject(n) is PdfStream s && s.Dictionary["Length1"] is null && s.Dictionary["Subtype"] is null && s.Dictionary["Type"] is null), 0))!);
        Assert.Contains(" 2 Tr ", content, StringComparison.Ordinal);
        Assert.Contains("1 0 0.2 1 ", content, StringComparison.Ordinal);
        Assert.Equal("Жирный Курсив", Extract(boldPdf).Text.Trim());
    }

    [Fact]
    public void Fonts_MissingRestrictedAndBroken()
    {
        var (pdf, result) = Convert("<p>Привет</p>");
        Assert.True(result.MissingCharacters > 0);
        Assert.Contains(result.Warnings, w => w.Contains("U+041F", StringComparison.Ordinal));
        Assert.Single(Extract(pdf).Pages);

        var options = new HtmlToPdfOptions();
        options.Fonts.Add(new PdfFontSource(TestFont.Build("Locked", restricted: true)));
        options.Fonts.Add(new PdfFontSource(Encoding.ASCII.GetBytes("not a font at all")));
        options.Fonts.Add(new PdfFontSource(Encoding.ASCII.GetBytes("OTTO" + new string('\0', 40))));
        var (_, restricted) = Convert("<p style=\"font-family: Locked\">Привет</p>", options);
        Assert.Contains(restricted.Warnings, w => w.Contains("does not allow embedding", StringComparison.Ordinal));
        Assert.Contains(restricted.Warnings, w => w.Contains("damaged", StringComparison.Ordinal));
        Assert.Contains(restricted.Warnings, w => w.Contains("PostScript", StringComparison.Ordinal));
        Assert.Empty(restricted.EmbeddedFonts);

        // A font folder.
        var dir = Directory.CreateTempSubdirectory("pdfutil-fonts-");
        try
        {
            File.WriteAllBytes(Path.Combine(dir.FullName, "folder.ttf"), TestFont.Build("Folder Font"));
            var folderOptions = new HtmlToPdfOptions();
            folderOptions.FontDirectories.Add(dir.FullName);
            var (folderPdf, folderResult) = Convert("<p style=\"font-family: 'Folder Font'\">Привет</p>", folderOptions);
            Assert.Equal(new[] { "Folder Font" }, folderResult.EmbeddedFonts);
            Assert.Equal("Привет", Extract(folderPdf).Text.Trim());
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void LinksAnchorsAndBookmarks()
    {
        var html = "<h1>Intro</h1><p><a href='https://example.com/a?b=1&amp;c=2'>External</a> <a href='#details'>Internal</a> <a href='javascript:alert(1)'>Bad</a></p><h2 id='details' style='break-before: page'>Details</h2><h3>Sub</h3>";
        var (pdf, result) = Convert(html);
        Assert.Equal(2, result.LinkCount);
        Assert.Equal(3, result.BookmarkCount);
        Assert.Contains(result.Warnings, w => w.Contains("unsafe", StringComparison.Ordinal));
        Assert.DoesNotContain("javascript", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);

        // Read the links back with our own PDF to HTML conversion.
        var back = HtmlConversionTests.Parse(HtmlConversionTests.Convert(pdf).Html);
        System.Xml.Linq.XNamespace x = "http://www.w3.org/1999/xhtml";
        var links = back.Descendants(x + "main").Single().Descendants(x + "a").Select(a => (a.Value, a.Attribute("href")!.Value)).ToList();
        Assert.Contains(("External", "https://example.com/a?b=1&c=2"), links);
        Assert.Contains(("Internal", "#page-2"), links);
        var outline = Pdf.Inspect(pdf, new PdfInspectionOptions { Details = PdfInspectionDetails.Bookmarks }).Bookmarks;
        Assert.Equal("Intro (page 1)", outline.Single().ToString());
        Assert.Equal("Details (page 2)", outline[0].Children.Single().ToString());
        Assert.Equal("Sub (page 2)", outline[0].Children[0].Children.Single().ToString());
    }

    [Fact]
    public void Images_PngJpegAndUnsupported()
    {
        var rgba = ImageCodecs.EncodePng(2, 1, 4, new byte[] { 255, 0, 0, 255, 0, 0, 255, 0 });
        var gray = ImageCodecs.EncodePng(3, 1, 1, new byte[] { 0, 128, 255 });
        var html = $"<p><img src='data:image/png;base64,{System.Convert.ToBase64String(rgba)}' style='width: 100pt'> <img src='data:image/png;base64,{System.Convert.ToBase64String(gray)}'></p><p><img src='data:image/gif;base64,R0lGODlhAQABAAAAACw=' alt='gif'></p><p>after</p>";
        var (pdf, result) = Convert(html);
        Assert.Equal(2, result.ImageCount);
        Assert.Contains(result.Warnings, w => w.Contains("GIF", StringComparison.Ordinal));
        var placements = Extract(pdf).Pages[0].Images;
        Assert.Equal(2, placements.Count);
        Assert.Contains(placements, p => Math.Abs(p.Bounds.Width - 100) < 0.01 && Math.Abs(p.Bounds.Height - 50) < 0.01);
        Assert.Contains(placements, p => Math.Abs(p.Bounds.Width - 2.25) < 0.01);
        var info = Pdf.Inspect(pdf, new PdfInspectionOptions { Details = PdfInspectionDetails.Images });
        Assert.Contains(info.Images, i => i.HasSoftMask);

        // Images wider than the page are scaled down.
        var (wide, _) = Convert($"<img src='data:image/png;base64,{System.Convert.ToBase64String(gray)}' width='5000' height='100'>");
        Assert.True(Extract(wide).Pages[0].Images.Single().Bounds.Width <= PdfPageSize.A4.Width - 108 + 0.01);
    }

    [Fact]
    public async Task Resources_OnlyThroughTheResolver()
    {
        var html = "<link rel='stylesheet' href='style.css'><p class='big'>Styled</p><img src='pic.png'>";
        var (_, without) = Convert(html, new HtmlToPdfOptions { BaseUri = new Uri("https://example.com/doc/") });
        Assert.Contains(without.Warnings, w => w.Contains("ResourceResolver", StringComparison.Ordinal));
        Assert.Equal(0, without.ImageCount);

        var requested = new List<Uri>();
        var png = ImageCodecs.EncodePng(1, 1, 3, new byte[] { 1, 2, 3 });
        var options = new HtmlToPdfOptions
        {
            BaseUri = new Uri("https://example.com/doc/"),
            ResourceResolver = (uri, _) =>
            {
                requested.Add(uri);
                return Task.FromResult<byte[]?>(uri.AbsolutePath switch
                {
                    "/doc/style.css" => Encoding.UTF8.GetBytes("@import url(more.css); .big { font-size: 30pt }"),
                    "/doc/more.css" => Encoding.UTF8.GetBytes("p { color: red }"),
                    "/doc/pic.png" => png,
                    _ => null,
                });
            },
        };
        var (pdf, result) = Convert(html, options);
        Assert.Equal(new[] { "https://example.com/doc/style.css", "https://example.com/doc/more.css", "https://example.com/doc/pic.png" }, requested.Select(u => u.ToString()));
        Assert.Equal(1, result.ImageCount);
        var run = Extract(pdf).Pages[0].Runs.Single(r => r.Text == "Styled");
        Assert.Equal(30, run.FontSize, 1);
        Assert.Equal("#FF0000", run.Color);

        // The directory resolver serves files inside its folder only.
        var dir = Directory.CreateTempSubdirectory("pdfutil-res-");
        try
        {
            File.WriteAllBytes(Path.Combine(dir.FullName, "pic.png"), png);
            File.WriteAllText(Path.Combine(dir.FullName, "page.html"), "<p>From a file</p><img src='pic.png'><img src='../secret.png'>");
            var resolver = HtmlResources.FromDirectory(dir.FullName);
            Assert.NotNull(await resolver(new Uri(Path.Combine(dir.FullName, "pic.png")), default));
            Assert.Null(await resolver(new Uri(Path.Combine(dir.FullName, "..", "secret.png")), default));
            Assert.Null(await resolver(new Uri("https://example.com/pic.png"), default));
            var output = Path.Combine(dir.FullName, "page.pdf");
            var fileResult = await Converter.ConvertFromHtmlFileAsync(Path.Combine(dir.FullName, "page.html"), output, new HtmlToPdfOptions { ResourceResolver = resolver });
            Assert.Equal(1, fileResult.ImageCount);
            Assert.Contains("From a file", Extract(File.ReadAllBytes(output)).Text, StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void FontFace_LoadsThroughTheResolver()
    {
        var font = TestFont.Build("Web Font");
        var html = "<style>@font-face { font-family: 'Brand'; src: url(brand.ttf) format('truetype') } p { font-family: Brand }</style><p>Ωμέγα</p>";
        var (pdf, result) = Convert(html, new HtmlToPdfOptions { BaseUri = new Uri("https://fonts.example/"), ResourceResolver = (_, _) => Task.FromResult<byte[]?>(font) });
        Assert.Equal(new[] { "Brand" }, result.EmbeddedFonts);
        Assert.Equal("Ωμέγα", Extract(pdf).Text.Trim());
    }

    [Fact]
    public void PageSetup_HeaderFooterAndProtection()
    {
        var html = "<style>@page { size: A5 landscape; margin: 2cm }</style><p>One</p><p style='break-before:page'>Two</p>";
        var options = new HtmlToPdfOptions
        {
            HeaderHtml = "<div style='font-size:8pt'>Report header</div>",
            FooterHtml = "<p style='text-align:center'>Page <span class='pageNumber'></span> of <span class='totalPages'></span></p>",
            Protection = new PdfProtectionOptions { UserPassword = "user", OwnerPassword = "owner", Algorithm = PdfEncryptionAlgorithm.Aes256 },
            Title = "Custom title",
        };
        var (pdf, result) = Convert(html, options);
        Assert.True(result.IsProtected);
        Assert.Throws<PdfInvalidPasswordException>(() => Extract(pdf));
        var pages = Extract(pdf, "user").Pages;
        Assert.Equal(2, pages.Count);
        Assert.Equal(595.28, pages[0].Width, 1);
        Assert.Equal(419.53, pages[0].Height, 1);
        Assert.Contains("Page 1 of 2", pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("Page 2 of 2", pages[1].Text, StringComparison.Ordinal);
        Assert.All(pages, p => Assert.Contains("Report header", p.Text, StringComparison.Ordinal));
        var one = pages[0].Runs.Single(r => r.Text == "One");
        Assert.Equal(56.69, one.X, 1);
        Assert.Equal("Custom title", Pdf.GetInfo(pdf, "user").Info!.Title);
    }

    [Fact]
    public async Task OutputSafety_LimitsAndValidation()
    {
        var output = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentException>(() => Converter.ConvertFromHtmlStringAsync("<p>x</p>", output, new HtmlToPdfOptions { PageSize = new PdfPageSize(10, 10) }));
        await Assert.ThrowsAsync<ArgumentException>(() => Converter.ConvertFromHtmlStringAsync("<p>x</p>", output, new HtmlToPdfOptions { Margins = PdfMargins.All(400) }));
        await Assert.ThrowsAsync<PdfLimitExceededException>(() => new PdfConverter(null, new PdfConversionLimits { MaxOutputElements = 5 }).ConvertFromHtmlStringAsync(string.Concat(Enumerable.Repeat("<p>x</p>", 50)), output));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Converter.ConvertFromHtmlStringAsync("<p>x</p>", output, ct: cts.Token));
        Assert.Equal(0, output.Length);

        // Hostile markup still converts: huge spans, unclosed tags, empty documents, control characters.
        var (pdf, _) = Convert("<table><tr><td colspan=999999 rowspan=999999>x</td></tr></table><p><b><i><u>unclosed\u0001\u0007");
        Assert.Contains("unclosed", Extract(pdf).Text, StringComparison.Ordinal);
        var (empty, emptyResult) = Convert("");
        Assert.Equal(1, emptyResult.PageCount);
        Assert.Equal(1, Pdf.GetInfo(empty).PageCount);
        var (scripts, scriptResult) = Convert("<p>שלום עולם</p><p>สวัสดี</p>", WithTestFont());
        Assert.Contains(scriptResult.Warnings, w => w.Contains("Right-to-left", StringComparison.Ordinal));
        Assert.NotEmpty(scripts);

        // Stream input with a charset, and the input size limit.
        using var stream = new MemoryStream(Encoding.Latin1.GetBytes("<meta charset='iso-8859-1'><p>Café</p>"));
        var streamOutput = new MemoryStream();
        await Converter.ConvertFromHtmlAsync(stream, streamOutput);
        Assert.Equal("Café", Extract(streamOutput.ToArray()).Text.Trim());
        await Assert.ThrowsAsync<PdfLimitExceededException>(() => new PdfConverter(null, new PdfConversionLimits { MaxInputBytes = 10 }).ConvertFromHtmlAsync(new MemoryStream(new byte[100]), new MemoryStream()));
    }

    [Fact]
    public void InteropSamples_AreValid()
    {
        foreach (var (name, pdf) in InteropSamples())
        {
            var text = Extract(pdf);
            Assert.Equal(2, text.Pages.Count);
            Assert.Contains("Hello page 1", text.Pages[0].Text, StringComparison.Ordinal);
            Assert.Equal("Test document", Pdf.GetInfo(pdf).Info!.Title);
            Assert.True(Pdf.Validate(pdf).IsValid, name + ": " + string.Join("; ", Pdf.Validate(pdf).Issues));
        }
    }
}
