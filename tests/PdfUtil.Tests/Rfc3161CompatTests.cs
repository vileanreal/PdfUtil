using System.Formats.Asn1;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PdfUtil.Signatures.Internal;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>
/// The RFC 3161 and CMS code that the .NET Framework build uses in place of Rfc3161TimestampRequest,
/// Rfc3161TimestampToken and SignerInfo.AddUnsignedAttribute, checked against those .NET classes.
/// </summary>
public class Rfc3161CompatTests
{
    private const string TimestampTokenOid = "1.2.840.113549.1.9.16.2.14";

    private static SignedCms SignedData(out SignerInfo info)
    {
        var cert = TestCertificates.Rsa();
        var cms = new SignedCms(new ContentInfo(new byte[] { 1, 2, 3, 4 }), detached: true);
        cms.ComputeSignature(new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert, TestCertificates.KeyFor(cert)) { IncludeOption = X509IncludeOption.EndCertOnly }, silent: true);
        info = cms.SignerInfos[0];
        return cms;
    }

    private static byte[] Post(byte[] request)
    {
        using var client = new HttpClient(new TestCertificates.FakeTsa());
        using var body = new ByteArrayContent(request);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
        using var response = client.PostAsync(new Uri("http://tsa.test/"), body).GetAwaiter().GetResult();
        return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public void SignatureValue_MatchesSignerInfo()
    {
        var cms = SignedData(out var info);
        Assert.Equal(info.GetSignature(), Rfc3161.SignatureValue(cms.Encode()));
    }

    [Fact]
    public void CreateRequest_DecodesAsRfc3161Request()
    {
        var cms = SignedData(out var info);
        var nonce = new byte[] { 0x80, 1, 2, 3, 4, 5, 6, 7 };
        var encoded = Rfc3161.CreateRequest(Rfc3161.SignatureValue(cms.Encode()), nonce);

        Assert.True(Rfc3161TimestampRequest.TryDecode(encoded, out var request, out var read));
        Assert.Equal(encoded.Length, read);
        Assert.Equal("2.16.840.1.101.3.4.2.1", request!.HashAlgorithmId.Value);
        Assert.Equal(SHA256.HashData(info.GetSignature()), request.GetMessageHash().ToArray());
        Assert.True(request.RequestSignerCertificate);

        // The nonce is a positive INTEGER, so a leading 0x80 byte gets a 0x00 in front of it.
        Assert.Equal(new byte[] { 0 }.Concat(nonce), request.GetNonce()!.Value.ToArray());
    }

    [Fact]
    public void ProcessResponse_AddUnsignedAttribute_GivesAValidTimestampedSignature()
    {
        var cms = SignedData(out _);
        var encoded = cms.Encode();
        var signature = Rfc3161.SignatureValue(encoded);
        var nonce = RandomNumberGenerator.GetBytes(8);

        var token = Rfc3161.ProcessResponse(Post(Rfc3161.CreateRequest(signature, nonce)), signature, nonce);
        var stamped = Rfc3161.AddUnsignedAttribute(encoded, TimestampTokenOid, token.Encoded);

        var decoded = new SignedCms(new ContentInfo(new byte[] { 1, 2, 3, 4 }), detached: true);
        decoded.Decode(stamped);
        decoded.CheckSignature(verifySignatureOnly: true);
        var info = decoded.SignerInfos[0];
        var attribute = Assert.Single(info.UnsignedAttributes.Cast<CryptographicAttributeObject>());
        Assert.Equal(TimestampTokenOid, attribute.Oid.Value);
        Assert.True(Rfc3161TimestampToken.TryDecode(attribute.Values[0].RawData, out var bclToken, out _));
        Assert.True(bclToken!.VerifySignatureForSignerInfo(info, out _));
        Assert.Equal(bclToken.TokenInfo.Timestamp, token.Timestamp);
        Assert.Equal(info.GetSignature(), Rfc3161.SignatureValue(stamped));
    }

    [Fact]
    public void AddUnsignedAttribute_KeepsExistingUnsignedAttributes()
    {
        var cms = SignedData(out var info);
        info.AddUnsignedAttribute(new AsnEncodedData(new Oid("1.2.3.4.5"), new byte[] { 0x05, 0x00 }));
        var stamped = Rfc3161.AddUnsignedAttribute(cms.Encode(), "1.2.3.4.6", new byte[] { 0x02, 0x01, 0x07 });

        var decoded = new SignedCms(new ContentInfo(new byte[] { 1, 2, 3, 4 }), detached: true);
        decoded.Decode(stamped);
        decoded.CheckSignature(verifySignatureOnly: true);
        var oids = decoded.SignerInfos[0].UnsignedAttributes.Cast<CryptographicAttributeObject>().Select(a => a.Oid.Value).Order().ToList();
        Assert.Equal(new[] { "1.2.3.4.5", "1.2.3.4.6" }, oids);
    }

    [Fact]
    public void ProcessResponse_RejectsAnotherNonce()
    {
        var cms = SignedData(out _);
        var signature = Rfc3161.SignatureValue(cms.Encode());
        var response = Post(Rfc3161.CreateRequest(signature, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.Throws<CryptographicException>(() => Rfc3161.ProcessResponse(response, signature, new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 }));
    }

    [Fact]
    public void ProcessResponse_RejectsATokenForOtherData()
    {
        var cms = SignedData(out _);
        var signature = Rfc3161.SignatureValue(cms.Encode());
        var nonce = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var response = Post(Rfc3161.CreateRequest(signature, nonce));
        Assert.Throws<CryptographicException>(() => Rfc3161.ProcessResponse(response, new byte[] { 9, 9, 9 }, nonce));
    }

    [Fact]
    public void ProcessResponse_RejectsARejection()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteInteger(2);
            }
        }

        Assert.Throws<CryptographicException>(() => Rfc3161.ProcessResponse(writer.Encode(), new byte[] { 1 }, new byte[] { 1 }));
    }

    [Fact]
    public void TimestampToken_VerifiesForData_LikeRfc3161TimestampToken()
    {
        var data = new byte[] { 10, 20, 30 };
        var time = new TestCertificates.FakeTsa().Time;
        var encoded = TestCertificates.TimestampToken(new Oid("2.16.840.1.101.3.4.2.1"), SHA256.HashData(data), null, time);

        Assert.True(TimestampToken.TryDecode(encoded, out var token));
        Assert.True(token!.VerifySignatureForData(data, out var signer));
        Assert.Equal(TestCertificates.Tsa.Thumbprint, signer!.Thumbprint);
        Assert.Equal(time, token.Timestamp);
        Assert.False(token.VerifySignatureForData(new byte[] { 1 }, out _));

        Assert.True(Rfc3161TimestampToken.TryDecode(encoded, out var bclToken, out _));
        Assert.Equal(bclToken!.TokenInfo.Timestamp, token.Timestamp);
        Assert.Equal(bclToken.TokenInfo.HashAlgorithmId.Value, token.HashAlgorithmId);
    }

    [Fact]
    public void TimestampToken_TryDecode_RejectsATimeOutsideTheTsaCertificate()
    {
        var encoded = TestCertificates.TimestampToken(new Oid("2.16.840.1.101.3.4.2.1"), SHA256.HashData(new byte[] { 1 }), null, TestCertificates.Tsa.NotAfter.AddDays(1));
        Assert.False(Rfc3161TimestampToken.TryDecode(encoded, out _, out _));
        Assert.False(TimestampToken.TryDecode(encoded, out _));
    }

    [Fact]
    public void TimestampToken_TryDecode_RejectsOtherContent()
    {
        var cms = SignedData(out _);
        Assert.False(TimestampToken.TryDecode(cms.Encode(), out _));
        Assert.False(TimestampToken.TryDecode(new byte[] { 0x30, 0x00 }, out _));
    }
}
