using PdfUtil.Internal.Objects;
using PdfUtil.Tests.Infrastructure;
using static PdfUtil.Tests.DecryptTests;

namespace PdfUtil.Tests;

/// <summary>F8: repair and normalize.</summary>
public class RepairTests
{
    private static (byte[] Output, PdfRepairResult Result) Repair(TestPdfBuilder builder, PdfRepairOptions? options = null)
    {
        var output = Pdf.Repair(builder.Build(), options, out var result);
        return (output, result);
    }

    /// <summary>The repaired output validates without errors and keeps the pages and their text.</summary>
    private static void AssertClean(byte[] output, string? password = null)
    {
        var report = Pdf.Validate(output, new PdfValidationOptions { Password = password });
        Assert.True(report.IsValid, string.Join("\n", report.Issues));
        Assert.DoesNotContain(report.Issues, i => i.Severity != PdfValidationSeverity.Info);
        using var doc = TestHelpers.Open(output, strict: true);
        Assert.Equal(1, doc.XrefSectionCount);
        Assert.False(doc.HasXrefStreams);
    }

    private static readonly Dictionary<string, (Func<TestPdfBuilder> Builder, string Repair)> DamageCases = new()
    {
        ["offsets"] = (() => new TestPdfBuilder { BreakXrefOffsets = true }, "Corrected the cross-reference offsets of"),
        ["startxref"] = (() => new TestPdfBuilder { BreakStartXref = true }, "Rebuilt the damaged cross-reference information"),
        ["length"] = (() => new TestPdfBuilder { WrongStreamLength = true }, "Corrected the /Length of 1 stream(s)."),
        ["junk"] = (() => new TestPdfBuilder { JunkBeforeHeader = 64 }, "Removed data that preceded the %PDF- header."),
        ["count"] = (() => new TestPdfBuilder { WrongPageCount = true }, "Corrected the /Count of 1 page tree node(s)."),
        ["duplicate"] = (() => new TestPdfBuilder { DuplicateKid = true }, "Removed 1 repeated or circular page tree reference(s)."),
        ["mediabox"] = (() => new TestPdfBuilder { NoMediaBox = true }, "Gave pages without a /MediaBox the US Letter size"),
        ["dangling"] = (() => new TestPdfBuilder { DanglingReference = true }, "Removed 1 reference(s) to objects that do not exist."),
    };

