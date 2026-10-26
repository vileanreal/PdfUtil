using PdfUtil.Internal.Document;
using PdfUtil.Internal.Objects;
using PdfUtil.Tests.Infrastructure;
using static PdfUtil.Tests.InspectionTests;

namespace PdfUtil.Tests;

/// <summary>F9: sanitize.</summary>
public class SanitizeTests
{
    /// <summary>Every dictionary reachable in the output (including nested direct ones).</summary>
    private static List<PdfDictionary> AllDictionaries(byte[] pdf, string? password = null)
    {
        using var doc = TestHelpers.Open(pdf, strict: true);
        if (doc.IsEncrypted)
        {
            doc.Authenticate(password);
        }

        var result = new List<PdfDictionary>();
        void Walk(PdfObject? o, int depth)
        {
            switch (o)
            {
                case PdfDictionary d when depth < 64:
                    result.Add(d);
                    foreach (var e in d.Entries)
                    {
                        Walk(e.Value, depth + 1);
                    }

                    break;
                case PdfArray a when depth < 64:
                    foreach (var i in a.Items)
                    {
                        Walk(i, depth + 1);
                    }

                    break;
                case PdfStream s:
                    Walk(s.Dictionary, depth);
                    break;
            }
        }

        foreach (var number in ObjectGraph.FindReachable(doc, CancellationToken.None, out _))
        {
            Walk(doc.GetObject(number), 0);
        }

        return result;
    }

    private static bool IsJavaScript(PdfDictionary d) => d["S"] is PdfName { Value: "JavaScript" } || d.ContainsKey("JS");

    [Fact]
    public void JavaScript_IsRemovedEverywhere_OtherActionsStay()
    {
        var input = Rich();
        var output = Pdf.Sanitize(input, new PdfSanitizeOptions { RemoveJavaScript = true }, out var result);

        var info = Pdf.Inspect(output);
        Assert.False(info.HasJavaScript);
        Assert.Empty(info.JavaScriptNames);
        Assert.DoesNotContain(info.Actions, a => a.Type == "JavaScript");
        Assert.DoesNotContain(AllDictionaries(output), IsJavaScript);

        // Non-script actions are kept.
        var actions = info.Actions.Select(a => (a.Location, a.Type)).ToList();
        Assert.Contains(("Page 1 /AA /C", "URI"), actions);
        Assert.Contains(("Annotation /Link on page 1", "URI"), actions);
        Assert.Contains(("Annotation /Widget on page 2", "SubmitForm"), actions);
        Assert.Contains(("Bookmark \"Website\"", "URI"), actions);

        var removed = result.Removed.Select(r => r.Description).ToList();
        Assert.All(result.Removed, r => Assert.Equal(PdfSanitizeItemKind.JavaScript, r.Kind));
        Assert.Contains("Document script \"init\"", removed);
        Assert.Contains("JavaScript open action", removed);
        Assert.Contains("JavaScript action /AA /WC of the document", removed);
        Assert.Contains("JavaScript action /AA /O of page 1", removed);
        Assert.Contains("JavaScript action of annotation /Link on page 1", removed);
        Assert.Contains("JavaScript action /AA /K of annotation /Widget on page 1", removed);
        Assert.Contains(removed, r => r.StartsWith("JavaScript action of object", StringComparison.Ordinal)); // the bookmark
        Assert.Equal(7, result.Removed.Count);

        // Everything else is untouched.
        Assert.Equal(Pdf.Inspect(input).Attachments.Count, info.Attachments.Count);
        Assert.Equal(Pdf.Inspect(input).FormFields.Count, info.FormFields.Count);
        Assert.Equal("Test document", info.Info!.Title);
    }

