using System.IO.Compression;
using System.Text;
using PdfUtil.Conversion;
using PdfUtil.Conversion.Internal.Docx;
using PdfUtil.Conversion.Internal.Model;
using PdfUtil.Internal.Images;
using PdfUtil.Tests.Infrastructure;
using static PdfUtil.Tests.Infrastructure.DocxFixture;

namespace PdfUtil.Tests;

/// <summary>F22: Word (DOCX) to PDF.</summary>
public class DocxToPdfTests
{
    private static readonly PdfConverter Converter = new();

    internal static (byte[] Pdf, DocxToPdfResult Result) Convert(byte[] docx, DocxToPdfOptions? options = null, PdfConverter? converter = null)
    {
        using var output = new MemoryStream();
        var result = (converter ?? Converter).ConvertFromDocxAsync(new MemoryStream(docx), output, options).GetAwaiter().GetResult();
        return (output.ToArray(), result);
    }

    private static PdfTextExtractionResult Extract(byte[] pdf) => HtmlToPdfTests.Extract(pdf);

    private static string Strip(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>A model written with the PDF to Word writer: headings, a nested list, a table with spans, an image and links.</summary>
    internal static DocModel Model(string firstHeading = "Model heading")
    {
        var model = new DocModel { Title = "Test document", Author = "Model Author", Language = "en-GB" };
        var section = new Section { PageWidth = 595.28, PageHeight = 841.89, Margins = Margins.Default };
        model.Sections.Add(section);
        RunStyle S(double size = 11, bool bold = false) => new() { FontFamily = "Arial", GenericFamily = "sans-serif", Size = size, Bold = bold };
        Paragraph P(string text, int heading = 0, params Inline[] extra)
        {
            var p = new Paragraph { HeadingLevel = heading };
            p.Inlines.Add(new TextInline(text, S(heading > 0 ? 16 : 11, heading > 0)));
            p.Inlines.AddRange(extra);
            return p;
        }

        section.Blocks.Add(P(firstHeading, 1));
        var link = new LinkInline { Uri = "https://example.com/" };
        link.Children.Add(new TextInline("a link", S()));
        var internalLink = new LinkInline { Anchor = "details" };
        internalLink.Children.Add(new TextInline("see details", S()));
        section.Blocks.Add(P("Body text with ", 0, link, new TextInline(" and ", S()), internalLink, new TextInline(".", S())));
        var list = new ListBlock { Kind = ListKind.Decimal, Start = 1 };
        foreach (var item in new[] { "First item", "Second item" })
        {
            var li = new ListItem();
            li.Blocks.Add(P(item));
            list.Items.Add(li);
        }

        var nested = new ListBlock { Kind = ListKind.Bullet };
        var nestedItem = new ListItem();
        nestedItem.Blocks.Add(P("Nested bullet"));
        nested.Items.Add(nestedItem);
        list.Items[1].Blocks.Add(nested);
        section.Blocks.Add(list);
        var table = new TableBlock { HeaderRows = 1, ColumnWidths = new List<double> { 150, 150, 150 } };
        TableCell Cell(string text, int colSpan = 1, int rowSpan = 1)
        {
            var c = new TableCell { ColumnSpan = colSpan, RowSpan = rowSpan };
            c.Blocks.Add(P(text));
            return c;
        }

        var header = new TableRow();
        header.Cells.AddRange(new[] { Cell("Name"), Cell("Qty"), Cell("Price") });
        var row1 = new TableRow();
        row1.Cells.AddRange(new[] { Cell("Merged down", rowSpan: 2), Cell("2"), Cell("1.50") });
        var row2 = new TableRow();
        row2.Cells.Add(Cell("Spans two", colSpan: 2));
        table.Rows.AddRange(new[] { header, row1, row2 });
        section.Blocks.Add(table);
        var png = ImageCodecs.EncodePng(2, 2, 3, new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255 });
        section.Blocks.Add(new ImageBlock { Image = new ImageData { Data = png, MimeType = "image/png", PixelWidth = 2, PixelHeight = 2 }, Width = 72, Height = 72 });
        section.Blocks.Add(new PageBreakBlock());
        var details = P("Details", 2);
        details.Anchors.Add("details");
        section.Blocks.Add(details);
        section.Blocks.Add(P("Second page text."));
        return model;
    }