    public static IEnumerable<object[]> Damage() => DamageCases.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Damage))]
    public void Damage_IsRepairedAndReported(string name)
    {
        var (builder, expectedRepair) = (DamageCases[name].Builder(), DamageCases[name].Repair);
        var input = builder.Build();
        var output = Pdf.Repair(input, null, out var result);
        Assert.True(result.WasDamaged);
        Assert.Contains(result.Repairs, r => r.StartsWith(expectedRepair, StringComparison.Ordinal));
        Assert.Equal(2, result.PageCount);
        Assert.Equal(2, Pdf.GetInfo(output).PageCount);
        AssertClean(output);

        // The content is untouched: every page's text is still there.
        using var doc = TestHelpers.Open(output, strict: true);
        var text = string.Concat(Internal.Document.PageTreeReader.ReadPageNodes(doc, 64, CancellationToken.None)
            .Select(p => (PdfStream)doc.Resolve(p.Page["Contents"])!)
            .Select(s => System.Text.Encoding.ASCII.GetString(doc.GetDecodedStreamData(s, new ObjectId(0, 0))!)));
        Assert.Contains("Hello page 1", text, StringComparison.Ordinal);
        Assert.Contains("Hello page 2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanDocument_IsOnlyNormalized()
    {
        var (output, result) = Repair(new TestPdfBuilder());
        Assert.False(result.WasDamaged);
        Assert.Empty(result.Repairs);
        Assert.False(result.PagesMayBeMissing);
        Assert.Equal(2, result.DeclaredPageCount);
        AssertClean(output);
    }

    [Fact]
    public void Normalization_IsReported()
    {
        Assert.Contains(Repair(new TestPdfBuilder { IncrementalUpdate = true }).Result.Repairs, r => r == "Merged 1 incremental update(s) into a single revision.");
        Assert.Contains(Repair(new TestPdfBuilder { Mode = XrefMode.XrefStream }).Result.Repairs, r => r.StartsWith("Rewrote the cross-reference stream", StringComparison.Ordinal));
        Assert.Contains(Repair(new TestPdfBuilder { Mode = XrefMode.XrefStreamWithObjectStreams }).Result.Repairs, r => r.StartsWith("Rewrote object streams", StringComparison.Ordinal));
        var linearized = Repair(new TestPdfBuilder { Linearized = true }).Result;
        Assert.Contains(linearized.Repairs, r => r.StartsWith("Removed linearization", StringComparison.Ordinal));
        Assert.False(linearized.WasDamaged);
    }

    [Fact]
    public void IncrementalUpdate_KeepsTheLatestRevision()
    {
        var (output, _) = Repair(new TestPdfBuilder { IncrementalUpdate = true });
        Assert.Equal("Updated title", Pdf.GetInfo(output).Info!.Title);
        Assert.Equal(0, Pdf.GetInfo(output).IncrementalUpdateCount);
    }

    [Fact]
    public void MissingPages_AreReportedNeverSilent()
    {
        var count = Repair(new TestPdfBuilder { WrongPageCount = true }).Result;
        Assert.Equal(5, count.DeclaredPageCount);
        Assert.Equal(2, count.PageCount);
        Assert.True(count.PagesMayBeMissing);
        Assert.Contains(count.Warnings, w => w.Contains("declares 5 pages, but only 2 could be read", StringComparison.Ordinal));

        var orphan = Repair(new TestPdfBuilder { OrphanPage = true }).Result;
        Assert.Equal(1, orphan.OrphanedPageCount);
        Assert.True(orphan.PagesMayBeMissing);
        Assert.Contains(orphan.Warnings, w => w.StartsWith("1 page object(s) in the file are not part of the page tree", StringComparison.Ordinal));
        Assert.Contains(orphan.Repairs, r => r.StartsWith("Dropped 1 unused object(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void CorruptStreams_AreCopiedUnchanged()
    {
        var input = new TestPdfBuilder { CorruptFlate = true }.Build();
        var output = Pdf.Repair(input, null, out var result);
        Assert.Contains(result.Repairs, r => r.StartsWith("Dropped 1 unused object(s)", StringComparison.Ordinal)); // the unreferenced garbage stream
        ContentPreservationTests.AssertStreamsPreserved(input, null, output, null);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public void Protected_KeepsTheSameSecurity(int revision)
    {
        var input = new TestPdfBuilder { Version = revision >= 5 ? "1.7" : "1.4", Encryption = (revision, "u-pass", "o-pass", true), BreakXrefOffsets = true }.Build();
        var output = Pdf.Repair(input, new PdfRepairOptions { Password = "o-pass" }, out var result);
        Assert.True(result.IsEncrypted);
        Assert.Equal(revision, result.Revision);
        Assert.True(result.WasDamaged);
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, "u-pass").MatchedAs);
        Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(output, "o-pass").MatchedAs);
        AssertClean(output, "u-pass");
    }

    [Fact]
    public void Protected_RemoveProtection()
    {
        var output = Pdf.Repair(Encrypted(2), new PdfRepairOptions { Password = "o-pass", RemoveProtection = true }, out var result);
        Assert.False(result.IsEncrypted);
        Assert.False(Pdf.IsEncrypted(output));
        AssertClean(output);
    }

    [Fact]
    public void Protected_NeedsTheOwnerPassword()
    {
        Assert.Throws<PdfInvalidPasswordException>(() => Pdf.Repair(Encrypted(6), new PdfRepairOptions { Password = "u-pass" }, out _));
        Assert.Throws<PdfInvalidPasswordException>(() => Pdf.Repair(Encrypted(6), null, out _));
        Assert.Throws<PdfInvalidPasswordException>(() => Pdf.Repair(Encrypted(6), new PdfRepairOptions { Password = "u-pass", RemoveProtection = true }, out _));

        // Revisions PdfUtil does not write cannot be kept, but can be removed.
        Assert.Throws<PdfUnsupportedEncryptionException>(() => Pdf.Repair(Encrypted(5), new PdfRepairOptions { Password = "o-pass" }, out _));
    }

    [Theory]
    [InlineData(XrefMode.XrefStream)]
    [InlineData(XrefMode.XrefStreamWithObjectStreams)]
    [InlineData(XrefMode.Hybrid)]
    public void RichContent_SurvivesRepair(XrefMode mode)
    {
        var input = new TestPdfBuilder { Rich = true, Mode = mode, Xmp = TestPdfBuilder.PlainXmp() }.Build();
        var output = Pdf.Repair(input, null, out _);
        var before = Pdf.Inspect(input);
        var after = Pdf.Inspect(output);
        Assert.Equal(before.Attachments.Select(a => a.ToString()), after.Attachments.Select(a => a.ToString()));
        Assert.Equal(before.FormFields.Select(f => f.ToString()), after.FormFields.Select(f => f.ToString()));
        Assert.Equal(before.Bookmarks.Count, after.Bookmarks.Count);
        Assert.Equal(before.Actions, after.Actions);
        AssertClean(output);
    }

    [Fact]
    public async Task FileApi_SamePathIsAtomic()
    {
        var path = Path.Combine(Path.GetTempPath(), "pdfutil-repair-" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, new TestPdfBuilder { BreakXrefOffsets = true }.Build());
        try
        {
            var result = await new PdfCleaner().RepairFileAsync(path, path);
            Assert.True(result.WasDamaged);
            AssertClean(await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".*.tmp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Progress_AndCancellation()
    {
        var stages = new HashSet<PdfProgressStage>();
        using (var input = new MemoryStream(new TestPdfBuilder().Build()))
        using (var output = new MemoryStream())
        {
            await new PdfCleaner().RepairAsync(input, output, new PdfRepairOptions { Progress = new SyncProgress(p => stages.Add(p.Stage)) });
        }

        Assert.Equal(3, stages.Count);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var cancelled = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PdfCleaner().RepairAsync(new MemoryStream(new TestPdfBuilder().Build()), cancelled, null, cts.Token));
        Assert.Equal(0, cancelled.Length);
    }

    [Fact]
    public void NotAPdf_Throws() =>
        Assert.Throws<PdfCorruptException>(() => Pdf.Repair(System.Text.Encoding.ASCII.GetBytes("not a pdf"), null, out _));

    internal sealed class SyncProgress : IProgress<PdfProgress>
    {
        private readonly Action<PdfProgress> _action;

        public SyncProgress(Action<PdfProgress> action) => _action = action;

        public void Report(PdfProgress value) => _action(value);
    }
}
