using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>F1: removing protection.</summary>
public class DecryptTests
{
    internal static byte[] Encrypted(int revision, XrefMode mode = XrefMode.Classic, string user = "u-pass", string owner = "o-pass", bool encryptMetadata = true) =>
        new TestPdfBuilder
        {
            Version = revision >= 5 ? "1.7" : "1.4",
            Mode = mode,
            Encryption = (revision, user, owner, encryptMetadata),
            Xmp = TestPdfBuilder.PlainXmp(),
        }.Build();

    public static IEnumerable<object[]> RevisionsAndLayouts() =>
        from r in new[] { 2, 3, 4, 5, 6 }
        from m in new[] { XrefMode.Classic, XrefMode.XrefStreamWithObjectStreams, XrefMode.Hybrid }
        select new object[] { r, m };

    [Theory]
    [MemberData(nameof(RevisionsAndLayouts))]
    public void EveryRevision_IsDecrypted_ByteExact(int revision, XrefMode mode)
    {
        var input = Encrypted(revision, mode);
        var output = Pdf.Decrypt(input, new PdfDecryptOptions { Password = "o-pass" }, out var result);

        Assert.False(result.IsEncrypted);
        Assert.Equal(PdfEncryptionAlgorithm.None, result.Algorithm);
        Assert.False(Pdf.IsEncrypted(output));
        Assert.False(Pdf.VerifyPassword(output, null).IsEncrypted);
        Assert.Equal(Pdf.GetInfo(input, "o-pass").PermanentId, Pdf.GetInfo(output).PermanentId);
        var info = Pdf.GetInfo(output);
        Assert.Equal(2, info.PageCount);
        Assert.Equal("Test document", info.Info!.Title);
        Assert.Equal("Plain XMP", info.Xmp!.Title);
        Assert.DoesNotContain("/Encrypt", System.Text.Encoding.Latin1.GetString(output), StringComparison.Ordinal);
        ContentPreservationTests.AssertStreamsPreserved(input, "o-pass", output, null);
        VersionMatrixTests.AssertSingleTrailerStructure(output);
    }

    [Theory]
    [InlineData(VersionMatrixTests.Structure.Classic)]
    [InlineData(VersionMatrixTests.Structure.ObjectStreams)]
    [InlineData(VersionMatrixTests.Structure.Hybrid)]
    [InlineData(VersionMatrixTests.Structure.Linearized)]
    [InlineData(VersionMatrixTests.Structure.IncrementalUpdate)]
    [InlineData(VersionMatrixTests.Structure.BrokenXrefOffsets)]
    [InlineData(VersionMatrixTests.Structure.WrongStreamLength)]
    [InlineData(VersionMatrixTests.Structure.JunkBeforeHeader)]
    public void ProtectedStructures_RoundTripThroughDecrypt(VersionMatrixTests.Structure structure)
    {
        var original = VersionMatrixTests.BuilderFor(structure).Build();
        foreach (var algorithm in TestHelpers.WritableAlgorithms)
        {
            var (protectedPdf, _) = TestHelpers.Protect(original, TestHelpers.Options(algorithm));
            var decrypted = Pdf.Decrypt(protectedPdf, "owner");
            Assert.False(Pdf.IsEncrypted(decrypted));
            ContentPreservationTests.AssertStreamsPreserved(original, null, decrypted, null);
            Assert.Equal(Pdf.GetInfo(original).Info!.Title, Pdf.GetInfo(decrypted).Info!.Title);
        }
    }

    [Fact]
    public void UserPassword_IsRejectedByDefault_AndAllowedOnRequest()
    {
        var input = Encrypted(6);
        var ex = Assert.Throws<PdfInvalidPasswordException>(() => Pdf.Decrypt(input, "u-pass"));
        Assert.Contains("owner password", ex.Message, StringComparison.Ordinal);

        var output = Pdf.Decrypt(input, new PdfDecryptOptions { Password = "u-pass", AllowUserPassword = true }, out var result);
        Assert.False(Pdf.IsEncrypted(output));
        Assert.Contains(result.Warnings, w => w.Contains("AllowUserPassword", StringComparison.Ordinal));
    }

