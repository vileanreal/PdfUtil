using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Text;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>Changing the document information (Info dictionary and XMP) while protecting.</summary>
public class InfoChangesTests
{
    private static PdfProtectionOptions WithChanges(PdfInfoChanges changes, PdfEncryptionAlgorithm algorithm = PdfEncryptionAlgorithm.Aes256)
    {
        var options = TestHelpers.Options(algorithm);
        options.InfoChanges = changes;
        return options;
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256)]
    [InlineData(PdfEncryptionAlgorithm.Aes128)]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128)]
    public void StandardFields_AreChanged_InInfoAndXmp(PdfEncryptionAlgorithm algorithm)
    {
        var input = new TestPdfBuilder { Xmp = TestPdfBuilder.PdfAXmp(2, "B") }.Build();
        var options = WithChanges(new PdfInfoChanges
        {
            Title = "Quarterly report – Q3 ñ 中文 😀",
            Author = "Jane Doe",
            Subject = "Finance",
            Keywords = "report, q3",
            Creator = "Report generator",
            Producer = "PdfUtil tests",
            CreationDate = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.FromHours(2)),
            ModificationDate = new DateTimeOffset(2024, 6, 7, 8, 9, 10, TimeSpan.FromHours(-5)),
        }, algorithm);
        options.PdfAPolicy = PdfAPolicy.Proceed;
        var (output, result) = TestHelpers.Protect(input, options);
        Assert.True(result.DocumentInfoChanged);
        Assert.True(result.XmpUpdated);

        var info = Pdf.GetInfo(output, "user");
        Assert.Equal("Quarterly report – Q3 ñ 中文 😀", info.Info!.Title);
        Assert.Equal("Jane Doe", info.Info.Author);
        Assert.Equal("Finance", info.Info.Subject);
        Assert.Equal("report, q3", info.Info.Keywords);
        Assert.Equal("Report generator", info.Info.Creator);
        Assert.Equal("PdfUtil tests", info.Info.Producer);
        Assert.Equal(new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.FromHours(2)), info.Info.CreationDate);
        Assert.Equal(new DateTimeOffset(2024, 6, 7, 8, 9, 10, TimeSpan.FromHours(-5)), info.Info.ModDate);

        // XMP mirrors the Info values; unrelated XMP (PDF/A identification) is kept.
        Assert.Equal("Quarterly report – Q3 ñ 中文 😀", info.Xmp!.Title);
        Assert.Equal(new[] { "Jane Doe" }, info.Xmp.Creators);
        Assert.Equal("Finance", info.Xmp.Description);
        Assert.Equal("report, q3", info.Xmp.Keywords);
        Assert.Equal("Report generator", info.Xmp.CreatorTool);
        Assert.Equal("PdfUtil tests", info.Xmp.Producer);
        Assert.Equal(new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.FromHours(2)), info.Xmp.CreateDate);
        Assert.Equal(new DateTimeOffset(2024, 6, 7, 8, 9, 10, TimeSpan.FromHours(-5)), info.Xmp.ModifyDate);
        Assert.NotNull(info.Xmp.MetadataDate);
        Assert.Equal(2, info.Xmp.PdfAPart);
        Assert.True(info.Xmp.IsWellFormed);
        Assert.Contains("<?xpacket begin=", info.Xmp.RawPacket, StringComparison.Ordinal);
        Assert.Contains("<?xpacket end=", info.Xmp.RawPacket, StringComparison.Ordinal);
    }

    [Fact]
    public void UnchangedFields_AreKept_AndOnlyChangedOnesDiffer()
    {
        var input = new TestPdfBuilder().Build();
        var (output, _) = TestHelpers.Protect(input, WithChanges(new PdfInfoChanges { Title = "New title" }));
        var info = Pdf.GetInfo(output, "user").Info!;
        Assert.Equal("New title", info.Title);
        Assert.Equal("PdfUtil tests", info.Author);
        Assert.Equal("TestPdfBuilder", info.Producer);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(1)), info.CreationDate);
        Assert.Equal("False", info.Trapped);
    }

    [Fact]
    public void CustomProperties_AndRemovals()
    {
        var input = new TestPdfBuilder().Build();
        var changes = new PdfInfoChanges();
        changes.CustomProperties["Department"] = "Legal – Ü";
        changes.CustomProperties["Ticket"] = "ABC-123";
        changes.Remove.Add("Author");
        changes.Remove.Add("Trapped");
        changes.Remove.Add("DoesNotExist");
        var (output, _) = TestHelpers.Protect(input, WithChanges(changes));
        var info = Pdf.GetInfo(output, "user").Info!;
        Assert.Equal("Legal – Ü", info.CustomEntries["Department"]);
        Assert.Equal("ABC-123", info.CustomEntries["Ticket"]);
        Assert.Null(info.Author);
        Assert.Null(info.Trapped);
        Assert.Equal("Test document", info.Title);
    }

    [Fact]
    public void RemovingAndSettingTheSameKey_Sets()
    {
        var changes = new PdfInfoChanges { Title = "Kept" };
        changes.Remove.Add("Title");
        var (output, _) = TestHelpers.Protect(new TestPdfBuilder().Build(), WithChanges(changes));
        Assert.Equal("Kept", Pdf.GetInfo(output, "user").Info!.Title);
    }

    [Fact]
    public void DocumentWithoutInfo_GetsANewInfoDictionary()
    {
        var input = new TestPdfBuilder { OmitInfo = true }.Build();
        Assert.Null(Pdf.GetInfo(input).Info);
        var (output, result) = TestHelpers.Protect(input, WithChanges(new PdfInfoChanges { Title = "Created", Author = "Me" }));
        Assert.False(result.XmpUpdated);
        var info = Pdf.GetInfo(output, "user").Info!;
        Assert.Equal("Created", info.Title);
        Assert.Equal("Me", info.Author);
        ContentPreservationTests.AssertStreamsPreserved(input, null, output, "owner");
    }

    [Fact]
    public void UpdateModificationDate_SetsNow()
    {
        var before = DateTimeOffset.Now.AddSeconds(-2);
        var (output, _) = TestHelpers.Protect(new TestPdfBuilder { Xmp = TestPdfBuilder.PlainXmp() }.Build(), WithChanges(new PdfInfoChanges { UpdateModificationDate = true }));
        var info = Pdf.GetInfo(output, "user");
        Assert.InRange(info.Info!.ModDate!.Value, before, DateTimeOffset.Now.AddSeconds(2));
        Assert.InRange(info.Xmp!.ModifyDate!.Value, before, DateTimeOffset.Now.AddSeconds(2));
    }

    [Fact]
    public void ChangedStrings_UsePdfDocEncodingOrUtf16_NeverUtf8()
    {
        var (output, _) = TestHelpers.Protect(new TestPdfBuilder { Version = "1.7" }.Build(), WithChanges(new PdfInfoChanges { Title = "中文", Author = "Plain é" }));
        using var doc = TestHelpers.Open(output, strict: true);
        doc.Authenticate("user");
        var info = (PdfDictionary)doc.Resolve(doc.Trailer["Info"])!;
        Assert.Equal(TestPdfBuilder.EncodeText("中文", TextEncodingKind.Utf16Be), ((PdfString)info["Title"]!).Value);
        Assert.Equal(new byte[] { (byte)'P', (byte)'l', (byte)'a', (byte)'i', (byte)'n', (byte)' ', 0xE9 }, ((PdfString)info["Author"]!).Value);
        Assert.False(TextString.HasUtf8Bom(((PdfString)info["Title"]!).Value));
    }

    [Fact]
    public void OtherContent_IsUntouched()
    {
        var input = new TestPdfBuilder().Build();
        var (output, _) = TestHelpers.Protect(input, WithChanges(new PdfInfoChanges { Title = "Changed", Keywords = "k" }));
        ContentPreservationTests.AssertStreamsPreserved(input, null, output, "owner");
        Assert.Equal(2, Pdf.GetInfo(output, "user").PageCount);
    }

    [Fact]
    public void CompressedXmp_IsRewrittenUncompressed()
    {
        var input = new TestPdfBuilder { Xmp = TestPdfBuilder.PlainXmp(), CompressXmp = true }.Build();
        var options = WithChanges(new PdfInfoChanges { Title = "Uncompressed XMP" });
        options.EncryptMetadata = false;
        var (output, result) = TestHelpers.Protect(input, options);
        Assert.True(result.XmpUpdated);
        Assert.Contains("Uncompressed XMP", System.Text.Encoding.UTF8.GetString(output), StringComparison.Ordinal);
        Assert.Equal("Uncompressed XMP", Pdf.GetInfo(output, "user").Xmp!.Title);
    }

    [Fact]
    public void UpdateXmpFalse_LeavesXmpAlone()
    {
        var input = new TestPdfBuilder { Xmp = TestPdfBuilder.PlainXmp() }.Build();
        var (output, result) = TestHelpers.Protect(input, WithChanges(new PdfInfoChanges { Title = "Info only", UpdateXmp = false }));
        Assert.False(result.XmpUpdated);
        var info = Pdf.GetInfo(output, "user");
        Assert.Equal("Info only", info.Info!.Title);
        Assert.Equal("Plain XMP", info.Xmp!.Title);
    }

    [Fact]
    public void MalformedXmp_IsLeftAlone_WithWarning()
    {
        var input = new TestPdfBuilder { Xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'><broken" }.Build();
        var (output, result) = TestHelpers.Protect(input, WithChanges(new PdfInfoChanges { Title = "Still changed" }));
        Assert.False(result.XmpUpdated);
        Assert.Contains(result.Warnings, w => w.Contains("XMP", StringComparison.Ordinal));
        Assert.Equal("Still changed", Pdf.GetInfo(output, "user").Info!.Title);
    }

    [Fact]
    public void ReEncryption_WithChanges()
    {
        var input = new TestPdfBuilder { Encryption = (4, "u", "o", true), Xmp = TestPdfBuilder.PlainXmp() }.Build();
        var options = WithChanges(new PdfInfoChanges { Title = "Re-encrypted and renamed" });
        options.AlreadyEncryptedPolicy = AlreadyEncryptedPolicy.ReEncryptWithOwnerPassword;
        options.CurrentOwnerPassword = "o";
        var (output, _) = TestHelpers.Protect(input, options);
        var info = Pdf.GetInfo(output, "user");
        Assert.Equal("Re-encrypted and renamed", info.Info!.Title);
        Assert.Equal("Re-encrypted and renamed", info.Xmp!.Title);
    }

    [Fact]
    public void EmptyChanges_AreANoOp()
    {
        var input = new TestPdfBuilder().Build();
        var (output, result) = TestHelpers.Protect(input, WithChanges(new PdfInfoChanges()));
        Assert.False(result.DocumentInfoChanged);
        Assert.Equal("Test document", Pdf.GetInfo(output, "user").Info!.Title);
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("ModDate")]
    [InlineData("Trapped")]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("ключ")]
    public void InvalidCustomKeys_AreRejected(string key)
    {
        var changes = new PdfInfoChanges();
        changes.CustomProperties[key] = "value";
        Assert.Throws<ArgumentException>(() => Pdf.Protect(new TestPdfBuilder().Build(), WithChanges(changes)));
    }

    [Fact]
    public void PdfDate_FormatRoundTrips()
    {
        var value = new DateTimeOffset(2025, 12, 31, 23, 59, 58, TimeSpan.FromMinutes(-330));
        Assert.Equal("D:20251231235958-05'30'", PdfDate.Format(value));
        Assert.Equal(value, PdfDate.Parse(PdfDate.Format(value)));
        Assert.Equal("D:20250101000000+00'00'", PdfDate.Format(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }
}
