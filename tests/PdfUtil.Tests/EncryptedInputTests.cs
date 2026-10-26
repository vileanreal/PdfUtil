using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>Reading, verifying, inspecting and re-encrypting documents encrypted with every Standard revision (R2-R6).</summary>
public class EncryptedInputTests
{
    public static IEnumerable<object[]> Revisions()
    {
        foreach (var revision in new[] { 2, 3, 4, 5, 6 })
        {
            foreach (var mode in new[] { XrefMode.Classic, XrefMode.XrefStreamWithObjectStreams, XrefMode.Hybrid })
            {
                yield return new object[] { revision, mode };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public void EncryptedInput_IsVerifiedInspectedAndReEncrypted(int revision, XrefMode mode)
    {
        var builder = new TestPdfBuilder
        {
            Version = revision >= 5 ? "1.7" : "1.4",
            Mode = mode,
            Encryption = (revision, "u-pass", "o-pass", true),
            Xmp = TestPdfBuilder.PlainXmp(),
        };
        builder.Info["Title"] = ("Encrypted ñ 中", TextEncodingKind.Utf16Be);
        var input = builder.Build();

        var expectedAlgorithm = revision switch
        {
            2 => PdfEncryptionAlgorithm.Rc4_40,
            3 => PdfEncryptionAlgorithm.Rc4_128,
            4 => PdfEncryptionAlgorithm.Aes128,
            5 => PdfEncryptionAlgorithm.Aes256R5,
            _ => PdfEncryptionAlgorithm.Aes256,
        };

        Assert.True(Pdf.IsEncrypted(input));
        var user = Pdf.VerifyPassword(input, "u-pass");
        Assert.Equal(PdfPasswordMatch.User, user.MatchedAs);
        Assert.Equal(revision, user.Revision);
        Assert.Equal(expectedAlgorithm, user.Algorithm);
        Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(input, "o-pass").MatchedAs);
        Assert.False(Pdf.VerifyPassword(input, "x").IsValid);

        var info = Pdf.GetInfo(input, "u-pass");
        Assert.Equal(2, info.PageCount);
        Assert.Equal("Encrypted ñ 中", info.Info!.Title);
        Assert.Equal("Plain XMP", info.Xmp!.Title);
        Assert.Equal(revision, info.Encryption!.R);

        var partial = Pdf.GetInfo(input);
        Assert.True(partial.RequiresPassword);
        Assert.Equal(revision, partial.Encryption!.R);

        foreach (var algorithm in TestHelpers.WritableAlgorithms)
        {
            var options = TestHelpers.Options(algorithm, "new-user", "new-owner");
            options.AlreadyEncryptedPolicy = AlreadyEncryptedPolicy.ReEncryptWithOwnerPassword;
            options.CurrentOwnerPassword = "o-pass";
            var (output, result) = TestHelpers.Protect(input, options);
            Assert.Contains(result.Warnings, w => w.Contains("re-encrypted", StringComparison.Ordinal));
            Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, "new-user").MatchedAs);
            Assert.False(Pdf.VerifyPassword(output, "u-pass").IsValid);
            var outInfo = Pdf.GetInfo(output, "new-user");
            Assert.Equal("Encrypted ñ 中", outInfo.Info!.Title);
            Assert.Equal("Plain XMP", outInfo.Xmp!.Title);
            ContentPreservationTests.AssertStreamsPreserved(input, "o-pass", output, "new-owner");
            VersionMatrixTests.AssertEncryptDictionary(output, algorithm);
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void EncryptMetadataFalse_Input_IsHandled(int revision)
    {
        var input = new TestPdfBuilder { Encryption = (revision, "u", "o", false), Xmp = TestPdfBuilder.PlainXmp() }.Build();
        var info = Pdf.GetInfo(input, "u");
        Assert.False(info.Encryption!.EncryptMetadata);
        Assert.Equal("Plain XMP", info.Xmp!.Title);

        var options = TestHelpers.Options(PdfEncryptionAlgorithm.Aes256);
        options.AlreadyEncryptedPolicy = AlreadyEncryptedPolicy.ReEncryptWithOwnerPassword;
        options.CurrentOwnerPassword = "o";
        var (output, _) = TestHelpers.Protect(input, options);
        Assert.Equal("Plain XMP", Pdf.GetInfo(output, "user").Xmp!.Title);
        Assert.DoesNotContain("Plain XMP", System.Text.Encoding.UTF8.GetString(output));
    }

    [Fact]
    public void EmptyUserPasswordInput_OpensWithoutPassword()
    {
        var input = new TestPdfBuilder { Encryption = (3, "", "owner", true) }.Build();
        var check = Pdf.VerifyPassword(input, null);
        Assert.True(check.IsValid);
        Assert.True(check.OpensWithoutPassword);
        var info = Pdf.GetInfo(input);
        Assert.False(info.RequiresPassword);
        Assert.Equal("Test document", info.Info!.Title);
    }
}