    internal static byte[] ModelDocx(string firstHeading = "Model heading") => DocxWriter.Write(Model(firstHeading), 100_000, out _);

    /// <summary>The corpus documents for pdf.js and PDFKit: two pages, "Hello page 1" on the first.</summary>
    internal static IEnumerable<(string Name, byte[] Pdf)> InteropSamples()
    {
        yield return ("docx-model", Convert(ModelDocx("Hello page 1")).Pdf);
        yield return ("docx-fixture", Convert(SectionsFixture()).Pdf);
    }

    [Fact]
    public void FromModel_StructureTextImagesAndLinks()
    {
        var (pdf, result) = Convert(ModelDocx());
        Assert.Equal(2, result.PageCount);
        Assert.Equal(1, result.ImageCount);
        Assert.Equal(2, result.LinkCount);
        Assert.Equal(2, result.BookmarkCount);
        var text = Extract(pdf);
        Assert.Equal(Strip("Model heading Body text with a link and see details. 1. First item 2. Second item o Nested bullet Name Qty Price Merged down 2 1.50 Spans two"), Strip(text.Pages[0].Text));
        Assert.Equal(Strip("Details Second page text."), Strip(text.Pages[1].Text));
        var heading = text.Pages[0].Runs.Single(r => r.Text == "Model heading");
        Assert.True(heading.IsBold);
        Assert.Equal(16, heading.FontSize, 1);
        Assert.Equal(72, heading.X, 1);
        Assert.Equal(595.28, text.Pages[0].Width, 1);
        var info = Pdf.GetInfo(pdf);
        Assert.Equal("Test document", info.Info!.Title);
        Assert.Equal("Model Author", info.Info.Author);
        Assert.Equal("Test document", info.Xmp!.Title);
        Assert.Equal("en-GB", Pdf.Inspect(pdf).Language);

        // The list item text sits right of its marker; the nested bullet further right.
        var first = text.Pages[0].Runs.Single(r => r.Text.EndsWith("First item", StringComparison.Ordinal));
        var nested = text.Pages[0].Runs.Single(r => r.Text.EndsWith("Nested bullet", StringComparison.Ordinal));
        Assert.True(nested.X > first.X + 10);
        var merged = text.Pages[0].Runs.Single(r => r.Text == "Merged down");
        var spans = text.Pages[0].Runs.Single(r => r.Text == "Spans two");
        Assert.True(spans.X > merged.X + 100, "the column-spanning cell starts in the second column");
    }

    [Fact]
    public async Task RoundTrip_PdfToWordToPdf()
    {
        var pdf = HtmlConversionTests.LayoutDocument();
        using var docx = new MemoryStream();
        await Converter.ConvertToDocxAsync(new MemoryStream(pdf), docx);
        var (back, result) = Convert(docx.ToArray());
        var expected = Strip(HtmlConversionTests.MainText(HtmlConversionTests.Convert(pdf, new PdfToHtmlOptions { IncludeNavigation = false, Images = PdfHtmlImageMode.None }).Html));
        var actual = Strip(Extract(back).Text);
        foreach (var phrase in new[] { "AnnualReport", "Firstpointaboutrevenuegrowth", "Stepnumber3", "Rightcolumnsecondparagraph", "Backtothestart" })
        {
            Assert.Contains(phrase, actual, StringComparison.Ordinal);
        }

        // The same letters (list markers and numbers aside) come back.
        // (The nested list's white bullet comes back as "o", so that letter is left out of the comparison.)
        static string Letters(string text) => new(text.Where(c => char.IsLetter(c) && c != 'o').Order().ToArray());
        Assert.Equal(Letters(expected), Letters(actual));
        Assert.Equal(2, result.LinkCount);
    }

