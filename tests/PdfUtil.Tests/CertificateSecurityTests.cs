using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfUtil.Internal.Objects;
using PdfUtil.Signatures;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>F18: certificate (public-key) encryption.</summary>
public class CertificateSecurityTests
{
    private static readonly PdfCertificateSecurity Security = new();

    private static byte[] Protect(byte[] pdf, PdfCertificateProtectionOptions options, out PdfCertificateProtectionResult result)
    {
        using var output = new MemoryStream();
        result = Security.ProtectAsync(new MemoryStream(pdf), output, options).GetAwaiter().GetResult();
        return output.ToArray();
    }

    private static byte[] Decrypt(byte[] pdf, X509Certificate2 certificate, bool allowRestricted = false)
    {
        using var output = new MemoryStream();
        Security.DecryptAsync(new MemoryStream(pdf), output, new PdfCertificateDecryptOptions { Certificate = certificate, PrivateKey = (RSA?)TestCertificates.KeyFor(certificate), AllowRestrictedRecipient = allowRestricted }).GetAwaiter().GetResult();
        return output.ToArray();
    }

    private static PdfDocumentInfo Inspect(byte[] pdf, X509Certificate2 certificate, PdfInspectionDetails details = PdfInspectionDetails.None) =>
        Security.InspectAsync(new MemoryStream(pdf), certificate, (RSA)TestCertificates.KeyFor(certificate)!, details).GetAwaiter().GetResult();

