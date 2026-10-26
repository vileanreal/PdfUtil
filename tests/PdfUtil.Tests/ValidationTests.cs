using System.Text;
using PdfUtil.Internal.Text;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>F7: validation report.</summary>
public class ValidationTests
{
    private static PdfValidationReport Validate(TestPdfBuilder builder, string? password = null) =>
        Pdf.Validate(builder.Build(), new PdfValidationOptions { Password = password });

    private static PdfValidationIssue Issue(PdfValidationReport report, string code) =>
        report.Issues.FirstOrDefault(i => i.Code == code) ?? throw new Xunit.Sdk.XunitException($"No {code} issue. Issues:\n{string.Join("\n", report.Issues)}");

    public static IEnumerable<object[]> CleanStructures() =>
        new[] { XrefMode.Classic, XrefMode.XrefStream, XrefMode.XrefStreamWithObjectStreams, XrefMode.Hybrid }.Select(m => new object[] { m });

    [Theory]
    [MemberData(nameof(CleanStructures))]
    public void CleanDocuments_AreValid(XrefMode mode)
    {
        foreach (var builder in new[] { new TestPdfBuilder { Mode = mode }, new TestPdfBuilder { Mode = mode, Rich = true, Xmp = TestPdfBuilder.PlainXmp() } })
        {
            var report = Validate(builder);
            Assert.True(report.IsReadable);
            Assert.True(report.IsValid, string.Join("\n", report.Issues));
            Assert.Empty(report.Issues);
            Assert.Equal(2, report.PageCount);
            Assert.True(report.ObjectCount > 5);
        }
    }

    [Fact]
    public void ProtectedOutput_IsValid()
    {
        var output = Pdf.Protect(new TestPdfBuilder { Rich = true }.Build(), TestHelpers.Options(PdfEncryptionAlgorithm.Aes256));
        Assert.Empty(Pdf.Validate(output, new PdfValidationOptions { Password = "user" }).Issues);
    }

    [Fact]
    public void IncrementalUpdatesAndLinearization_AreInformational()
    {
        var report = Validate(new TestPdfBuilder { IncrementalUpdate = true });
        Assert.True(report.IsValid);
        Assert.Equal(PdfValidationSeverity.Info, Issue(report, PdfValidationCodes.IncrementalUpdates).Severity);
        Assert.True(Validate(new TestPdfBuilder { Linearized = true }).IsValid);
    }

    [Fact]
    public void BrokenXrefOffsets_AreErrorsPerObject()
    {
        var report = Validate(new TestPdfBuilder { BreakXrefOffsets = true });
        Assert.True(report.IsReadable);
        Assert.False(report.IsValid);
        var offsets = report.Issues.Where(i => i.Code == PdfValidationCodes.XrefOffsetWrong).ToList();
        Assert.NotEmpty(offsets);
        Assert.All(offsets, i => Assert.Equal(PdfValidationSeverity.Error, i.Severity));
        Assert.All(offsets, i => Assert.NotNull(i.ObjectNumber));
        Assert.Equal(2, report.PageCount);
    }

    [Fact]
    public void BrokenStartXref_IsReconstructed()
    {
        var report = Validate(new TestPdfBuilder { BreakStartXref = true });
        Assert.Equal(PdfValidationSeverity.Error, Issue(report, PdfValidationCodes.XrefReconstructed).Severity);
        Assert.Equal(2, report.PageCount);
    }

    [Fact]
    public void WrongStreamLength_NamesTheObject()
    {
        var issue = Issue(Validate(new TestPdfBuilder { WrongStreamLength = true }), PdfValidationCodes.StreamLengthWrong);
        Assert.Equal(PdfValidationSeverity.Error, issue.Severity);
        Assert.NotNull(issue.ObjectNumber);
    }

    [Fact]
    public void JunkBeforeHeader_IsAWarning()
    {
        var report = Validate(new TestPdfBuilder { JunkBeforeHeader = 50 });
        Assert.Equal(PdfValidationSeverity.Warning, Issue(report, PdfValidationCodes.HeaderJunk).Severity);
    }

