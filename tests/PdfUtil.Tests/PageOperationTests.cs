using System.Text;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Objects;
using PdfUtil.Tests.Infrastructure;
using static PdfUtil.Tests.InspectionTests;

namespace PdfUtil.Tests;

/// <summary>F13 rotate and delete, F14 merge and split, and the page-range syntax.</summary>
public class PageOperationTests
{
    /// <summary>The decoded content of each page, in order.</summary>
    internal static List<string> PageTexts(byte[] pdf, string? password = null)
    {
        using var doc = TestHelpers.Open(pdf);
        if (doc.IsEncrypted)
        {
            Assert.NotEqual(PdfPasswordMatch.None, doc.Authenticate(password));
        }

        return PageTreeReader.ReadPageNodes(doc, 64, CancellationToken.None).Select(p =>
        {
            var contents = doc.Resolve(p.Page["Contents"]);
            var items = contents is PdfArray a ? a.Items : new List<PdfObject> { p.Page["Contents"]! };
            return string.Concat(items.OfType<PdfReference>().Select(r =>
            {
                var s = (PdfStream)doc.GetObject(r.Id.Number)!;
                return Encoding.ASCII.GetString(doc.GetDecodedStreamData(s, new ObjectId(r.Id.Number, doc.GetGeneration(r.Id.Number)))!);
            }));
        }).ToList();
    }

    private static void AssertValid(byte[] pdf, string? password = null)
    {
        var report = Pdf.Validate(pdf, new PdfValidationOptions { Password = password });
        Assert.True(report.IsValid, string.Join("\n", report.Issues));
        Assert.DoesNotContain(report.Issues, i => i.Code == PdfValidationCodes.DanglingReference);
    }

    private static byte[] Pages(int count) => new TestPdfBuilder { PageCount = count }.Build();

    // ------------------------------------------------------------------ page ranges

    [Theory]
    [InlineData("1-3,5,8-", 10, "1,2,3,5,8,9,10")]
    [InlineData(" 2 , -3 ", 5, "2,1,3")]
    [InlineData("odd", 5, "1,3,5")]
    [InlineData("even,last", 5, "2,4,5")]
    [InlineData("3-3,3", 4, "3")]
    [InlineData("1-", 1, "1")]
    public void PageRange_Resolves(string text, int pages, string expected) =>
        Assert.Equal(expected, string.Join(",", PdfPageRange.Parse(text).Resolve(pages)));