    [Fact]
    public void Actions_OpenActionAndEveryAdditionalActionsDictionary()
    {
        var output = Pdf.Sanitize(Rich(), new PdfSanitizeOptions { RemoveJavaScript = false, RemoveActions = true }, out var result);
        var dictionaries = AllDictionaries(output);
        Assert.DoesNotContain(dictionaries, d => d.ContainsKey("AA") || d.ContainsKey("OpenAction"));
        Assert.Equal(1, result.Count(PdfSanitizeItemKind.OpenAction));
        Assert.Equal(3, result.Count(PdfSanitizeItemKind.AdditionalActions)); // document, page 1, the text field
        Assert.Contains(result.Removed, r => r.Description == "Open action (JavaScript)");

        // /A actions are not additional actions: the link and the bookmarks keep theirs.
        Assert.Contains(Pdf.Inspect(output).Actions, a => a.Location == "Annotation /Link on page 1");
    }

    [Fact]
    public void Attachments_TreeAnnotationsAndAssociatedFiles()
    {
        var output = Pdf.Sanitize(Rich(), new PdfSanitizeOptions { RemoveJavaScript = false, RemoveAttachments = true }, out var result);
        Assert.Empty(Pdf.ListAttachments(output));
        Assert.DoesNotContain(AllDictionaries(output), d => d.ContainsKey("AF") || d.ContainsKey("EmbeddedFiles") || d["Type"] is PdfName { Value: "EmbeddedFile" });
        var removed = result.Removed.Select(r => r.Description).ToList();
        Assert.Contains("Embedded file \"a.txt\"", removed);
        Assert.Contains("Embedded file \"data.bin\"", removed);
        Assert.Contains("Annotation /FileAttachment on page 2", removed);
        Assert.Contains("Associated files of the document (/AF)", removed);
        Assert.All(result.Removed, r => Assert.Equal(PdfSanitizeItemKind.Attachment, r.Kind));

        // Other annotations stay.
        Assert.Contains(Pdf.Inspect(output).Actions, a => a.Location == "Annotation /Link on page 1");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Annotations_OptionallyKeepingLinks(bool keepLinks)
    {
        var output = Pdf.Sanitize(Rich(), new PdfSanitizeOptions { RemoveJavaScript = false, RemoveAnnotations = true, KeepLinks = keepLinks }, out var result);
        using var doc = TestHelpers.Open(output);
        var subtypes = PageTreeReader.ReadPageNodes(doc, 64, CancellationToken.None)
            .SelectMany(p => doc.Resolve(p.Page["Annots"]) is PdfArray a ? a.Items : new List<PdfObject>())
            .Select(a => (doc.ResolveDictionary(a)!["Subtype"] as PdfName)!.Value)
            .ToList();
        Assert.Equal(keepLinks ? new[] { "Link" } : Array.Empty<string>(), subtypes);
        Assert.Empty(Pdf.Inspect(output).FormFields);
        Assert.Equal(1, result.Count(PdfSanitizeItemKind.Form));
        Assert.Contains(result.Removed, r => r.Description == "Annotation /Text on page 1");
        Assert.Contains(result.Removed, r => r.Description == "Annotation /FileAttachment on page 2" && r.Kind == PdfSanitizeItemKind.Annotation);
    }

    [Fact]
    public void Metadata_InfoAndXmp()
    {
        var input = new TestPdfBuilder { Xmp = TestPdfBuilder.PdfAXmp(2, "B") }.Build();
        var output = Pdf.Sanitize(input, new PdfSanitizeOptions { RemoveJavaScript = false, RemoveDocumentInfo = true, RemoveXmp = true }, out var result);
        var info = Pdf.GetInfo(output);
        Assert.Null(info.Info);
        Assert.Null(info.Xmp);
        Assert.Equal(1, result.Count(PdfSanitizeItemKind.DocumentInfo));
        Assert.Contains(result.Removed, r => r.Description == "Document XMP metadata");
        Assert.Contains(result.Warnings, w => w.Contains("PDF/A-2B", StringComparison.Ordinal));
    }

    [Fact]
    public void All_LeavesAValidDocumentWithTheSamePages()
    {
        var input = Rich();
        var output = Pdf.Sanitize(input, PdfSanitizeOptions.All(), out var result);
        var info = Pdf.Inspect(output);
        Assert.Equal(2, info.PageCount);
        Assert.Null(info.Info);
        Assert.False(info.HasJavaScript);
        Assert.Empty(info.Attachments);
        Assert.Empty(info.FormFields);
        Assert.True(Pdf.Validate(output).IsValid);
        Assert.Contains(result.Removed, r => r.Kind == PdfSanitizeItemKind.OpenAction);

        // Page content is untouched.
        ContentPreservationTests.AssertStreamsPreserved(output, null, output, null);
        using var src = TestHelpers.Open(input);
        using var dst = TestHelpers.Open(output, strict: true);
        var srcPage = PageTreeReader.ReadPageNodes(src, 64, CancellationToken.None)[0].Page;
        var dstPage = PageTreeReader.ReadPageNodes(dst, 64, CancellationToken.None)[0].Page;
        var contents = ((PdfReference)srcPage["Contents"]!).Id.Number;
        Assert.Equal(src.GetStreamData((PdfStream)src.GetObject(contents)!, new ObjectId(contents, 0)), dst.GetStreamData((PdfStream)dst.Resolve(dstPage["Contents"])!, new ObjectId(contents, 0)));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public void Protected_KeepsExactlyTheSameSecurity(int revision)
    {
        var input = Rich(encryption: (revision, "u-pass", "o-pass", true));
        var output = Pdf.Sanitize(input, new PdfSanitizeOptions { Password = "o-pass", RemoveAttachments = true }, out var result);
        Assert.True(result.IsEncrypted);
        Assert.Equal(revision, result.Revision);
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, "u-pass").MatchedAs);
        var info = Pdf.Inspect(output, new PdfInspectionOptions { Password = "u-pass" });
        Assert.False(info.HasJavaScript);
        Assert.Empty(info.Attachments);
        Assert.Throws<PdfInvalidPasswordException>(() => Pdf.Sanitize(input, new PdfSanitizeOptions { Password = "u-pass" }, out _));
    }