    [Fact]
    public void WrongOrMissingPassword_Throws()
    {
        var input = Encrypted(4);
        Assert.Throws<PdfInvalidPasswordException>(() => Pdf.Decrypt(input, "nope"));
        Assert.Throws<PdfInvalidPasswordException>(() => Pdf.Decrypt(input, new PdfDecryptOptions(), out _));
    }

    [Fact]
    public void EmptyUserPassword_DocumentNeedsOwnerPassword()
    {
        var input = Encrypted(6, user: "");
        Assert.Throws<PdfInvalidPasswordException>(() => Pdf.Decrypt(input, new PdfDecryptOptions(), out _));
        Assert.False(Pdf.IsEncrypted(Pdf.Decrypt(input, "o-pass")));
    }

    [Fact]
    public void UnencryptedInput_IsRewrittenWithAWarning()
    {
        var input = new TestPdfBuilder().Build();
        var output = Pdf.Decrypt(input, new PdfDecryptOptions(), out var result);
        Assert.Contains(result.Warnings, w => w.Contains("not encrypted", StringComparison.Ordinal));
        ContentPreservationTests.AssertStreamsPreserved(input, null, output, null);
    }

    [Fact]
    public void Decrypt_WithInfoChanges()
    {
        var input = Encrypted(6);
        var output = Pdf.Decrypt(input, new PdfDecryptOptions { Password = "o-pass", InfoChanges = new PdfInfoChanges { Title = "Unlocked" } }, out var result);
        Assert.True(result.DocumentInfoChanged);
        Assert.True(result.XmpUpdated);
        var info = Pdf.GetInfo(output);
        Assert.Equal("Unlocked", info.Info!.Title);
        Assert.Equal("Unlocked", info.Xmp!.Title);
    }

    [Fact]
    public void EncryptMetadataFalse_Input_IsDecrypted()
    {
        var input = Encrypted(4, encryptMetadata: false);
        var output = Pdf.Decrypt(input, "o-pass");
        Assert.Equal("Plain XMP", Pdf.GetInfo(output).Xmp!.Title);
        ContentPreservationTests.AssertStreamsPreserved(input, "o-pass", output, null);
    }

    [Fact]
    public async Task DecryptFile_InPlace_IsAtomic()
    {
        var dir = Directory.CreateTempSubdirectory("pdfutil-decrypt-").FullName;
        try
        {
            var path = Path.Combine(dir, "doc.pdf");
            await File.WriteAllBytesAsync(path, Encrypted(3));
            await Assert.ThrowsAsync<PdfInvalidPasswordException>(() => new PdfEditor().DecryptFileAsync(path, path, new PdfDecryptOptions { Password = "bad" }));
            Assert.True(Pdf.IsEncrypted(path));
            Assert.Single(Directory.GetFiles(dir));

            Pdf.Decrypt(path, path, "o-pass");
            Assert.False(Pdf.IsEncrypted(path));
            Assert.Single(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PubSec_IsUnsupported()
    {
        var input = new TestPdfBuilder { PubSecEncryption = true }.Build();
        Assert.Throws<PdfUnsupportedEncryptionException>(() => Pdf.Decrypt(input, "x"));
    }

    [Fact]
    public void Aes256Source_KeepsVersion_AndDropsNothing()
    {
        var (protectedPdf, _) = TestHelpers.Protect(new TestPdfBuilder { Version = "1.4" }.Build(), TestHelpers.Options(PdfEncryptionAlgorithm.Aes256));
        var output = Pdf.Decrypt(protectedPdf, new PdfDecryptOptions { Password = "owner" }, out var result);
        Assert.Equal("1.7", result.SourceVersion);
        Assert.Equal("1.7", result.OutputVersion);
        using var doc = TestHelpers.Open(output, strict: true);
        Assert.False(doc.IsEncrypted);
        Assert.Null(doc.Trailer["Encrypt"]);
    }
}