    private const string Styles = """
        <w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri"/><w:sz w:val="22"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="259" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>
        <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
        <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:pPr><w:keepNext/><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:b/><w:sz w:val="32"/><w:color w:val="2F5496"/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="Quote"><w:name w:val="Quote"/><w:basedOn w:val="Normal"/><w:pPr><w:ind w:left="864"/></w:pPr><w:rPr><w:i/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="BigQuote"><w:name w:val="Big Quote"/><w:basedOn w:val="Quote"/><w:rPr><w:sz w:val="28"/></w:rPr></w:style>
        <w:style w:type="character" w:styleId="Strong"><w:name w:val="Strong"/><w:rPr><w:b/></w:rPr></w:style>
        <w:style w:type="table" w:styleId="TableGrid"><w:name w:val="Table Grid"/><w:tblPr><w:tblBorders><w:top w:val="single" w:sz="4" w:color="000000"/><w:left w:val="single" w:sz="4"/><w:bottom w:val="single" w:sz="4"/><w:right w:val="single" w:sz="4"/><w:insideH w:val="single" w:sz="4"/><w:insideV w:val="single" w:sz="4"/></w:tblBorders></w:tblPr></w:style>
        """;

    private const string Numbering = """
        <w:abstractNum w:abstractNumId="0"><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/><w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr></w:lvl><w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="lowerLetter"/><w:lvlText w:val="%2)"/><w:pPr><w:ind w:left="1440" w:hanging="360"/></w:pPr></w:lvl></w:abstractNum>
        <w:abstractNum w:abstractNumId="1"><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val=""/><w:rPr><w:rFonts w:ascii="Symbol"/></w:rPr><w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr></w:lvl></w:abstractNum>
        <w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>
        <w:num w:numId="2"><w:abstractNumId w:val="1"/></w:num>
        <w:num w:numId="3"><w:abstractNumId w:val="0"/><w:lvlOverride w:ilvl="0"><w:startOverride w:val="5"/></w:lvlOverride></w:num>
        """;

    private static string Num(string text, int numId, int level = 0) => P(text, $"<w:numPr><w:ilvl w:val=\"{level}\"/><w:numId w:val=\"{numId}\"/></w:numPr>");

    [Fact]
    public void Styles_InheritanceAndDefaults()
    {
        var body = P("Heading text", "<w:pStyle w:val=\"Heading1\"/>") + P("Normal text") + P("Quoted", "<w:pStyle w:val=\"BigQuote\"/>")
            + "<w:p><w:r><w:t xml:space=\"preserve\">Plain </w:t></w:r><w:r><w:rPr><w:rStyle w:val=\"Strong\"/><w:color w:val=\"FF0000\"/><w:u w:val=\"single\"/></w:rPr><w:t>strong red</w:t></w:r><w:r><w:rPr><w:vertAlign w:val=\"superscript\"/></w:rPr><w:t>2</w:t></w:r><w:r><w:rPr><w:vanish/></w:rPr><w:t>hidden</w:t></w:r><w:r><w:rPr><w:caps/><w:highlight w:val=\"yellow\"/></w:rPr><w:t>loud</w:t></w:r></w:p>"
            + "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1800\" w:header=\"708\" w:footer=\"708\"/></w:sectPr>";
        var (pdf, result) = Convert(Build(body, Styles));
        Assert.Equal(1, result.BookmarkCount);
        var runs = Extract(pdf).Pages[0].Runs;
        var heading = runs.Single(r => r.Text == "Heading text");
        Assert.Equal(16, heading.FontSize, 1);
        Assert.True(heading.IsBold);
        Assert.Equal("#2F5496", heading.Color);
        Assert.Equal(90, heading.X, 1);
        Assert.Equal(11, runs.Single(r => r.Text == "Normal text").FontSize, 1);
        var quote = runs.Single(r => r.Text == "Quoted");
        Assert.Equal(14, quote.FontSize, 1);
        Assert.True(quote.IsItalic);
        Assert.Equal(90 + 43.2, quote.X, 1);
        var strong = runs.Single(r => r.Text.Contains("strong red", StringComparison.Ordinal));
        Assert.True(strong.IsBold);
        Assert.Equal("#FF0000", strong.Color);
        Assert.DoesNotContain(runs, r => r.Text.Contains("hidden", StringComparison.Ordinal));
        Assert.Contains(runs, r => r.Text.Contains("LOUD", StringComparison.Ordinal));
        Assert.True(runs.Single(r => r.Text == "2").Y > strong.Y + 2, "superscript is raised");
    }