    [Fact]
    public void PageTreeProblems()
    {
        var count = Issue(Validate(new TestPdfBuilder { WrongPageCount = true }), PdfValidationCodes.PageTreeCountWrong);
        Assert.Equal(PdfValidationSeverity.Error, count.Severity);
        Assert.Contains("/Count 5", count.Message, StringComparison.Ordinal);

        Assert.Equal(PdfValidationSeverity.Error, Issue(Validate(new TestPdfBuilder { DuplicateKid = true }), PdfValidationCodes.PageTreeCycle).Severity);
        Assert.Equal(PdfValidationSeverity.Warning, Issue(Validate(new TestPdfBuilder { NoMediaBox = true }), PdfValidationCodes.PageMediaBoxMissing).Severity);

        var noPages = RawPdf.Build(new Dictionary<int, string> { [1] = "<< /Type /Catalog >>" });
        var report = Pdf.Validate(noPages);
        Assert.Equal(PdfValidationSeverity.Error, Issue(report, PdfValidationCodes.PageTreeMissing).Severity);

        var badNodes = RawPdf.Build(new Dictionary<int, string>
        {
            [1] = "<< /Pages 2 0 R >>",
            [2] = "<< /Type /Pages /Kids [3 0 R 5 0 R (junk)] /Count 1 >>",
            [3] = "<< /Type /Page /Parent 4 0 R /MediaBox [0 0 1 1] >>",
            [5] = "<< /Type /Font >>",
        });
        var bad = Pdf.Validate(badNodes);
        Assert.Contains(bad.Issues, i => i.Code == PdfValidationCodes.CatalogInvalid);
        Assert.Contains(bad.Issues, i => i.Code == PdfValidationCodes.PageTreeNodeInvalid && i.Message.Contains("/Parent 2", StringComparison.Ordinal));
        Assert.Contains(bad.Issues, i => i.Code == PdfValidationCodes.PageTreeNodeInvalid && i.ObjectNumber == 5);
        Assert.Contains(bad.Issues, i => i.Code == PdfValidationCodes.PageTreeNodeInvalid && i.Message.Contains("not a page", StringComparison.Ordinal));
    }

    [Fact]
    public void DanglingReference_NamesTheReferringObject()
    {
        var issue = Issue(Validate(new TestPdfBuilder { DanglingReference = true }), PdfValidationCodes.DanglingReference);
        Assert.Equal(PdfValidationSeverity.Warning, issue.Severity);
        Assert.Contains("object 900", issue.Message, StringComparison.Ordinal);
        Assert.NotNull(issue.ObjectNumber);
    }

