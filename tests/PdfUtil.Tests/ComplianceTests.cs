using PdfUtil.Internal.Objects;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

public class ComplianceTests
{
    [Theory]
    [InlineData(1, "B")]
    [InlineData(2, "U")]
    [InlineData(3, "A")]
    [InlineData(4, "F")]
    public void PdfA_DefaultPolicy_Throws(int part, string conformance)
    {
        var input = new TestPdfBuilder { Xmp = TestPdfBuilder.PdfAXmp(part, conformance) }.Build();
        var ex = Assert.Throws<PdfComplianceException>(() => Pdf.Protect(input, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256)));
        Assert.Contains($"PDF/A-{part}{conformance}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfA_CompressedXmp_IsDetected()
    {
        var input = new TestPdfBuilder { Xmp = TestPdfBuilder.PdfAXmp(2, "B"), CompressXmp = true }.Build();
        Assert.Throws<PdfComplianceException>(() => Pdf.Protect(input, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256)));
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256)]
    [InlineData(PdfEncryptionAlgorithm.Aes128)]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128)]
    public void PdfA_ProceedPolicy_EncryptsWithWarning(PdfEncryptionAlgorithm algorithm)
    {
        var input = new TestPdfBuilder { Xmp = TestPdfBuilder.PdfAXmp(2, "B") }.Build();
        var options = TestHelpers.Options(algorithm);
        options.PdfAPolicy = PdfAPolicy.Proceed;
        var (output, result) = TestHelpers.Protect(input, options);
        Assert.Contains(result.Warnings, w => w.Contains("PDF/A-2B", StringComparison.Ordinal));
        Assert.True(Pdf.VerifyPassword(output, "user").IsValid);
        Assert.Equal(2, Pdf.GetInfo(output, "user").Xmp!.PdfAPart);
    }

    [Fact]
    public void NonPdfA_Xmp_DoesNotTrigger()
    {
        var input = new TestPdfBuilder { Xmp = TestPdfBuilder.PlainXmp() }.Build();
        var (_, result) = TestHelpers.Protect(input, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("PDF/A", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256)]
    [InlineData(PdfEncryptionAlgorithm.Aes128)]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128)]
    public void SignedInput_ProducesWarning_AndSignatureContentsAreNotEncrypted(PdfEncryptionAlgorithm algorithm)
    {
        var input = new TestPdfBuilder { Signed = true }.Build();
        Assert.True(Pdf.GetInfo(input).IsSigned);
        var (output, result) = TestHelpers.Protect(input, TestHelpers.Options(algorithm));
        Assert.Contains(result.Warnings, w => w.Contains("signed", StringComparison.OrdinalIgnoreCase) && w.Contains("invalidates", StringComparison.Ordinal));

        // /Contents of the signature dictionary is written as-is (it is never encrypted).
        var expectedHex = Convert.ToHexString(Enumerable.Range(0, 64).Select(i => (byte)(i * 3)).ToArray());
        Assert.Contains("/Contents <" + expectedHex + ">", System.Text.Encoding.Latin1.GetString(output));

        // ...while other strings of the same dictionary are encrypted and decrypt correctly.
        using var doc = TestHelpers.Open(output, strict: true);
        doc.Authenticate("user");
        var sig = doc.Xref.Keys.Select(doc.GetObject).OfType<PdfDictionary>().Single(d => d["Type"] is PdfName { Value: "Sig" });
        Assert.Equal("D:20240101000000Z", System.Text.Encoding.ASCII.GetString(((PdfString)sig["M"]!).Value));
        Assert.Equal(expectedHex, Convert.ToHexString(((PdfString)sig["Contents"]!).Value));
        Assert.DoesNotContain("D:20240101000000Z", System.Text.Encoding.Latin1.GetString(output));
    }
}