    [Fact]
    public async Task Numbering_LevelsContinuationAndRestart()
    {
        var body = Num("One", 1) + Num("Sub a", 1, 1) + Num("Sub b", 1, 1) + Num("Two", 1) + P("Interruption") + Num("Three", 1) + Num("Bullet", 2) + Num("Five", 3) + Num("Six", 3);
        var (pdf, _) = Convert(Build(body, Styles, Numbering));
        var text = Extract(pdf).Pages[0].Text.Replace(" ", "", StringComparison.Ordinal);
        foreach (var expected in new[] { "1.One", "a.Suba", "b.Subb", "2.Two", "Interruption", "3.Three", "•Bullet", "5.Five", "6.Six" })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }

        // Item text starts at the level's left indent (the marker hangs before it).
        var glyphs = (await new PdfTextExtractor().ExtractAsync(new MemoryStream(pdf), new PdfTextExtractionOptions { IncludeGlyphs = true })).Pages[0].Runs.SelectMany(r => r.Glyphs).ToList();
        Assert.Equal(72 + 36, glyphs.First(g => g.Text == "O").X, 1);
        Assert.Equal(72 + 72, glyphs.First(g => g.Text == "S").X, 1);
    }

    private static byte[] SectionsFixture()
    {
        var header = Part("hdr", P("Default header"));
        var first = Part("hdr", P("First page header"));
        var footer = Part("ftr", "<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:t xml:space=\"preserve\">Page </w:t></w:r><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> PAGE </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>9</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r><w:r><w:t xml:space=\"preserve\"> of </w:t></w:r><w:fldSimple w:instr=\" NUMPAGES \"><w:r><w:t>9</w:t></w:r></w:fldSimple></w:p>");
        var sect = "<w:headerReference w:type=\"default\" r:id=\"rIdH\"/><w:headerReference w:type=\"first\" r:id=\"rIdF\"/><w:footerReference w:type=\"default\" r:id=\"rIdP\"/><w:titlePg/>";
        var body = P("Hello page 1", "<w:pStyle w:val=\"Heading1\"/>") + P("Portrait section.")
            + $"<w:p><w:pPr><w:sectPr>{sect}<w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"708\" w:footer=\"708\"/></w:sectPr></w:pPr></w:p>"
            + P("Landscape section.") + "<w:p><w:r><w:t>Before break</w:t><w:br w:type=\"page\"/><w:t>After break</w:t></w:r></w:p>"
            + "<w:sectPr><w:pgSz w:w=\"16838\" w:h=\"11906\" w:orient=\"landscape\"/><w:pgMar w:top=\"1000\" w:right=\"1000\" w:bottom=\"1000\" w:left=\"1000\" w:header=\"500\" w:footer=\"500\"/><w:cols w:num=\"2\"/></w:sectPr>";
        return Build(body, Styles, relationships: new[] { ("rIdH", "header", "header1.xml", false), ("rIdF", "header", "header2.xml", false), ("rIdP", "footer", "footer1.xml", false) },
            parts: new Dictionary<string, byte[]> { ["word/header1.xml"] = header, ["word/header2.xml"] = first, ["word/footer1.xml"] = footer },
            core: "<dc:title>Test document</dc:title>");
    }

    [Fact]
    public void Sections_HeadersFootersAndFields()
    {
        var (pdf, result) = Convert(SectionsFixture());
        Assert.Equal(2, result.SectionCount);
        Assert.Contains(result.Warnings, w => w.Contains("Multi-column", StringComparison.Ordinal));
        var pages = Extract(pdf).Pages;
        Assert.Equal(3, pages.Count);
        Assert.Equal((595.3, 841.9), (Math.Round(pages[0].Width, 1), Math.Round(pages[0].Height, 1)));
        Assert.Equal((841.9, 595.3), (Math.Round(pages[1].Width, 1), Math.Round(pages[1].Height, 1)));
        Assert.Contains("First page header", pages[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Default header", pages[0].Text, StringComparison.Ordinal);
        Assert.Contains("Default header", pages[1].Text, StringComparison.Ordinal); // titlePg is not inherited
        Assert.Contains("Default header", pages[2].Text, StringComparison.Ordinal);
        // With titlePg and no first-page footer, Word leaves the first page's footer empty.
        Assert.DoesNotContain("Page", pages[0].Text, StringComparison.Ordinal);
        for (var i = 1; i < 3; i++)
        {
            Assert.True(pages[i].Text.Contains($"Page {i + 1} of 3", StringComparison.Ordinal), pages[i].Text);
        }

        Assert.Contains("Before break", pages[1].Text, StringComparison.Ordinal);
        Assert.Contains("After break", pages[2].Text, StringComparison.Ordinal);
        Assert.Equal(50, pages[1].Runs.Single(r => r.Text == "Landscape section.").X, 1);
    }

    [Fact]
    public void Tables_GridMergesBordersAndShading()
    {
        var body = """
            <w:tbl><w:tblPr><w:tblStyle w:val="TableGrid"/><w:tblW w:w="6000" w:type="dxa"/></w:tblPr>
            <w:tblGrid><w:gridCol w:w="2000"/><w:gridCol w:w="2000"/><w:gridCol w:w="2000"/></w:tblGrid>
            <w:tr><w:trPr><w:tblHeader/></w:trPr><w:tc><w:p><w:r><w:t>H1</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>H2</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>H3</w:t></w:r></w:p></w:tc></w:tr>
            <w:tr><w:tc><w:tcPr><w:vMerge w:val="restart"/><w:shd w:val="clear" w:fill="FFFF00"/><w:vAlign w:val="center"/></w:tcPr><w:p><w:r><w:t>Tall</w:t></w:r></w:p></w:tc><w:tc><w:tcPr><w:gridSpan w:val="2"/></w:tcPr><w:p><w:r><w:t>Wide</w:t></w:r></w:p></w:tc></w:tr>
            <w:tr><w:tc><w:tcPr><w:vMerge/></w:tcPr><w:p/></w:tc><w:tc><w:p><w:r><w:t>B</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>C</w:t></w:r></w:p></w:tc></w:tr>
            </w:tbl><w:p/>
            """;
        var (pdf, _) = Convert(Build(body, Styles));
        var runs = Extract(pdf).Pages[0].Runs;
        // Text starts after the 0.5 pt border and the 5.4 pt cell margin.
        Assert.Equal(72 + 0.5 + 5.4, runs.Single(r => r.Text == "H1").X, 1);
        Assert.Equal(72 + 100 + 0.5 + 5.4, runs.Single(r => r.Text == "Wide").X, 1);
        Assert.Equal(72 + 100 + 0.5 + 5.4, runs.Single(r => r.Text == "B").X, 1);
        var tall = runs.Single(r => r.Text == "Tall");
        var wide = runs.Single(r => r.Text == "Wide");
        var b = runs.Single(r => r.Text == "B");
        Assert.True(tall.Y < wide.Y && tall.Y > b.Y, "the merged cell is centred over both rows");
        var images = Pdf.Inspect(pdf);
        Assert.Equal(1, images.PageCount);
    }

    [Fact]
    public void Content_LinksBookmarksImagesAndUnsupported()
    {
        var png = ImageCodecs.EncodePng(4, 2, 3, new byte[24]);
        var drawing = """<w:r><w:drawing><wp:inline><wp:extent cx="914400" cy="457200"/><wp:docPr id="1" name="Picture 1" descr="A box"/><a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic><pic:blipFill><a:blip r:embed="rIdImg"/></pic:blipFill></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>""";
        var body = $"""
            <w:p><w:bookmarkStart w:id="0" w:name="target"/><w:r><w:t>Target paragraph</w:t></w:r><w:bookmarkEnd w:id="0"/></w:p>
            <w:p><w:hyperlink r:id="rIdLink"><w:r><w:t>External</w:t></w:r></w:hyperlink><w:r><w:t xml:space="preserve"> and </w:t></w:r><w:hyperlink w:anchor="target"><w:r><w:t>internal</w:t></w:r></w:hyperlink><w:hyperlink r:id="rIdBad"><w:r><w:t xml:space="preserve"> bad</w:t></w:r></w:hyperlink></w:p>
            <w:p>{drawing}</w:p>
            <w:p><w:ins w:id="1" w:author="a"><w:r><w:t>Inserted</w:t></w:r></w:ins><w:del w:id="2" w:author="a"><w:r><w:delText>Deleted</w:delText></w:r></w:del><w:commentRangeStart w:id="3"/><w:r><w:t xml:space="preserve"> kept</w:t></w:r></w:p>
            <w:p><m:oMath><m:r><m:t>x</m:t></m:r></m:oMath><w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText>DATE</w:instrText></w:r><w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:t>1 Jan 2024</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r></w:p>
            <w:p><mc:AlternateContent><mc:Choice Requires="wps"><w:r><w:t>box</w:t></w:r></mc:Choice></mc:AlternateContent></w:p>
            """;
        var (pdf, result) = Convert(Build(body, Styles, relationships: new[] { ("rIdLink", "hyperlink", "https://example.com/doc", true), ("rIdBad", "hyperlink", "javascript:alert(1)", true), ("rIdImg", "image", "media/box.png", false) }, parts: new Dictionary<string, byte[]> { ["word/media/box.png"] = png }));
        Assert.Equal(1, result.ImageCount);
        Assert.Equal(2, result.LinkCount);
        var text = Extract(pdf).Text;
        Assert.Contains("Inserted kept", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Deleted", text, StringComparison.Ordinal);
        Assert.Contains("1 Jan 2024", text, StringComparison.Ordinal);
        var image = Extract(pdf).Pages[0].Images.Single();
        Assert.Equal((72, 36), (Math.Round(image.Bounds.Width), Math.Round(image.Bounds.Height)));
        foreach (var warning in new[] { "Tracked changes", "Comments", "Equations", "Fields other than", "Text boxes", "unsafe" })
        {
            Assert.Contains(result.Warnings, w => w.Contains(warning, StringComparison.Ordinal));
        }

        var back = HtmlConversionTests.Parse(HtmlConversionTests.Convert(pdf).Html);
        System.Xml.Linq.XNamespace x = "http://www.w3.org/1999/xhtml";
        var links = back.Descendants(x + "main").Single().Descendants(x + "a").Select(a => a.Attribute("href")!.Value).ToList();
        Assert.Contains("https://example.com/doc", links);
        Assert.Contains("#page-1", links);
    }

    [Fact]
    public async Task HostileAndInvalidPackages()
    {
        await Assert.ThrowsAnyAsync<PdfCorruptException>(() => Converter.ConvertFromDocxAsync(new MemoryStream(Encoding.ASCII.GetBytes("not a zip")), new MemoryStream()));
        var legacy = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0 };
        var ex = await Assert.ThrowsAsync<PdfCorruptException>(() => Converter.ConvertFromDocxAsync(new MemoryStream(legacy), new MemoryStream()));
        Assert.Contains(".doc", ex.Message, StringComparison.Ordinal);

        byte[] Zip(params (string Name, byte[] Data)[] entries)
        {
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, data) in entries)
                {
                    using var s = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                    s.Write(data);
                }
            }

            return output.ToArray();
        }

        await Assert.ThrowsAnyAsync<PdfCorruptException>(() => Converter.ConvertFromDocxAsync(new MemoryStream(Zip(("../../evil.xml", new byte[1]))), new MemoryStream()));
        await Assert.ThrowsAnyAsync<PdfCorruptException>(() => Converter.ConvertFromDocxAsync(new MemoryStream(Zip(("a.xml", new byte[1]))), new MemoryStream()));
        await Assert.ThrowsAsync<PdfLimitExceededException>(() => Converter.ConvertFromDocxAsync(new MemoryStream(Zip(("bomb.xml", new byte[60_000_000]))), new MemoryStream()));
        var limited = new PdfConverter(null, new PdfConversionLimits { MaxPackageEntries = 3 });
        await Assert.ThrowsAsync<PdfLimitExceededException>(() => limited.ConvertFromDocxAsync(new MemoryStream(ModelDocx()), new MemoryStream()));

        // Deeply nested tables and huge documents hit the element limits.
        var nested = string.Concat(Enumerable.Repeat("<w:tbl><w:tr><w:tc>", 120)) + "<w:p/>" + string.Concat(Enumerable.Repeat("</w:tc></w:tr></w:tbl>", 120));
        Assert.Throws<PdfLimitExceededException>(() => Convert(Build(nested), converter: new PdfConverter(null, new PdfConversionLimits { MaxNestingDepth = 50 })));
        Assert.Throws<PdfLimitExceededException>(() => Convert(Build(string.Concat(Enumerable.Repeat(P("x"), 500))), converter: new PdfConverter(null, new PdfConversionLimits { MaxElements = 100 })));

        // A DTD in a part is refused.
        var dtd = Build("<w:p/>", parts: new Dictionary<string, byte[]> { ["word/document.xml"] = Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY a \"aaaa\">]><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>&a;</w:t></w:r></w:p></w:body></w:document>") });
        Assert.Throws<PdfCorruptException>(() => Convert(dtd));

        // Missing styles, numbering and images degrade gracefully.
        var (pdf, result) = Convert(Build(Num("Orphan list item", 7) + "<w:p><w:r><w:drawing><wp:inline><wp:extent cx=\"1\" cy=\"1\"/><a:graphic><a:graphicData uri=\"x\"><a:blip r:embed=\"nope\"/></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>" + P("Styled", "<w:pStyle w:val=\"Missing\"/>")));
        Assert.Contains("Orphan list item", Extract(pdf).Text, StringComparison.Ordinal);
        Assert.Contains(result.Warnings, w => w.Contains("missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FileOutput_ProtectionAndFonts()
    {
        var dir = Directory.CreateTempSubdirectory("pdfutil-docx2pdf-");
        try
        {
            var input = Path.Combine(dir.FullName, "in.docx");
            var output = Path.Combine(dir.FullName, "out.pdf");
            File.WriteAllBytes(input, Build(P("Привет мир", rPr: "<w:rFonts w:ascii=\"Test Sans\" w:hAnsi=\"Test Sans\"/>"), Styles));
            var options = new DocxToPdfOptions { Protection = new PdfProtectionOptions { UserPassword = "u", OwnerPassword = "o" } };
            options.Fonts.Add(new PdfFontSource(TestFont.Build()));
            var result = await Converter.ConvertFromDocxFileAsync(input, output, options);
            Assert.True(result.IsProtected);
            Assert.Equal(new[] { "Test Sans" }, result.EmbeddedFonts);
            Assert.Equal("Привет мир", HtmlToPdfTests.Extract(File.ReadAllBytes(output), "u").Text.Trim());
            File.WriteAllBytes(input, Encoding.ASCII.GetBytes("broken"));
            var before = File.ReadAllBytes(output);
            await Assert.ThrowsAnyAsync<PdfCorruptException>(() => Converter.ConvertFromDocxFileAsync(input, output));
            Assert.Equal(before, File.ReadAllBytes(output));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void InteropSamples_AreValid()
    {
        foreach (var (name, pdf) in InteropSamples())
        {
            var text = Extract(pdf);
            Assert.True(text.Pages.Count >= 2, name);
            Assert.Contains("Hello page 1", text.Pages[0].Text, StringComparison.Ordinal);
            Assert.Equal("Test document", Pdf.GetInfo(pdf).Info!.Title);
            Assert.True(Pdf.Validate(pdf).IsValid, name);
        }
    }

    /// <summary>LibreOffice, when installed, lays the fixture out on the same number of pages.</summary>
    [RequiresToolFact("soffice")]
    public void LibreOffice_PageCountMatches()
    {
        var dir = Directory.CreateTempSubdirectory("pdfutil-lo-");
        try
        {
            var docx = Path.Combine(dir.FullName, "fixture.docx");
            File.WriteAllBytes(docx, SectionsFixture());
            var (code, output) = TestHelpers.Run(TestHelpers.FindTool("soffice")!, "--headless", "--convert-to", "pdf", "--outdir", dir.FullName, docx);
            Assert.True(code == 0, output);
            Assert.Equal(Pdf.GetInfo(Path.Combine(dir.FullName, "fixture.pdf")).PageCount, Convert(SectionsFixture()).Result.PageCount);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
