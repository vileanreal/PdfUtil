using PdfUtil.Internal.Objects;
using PdfUtil.Tests.Infrastructure;
using static PdfUtil.Tests.DecryptTests;

namespace PdfUtil.Tests;

/// <summary>F3: editing document information without changing protection.</summary>
public class EditInfoTests
{
    [Fact]
    public void UnencryptedDocument_StaysUnencrypted()
    {
        var input = new TestPdfBuilder { Xmp = TestPdfBuilder.PlainXmp() }.Build();
        var output = Pdf.EditInfo(input, new PdfInfoChanges { Title = "Edited", Author = "Me" }, null, out var result);
        Assert.False(result.IsEncrypted);
        Assert.True(result.XmpUpdated);
        Assert.False(Pdf.IsEncrypted(output));
        var info = Pdf.GetInfo(output);
        Assert.Equal("Edited", info.Info!.Title);
        Assert.Equal("Edited", info.Xmp!.Title);
        Assert.Equal("Me", info.Info.Author);
    }

    public static IEnumerable<object[]> PreservedRevisions() =>
        from r in new[] { 3, 4, 6 }
        from m in new[] { XrefMode.Classic, XrefMode.XrefStreamWithObjectStreams }
        select new object[] { r, m };

    [Theory]
    [MemberData(nameof(PreservedRevisions))]
    public void ProtectedDocument_KeepsExactlyTheSameSecurity(int revision, XrefMode mode)
    {
        var input = Encrypted(revision, mode);
        var output = Pdf.EditInfo(input, new PdfInfoChanges { Title = "Edited while protected" }, "o-pass", out var result);
        Assert.True(result.IsEncrypted);
        Assert.Equal(revision, result.Revision);

        // The same /Encrypt dictionary: so the user password works although PdfUtil never knew it (AES-256 included).
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, "u-pass").MatchedAs);
        Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(output, "o-pass").MatchedAs);
        Assert.False(Pdf.VerifyPassword(output, "x").IsValid);
        using (var src = TestHelpers.Open(input))
        using (var dst = TestHelpers.Open(output, strict: true))
        {
            var a = src.GetEncryptDictionary()!;
            var b = dst.GetEncryptDictionary()!;
            foreach (var key in new[] { "O", "U", "OE", "UE", "Perms" })
            {
                Assert.Equal((a[key] as PdfString)?.Value, (b[key] as PdfString)?.Value);
            }

            Assert.Equal(((PdfInteger)a["P"]!).Value, ((PdfInteger)b["P"]!).Value);
        }

        var info = Pdf.GetInfo(output, "u-pass");
        Assert.Equal("Edited while protected", info.Info!.Title);
        Assert.Equal("Edited while protected", info.Xmp!.Title);
        Assert.Equal(Pdf.GetInfo(input, "u-pass").PermanentId, info.PermanentId);
    }

    [Fact]
    public void ProtectedDocument_OtherContentIsUntouched()
    {
        var input = new TestPdfBuilder { Encryption = (6, "u-pass", "o-pass", true) }.Build();
        var output = Pdf.EditInfo(input, new PdfInfoChanges { Keywords = "k" }, "o-pass", out _);
        ContentPreservationTests.AssertStreamsPreserved(input, "o-pass", output, "o-pass");
    }

    [Fact]
    public void EncryptMetadataFalse_XmpStaysReadable()
    {
        var input = Encrypted(6, encryptMetadata: false);
        var output = Pdf.EditInfo(input, new PdfInfoChanges { Title = "Visible title" }, "o-pass", out _);
        Assert.Contains("Visible title", System.Text.Encoding.UTF8.GetString(output), StringComparison.Ordinal);
        Assert.False(Pdf.GetInfo(output, "u-pass").Encryption!.EncryptMetadata);
    }

    [Fact]
    public void ProtectedDocument_RequiresTheOwnerPassword()
    {
        var input = Encrypted(4);
        var ex = Assert.Throws<PdfInvalidPasswordException>(() => Pdf.EditInfo(input, new PdfInfoChanges { Title = "x" }, "u-pass", out _));
        Assert.Contains("owner password", ex.Message, StringComparison.Ordinal);
        Assert.Throws<PdfInvalidPasswordException>(() => Pdf.EditInfo(input, new PdfInfoChanges { Title = "x" }, null, out _));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void DeprecatedRevisions_AreNotRewritten(int revision)
    {
        var ex = Assert.Throws<PdfUnsupportedEncryptionException>(() => Pdf.EditInfo(Encrypted(revision), new PdfInfoChanges { Title = "x" }, "o-pass", out _));
        Assert.Contains("ChangeSecurityAsync", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidChanges_AreRejected()
    {
        var changes = new PdfInfoChanges();
        changes.CustomProperties["Title"] = "x";
        Assert.Throws<ArgumentException>(() => Pdf.EditInfo(new TestPdfBuilder().Build(), changes, null, out _));
        await Assert.ThrowsAsync<ArgumentException>(() => new PdfEditor().EditInfoAsync(new MemoryStream(), new MemoryStream(), new PdfEditInfoOptions { Changes = null! }));
    }
}
