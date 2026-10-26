using System.Security.Cryptography;
using System.Text;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Objects;
using PdfUtil.Tests.Infrastructure;
using static PdfUtil.Tests.InspectionTests;

namespace PdfUtil.Tests;

/// <summary>F10: attachments.</summary>
public class AttachmentTests
{
    private static readonly byte[] Xml = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><invoice total=\"42.00\"/>");

    private static PdfAddAttachmentOptions Invoice(string name = "invoice.xml") => new()
    {
        Name = name,
        Content = Xml,
        Description = "Machine-readable invoice",
        MimeType = "application/xml",
        CreationDate = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.FromHours(2)),
        ModificationDate = new DateTimeOffset(2024, 6, 2, 12, 0, 0, TimeSpan.Zero),
        Relationship = PdfAttachmentRelationship.Data,
    };

    /// <summary>The keys of the output's /EmbeddedFiles tree, which must be a flat, sorted root.</summary>
    private static List<string> TreeKeys(byte[] pdf, string? password = null)
    {
        using var doc = TestHelpers.Open(pdf, strict: true);
        if (doc.IsEncrypted)
        {
            doc.Authenticate(password);
        }

        var tree = doc.ResolveDictionary(doc.ResolveDictionary(doc.Catalog!["Names"])?["EmbeddedFiles"]);
        if (tree is null)
        {
            return new List<string>();
        }

        Assert.False(tree.ContainsKey("Kids"));
        var keys = ((PdfArray)doc.Resolve(tree["Names"])!).Items.Where((_, i) => i % 2 == 0).Select(k => ((PdfString)k).Value).ToList();
        Assert.Equal(keys.Order(NameTree.KeyComparer), keys);
        return keys.Select(k => Internal.Text.TextString.Decode(k)).ToList();
    }

    [Fact]
    public void Add_WritesAllParameters()
    {
        var output = Pdf.AddAttachment(new TestPdfBuilder().Build(), Invoice(), out var result);
        Assert.False(result.IsEncrypted);

        var attachment = Pdf.ListAttachments(output).Single(a => a.Name == "invoice.xml");
        Assert.Equal("invoice.xml", attachment.FileName);
        Assert.Equal("Machine-readable invoice", attachment.Description);
        Assert.Equal(Xml.Length, attachment.Size);
        Assert.Equal("application/xml", attachment.MimeType);
        Assert.Equal(Convert.ToHexString(MD5.HashData(Xml)), attachment.CheckSum);
        Assert.Equal(new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.FromHours(2)), attachment.CreationDate);
        Assert.Equal(new DateTimeOffset(2024, 6, 2, 12, 0, 0, TimeSpan.Zero), attachment.ModificationDate);
        Assert.Equal("Data", attachment.Relationship);
        Assert.Equal(Xml, Pdf.ExtractAttachment(output, "invoice.xml"));

        Assert.Equal(new[] { "data.bin", "invoice.xml" }, TreeKeys(output));

        // With a relationship, the file is also an associated file of the document; the stream is compressed.
        using var doc = TestHelpers.Open(output, strict: true);
        var af = Assert.IsType<PdfArray>(doc.Catalog!["AF"]);
        var fileSpec = doc.ResolveDictionary(Assert.Single(af.Items))!;
        Assert.Equal("Data", ((PdfName)fileSpec["AFRelationship"]!).Value);
        var stream = (PdfStream)doc.Resolve(doc.ResolveDictionary(fileSpec["EF"])!["F"])!;
        Assert.Equal("FlateDecode", ((PdfName)stream.Dictionary["Filter"]!).Value);
    }

    [Fact]
    public void Add_ToKidsTree_FlattensAndSorts()
    {
        var output = Pdf.AddAttachment(Rich(), Invoice("c.csv"));
        Assert.Equal(new[] { "a.txt", "b.txt", "c.csv", "data.bin" }, TreeKeys(output));

        // Existing attachments still extract, including from the old /Kids leaves and the annotation.
        Assert.Equal(TestPdfBuilder.AttachmentA, Pdf.ExtractAttachment(output, "a.txt"));
        Assert.Equal(TestPdfBuilder.AttachmentB, Pdf.ExtractAttachment(output, "b.txt"));
        Assert.Equal(TestPdfBuilder.AttachmentNote, Pdf.ExtractAttachment(output, "note.txt"));
        Assert.Equal(Enumerable.Range(0, 256).Select(i => (byte)i), Pdf.ExtractAttachment(output, "data.bin"));

        // The existing associated file is kept alongside the new one.
        using var doc = TestHelpers.Open(output, strict: true);
        Assert.Equal(2, ((PdfArray)doc.Catalog!["AF"]!).Count);
    }

    [Fact]
    public void Add_ToDocumentWithoutNames()
    {
        var input = RawPdf.Minimal();
        var output = Pdf.AddAttachment(input, new PdfAddAttachmentOptions { Name = "a.bin", Content = new byte[] { 1, 2, 3 }, Compress = false }, out var result);
        Assert.Equal(new byte[] { 1, 2, 3 }, Pdf.ExtractAttachment(output, "a.bin"));
        Assert.Null(Pdf.ListAttachments(output).Single().Relationship);
        Assert.Equal("1.7", result.OutputVersion);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.2")]
    public void Add_RaisesOldVersionsTo13(string version)
    {
        var output = Pdf.AddAttachment(new TestPdfBuilder { Version = version }.Build(), Invoice(), out var result);
        Assert.Equal("1.3", result.OutputVersion);
        Assert.Equal("%PDF-1.3", TestHelpers.HeaderOf(output));
    }

    [Fact]
    public void Add_UnicodeName()
    {
        const string name = "résumé 履歴書.txt";
        var output = Pdf.AddAttachment(new TestPdfBuilder().Build(), new PdfAddAttachmentOptions { Name = name, Content = Xml });
        Assert.Equal(Xml, Pdf.ExtractAttachment(output, name));
        using var doc = TestHelpers.Open(output, strict: true);
        var fileSpec = Pdf.ListAttachments(output).Single(a => a.Name == name);
        Assert.Equal(name, fileSpec.FileName);
        var dict = (PdfDictionary)doc.GetObject(fileSpec.ObjectNumber!.Value)!;
        Assert.Equal("r_sum_ ___.txt", Encoding.ASCII.GetString(((PdfString)dict["F"]!).Value));
    }

    [Fact]
    public void Add_ExistingName_ThrowsUnlessReplacing()
    {
        var input = Pdf.AddAttachment(new TestPdfBuilder().Build(), Invoice());
        Assert.Throws<ArgumentException>(() => Pdf.AddAttachment(input, Invoice()));

        var replaced = Pdf.AddAttachment(input, new PdfAddAttachmentOptions { Name = "invoice.xml", Content = new byte[] { 9 }, ReplaceExisting = true }, out var result);
        Assert.Equal(new byte[] { 9 }, Pdf.ExtractAttachment(replaced, "invoice.xml"));
        Assert.Equal(new[] { "data.bin", "invoice.xml" }, TreeKeys(replaced));
        Assert.Contains(result.Warnings, w => w.Contains("was replaced", StringComparison.Ordinal));

        // The replaced file is no longer an associated file.
        using var doc = TestHelpers.Open(replaced, strict: true);
        Assert.False(doc.Catalog!.ContainsKey("AF"));
    }

    [Fact]
    public void Add_LargeContent()
    {
        var content = new byte[3 * 1024 * 1024];
        new Random(7).NextBytes(content.AsSpan(0, content.Length / 2));
        var output = Pdf.AddAttachment(new TestPdfBuilder().Build(), new PdfAddAttachmentOptions { Name = "big.bin", Content = content });
        Assert.Equal(content, Pdf.ExtractAttachment(output, "big.bin"));
        Assert.True(output.Length < content.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-slash")]
    [InlineData("text/ plain")]
    public void Add_InvalidOptions(string mime)
    {
        var input = new TestPdfBuilder().Build();
        Assert.Throws<ArgumentException>(() => Pdf.AddAttachment(input, new PdfAddAttachmentOptions { Name = "x", Content = Xml, MimeType = mime }));
        Assert.Throws<ArgumentException>(() => Pdf.AddAttachment(input, new PdfAddAttachmentOptions { Name = "", Content = Xml }));
        Assert.Throws<ArgumentException>(() => Pdf.AddAttachment(input, new PdfAddAttachmentOptions { Name = "x", Content = Xml, Relationship = (PdfAttachmentRelationship)99 }));
    }

    [Fact]
    public void PdfA_Warnings()
    {
        var pdfA2 = new TestPdfBuilder { Xmp = TestPdfBuilder.PdfAXmp(2, "B") }.Build();
        Pdf.AddAttachment(pdfA2, Invoice(), out var a2);
        Assert.Contains(a2.Warnings, w => w.StartsWith("PDF/A-2B does not allow", StringComparison.Ordinal));

        var pdfA3 = new TestPdfBuilder { Xmp = TestPdfBuilder.PdfAXmp(3, "B") }.Build();
        Pdf.AddAttachment(pdfA3, new PdfAddAttachmentOptions { Name = "x.xml", Content = Xml }, out var noRelationship);
        Assert.Contains(noRelationship.Warnings, w => w.StartsWith("PDF/A-3 requires", StringComparison.Ordinal));
        Pdf.AddAttachment(pdfA3, Invoice(), out var conformant);
        Assert.DoesNotContain(conformant.Warnings, w => w.Contains("PDF/A", StringComparison.Ordinal));
    }

    [Fact]
    public void Remove_ByNameOrFileName()
    {
        var input = Rich();
        var output = Pdf.RemoveAttachment(input, "a.txt");
        Assert.Equal(new[] { "b.txt", "data.bin" }, TreeKeys(output));

        // a.txt was also the document's only associated file.
        using (var doc = TestHelpers.Open(output, strict: true))
        {
            Assert.False(doc.Catalog!.ContainsKey("AF"));
        }

        // data.bin's /UF file name is "dätä.bin".
        Assert.Equal(new[] { "b.txt" }, TreeKeys(Pdf.RemoveAttachment(output, "dätä.bin")));
        Assert.Throws<PdfAttachmentNotFoundException>(() => Pdf.RemoveAttachment(input, "missing.txt"));
    }

    [Fact]
    public void Remove_Last_RemovesTheTree()
    {
        var output = Pdf.RemoveAttachment(new TestPdfBuilder().Build(), "data.bin");
        Assert.Empty(Pdf.ListAttachments(output));
        using var doc = TestHelpers.Open(output, strict: true);
        Assert.False(doc.Catalog!.ContainsKey("Names"));
        Assert.DoesNotContain(ObjectGraph.FindReachable(doc, CancellationToken.None, out _), n => doc.GetObject(n) is PdfStream { Dictionary: var d } && d["Type"] is PdfName { Value: "EmbeddedFile" });
    }

    [Fact]
    public void Extract_NotFound_AndExternalFile()
    {
        Assert.Throws<PdfAttachmentNotFoundException>(() => Pdf.ExtractAttachment(Rich(), "nope"));
        var external = RawPdf.Minimal("/Names << /EmbeddedFiles << /Names [(ext.pdf) << /Type /Filespec /F (ext.pdf) >>] >> >>");
        var ex = Assert.Throws<PdfAttachmentNotFoundException>(() => Pdf.ExtractAttachment(external, "ext.pdf"));
        Assert.Contains("external file", ex.Message, StringComparison.Ordinal);
        Assert.False(Pdf.ListAttachments(external).Single().IsEmbedded);
    }

    [Fact]
    public void Extract_UnsupportedFilter_Throws()
    {
        var pdf = RawPdf.Minimal("/Names << /EmbeddedFiles << /Names [(x.bin) << /Type /Filespec /F (x.bin) /EF << /F 4 0 R >> >>] >> >>", new Dictionary<int, string>
        {
            [4] = RawPdf.Stream("/Type /EmbeddedFile /Filter /LZWDecode", "abc"),
        });
        var ex = Assert.Throws<PdfUtilException>(() => Pdf.ExtractAttachment(pdf, "x.bin"));
        Assert.Contains("LZWDecode", ex.Message, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> Revisions() => new[] { 3, 4, 6 }.Select(r => new object[] { r });

    [Theory]
    [MemberData(nameof(Revisions))]
    public void Protected_AddRemoveExtract_KeepTheSecurity(int revision)
    {
        var input = Rich(encryption: (revision, "u-pass", "o-pass", true));
        var added = Pdf.AddAttachment(input, new PdfAddAttachmentOptions { Name = "invoice.xml", Content = Xml, Password = "o-pass" }, out var result);
        Assert.True(result.IsEncrypted);
        Assert.Equal(revision, result.Revision);
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(added, "u-pass").MatchedAs);
        Assert.Equal(Xml, Pdf.ExtractAttachment(added, "invoice.xml", "u-pass"));
        Assert.DoesNotContain(Encoding.Latin1.GetString(added), "invoice total", StringComparison.Ordinal);

        var removed = Pdf.RemoveAttachment(added, "b.txt", "o-pass");
        Assert.Equal(new[] { "a.txt", "data.bin", "invoice.xml" }, TreeKeys(removed, "u-pass"));

        Assert.Throws<PdfInvalidPasswordException>(() => Pdf.AddAttachment(input, new PdfAddAttachmentOptions { Name = "x", Content = Xml, Password = "u-pass" }));
        Assert.Throws<PdfInvalidPasswordException>(() => Pdf.ExtractAttachment(input, "a.txt"));
        Assert.Throws<PdfInvalidPasswordException>(() => Pdf.ExtractAttachment(input, "a.txt", "wrong"));
    }

    [Theory]
    [InlineData(XrefMode.XrefStream)]
    [InlineData(XrefMode.XrefStreamWithObjectStreams)]
    [InlineData(XrefMode.Hybrid)]
    public void EveryStructure(XrefMode mode)
    {
        var output = Pdf.AddAttachment(Rich(mode), Invoice());
        Assert.Equal(new[] { "a.txt", "b.txt", "data.bin", "invoice.xml" }, TreeKeys(output));
        Assert.Equal(Xml, Pdf.ExtractAttachment(output, "invoice.xml"));
        Assert.True(Pdf.Validate(output).IsValid);
    }

    [Fact]
    public async Task FileApi_AndProgress()
    {
        var path = Path.Combine(Path.GetTempPath(), "pdfutil-attach-" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, new TestPdfBuilder().Build());
        try
        {
            var stages = new HashSet<PdfProgressStage>();
            var manager = new PdfAttachmentManager();
            var options = Invoice();
            options.Progress = new RepairTests.SyncProgress(p => stages.Add(p.Stage));
            await manager.AddAttachmentFileAsync(path, path, options);
            Assert.Equal(3, stages.Count);
#if NETFRAMEWORK
            using (var stream = File.OpenRead(path))
#else
            await using (var stream = File.OpenRead(path))
#endif
            {
                Assert.Equal(Xml, await manager.ExtractAttachmentAsync(stream, "invoice.xml"));
            }

            await manager.RemoveAttachmentFileAsync(path, path, new PdfRemoveAttachmentOptions { Name = "invoice.xml" });
            Assert.Equal(new[] { "data.bin" }, Pdf.ListAttachments(path).Select(a => a.Name));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Hostile_KidsCycle()
    {
        var pdf = RawPdf.Minimal("/Names << /EmbeddedFiles 4 0 R >>", new Dictionary<int, string>
        {
            [4] = "<< /Kids [5 0 R 4 0 R] >>",
            [5] = "<< /Kids [4 0 R] /Names [(a) << /Type /Filespec /F (a) >>] >>",
        });
        Assert.Single(Pdf.ListAttachments(pdf));
        var output = Pdf.AddAttachment(pdf, new PdfAddAttachmentOptions { Name = "b", Content = Xml });
        Assert.Equal(new[] { "a", "b" }, TreeKeys(output));
    }
}
