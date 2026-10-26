using PdfUtil.Tests.Infrastructure;
using static PdfUtil.Tests.DecryptTests;

namespace PdfUtil.Tests;

/// <summary>F2: changing passwords, permissions or algorithm.</summary>
public class ChangeSecurityTests
{
    private static byte[] Change(byte[] input, PdfSecurityChanges changes, out PdfProtectionResult result) => Pdf.ChangeSecurity(input, changes, out result);

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Legacy_PermissionsOnly_KeepsBothPasswordsAndAlgorithm(int revision)
    {
        var input = Encrypted(revision);
        var output = Change(input, new PdfSecurityChanges { CurrentOwnerPassword = "o-pass", Permissions = PdfPermissions.Print }, out var result);
        Assert.Equal(revision, result.Revision);
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, "u-pass").MatchedAs);
        Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(output, "o-pass").MatchedAs);
        Assert.Equal(PdfPermissions.Print, Pdf.GetInfo(output, "u-pass").Encryption!.Permissions);
        Assert.Empty(result.Warnings);
        ContentPreservationTests.AssertStreamsPreserved(input, "o-pass", output, "o-pass");
    }

    [Fact]
    public void Legacy_ToAes256_KeepsTheUserPassword()
    {
        var input = Encrypted(3);
        var output = Change(input, new PdfSecurityChanges { CurrentOwnerPassword = "o-pass", Algorithm = PdfEncryptionAlgorithm.Aes256 }, out var result);
        Assert.Equal(PdfEncryptionAlgorithm.Aes256, result.Algorithm);
        Assert.Equal("1.7", result.OutputVersion);
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, "u-pass").MatchedAs);
        Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(output, "o-pass").MatchedAs);
        Assert.Equal(PdfPermissions.Print | PdfPermissions.CopyContent, Pdf.GetInfo(output, "o-pass").Encryption!.Permissions);
    }

    [Fact]
    public void Legacy_NonAsciiUserPassword_IsKeptByteForByte()
    {
        // A third-party file whose legacy password was stored as ISO 8859-1 bytes.
        var input = new TestPdfBuilder { Encryption = (3, "Café", "o-pass", true) }.Build();
        var output = Change(input, new PdfSecurityChanges { CurrentOwnerPassword = "o-pass", Permissions = PdfPermissions.None }, out _);
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, "Café").MatchedAs);
    }

    [Fact]
    public void Aes256_ChangingBothPasswords()
    {
        var input = Encrypted(6);
        var output = Change(input, new PdfSecurityChanges { CurrentOwnerPassword = "o-pass", UserPassword = "new-user", OwnerPassword = "new-owner" }, out _);
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, "new-user").MatchedAs);
        Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(output, "new-owner").MatchedAs);
        Assert.False(Pdf.VerifyPassword(output, "u-pass").IsValid);
        Assert.False(Pdf.VerifyPassword(output, "o-pass").IsValid);
    }

    [Fact]
    public void Aes256_UserPasswordCannotBeKeptUnlessEmpty()
    {
        var ex = Assert.Throws<ArgumentException>(() => Change(Encrypted(6), new PdfSecurityChanges { CurrentOwnerPassword = "o-pass", Permissions = PdfPermissions.Print }, out _));
        Assert.Contains("UserPassword", ex.Message, StringComparison.Ordinal);

        var output = Change(Encrypted(6, user: ""), new PdfSecurityChanges { CurrentOwnerPassword = "o-pass", Permissions = PdfPermissions.Print }, out _);
        var check = Pdf.VerifyPassword(output, null);
        Assert.True(check.IsValid);
        Assert.True(check.OpensWithoutPassword);
        Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(output, "o-pass").MatchedAs);
    }

    [Fact]
    public void RemovingTheUserPassword()
    {
        var output = Change(Encrypted(4), new PdfSecurityChanges { CurrentOwnerPassword = "o-pass", UserPassword = "" }, out _);
        Assert.True(Pdf.VerifyPassword(output, null).OpensWithoutPassword);
        Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(output, "o-pass").MatchedAs);
    }

    [Theory]
    [InlineData(2, PdfEncryptionAlgorithm.Rc4_128)]
    [InlineData(5, PdfEncryptionAlgorithm.Aes256)]
    public void DeprecatedRevisions_AreUpgraded(int revision, PdfEncryptionAlgorithm expected)
    {
        var changes = new PdfSecurityChanges { CurrentOwnerPassword = "o-pass" };
        if (revision == 5)
        {
            changes.UserPassword = "u-pass";
        }

        var output = Change(Encrypted(revision), changes, out var result);
        Assert.Equal(expected, result.Algorithm);
        Assert.Contains(result.Warnings, w => w.Contains("upgraded", StringComparison.Ordinal));
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, "u-pass").MatchedAs);
    }

    [Fact]
    public void EncryptMetadataFalse_IsKept()
    {
        var output = Change(Encrypted(4, encryptMetadata: false), new PdfSecurityChanges { CurrentOwnerPassword = "o-pass" }, out _);
        Assert.False(Pdf.GetInfo(output, "u-pass").Encryption!.EncryptMetadata);
        Assert.Contains("Plain XMP", System.Text.Encoding.UTF8.GetString(output), StringComparison.Ordinal);
    }

    [Fact]
    public void UserPasswordAsCurrentOwner_IsRejected()
    {
        var ex = Assert.Throws<PdfInvalidPasswordException>(() => Change(Encrypted(6), new PdfSecurityChanges { CurrentOwnerPassword = "u-pass", UserPassword = "x" }, out _));
        Assert.Contains("not the owner password", ex.Message, StringComparison.Ordinal);
        Assert.Throws<PdfInvalidPasswordException>(() => Change(Encrypted(6), new PdfSecurityChanges { CurrentOwnerPassword = "bad", UserPassword = "x" }, out _));
    }

    [Fact]
    public void UnencryptedInput_IsRejected()
    {
        Assert.Throws<PdfNotEncryptedException>(() => Change(new TestPdfBuilder().Build(), new PdfSecurityChanges { CurrentOwnerPassword = "x" }, out _));
    }

    [Fact]
    public void InvalidChanges_AreRejected()
    {
        var input = Encrypted(4);
        Assert.Throws<ArgumentException>(() => Change(input, new PdfSecurityChanges(), out _));
        Assert.Throws<ArgumentException>(() => Change(input, new PdfSecurityChanges { CurrentOwnerPassword = "o-pass", Algorithm = PdfEncryptionAlgorithm.Rc4_40 }, out _));
        Assert.Throws<ArgumentException>(() => Change(input, new PdfSecurityChanges { CurrentOwnerPassword = "o-pass", Permissions = (PdfPermissions)1 }, out _));
        Assert.Throws<PdfPasswordEncodingException>(() => Change(input, new PdfSecurityChanges { CurrentOwnerPassword = "o-pass", UserPassword = "密码" }, out _));
    }

    [Fact]
    public void ChangeSecurity_WithInfoChanges()
    {
        var output = Change(Encrypted(4), new PdfSecurityChanges { CurrentOwnerPassword = "o-pass", InfoChanges = new PdfInfoChanges { Title = "Re-secured" } }, out var result);
        Assert.True(result.DocumentInfoChanged);
        Assert.Equal("Re-secured", Pdf.GetInfo(output, "u-pass").Info!.Title);
    }

    [Fact]
    public void Secrets_AreNotInToString()
    {
        var changes = new PdfSecurityChanges { CurrentOwnerPassword = "secret-1", UserPassword = "secret-2", OwnerPassword = "secret-3" };
        Assert.DoesNotContain("secret", changes.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", new PdfDecryptOptions { Password = "secret" }.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", new PdfEditInfoOptions { Password = "secret" }.ToString(), StringComparison.Ordinal);
    }
}