    [Theory]
    [InlineData(XrefMode.XrefStream)]
    [InlineData(XrefMode.XrefStreamWithObjectStreams)]
    [InlineData(XrefMode.Hybrid)]
    public void EveryStructure(XrefMode mode)
    {
        var output = Pdf.Sanitize(Rich(mode), PdfSanitizeOptions.All(), out var result);
        Assert.False(Pdf.Inspect(output).HasJavaScript);
        Pdf.Sanitize(Rich(), PdfSanitizeOptions.All(), out var classic);
        Assert.Equal(classic.Removed.Select(r => (r.Kind, r.Description)), result.Removed.Select(r => (r.Kind, r.Description)));
    }

    [Fact]
    public void NothingSelected_Throws() =>
        Assert.Throws<ArgumentException>(() => Pdf.Sanitize(Rich(), new PdfSanitizeOptions { RemoveJavaScript = false }, out _));

    [Fact]
    public void DocumentWithoutAnything_ReportsNothing()
    {
        var output = Pdf.Sanitize(new TestPdfBuilder().Build(), new PdfSanitizeOptions(), out var result);
        Assert.Empty(result.Removed);
        Assert.Equal(2, Pdf.GetInfo(output).PageCount);
    }

    [Fact]
    public void Hostile_ActionChainCycle()
    {
        var pdf = RawPdf.Minimal("/OpenAction 4 0 R", new Dictionary<int, string>
        {
            [4] = "<< /S /URI /URI (https://x/) /Next 5 0 R >>",
            [5] = "<< /S /URI /URI (https://y/) /Next 4 0 R >>",
        });
        var output = Pdf.Sanitize(pdf, new PdfSanitizeOptions(), out var result);
        Assert.Empty(result.Removed);
        Assert.Equal(1, Pdf.GetInfo(output).PageCount);
    }
}