    [Theory]
    [InlineData("2024-01-01", "does not start with 'D:'")]
    [InlineData("D:20241301", "month")]
    [InlineData("D:20240230", "day")]
    [InlineData("D:2024010125", "time")]
    [InlineData("D:20240101120000+25'00'", "offset")]
    [InlineData("D:2024 01", "does not match")]
    public void InvalidDates_AreWarnings(string date, string reason)
    {
        var builder = new TestPdfBuilder();
        builder.Info["ModDate"] = (date, TextEncodingKind.PdfDoc);
        var issue = Issue(Validate(builder), PdfValidationCodes.InvalidDate);
        Assert.Equal(PdfValidationSeverity.Warning, issue.Severity);
        Assert.Contains("/ModDate", issue.Message, StringComparison.Ordinal);
        Assert.Contains(reason, issue.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("D:2024")]
    [InlineData("D:202402")]
    [InlineData("D:20240229")]
    [InlineData("D:20240101120000Z")]
    [InlineData("D:20240101120000Z00'00'")]
    [InlineData("D:20240101120000+05'30'")]
    [InlineData("D:20240101120000-08'00")]
    [InlineData("D:20240101120000-08")]
    public void ValidDates_PassTheStrictCheck(string date) => Assert.Null(PdfDate.Check(date));

    [Fact]
    public void FlateProblems_TruncatedAndGarbage()
    {
        var report = Validate(new TestPdfBuilder { CorruptFlate = true });
        Assert.Equal(PdfValidationSeverity.Warning, Issue(report, PdfValidationCodes.FlateDecodeTruncated).Severity);
        Assert.Equal(PdfValidationSeverity.Error, Issue(report, PdfValidationCodes.FlateDecodeFailed).Severity);
        Assert.False(report.IsValid);

        // Without stream checks, neither is found.
        var quick = Pdf.Validate(new TestPdfBuilder { CorruptFlate = true }.Build(), new PdfValidationOptions { CheckStreams = false });
        Assert.DoesNotContain(quick.Issues, i => i.Code.StartsWith("FlateDecode", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    public void Encrypted_WithoutPassword_OnlyStructureIsChecked(int revision)
    {
        var builder = new TestPdfBuilder { Encryption = (revision, "u", "o", true), CorruptFlate = true };
        var structural = Validate(builder);
        Assert.True(structural.IsValid);
        Assert.Equal(PdfValidationSeverity.Info, Issue(structural, PdfValidationCodes.EncryptedContentNotChecked).Severity);
        Assert.Null(structural.PageCount);

        var full = Validate(builder, "u");
        Assert.DoesNotContain(full.Issues, i => i.Code == PdfValidationCodes.EncryptedContentNotChecked);
        Assert.Contains(full.Issues, i => i.Code == PdfValidationCodes.FlateDecodeFailed);
        Assert.Equal(2, full.PageCount);

        Assert.Throws<PdfInvalidPasswordException>(() => Validate(builder, "wrong"));
    }

    [Fact]
    public void Encrypted_WithoutId_IsAnError()
    {
        var report = Validate(new TestPdfBuilder { Encryption = (4, "", "o", true), IdMode = IdMode.Missing });
        Assert.Equal(PdfValidationSeverity.Error, Issue(report, PdfValidationCodes.MissingId).Severity);
    }

    [Fact]
    public void Pdf20_WithoutId_IsAWarning()
    {
        var report = Validate(new TestPdfBuilder { Version = "2.0", IdMode = IdMode.Missing });
        Assert.Equal(PdfValidationSeverity.Warning, Issue(report, PdfValidationCodes.MissingId).Severity);
    }

    [Fact]
    public void InconsistentEncryptionDictionary()
    {
        var pdf = RawPdf.Build(
            new Dictionary<int, string>
            {
                [1] = "<< /Type /Catalog /Pages 2 0 R >>",
                [2] = "<< /Type /Pages /Kids [] /Count 0 >>",
                [3] = "<< /Filter /Standard /V 2 /R 4 /Length 129 /O <0102> /U <0102> >>",
            },
            "/Root 1 0 R /Encrypt 3 0 R");
        var report = Pdf.Validate(pdf);
        var problems = report.Issues.Where(i => i.Code == PdfValidationCodes.EncryptionInconsistent).Select(i => i.Message).ToList();
        Assert.Contains(problems, m => m.Contains("/P", StringComparison.Ordinal));
        Assert.Contains(problems, m => m.Contains("/O is 2 bytes", StringComparison.Ordinal));
        Assert.Contains(problems, m => m.Contains("/V 2 is inconsistent with /R 4", StringComparison.Ordinal));
        Assert.Contains(problems, m => m.Contains("/Length 129", StringComparison.Ordinal));
        Assert.All(report.Issues.Where(i => i.Code == PdfValidationCodes.EncryptionInconsistent), i => Assert.Equal(3, i.ObjectNumber));
    }

    [Fact]
    public void NotAPdf_IsReportedNotThrown()
    {
        var report = Pdf.Validate(Encoding.ASCII.GetBytes("This is not a PDF at all."));
        Assert.False(report.IsReadable);
        Assert.False(report.IsValid);
        Assert.Equal(PdfValidationCodes.Unreadable, Assert.Single(report.Issues).Code);
    }

    [Fact]
    public void MalformedXmp_IsAWarning()
    {
        var report = Validate(new TestPdfBuilder { Xmp = "<x:xmpmeta><unclosed>" });
        Assert.Equal(PdfValidationSeverity.Warning, Issue(report, PdfValidationCodes.XmpMalformed).Severity);
    }

    [Fact]
    public void Issues_AreSortedBySeverity_AndCapped()
    {
        var builder = new TestPdfBuilder { BreakXrefOffsets = true, DanglingReference = true, JunkBeforeHeader = 10 };
        var report = Validate(builder);
        var severities = report.Issues.Select(i => (int)i.Severity).ToList();
        Assert.Equal(severities.OrderByDescending(s => s), severities);

        var capped = Pdf.Validate(builder.Build(), new PdfValidationOptions { MaxIssues = 2 });
        Assert.Equal(3, capped.Issues.Count);
        Assert.Equal(PdfValidationCodes.TooManyIssues, capped.Issues[^1].Code);
        Assert.Throws<ArgumentException>(() => Pdf.Validate(builder.Build(), new PdfValidationOptions { MaxIssues = 0 }));
    }

    [Fact]
    public async Task Limits_StillThrow()
    {
        var analyzer = new PdfAnalyzer(new PdfUtilOptions { MaxObjectCount = 5 });
        using var input = new MemoryStream(new TestPdfBuilder().Build());
        await Assert.ThrowsAsync<PdfLimitExceededException>(() => analyzer.ValidateAsync(input));
    }

    [Fact]
    public async Task Progress_IsReported()
    {
        var reports = new List<PdfProgress>();
        var progress = new SynchronousProgress(reports.Add);
        using var input = new MemoryStream(new TestPdfBuilder { Rich = true }.Build());
        await new PdfAnalyzer().ValidateAsync(input, new PdfValidationOptions { Progress = progress });
        Assert.NotEmpty(reports);
        Assert.All(reports, r => Assert.Equal(PdfProgressStage.Reading, r.Stage));
        Assert.Equal(1.0, reports[^1].Fraction);
    }

    [Fact]
    public async Task Cancellation_IsHonoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var input = new MemoryStream(new TestPdfBuilder().Build());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PdfAnalyzer().ValidateAsync(input, null, cts.Token));
    }

    private sealed class SynchronousProgress : IProgress<PdfProgress>
    {
        private readonly Action<PdfProgress> _action;

        public SynchronousProgress(Action<PdfProgress> action) => _action = action;

        public void Report(PdfProgress value) => _action(value);
    }
}
