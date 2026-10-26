using System.Text;
using PdfUtil.Internal.Security;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>Section 8.1: password encoding rules per revision.</summary>
public class PasswordEncodingTests
{
    private static readonly byte[] Input = new TestPdfBuilder().Build();

    [Fact]
    public void Aes256_NfcAndNfdForms_BothVerify()
    {
        var nfc = "Café-Ångström";
        var nfd = nfc.Normalize(NormalizationForm.FormD);
        Assert.NotEqual(nfc, nfd);

        // Composed characters are normalised differently by readers, so the default policy rejects them.
        Assert.Throws<PdfPasswordEncodingException>(() => Pdf.Protect(Input, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256, nfc, "owner")));
        Assert.Throws<PdfPasswordEncodingException>(() => Pdf.Protect(Input, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256, nfd, "owner")));

        var (fromNfc, r1) = TestHelpers.Protect(Input, Spec(TestHelpers.Options(PdfEncryptionAlgorithm.Aes256, nfc, "owner")));
        Assert.False(r1.UserPasswordWasNormalized);
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(fromNfc, nfc).MatchedAs);
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(fromNfc, nfd).MatchedAs);

        var (fromNfd, r2) = TestHelpers.Protect(Input, Spec(TestHelpers.Options(PdfEncryptionAlgorithm.Aes256, nfd, "owner")));
        Assert.True(r2.UserPasswordWasNormalized);
        Assert.Contains(r2.Warnings, w => w.Contains("SASLprep", StringComparison.Ordinal));
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(fromNfd, nfd).MatchedAs);
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(fromNfd, nfc).MatchedAs);
    }

    [Fact]
    public void Aes256_UnicodeSpaces_MapToAsciiSpace()
    {
        var withNbsp = "pass word　x";
        Assert.Throws<PdfPasswordEncodingException>(() => Pdf.Protect(Input, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256, withNbsp, "owner")));
        var (output, _) = TestHelpers.Protect(Input, Spec(TestHelpers.Options(PdfEncryptionAlgorithm.Aes256, withNbsp, "owner")));
        Assert.True(Pdf.VerifyPassword(output, withNbsp).IsValid);
        Assert.True(Pdf.VerifyPassword(output, "pass word x").IsValid);
        Assert.False(Pdf.VerifyPassword(output, "password x").IsValid);
    }

    [Fact]
    public void Aes256_SoftHyphenAndZeroWidthCharacters_AreMappedToNothing()
    {
        var (output, _) = TestHelpers.Protect(Input, Spec(TestHelpers.Options(PdfEncryptionAlgorithm.Aes256, "pa­ss﻿word", "owner")));
        Assert.True(Pdf.VerifyPassword(output, "password").IsValid);
    }

    [Fact]
    public void Aes256_PasswordsLongerThan127Bytes_AreTruncatedConsistently()
    {
        var longPassword = string.Concat(Enumerable.Repeat("密码", 40)); // 240 UTF-8 bytes
        var (output, _) = TestHelpers.Protect(Input, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256, longPassword, "owner"));
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, longPassword).MatchedAs);

        // Only the first 127 bytes matter (a split UTF-8 sequence included).
        var bytes = Encoding.UTF8.GetBytes(longPassword);
        Assert.Equal(127, PasswordEncoder.EncodeForProtection(longPassword, 6, false, "user", PdfPasswordPolicy.Interoperable, out _).Length);
        Assert.Equal(bytes.AsSpan(0, 127).ToArray(), PasswordEncoder.EncodeForVerification(longPassword, 6)[0]);
        Assert.True(Pdf.VerifyPassword(output, longPassword + "different tail").IsValid);

        var ascii = new string('k', 200);
        var (o2, _) = TestHelpers.Protect(Input, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256, ascii, "owner"));
        Assert.True(Pdf.VerifyPassword(o2, new string('k', 127)).IsValid);
        Assert.False(Pdf.VerifyPassword(o2, new string('k', 126)).IsValid);
    }

    [Theory]
    [InlineData("ΚΩΔΙΚΟΣ")]
    [InlineData("密码")]
    [InlineData("пароль")]
    [InlineData("كلمة")]
    [InlineData("東京-Москва")]
    public void Aes256_UnicodePasswords_Verify(string password)
    {
        var owner = password + password + "2" + password; // same script, so the bidi rule holds
        var (output, _) = TestHelpers.Protect(Input, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256, password, owner));
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, password).MatchedAs);
        Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(output, owner).MatchedAs);
    }

    [Theory]
    [InlineData("pässwörd")]
    [InlineData("한국어")]
    [InlineData("😀🔐")]
    [InlineData("x𝄞")]
    [InlineData("ﬁle")]
    public void Aes256_ReaderDependentPasswords_RequireSpecificationPolicy(string password)
    {
        var ex = Assert.Throws<PdfPasswordEncodingException>(() => Pdf.Protect(Input, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256, password, "owner")));
        Assert.Contains("PasswordPolicy", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(password, ex.Message, StringComparison.Ordinal);
        var (output, _) = TestHelpers.Protect(Input, Spec(TestHelpers.Options(PdfEncryptionAlgorithm.Aes256, password, "owner")));
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, password).MatchedAs);
    }

    [Theory]
    [InlineData("pass\u0007word")]
    [InlineData("privateuse")]
    [InlineData("الa")]
    public void Aes256_SaslPrepProhibitedPasswords_AreRejectedClearly(string password)
    {
        var ex = Assert.Throws<PdfPasswordEncodingException>(() => Pdf.Protect(Input, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256, password, "owner")));
        Assert.Contains("SASLprep", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(password, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes128, "密码")]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128, "пароль")]
    [InlineData(PdfEncryptionAlgorithm.Aes128, "emoji😀")]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128, "Ā")]
    public void Legacy_NonPdfDocEncodingPasswords_AreRejected(PdfEncryptionAlgorithm algorithm, string password)
    {
        var ex = Assert.Throws<PdfPasswordEncodingException>(() => Pdf.Protect(Input, TestHelpers.Options(algorithm, password, "owner")));
        Assert.Contains("PDFDocEncoding", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(password, ex.Message, StringComparison.Ordinal);
        Assert.Throws<PdfPasswordEncodingException>(() => Pdf.Protect(Input, TestHelpers.Options(algorithm, "user", password)));
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes128)]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128)]
    public void Legacy_AsciiPasswords_Work(PdfEncryptionAlgorithm algorithm)
    {
        foreach (var password in new[] { "Cafe nandu", "  spaces  ", "UPPER lower", "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~", "0123456789" })
        {
            var (output, _) = TestHelpers.Protect(Input, TestHelpers.Options(algorithm, password, "owner"));
            Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, password).MatchedAs);
            Assert.False(Pdf.VerifyPassword(output, password.Trim()).IsValid && password != password.Trim());
            Assert.False(Pdf.VerifyPassword(output, password.ToUpperInvariant()).IsValid && password != password.ToUpperInvariant());
        }
    }

    /// <summary>
    /// Characters that are in PDFDocEncoding but that readers encode differently for legacy passwords (pdf.js uses
    /// the low byte of the code point, Apple PDFKit UTF-8, Acrobat historically the platform code page) are
    /// rejected, so a protected file never opens in one viewer and fails in another.
    /// </summary>
    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes128, "price€")]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128, "a\u2022b")]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128, "\u201Cquoted\u201D")]
    [InlineData(PdfEncryptionAlgorithm.Aes128, "\u0141\u0153\u017E")]
    [InlineData(PdfEncryptionAlgorithm.Aes128, "soft\u00ADhyphen")]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128, "tab\there")]
    [InlineData(PdfEncryptionAlgorithm.Aes128, "Café")]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128, "ñandú")]
    [InlineData(PdfEncryptionAlgorithm.Aes128, "Ü\u00FF")]
    public void Legacy_InconsistentlyEncodedCharacters_AreRejected(PdfEncryptionAlgorithm algorithm, string password)
    {
        var ex = Assert.Throws<PdfPasswordEncodingException>(() => Pdf.Protect(Input, TestHelpers.Options(algorithm, password, "owner")));
        Assert.Contains("AES-256", ex.Message, StringComparison.Ordinal);

        // The Specification policy applies only the PDFDocEncoding rule.
        var (output, _) = TestHelpers.Protect(Input, Spec(TestHelpers.Options(algorithm, password, "owner")));
        Assert.True(Pdf.VerifyPassword(output, password).IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_ThirdPartyFiles_WithLatin1OrUtf8Passwords_Verify(bool utf8)
    {
        var input = new TestPdfBuilder { Encryption = (3, "Café-ñ", "Öwner", true), LegacyPasswordUtf8 = utf8 }.Build();
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(input, "Café-ñ").MatchedAs);
        Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(input, "Öwner").MatchedAs);
        Assert.False(Pdf.VerifyPassword(input, "Cafe-n").IsValid);
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes128)]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128)]
    public void Legacy_PasswordsLongerThan32Bytes_RequireOptIn(PdfEncryptionAlgorithm algorithm)
    {
        var password = new string('x', 40);
        var ex = Assert.Throws<PdfPasswordEncodingException>(() => Pdf.Protect(Input, TestHelpers.Options(algorithm, password, "owner")));
        Assert.Contains("32 bytes", ex.Message, StringComparison.Ordinal);

        var options = TestHelpers.Options(algorithm, password, "owner");
        options.AllowPasswordTruncation = true;
        var (output, _) = TestHelpers.Protect(Input, options);
        Assert.True(Pdf.VerifyPassword(output, password).IsValid);
        Assert.True(Pdf.VerifyPassword(output, new string('x', 32)).IsValid);
        Assert.False(Pdf.VerifyPassword(output, new string('x', 31)).IsValid);

        var exact = new string('y', 32);
        var (o2, _) = TestHelpers.Protect(Input, TestHelpers.Options(algorithm, exact, "owner"));
        Assert.True(Pdf.VerifyPassword(o2, exact).IsValid);
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256)]
    [InlineData(PdfEncryptionAlgorithm.Aes128)]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128)]
    public void EmptyUserPassword_WithOwnerPassword(PdfEncryptionAlgorithm algorithm)
    {
        foreach (var empty in new[] { null, "" })
        {
            var (output, _) = TestHelpers.Protect(Input, TestHelpers.Options(algorithm, empty, "owner"));
            var check = Pdf.VerifyPassword(output, null);
            Assert.Equal(PdfPasswordMatch.User, check.MatchedAs);
            Assert.True(check.OpensWithoutPassword);
            Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, "").MatchedAs);
            Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(output, "owner").MatchedAs);
            Assert.True(Pdf.VerifyPassword(output, "owner").OpensWithoutPassword);

            // Inspection without a password works because the empty user password opens the file.
            var info = Pdf.GetInfo(output);
            Assert.False(info.RequiresPassword);
            Assert.Equal(PdfPasswordMatch.User, info.Encryption!.AuthenticatedAs);
            Assert.Equal("Test document", info.Info!.Title);
        }
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256)]
    [InlineData(PdfEncryptionAlgorithm.Aes128)]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128)]
    public void UserPasswordOnly_GeneratesHiddenOwnerPassword(PdfEncryptionAlgorithm algorithm)
    {
        var (output, _) = TestHelpers.Protect(Input, TestHelpers.Options(algorithm, "user", null));
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, "user").MatchedAs);
        Assert.False(Pdf.VerifyPassword(output, "").IsValid);
    }

    [Fact]
    public void SameUserAndOwnerPassword_MatchesAsOwner_WithWarning()
    {
        var (output, result) = TestHelpers.Protect(Input, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256, "same", "same"));
        Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(output, "same").MatchedAs);
        Assert.Contains(result.Warnings, w => w.Contains("identical", StringComparison.Ordinal));
    }

    [Fact]
    public void NoPasswordsAtAll_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => Pdf.Protect(Input, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256, null, null)));
        Assert.Throws<ArgumentException>(() => Pdf.Protect(Input, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256, "", "")));
    }

    [Fact]
    public void SaslPrep_Behaviour()
    {
        Assert.True(SaslPrep.TryPrepare("I­X", out var a));
        Assert.Equal("IX", a);
        Assert.True(SaslPrep.TryPrepare("ª", out var b));
        Assert.Equal("a", b);
        Assert.True(SaslPrep.TryPrepare("Ⅸ", out var c));
        Assert.Equal("IX", c);
        Assert.True(SaslPrep.TryPrepare("user", out var d));
        Assert.Equal("user", d);
        Assert.False(SaslPrep.TryPrepare("\u0007", out _));
        Assert.True(SaslPrep.TryPrepare("אב", out _)); // all RandAL
        Assert.False(SaslPrep.TryPrepare("אaב", out _)); // RandAL mixed with L
        Assert.False(SaslPrep.TryPrepare("א1", out _)); // must end with RandAL
        Assert.True(SaslPrep.TryPrepare("א1ב", out _)); // digits in between are fine
        Assert.True(SaslPrep.TryPrepare("😀", out var e)); // unassigned in Unicode 3.2: allowed (query semantics)
        Assert.Equal("😀", e);
    }

    private static PdfProtectionOptions Spec(PdfProtectionOptions o)
    {
        o.PasswordPolicy = PdfPasswordPolicy.Specification;
        return o;
    }

    [Fact]
    public void Encoder_NeverTrimsOrCaseFolds()
    {
        Assert.Equal(Encoding.ASCII.GetBytes(" Ab "), PasswordEncoder.EncodeForProtection(" Ab ", 4, false, "user", PdfPasswordPolicy.Interoperable, out _));
        Assert.Equal(Encoding.UTF8.GetBytes(" Ab "), PasswordEncoder.EncodeForProtection(" Ab ", 6, false, "user", PdfPasswordPolicy.Interoperable, out _));
        Assert.Empty(PasswordEncoder.EncodeForProtection(null, 6, false, "user", PdfPasswordPolicy.Interoperable, out _));
        Assert.Empty(PasswordEncoder.EncodeForProtection("", 3, false, "user", PdfPasswordPolicy.Interoperable, out _));
    }
}