    [Theory]
    [InlineData("")]
    [InlineData("1,,2")]
    [InlineData("0")]
    [InlineData("3-1")]
    [InlineData("a-b")]
    [InlineData("-")]
    [InlineData("1.5")]
    public void PageRange_RejectsBadSyntax(string text)
    {
        Assert.Throws<FormatException>(() => PdfPageRange.Parse(text));
        Assert.False(PdfPageRange.TryParse(text, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void PageRange_BeyondTheLastPage() =>
        Assert.Throws<ArgumentException>(() => PdfPageRange.Parse("2-5").Resolve(3));

    // ------------------------------------------------------------------ rotate

    [Fact]
    public void Rotate_AddsToTheCurrentRotation()
    {
        var input = Pages(3);
        var output = Pdf.RotatePages(input, new PdfRotatePagesOptions { Angle = 90 }, out var result);
        Assert.Equal(new[] { 90, 180, 90 }, Pdf.GetInfo(output).Pages.Select(p => p.Rotate)); // page 2 was already 90
        Assert.Equal(3, result.PageCount);
        Assert.Equal(PageTexts(input), PageTexts(output));
        AssertValid(output);

        var back = Pdf.RotatePages(output, new PdfRotatePagesOptions { Angle = -270, Pages = PdfPageRange.Parse("2") }, out _);
        Assert.Equal(new[] { 90, 270, 90 }, Pdf.GetInfo(back).Pages.Select(p => p.Rotate));
    }

    [Fact]
    public void Rotate_InheritedRotation()
    {
        var pdf = RawPdf.Build(new Dictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R >>",
            [2] = "<< /Type /Pages /Kids [3 0 R] /Count 2 /Rotate 180 /MediaBox [0 0 200 300] >>",
            [3] = "<< /Type /Pages /Parent 2 0 R /Kids [4 0 R 5 0 R] /Count 2 /Rotate 270 >>",
            [4] = "<< /Type /Page /Parent 3 0 R >>",
            [5] = "<< /Type /Page /Parent 3 0 R /Rotate 0 >>",
        });
        var output = Pdf.RotatePages(pdf, new PdfRotatePagesOptions { Angle = 180 }, out _);
        Assert.Equal(new[] { 90, 180 }, Pdf.GetInfo(output).Pages.Select(p => p.Rotate));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void Rotate_ProtectedKeepsSecurity(int revision)
    {
        var output = Pdf.RotatePages(DecryptTests.Encrypted(revision), new PdfRotatePagesOptions { Password = "o-pass" }, out var result);
        Assert.Equal(revision, result.Revision);
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, "u-pass").MatchedAs);
        Assert.Equal(new[] { 90, 180 }, Pdf.GetInfo(output, "u-pass").Pages.Select(p => p.Rotate));
    }

    [Fact]
    public void Rotate_InvalidAngle() =>
        Assert.Throws<ArgumentException>(() => Pdf.RotatePages(Pages(1), new PdfRotatePagesOptions { Angle = 45 }, out _));

    // ------------------------------------------------------------------ delete

    [Fact]
    public void Delete_KeepsTheOtherPagesInOrder()
    {
        var input = Pages(6);
        var output = Pdf.DeletePages(input, new PdfDeletePagesOptions { Pages = PdfPageRange.Parse("2-3,5") }, out var result);
        Assert.Equal(6, result.SourcePageCount);
        Assert.Equal(3, result.PageCount);
        var texts = PageTexts(output);
        Assert.Contains("Hello page 1 ", texts[0], StringComparison.Ordinal);
        Assert.Contains("Hello page 4 ", texts[1], StringComparison.Ordinal);
        Assert.Contains("Hello page 6 ", texts[2], StringComparison.Ordinal);
        Assert.Contains("Page 5 was deleted.", result.Removed);
        AssertValid(output);
    }

    [Fact]
    public void Delete_RemovesEverythingThatPointedAtDeletedPages()
    {
        var input = new TestPdfBuilder { Rich = true, InternalLinks = true }.Build();
        var output = Pdf.DeletePages(input, new PdfDeletePagesOptions { Pages = PdfPageRange.Parse("2") }, out var result);
        Assert.Equal(1, result.PageCount);
        AssertValid(output);
        var removed = string.Join("\n", result.Removed);
        Assert.Contains("The link on page 1 to deleted page 2 was removed.", result.Removed);
        Assert.Contains("The bookmark \"Section 1.1\" went to deleted page 2", removed, StringComparison.Ordinal);
        Assert.Contains("The bookmark \"Section 1.2 é\" went to deleted page 2", removed, StringComparison.Ordinal);
        Assert.Contains("named destination(s) went to deleted pages", removed, StringComparison.Ordinal);
        Assert.Contains("The form field \"country\" was only on deleted pages and was removed.", result.Removed);

        var info = Pdf.Inspect(output);
        Assert.Equal(new[] { "agree", "choice", "name" }, info.FormFields.Select(f => f.FullName).Order());
        Assert.Equal(new[] { 1 }, info.FormFields.Single(f => f.FullName == "choice").Pages);
        Assert.Null(info.Bookmarks[0].Children[0].Page);
        Assert.Equal(1, info.Bookmarks[0].Page);
        Assert.Equal(new[] { "i" }, info.PageLabels);
        Assert.Equal(new[] { "a.txt", "b.txt", "data.bin" }, info.Attachments.Select(a => a.Name)); // note.txt was on page 2

        // Deleting the first page instead shifts the page labels.
        var first = Pdf.DeletePages(input, new PdfDeletePagesOptions { Pages = PdfPageRange.Parse("1") }, out var firstResult);
        Assert.Equal(new[] { "A-5" }, Pdf.Inspect(first).PageLabels);
        Assert.Contains(firstResult.Removed, r => r.StartsWith("The open action", StringComparison.Ordinal) || r.Contains("link", StringComparison.Ordinal));
        AssertValid(first);
    }

    [Fact]
    public void Delete_NestedTree_ResolvesInheritedAttributes()
    {
        var pdf = RawPdf.Build(new Dictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R >>",
            [2] = "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 3 /MediaBox [0 0 200 300] /Resources << /Font << /F1 9 0 R >> >> >>",
            [3] = "<< /Type /Pages /Parent 2 0 R /Kids [4 0 R 5 0 R] /Count 2 /Rotate 90 /CropBox [10 10 190 290] >>",
            [4] = "<< /Type /Page /Parent 3 0 R /Contents 7 0 R >>",
            [5] = "<< /Type /Page /Parent 3 0 R /Contents 8 0 R >>",
            [6] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] >>",
            [7] = RawPdf.Stream("", "BT /F1 9 Tf (A) Tj ET"),
            [8] = RawPdf.Stream("", "BT /F1 9 Tf (B) Tj ET"),
            [9] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        });
        var before = Pdf.GetInfo(pdf).Pages;
        var output = Pdf.DeletePages(pdf, new PdfDeletePagesOptions { Pages = PdfPageRange.Parse("1") }, out _);
        var after = Pdf.GetInfo(output).Pages;
        Assert.Equal(2, after.Count);
        Assert.Equal(before[1].MediaBox, after[0].MediaBox);
        Assert.Equal(before[1].CropBox, after[0].CropBox);
        Assert.Equal(90, after[0].Rotate);
        Assert.Equal(before[2].MediaBox, after[1].MediaBox);
        Assert.Equal("Helvetica", Assert.Single(Pdf.Inspect(output).Fonts).Name);
    }

    [Fact]
    public void Delete_Protected_AndEveryPageRefused()
    {
        var output = Pdf.DeletePages(DecryptTests.Encrypted(6), new PdfDeletePagesOptions { Pages = PdfPageRange.Parse("1"), Password = "o-pass" }, out var result);
        Assert.True(result.IsEncrypted);
        Assert.Equal(1, Pdf.GetInfo(output, "u-pass").PageCount);
        Assert.Throws<ArgumentException>(() => Pdf.DeletePages(Pages(2), new PdfDeletePagesOptions { Pages = PdfPageRange.Parse("1-2") }, out _));
        Assert.Throws<ArgumentException>(() => Pdf.DeletePages(Pages(2), new PdfDeletePagesOptions(), out _));
    }

    // ------------------------------------------------------------------ merge

    [Fact]
    public void Merge_PagesInOrder_WithPerDocumentBookmarks()
    {
        var a = new TestPdfBuilder { Rich = true, InternalLinks = true }.Build();
        var b = Pages(3);
        var output = Pdf.Merge(new[] { PdfMergeInput.FromBytes(a), PdfMergeInput.FromBytes(b) }, null, out var result);
        Assert.Equal(5, result.PageCount);
        Assert.Equal(new[] { 2, 3 }, result.PagesPerInput);
        Assert.Equal(PageTexts(a).Concat(PageTexts(b)), PageTexts(output));
        AssertValid(output);

        var info = Pdf.Inspect(output);
        Assert.Equal(new[] { "Test document", "Test document" }, info.Bookmarks.Select(x => x.Title));
        Assert.Equal(new int?[] { 1, 3 }, info.Bookmarks.Select(x => x.Page));
        var chapters = info.Bookmarks[0].Children;
        Assert.Equal(new[] { "Chapter 1", "Website" }, chapters.Select(c => c.Title));
        Assert.Equal(2, chapters[0].Children[0].Page); // the named destination still resolves
        Assert.Equal("Jane Doe", info.FormFields.Single(f => f.FullName == "name").Value);
        Assert.Equal(TestPdfBuilder.AttachmentA, Pdf.ExtractAttachment(output, "a.txt"));

        // Links still go to the right pages.
        using var doc = TestHelpers.Open(output);
        var pages = PageTreeReader.ReadPageNodes(doc, 64, CancellationToken.None);
        var link = ((PdfArray)doc.Resolve(pages[0].Page["Annots"])!).Items.Select(doc.ResolveDictionary).Single(x => x!["Dest"] is PdfArray)!;
        Assert.Equal(pages[1].ObjectNumber, ((PdfReference)((PdfArray)link["Dest"]!)[0]).Id.Number);
    }

    [Fact]
    public void Merge_SameDocumentTwice_RenamesConflicts()
    {
        var a = new TestPdfBuilder { Rich = true, InternalLinks = true }.Build();
        var output = Pdf.Merge(new[] { PdfMergeInput.FromBytes(a), PdfMergeInput.FromBytes(a) }, new PdfMergeOptions { Outlines = PdfMergeOutlineMode.Flat }, out var result);
        Assert.Equal(4, result.PageCount);
        Assert.Contains("The form field \"name\" of input 2 was renamed to \"name_2\".", result.Renamed);
        Assert.Contains("The named destination \"sec11\" of input 2 was renamed to \"sec11_2\".", result.Renamed);
        Assert.Contains("The attachment \"a.txt\" of input 2 was renamed to \"a.txt_2\".", result.Renamed);

        var info = Pdf.Inspect(output);
        Assert.Equal(16, info.FormFields.Count);
        Assert.Equal("Oslo", info.FormFields.Single(f => f.FullName == "address_2.city").Value);
        Assert.Equal(new[] { 1, 2 }, info.FormFields.Single(f => f.FullName == "choice").Pages);
        Assert.Equal(new[] { 3, 4 }, info.FormFields.Single(f => f.FullName == "choice_2").Pages);
        Assert.Equal(4, info.Bookmarks.Count); // flat
        Assert.Equal(4, info.Bookmarks[2].Children[0].Page); // input 2's "sec11_2"
        AssertValid(output);

        // The link to the named destination "top" in input 2 goes to input 2's first page.
        using var doc = TestHelpers.Open(output);
        var page4 = PageTreeReader.ReadPageNodes(doc, 64, CancellationToken.None)[3].Page;
        var named = ((PdfArray)doc.Resolve(page4["Annots"])!).Items.Select(doc.ResolveDictionary).Single(x => x!["Dest"] is PdfString)!;
        Assert.Equal("top_2", Encoding.Latin1.GetString(((PdfString)named["Dest"]!).Value));
    }

    [Fact]
    public void Merge_PageSelection_DropsLinksToMissingPages()
    {
        var a = new TestPdfBuilder { Rich = true, InternalLinks = true }.Build();
        var output = Pdf.Merge(new[] { PdfMergeInput.FromBytes(a, pages: PdfPageRange.Parse("1")), PdfMergeInput.FromBytes(Pages(1)) }, new PdfMergeOptions { Outlines = PdfMergeOutlineMode.None }, out var result);
        Assert.Equal(2, result.PageCount);
        Assert.Contains(result.Removed, r => r.StartsWith("A link on page 1 of input 1", StringComparison.Ordinal));
        var info = Pdf.Inspect(output);
        Assert.Empty(info.Bookmarks);
        Assert.Equal(new[] { "agree", "choice", "name" }, info.FormFields.Select(f => f.FullName).Order());
        AssertValid(output);
    }

    [Fact]
    public void Merge_ProtectedInputs_AndProtectedOutput()
    {
        var encrypted = DecryptTests.Encrypted(6);
        Assert.Throws<PdfInvalidPasswordException>(() => Pdf.Merge(new[] { PdfMergeInput.FromBytes(encrypted, "u-pass") }, null, out _));
        Assert.Throws<PdfInvalidPasswordException>(() => Pdf.Merge(new[] { PdfMergeInput.FromBytes(encrypted) }, null, out _));
        var withUser = Pdf.Merge(new[] { PdfMergeInput.FromBytes(encrypted, "u-pass") }, new PdfMergeOptions { AllowUserPassword = true }, out _);
        Assert.False(Pdf.IsEncrypted(withUser));

        var output = Pdf.Merge(
            new[] { PdfMergeInput.FromBytes(encrypted, "o-pass"), PdfMergeInput.FromBytes(DecryptTests.Encrypted(3), "o-pass") },
            new PdfMergeOptions { Protection = new PdfProtectionOptions { UserPassword = "merged" }, Write = new PdfWriteOptions { UseObjectStreams = true } },
            out var result);
        Assert.True(result.IsEncrypted);
        Assert.Equal(4, Pdf.GetInfo(output, "merged").PageCount);
        CompressedOutputTests.AssertCompressedLayout(output, "merged");
    }

    [Fact]
    public void Merge_Version_IsTheHighest()
    {
        var output = Pdf.Merge(new[] { PdfMergeInput.FromBytes(new TestPdfBuilder { Version = "1.3" }.Build()), PdfMergeInput.FromBytes(new TestPdfBuilder { Version = "2.0" }.Build()) }, null, out var result);
        Assert.Equal("2.0", result.OutputVersion);
        Assert.Equal("%PDF-2.0", TestHelpers.HeaderOf(output));
    }

    [Fact]
    public void Merge_Deduplication()
    {
        var a = new TestPdfBuilder { Rich = true }.Build();
        var plain = Pdf.Merge(new[] { PdfMergeInput.FromBytes(a), PdfMergeInput.FromBytes(a) }, null, out _);
        var dedup = Pdf.Merge(new[] { PdfMergeInput.FromBytes(a), PdfMergeInput.FromBytes(a) }, new PdfMergeOptions { DeduplicateResources = true }, out var result);
        Assert.True(result.DeduplicatedObjects > 5);
        Assert.True(dedup.Length < plain.Length);
        Assert.Equal(PageTexts(plain), PageTexts(dedup));
        AssertValid(dedup);
    }

    [Theory]
    [InlineData(XrefMode.XrefStreamWithObjectStreams)]
    [InlineData(XrefMode.Hybrid)]
    public void Merge_EveryStructure(XrefMode mode)
    {
        var a = new TestPdfBuilder { Rich = true, Mode = mode }.Build();
        var output = Pdf.Merge(new[] { PdfMergeInput.FromBytes(a), PdfMergeInput.FromBytes(new TestPdfBuilder { IncrementalUpdate = true }.Build()) }, null, out var result);
        Assert.Equal(4, result.PageCount);
        Assert.Contains("Updated page 1", PageTexts(output)[2], StringComparison.Ordinal);
        AssertValid(output);
    }

    [Fact]
    public async Task Merge_Files()
    {
        var dir = Directory.CreateTempSubdirectory("pdfutil-merge-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "first.pdf"), Pages(2));
            File.WriteAllBytes(Path.Combine(dir, "second.pdf"), Pages(1));
            var result = await new PdfPageEditor().MergeFileAsync(
                new[] { PdfMergeInput.FromFile(Path.Combine(dir, "first.pdf")), PdfMergeInput.FromFile(Path.Combine(dir, "second.pdf")) },
                Path.Combine(dir, "merged.pdf"));
            Assert.Equal(3, result.PageCount);
            Assert.Equal(new[] { "first", "second" }, Pdf.Inspect(Path.Combine(dir, "merged.pdf")).Bookmarks.Select(b => b.Title));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ------------------------------------------------------------------ split

    [Fact]
    public void Split_EveryNPages()
    {
        var input = Pages(5);
        var parts = Pdf.Split(input, new PdfSplitOptions { EveryNPages = 2 }, out var result);
        Assert.Equal(3, parts.Count);
        Assert.Equal(new[] { 2, 2, 1 }, parts.Select(p => Pdf.GetInfo(p).PageCount!.Value));
        Assert.Equal(new[] { 5 }, result.Parts[2].Pages);
        Assert.Equal(PageTexts(input), parts.SelectMany(p => PageTexts(p)));
        Assert.All(parts, p => AssertValid(p));
    }

    [Fact]
    public void Split_Ranges_DropLinksToOtherParts()
    {
        var input = new TestPdfBuilder { Rich = true, InternalLinks = true }.Build();
        var parts = Pdf.Split(input, new PdfSplitOptions { Ranges = new[] { PdfPageRange.Parse("1"), PdfPageRange.Parse("2"), PdfPageRange.Parse("1-2") } }, out var result);
        Assert.Equal(3, parts.Count);
        Assert.Contains(result.Removed, r => r.StartsWith("Part 1: A link on page 1", StringComparison.Ordinal));
        Assert.Contains(result.Removed, r => r.StartsWith("Part 2: A link on page 1", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Removed, r => r.StartsWith("Part 3", StringComparison.Ordinal));
        Assert.Equal(new[] { "agree", "choice", "name" }, Pdf.Inspect(parts[0]).FormFields.Select(f => f.FullName).Order());
        Assert.Equal(8, Pdf.Inspect(parts[2]).FormFields.Count);
        Assert.All(parts, p => AssertValid(p));
    }

    [Fact]
    public void Split_ProtectedParts()
    {
        var parts = Pdf.Split(DecryptTests.Encrypted(4), new PdfSplitOptions { EveryNPages = 1, Password = "o-pass", Protection = new PdfProtectionOptions { UserPassword = "part" } }, out _);
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(p, "part").MatchedAs));
    }

    [Fact]
    public void Split_InvalidOptions()
    {
        var input = Pages(2);
        Assert.Throws<ArgumentException>(() => Pdf.Split(input, new PdfSplitOptions(), out _));
        Assert.Throws<ArgumentException>(() => Pdf.Split(input, new PdfSplitOptions { EveryNPages = 0 }, out _));
        Assert.Throws<ArgumentException>(() => Pdf.Split(input, new PdfSplitOptions { EveryNPages = 1, Ranges = new[] { PdfPageRange.All } }, out _));
        Assert.Throws<ArgumentException>(() => Pdf.Split(input, new PdfSplitOptions { Ranges = new[] { PdfPageRange.Parse("3") } }, out _));
    }

    [Fact]
    public void Split_Files()
    {
        var dir = Directory.CreateTempSubdirectory("pdfutil-split-").FullName;
        try
        {
            var input = Path.Combine(dir, "in.pdf");
            File.WriteAllBytes(input, Pages(3));
            var result = Pdf.Split(input, Path.Combine(dir, "part-{0}.pdf"), new PdfSplitOptions { EveryNPages = 1 });
            Assert.Equal(3, result.Files.Count);
            Assert.All(result.Files, f => Assert.Equal(1, Pdf.GetInfo(f).PageCount));
            Assert.EndsWith("part-3.pdf", result.Files[2], StringComparison.Ordinal);
            Assert.Throws<ArgumentException>(() => Pdf.Split(input, Path.Combine(dir, "part.pdf"), new PdfSplitOptions { EveryNPages = 1 }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
