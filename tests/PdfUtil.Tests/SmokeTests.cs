using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

public class SmokeTests
{
    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256)]
    [InlineData(PdfEncryptionAlgorithm.Aes128)]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128)]
    public void ProtectThenVerify_RoundTrips(PdfEncryptionAlgorithm algorithm)
    {
        var input = new TestPdfBuilder().Build();
        var (output, result) = TestHelpers.Protect(input, TestHelpers.Options(algorithm));
        Assert.Equal(algorithm, result.Algorithm);

        var user = Pdf.VerifyPassword(output, "user");
        Assert.True(user.IsEncrypted);
        Assert.True(user.IsValid);
        Assert.Equal(PdfPasswordMatch.User, user.MatchedAs);
        Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(output, "owner").MatchedAs);
        Assert.False(Pdf.VerifyPassword(output, "wrong").IsValid);

        var info = Pdf.GetInfo(output, "user");
        Assert.Equal(2, info.PageCount);
        Assert.Equal("Test document", info.Info!.Title);
    }
}
