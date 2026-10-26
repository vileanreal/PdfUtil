using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using PdfUtil.Conversion;
using PdfUtil.Conversion.Internal.Docx;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>F20: PDF to Word (DOCX).</summary>
public class DocxConversionTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly PdfConverter Converter = new();

    internal static (byte[] Docx, PdfToDocxResult Result) Convert(byte[] pdf, PdfToDocxOptions? options = null, PdfConverter? converter = null)
    {
        using var output = new MemoryStream();
        var result = (converter ?? Converter).ConvertToDocxAsync(new MemoryStream(pdf), output, options).GetAwaiter().GetResult();
        return (output.ToArray(), result);
    }

    internal static XDocument Part(byte[] docx, string name) => DocxPackage.Read(docx, 1000, 1 << 30).Xml(name)!;

    /// <summary>The text of each paragraph of the body, in order (tables included).</summary>
    internal static List<string> Paragraphs(byte[] docx) => Part(docx, "word/document.xml").Descendants(W + "p")
        .Select(p => string.Concat(p.Descendants().Where(e => e.Name == W + "t" || e.Name == W + "tab" || e.Name == W + "br").Select(e => e.Name == W + "t" ? e.Value : e.Name == W + "tab" ? "\t" : "\n")))
        .ToList();

    private static string Strip(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static string? Style(XElement p) => (string?)p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val");

    private static string Text(XElement e) => string.Concat(e.Descendants(W + "t").Select(t => t.Value));

    /// <summary>A ruled 3 × 3 table: a bold header row, a body row, and a last row whose first two cells are merged.</summary>
    internal static byte[] TableDocument()
    {
        var doc = new LayoutPdf { Title = "Table test" };
        var page = doc.Page();
        page.Text(72, 640, 16, "Price list", "F2");
        foreach (var y in new[] { 600, 580, 560, 540 })
        {
            page.Line(72, y, 372, y);
        }

        page.Line(72, 540, 72, 600).Line(372, 540, 372, 600).Line(272, 540, 272, 600).Line(172, 560, 172, 600);
        page.Text(76, 585, 10, "Name", "F2").Text(176, 585, 10, "Quantity", "F2").Text(276, 585, 10, "Price", "F2");
        page.Text(76, 565, 10, "Apples").Text(176, 565, 10, "3").Text(276, 565, 10, "1.20");
        page.Text(76, 545, 10, "Total of all the items").Text(276, 545, 10, "4.20");
        page.Text(72, 500, 10, "Prices include tax.");
        return doc.Build();
    }

    [Fact]
    public void Structure_HeadingsListsLinksImagesAndProperties()
    {
        var (docx, result) = Convert(HtmlConversionTests.LayoutDocument());
        var document = Part(docx, "word/document.xml");
        var paragraphs = document.Descendants(W + "p").ToList();
        string StyleOf(string text) => Style(paragraphs.First(p => Text(p) == text)) ?? "";
        Assert.Equal("Heading1", StyleOf("Annual Report"));
        Assert.Equal("Heading2", StyleOf("Highlights"));
        Assert.Equal("Heading2", StyleOf("Two Columns"));
        Assert.Equal("Heading3", StyleOf("Steps"));
        Assert.Equal(4, result.HeadingCount);

        // Lists: three bullets, one nested bullet at level 1, three numbered steps; each list its own instance.
        var numbered = paragraphs.Where(p => p.Element(W + "pPr")?.Element(W + "numPr") is not null).Select(p => (Text: Text(p), Level: (string?)p.Descendants(W + "ilvl").Single().Attribute(W + "val"), Num: (string?)p.Descendants(W + "numId").Single().Attribute(W + "val"))).ToList();
        Assert.Equal(new[] { "First point about revenue growth", "Third point", "A nested point", "Step number 1", "Step number 3" }, numbered.Where(n => n.Text != "" && !n.Text.StartsWith("Second", StringComparison.Ordinal) && n.Text != "Step number 2").Select(n => n.Text));
        Assert.Equal("1", numbered.Single(n => n.Text == "A nested point").Level);
        Assert.Equal(3, numbered.Select(n => n.Num).Distinct().Count());
        var numbering = Part(docx, "word/numbering.xml");
        var stepsNum = numbered.First(n => n.Text.StartsWith("Step", StringComparison.Ordinal)).Num;
        var abstractId = (string?)numbering.Root!.Elements(W + "num").Single(n => (string?)n.Attribute(W + "numId") == stepsNum).Element(W + "abstractNumId")!.Attribute(W + "val");
        var format = numbering.Root.Elements(W + "abstractNum").Single(a => (string?)a.Attribute(W + "abstractNumId") == abstractId).Elements(W + "lvl").First().Element(W + "numFmt")!.Attribute(W + "val")!.Value;
        Assert.Equal("decimal", format);
        Assert.Equal(3, result.ListCount);

        // Links: one external hyperlink, one to the bookmark at page 1.
        var rels = DocxPackage.Read(docx, 1000, 1 << 30).Relationships("word/document.xml");
        var hyperlinks = document.Descendants(W + "hyperlink").ToList();
        var external = hyperlinks.Single(h => h.Attribute(XName.Get("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships")) is not null);
        Assert.Equal("example.com", Text(external));
        Assert.Equal(("https://example.com/", true), (rels[external.Attribute(XName.Get("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"))!.Value].Target, rels[external.Attribute(XName.Get("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"))!.Value].External));
        var internalLink = hyperlinks.Single(h => h.Attribute(W + "anchor") is not null);
        Assert.Equal("page_1", internalLink.Attribute(W + "anchor")!.Value);
        Assert.Contains(document.Descendants(W + "bookmarkStart"), b => b.Attribute(W + "name")!.Value == "page_1");
        Assert.Equal(2, result.LinkCount);

        // Superscript, image, page setup and properties.
        Assert.Contains(document.Descendants(W + "r"), r => r.Descendants(W + "vertAlign").Any(v => v.Attribute(W + "val")!.Value == "superscript") && Text(r) == "2");
        Assert.Equal(1, result.ImageCount);
        Assert.NotNull(DocxPackage.Read(docx, 1000, 1 << 30).Get("word/media/image1.png"));
        var size = document.Descendants(W + "pgSz").Single();
        Assert.Equal(("12240", "15840"), (size.Attribute(W + "w")!.Value, size.Attribute(W + "h")!.Value));
        var core = Part(docx, "docProps/core.xml");
        Assert.Equal("Layout test", core.Descendants(XName.Get("title", "http://purl.org/dc/elements/1.1/")).Single().Value);
        Assert.Equal("Test Author", core.Descendants(XName.Get("creator", "http://purl.org/dc/elements/1.1/")).Single().Value);
        Assert.Equal("en-GB", core.Descendants(XName.Get("language", "http://purl.org/dc/elements/1.1/")).Single().Value);
    }

    [Fact]
    public void Tables_FromRulingLines()
    {
        var pdf = TableDocument();
        var (docx, result) = Convert(pdf);
        Assert.Equal(1, result.TableCount);
        var table = Part(docx, "word/document.xml").Descendants(W + "tbl").Single();
        Assert.Equal(new[] { "2000", "2000", "2000" }, table.Element(W + "tblGrid")!.Elements(W + "gridCol").Select(g => g.Attribute(W + "w")!.Value));
        var rows = table.Elements(W + "tr").ToList();
        Assert.Equal(3, rows.Count);
        Assert.NotNull(rows[0].Element(W + "trPr")?.Element(W + "tblHeader"));
        Assert.Equal(new[] { "Name", "Quantity", "Price" }, rows[0].Elements(W + "tc").Select(Text));
        Assert.Equal(new[] { "Apples", "3", "1.20" }, rows[1].Elements(W + "tc").Select(Text));
        var last = rows[2].Elements(W + "tc").ToList();
        Assert.Equal(new[] { "Total of all the items", "4.20" }, last.Select(Text));
        Assert.Equal("2", last[0].Descendants(W + "gridSpan").Single().Attribute(W + "val")!.Value);
        Assert.Contains("Prices include tax.", Paragraphs(docx));

        // The same table in reflowable HTML.
        var html = HtmlConversionTests.Parse(HtmlConversionTests.Convert(pdf).Html);
        XNamespace x = "http://www.w3.org/1999/xhtml";
        var htmlTable = html.Descendants(x + "table").Single();
        Assert.Equal(new[] { "Name", "Quantity", "Price" }, htmlTable.Descendants(x + "th").Select(t => t.Value));
        Assert.Equal("2", htmlTable.Descendants(x + "td").First(t => t.Value == "Total of all the items").Attribute("colspan")!.Value);
    }

    [Fact]
    public void Tables_OnlyWhenTheTextFits()
    {
        // A box with text running across its inner border is not a table; nor is a single frame.
        var doc = new LayoutPdf();
        var page = doc.Page();
        page.Line(72, 600, 372, 600).Line(72, 560, 372, 560).Line(72, 560, 72, 600).Line(372, 560, 372, 600).Line(222, 560, 222, 600);
        page.Text(76, 575, 10, "This sentence runs straight across the inner border of the box");
        page.Line(72, 500, 372, 500).Line(72, 460, 372, 460).Line(72, 460, 72, 500).Line(372, 460, 372, 500);
        page.Text(76, 475, 10, "Framed note");
        var (docx, result) = Convert(doc.Build());
        Assert.Equal(0, result.TableCount);
        Assert.Contains("This sentence runs straight across the inner border of the box", Paragraphs(docx));
        Assert.Contains("Framed note", Paragraphs(docx));
    }

    public static IEnumerable<object[]> TextFixtureNames() => TextFixtures.All().Select(f => new object[] { f.Name });

    [Theory]
    [MemberData(nameof(TextFixtureNames))]
    public async Task TextRoundTrips_EveryFontEncoding(string name)
    {
        var pdf = TextFixtures.All().Single(f => f.Name == name).Pdf;
        var expected = (await new PdfTextExtractor().ExtractAsync(new MemoryStream(pdf), new PdfTextExtractionOptions { Order = PdfTextOrder.Layout })).Text;
        Assert.Equal(Strip(expected), Strip(string.Concat(Paragraphs(Convert(pdf).Docx))));
    }

    [Theory]
    [MemberData(nameof(HtmlConversionTests.Structures), MemberType = typeof(HtmlConversionTests))]
    public void TextRoundTrips_EveryInputStructure(XrefMode mode)
    {
        var pdf = new TestPdfBuilder { Mode = mode, Rich = true, PageCount = 3 }.Build();
        var html = HtmlConversionTests.MainText(HtmlConversionTests.Convert(pdf, new PdfToHtmlOptions { IncludeNavigation = false }).Html);
        Assert.Equal(html.Order(), Strip(string.Concat(Paragraphs(Convert(pdf).Docx))).Order());
    }

    [Fact]
    public void PageBreaks_EncryptedInput_AndOptions()
    {
        var (docx, _) = Convert(HtmlConversionTests.LayoutDocument(), new PdfToDocxOptions { PageBreaks = true });
        Assert.Single(Part(docx, "word/document.xml").Descendants(W + "br"), b => (string?)b.Attribute(W + "type") == "page");
        var (noImages, result) = Convert(HtmlConversionTests.LayoutDocument(), new PdfToDocxOptions { IncludeImages = false, Pages = PdfPageRange.Parse("2") });
        Assert.Equal(0, result.ImageCount);
        Assert.Equal(1, result.PageCount);
        Assert.DoesNotContain(Paragraphs(noImages), p => p.Contains("Annual Report", StringComparison.Ordinal));

        var encrypted = new TestPdfBuilder { Encryption = (4, "user", "owner", true) }.Build();
        Assert.Throws<PdfInvalidPasswordException>(() => Convert(encrypted));
        Assert.Contains(Paragraphs(Convert(encrypted, new PdfToDocxOptions { Password = "owner" }).Docx), p => p.Contains("Hello page 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FileOutput_LimitsAndFailures()
    {
        var dir = Directory.CreateTempSubdirectory("pdfutil-docx-");
        try
        {
            var input = Path.Combine(dir.FullName, "in.pdf");
            var output = Path.Combine(dir.FullName, "out.docx");
            File.WriteAllBytes(input, TableDocument());
            var result = await Converter.ConvertToDocxFileAsync(input, output);
            Assert.Equal(new FileInfo(output).Length, result.DocxLength);
            DocxValidator.Validate(File.ReadAllBytes(output));
            File.WriteAllBytes(input, Encoding.ASCII.GetBytes("%PDF-1.7 nothing"));
            var before = File.ReadAllBytes(output);
            await Assert.ThrowsAnyAsync<PdfCorruptException>(() => Converter.ConvertToDocxFileAsync(input, output));
            Assert.Equal(before, File.ReadAllBytes(output));
            Assert.Equal(new[] { "in.pdf", "out.docx" }, dir.EnumerateFiles().Select(f => f.Name).Order(StringComparer.Ordinal));
        }
        finally
        {
            dir.Delete(recursive: true);
        }

        var stream = new MemoryStream();
        await Assert.ThrowsAsync<PdfLimitExceededException>(() => new PdfConverter(null, new PdfConversionLimits { MaxOutputElements = 10 }).ConvertToDocxAsync(new MemoryStream(HtmlConversionTests.LayoutDocument()), stream));
        Assert.Equal(0, stream.Length);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Converter.ConvertToDocxAsync(new MemoryStream(TableDocument()), stream, ct: cts.Token));
    }

    private static byte[] Modify(byte[] docx, string part, Func<string, string> change)
    {
        using var output = new MemoryStream();
        using (var source = new ZipArchive(new MemoryStream(docx)))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var content = reader.ReadToEnd();
                var newEntry = target.CreateEntry(entry.FullName);
                using var writer = new StreamWriter(newEntry.Open());
                writer.Write(entry.FullName == part ? change(content) : content);
            }
        }

        return output.ToArray();
    }

    [Fact]
    public void Validator_RejectsBrokenPackages()
    {
        var docx = Convert(HtmlConversionTests.LayoutDocument(), new PdfToDocxOptions { IncludeImages = false }).Docx;
        DocxValidator.Validate(docx);
        var broken = new (string Part, Func<string, string> Change, string Message)[]
        {
            ("[Content_Types].xml", s => s.Replace("<Override PartName=\"/word/styles.xml\"", "<Override PartName=\"/word/other.xml\"", StringComparison.Ordinal).Replace("<Default Extension=\"xml\" ContentType=\"application/xml\" />", "", StringComparison.Ordinal).Replace("<Default Extension=\"xml\" ContentType=\"application/xml\"/>", "", StringComparison.Ordinal), "content type"),
            ("word/_rels/document.xml.rels", s => s.Replace("Target=\"styles.xml\"", "Target=\"missing.xml\"", StringComparison.Ordinal), "missing"),
            ("word/document.xml", s => s.Replace("w:anchor=\"page_1\"", "w:anchor=\"nowhere\"", StringComparison.Ordinal), "bookmark"),
            ("word/document.xml", s => s.Replace("<w:pStyle w:val=\"Heading1\"", "<w:pStyle w:val=\"Nope\"", StringComparison.Ordinal), "style"),
            ("word/numbering.xml", s => s.Replace("<w:num w:numId=\"1\">", "<w:num w:numId=\"99\">", StringComparison.Ordinal), "numbering"),
            ("word/document.xml", s => s.Replace("</w:body>", "<w:p/></w:body>", StringComparison.Ordinal), "section"),
            ("word/document.xml", s => s.Replace("<w:body>", "<w:body><w:p><w:hyperlink r:id=\"rId999\"/></w:p>", StringComparison.Ordinal), "rId999"),
            ("word/document.xml", s => s[..^20], "well-formed"),
        };
        foreach (var (part, change, message) in broken)
        {
            var ex = Assert.Throws<PdfVerificationException>(() => DocxValidator.Validate(Modify(docx, part, change)));
            Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        }

        var tableDocx = Convert(TableDocument()).Docx;
        var gridEx = Assert.Throws<PdfVerificationException>(() => DocxValidator.Validate(Modify(tableDocx, "word/document.xml", s => s.Replace("<w:gridSpan w:val=\"2\" />", "", StringComparison.Ordinal).Replace("<w:gridSpan w:val=\"2\"/>", "", StringComparison.Ordinal))));
        Assert.Contains("grid", gridEx.Message, StringComparison.Ordinal);
    }

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                stream.Write(data);
            }
        }

        return output.ToArray();
    }

    [Fact]
    public void Package_HostileInput()
    {
        Assert.Throws<PdfCorruptException>(() => DocxPackage.Read(Encoding.ASCII.GetBytes("not a zip"), 100, 1000));
        foreach (var name in new[] { "../evil.xml", "/abs.xml", "word/../../x.xml", "a\\b.xml", "c:/x.xml", "word//x.xml" })
        {
            Assert.Throws<PdfCorruptException>(() => DocxPackage.Read(Zip((name, new byte[1])), 100, 1000));
        }

        Assert.Throws<PdfLimitExceededException>(() => DocxPackage.Read(Zip(Enumerable.Range(0, 20).Select(i => ($"p{i}.xml", new byte[1])).ToArray()), 10, 1000));
        Assert.Throws<PdfLimitExceededException>(() => DocxPackage.Read(Zip(("big.xml", new byte[5000])), 100, 4000));
        Assert.Throws<PdfLimitExceededException>(() => DocxPackage.Read(Zip(("bomb.xml", new byte[50_000_000])), 100, long.MaxValue));
        Assert.Throws<PdfCorruptException>(() => DocxPackage.Read(Zip(("a.xml", new byte[1]), ("A.xml", new byte[1])), 100, 1000));
        var dtd = DocxPackage.Read(Zip(("x.xml", Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY a \"aaaa\">]><x>&a;</x>"))), 100, 1000);
        Assert.Throws<PdfCorruptException>(() => dtd.Xml("x.xml"));
        Assert.Equal("word/media/a.png", DocxPackage.ResolveTarget("word/document.xml", "media/a.png"));
        Assert.Equal("customXml/item.xml", DocxPackage.ResolveTarget("word/document.xml", "../customXml/item.xml"));
        Assert.Equal("word/_rels/document.xml.rels", DocxPackage.RelationshipsPartFor("word/document.xml"));
    }

    /// <summary>Apple's OOXML reader (TextEdit, Quick Look) reads the package and finds the text.</summary>
    [RequiresToolFact("textutil")]
    public void Textutil_ReadsOutput()
    {
        var dir = Directory.CreateTempSubdirectory("pdfutil-textutil-");
        try
        {
            foreach (var (name, pdf) in new[] { ("layout", HtmlConversionTests.LayoutDocument()), ("table", TableDocument()) })
            {
                var docx = Path.Combine(dir.FullName, name + ".docx");
                File.WriteAllBytes(docx, Convert(pdf).Docx);
                var txt = Path.Combine(dir.FullName, name + ".txt");
                var (code, output) = TestHelpers.Run(TestHelpers.FindTool("textutil")!, "-convert", "txt", "-output", txt, docx);
                Assert.True(code == 0, output);
                var text = File.ReadAllText(txt);
                foreach (var phrase in name == "layout" ? new[] { "Annual Report", "First point about revenue growth", "Step number 3", "Right column second paragraph" } : new[] { "Quantity", "Total of all the items", "Prices include tax." })
                {
                    Assert.Contains(phrase, text, StringComparison.Ordinal);
                }
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