    private static void DecryptWithAttachedKeyOnly(byte[] pdf, X509Certificate2 certificate) =>
        Security.DecryptAsync(new MemoryStream(pdf), new MemoryStream(), new PdfCertificateDecryptOptions { Certificate = certificate }).GetAwaiter().GetResult();

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256)]
    [InlineData(PdfEncryptionAlgorithm.Aes128)]
    public void Protect_TwoRecipients_EachCanOpen(PdfEncryptionAlgorithm algorithm)
    {
        var alice = TestCertificates.Rsa("Alice");
        var bob = TestCertificates.Rsa("Bob");
        var input = new TestPdfBuilder { Rich = true, Xmp = TestPdfBuilder.PlainXmp() }.Build();
        var output = Protect(input, new PdfCertificateProtectionOptions
        {
            Recipients = new[] { new PdfCertificateRecipient(alice), new PdfCertificateRecipient(bob, PdfPermissions.Print) },
            Algorithm = algorithm,
        }, out var result);
        Assert.Equal(2, result.RecipientCount);
        Assert.Equal(algorithm == PdfEncryptionAlgorithm.Aes256 ? "1.7" : "1.7", result.OutputVersion);

        // Without a certificate: details only, nothing thrown.
        var locked = Pdf.GetInfo(output);
        Assert.True(locked.RequiresCertificate);
        Assert.Null(locked.PageCount);
        Assert.Equal("Adobe.PubSec", locked.Encryption!.Filter);
        Assert.Equal("adbe.pkcs7.s5", locked.Encryption.SubFilter);
        Assert.Equal(2, locked.Encryption.RecipientCount);
        Assert.Equal(algorithm, locked.Encryption.Algorithm);
        var check = Pdf.VerifyPassword(output, "anything");
        Assert.True(check.RequiresCertificate);
        Assert.False(check.IsValid);
        Assert.True(Pdf.IsEncrypted(output));
        Assert.DoesNotContain("Hello page", System.Text.Encoding.Latin1.GetString(output), StringComparison.Ordinal);

        // With Alice's or Bob's certificate: everything.
        var info = Inspect(output, alice, PdfInspectionDetails.All);
        Assert.Equal(2, info.PageCount);
        Assert.Equal(PdfPasswordMatch.User, info.Encryption!.AuthenticatedAs);
        Assert.Equal("Test document", info.Info!.Title);
        Assert.Equal(8, info.FormFields.Count);
        var asBob = Inspect(output, bob);
        Assert.Equal(PdfPermissions.Print, asBob.Encryption!.Permissions);

        // Alice has every permission and may remove the protection; the content is byte for byte the source's.
        var plain = Decrypt(output, alice);
        Assert.False(Pdf.IsEncrypted(plain));
        ContentPreservationTests.AssertStreamsPreserved(input, null, plain, null);
        Assert.Equal(Pdf.ExtractText(input).Text, Pdf.ExtractText(plain).Text);
    }

    [Fact]
    public void RestrictedOrUnknownRecipients()
    {
        var alice = TestCertificates.Rsa("Alice");
        var bob = TestCertificates.Rsa("Bob");
        var eve = TestCertificates.Rsa("Eve");
        var output = Protect(new TestPdfBuilder().Build(), new PdfCertificateProtectionOptions { Recipients = new[] { new PdfCertificateRecipient(alice), new PdfCertificateRecipient(bob, PdfPermissions.Print) } }, out _);
        Assert.Throws<PdfInvalidPasswordException>(() => Decrypt(output, bob));
        Assert.False(Pdf.IsEncrypted(Decrypt(output, bob, allowRestricted: true)));
        Assert.Throws<PdfInvalidPasswordException>(() => Decrypt(output, eve));
        Assert.True(Inspect(output, eve).RequiresCertificate);
    }

    [Fact]
    public void EncryptMetadataFalse_AndObjectStreams()
    {
        var alice = TestCertificates.Rsa("Alice");
        var input = new TestPdfBuilder { Xmp = TestPdfBuilder.PlainXmp() }.Build();
        var output = Protect(input, new PdfCertificateProtectionOptions { Recipients = new[] { new PdfCertificateRecipient(alice) }, EncryptMetadata = false, Write = new PdfWriteOptions { UseObjectStreams = true } }, out _);
        Assert.Contains("Plain XMP", System.Text.Encoding.UTF8.GetString(output), StringComparison.Ordinal);
        Assert.False(Pdf.GetInfo(output).Encryption!.EncryptMetadata);
        Assert.Equal("Plain XMP", Pdf.GetInfo(Decrypt(output, alice)).Xmp!.Title);
        using var doc = TestHelpers.Open(output, strict: true);
        Assert.True(doc.HasObjectStreams);
    }

    [Fact]
    public void InvalidInputs()
    {
        var alice = TestCertificates.Rsa("Alice");
        var input = new TestPdfBuilder().Build();
        Assert.Throws<ArgumentException>(() => Protect(input, new PdfCertificateProtectionOptions(), out _));
        Assert.Throws<ArgumentException>(() => Protect(input, new PdfCertificateProtectionOptions { Recipients = new[] { new PdfCertificateRecipient(alice) }, Algorithm = PdfEncryptionAlgorithm.Rc4_128 }, out _));
        Assert.Throws<ArgumentException>(() => Protect(input, new PdfCertificateProtectionOptions { Recipients = new[] { new PdfCertificateRecipient(TestCertificates.Ecdsa()) } }, out _));
        Assert.Throws<PdfAlreadyEncryptedException>(() => Protect(DecryptTests.Encrypted(6), new PdfCertificateProtectionOptions { Recipients = new[] { new PdfCertificateRecipient(alice) } }, out _));
        Assert.Throws<PdfNotEncryptedException>(() => Decrypt(input, alice));
        Assert.Throws<ArgumentException>(() => DecryptWithAttachedKeyOnly(input, alice));
    }

    [Fact]
    public void EncryptDictionary_FollowsTheSpecification()
    {
        var alice = TestCertificates.Rsa("Alice");
        var output = Protect(new TestPdfBuilder().Build(), new PdfCertificateProtectionOptions { Recipients = new[] { new PdfCertificateRecipient(alice) } }, out _);
        using var doc = TestHelpers.Open(output, strict: true);
        var dict = doc.GetEncryptDictionary()!;
        Assert.Equal("Adobe.PubSec", ((PdfName)dict["Filter"]!).Value);
        Assert.Equal(5, ((PdfInteger)dict["V"]!).Value);
        Assert.Equal(256, ((PdfInteger)dict["Length"]!).Value);
        var cf = (PdfDictionary)((PdfDictionary)dict["CF"]!)["DefaultCryptFilter"]!;
        Assert.Equal("AESV3", ((PdfName)cf["CFM"]!).Value);
        var recipient = Assert.Single(((PdfArray)cf["Recipients"]!).Items);
        var envelope = new System.Security.Cryptography.Pkcs.EnvelopedCms();
        envelope.Decode(((PdfString)recipient).Value);
        Assert.Equal("2.16.840.1.101.3.4.1.42", envelope.ContentEncryptionAlgorithm.Oid.Value); // AES-256-CBC
    }
}
