using System.Formats.Asn1;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace PdfUtil.WinFormsSample;

/// <summary>Self-signed certificates for the self-test, and a time-stamping authority that runs in-process.</summary>
internal static class TestCertificates
{
    private const string TimeStampingEku = "1.3.6.1.5.5.7.3.8";
    private const string TstInfoOid = "1.2.840.113549.1.9.16.1.4";
    private const string SigningCertificateV2Oid = "1.2.840.113549.1.9.16.2.47";
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";

    private static readonly Lazy<X509Certificate2> LazySigner = new(() => Create("CN=PdfUtil Sample Signer, O=PdfUtil", null));
    private static readonly Lazy<X509Certificate2> LazyTsa = new(() => Create("CN=PdfUtil Sample TSA, O=PdfUtil", TimeStampingEku));

    /// <summary>An RSA signing certificate with its private key attached (what .NET Framework requires).</summary>
    public static X509Certificate2 Signer => LazySigner.Value;

    public static X509Certificate2 Tsa => LazyTsa.Value;

    private static X509Certificate2 Create(string subject, string? eku)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation | X509KeyUsageFlags.KeyEncipherment, false));
        if (eku is not null)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(eku) }, true));
        }

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // A round trip through PFX gives a key that .NET Framework's SignedCms and EnvelopedCms can use.
        const string password = "sample";
        return new X509Certificate2(certificate.Export(X509ContentType.Pfx, password), password, X509KeyStorageFlags.Exportable);
    }

    /// <summary>An HTTP handler that answers RFC 3161 requests like a time-stamping authority.</summary>
    public sealed class LocalTsa : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var body = await request.Content!.ReadAsByteArrayAsync().ConfigureAwait(false);

            // TimeStampReq ::= SEQUENCE { version, messageImprint, reqPolicy OPTIONAL, nonce OPTIONAL, certReq OPTIONAL, ... }
            var req = new AsnReader(body, AsnEncodingRules.DER).ReadSequence();
            req.ReadInteger();
            var imprint = req.ReadEncodedValue().ToArray();
            if (req.HasData && req.PeekTag().HasSameClassAndValue(Asn1Tag.ObjectIdentifier))
            {
                req.ReadObjectIdentifier();
            }

            byte[]? nonce = req.HasData && req.PeekTag().HasSameClassAndValue(Asn1Tag.Integer) ? req.ReadEncodedValue().ToArray() : null;

            var response = new AsnWriter(AsnEncodingRules.DER);
            using (response.PushSequence())
            {
                using (response.PushSequence())
                {
                    response.WriteInteger(0); // granted
                }

                response.WriteEncodedValue(Token(imprint, nonce));
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(response.Encode()) };
        }

        private static byte[] Token(byte[] imprint, byte[]? nonce)
        {
            var tst = new AsnWriter(AsnEncodingRules.DER);
            using (tst.PushSequence())
            {
                tst.WriteInteger(1);
                tst.WriteObjectIdentifier("1.2.3.4.1");
                tst.WriteEncodedValue(imprint);
                tst.WriteInteger(DateTime.UtcNow.Ticks);
                tst.WriteGeneralizedTime(DateTimeOffset.UtcNow, omitFractionalSeconds: true);
                if (nonce is not null)
                {
                    tst.WriteEncodedValue(nonce);
                }
            }

            var cms = new SignedCms(new ContentInfo(new Oid(TstInfoOid), tst.Encode()));
            var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, Tsa)
            {
                IncludeOption = X509IncludeOption.EndCertOnly,
                DigestAlgorithm = new Oid(Sha256Oid),
            };
            signer.SignedAttributes.Add(new AsnEncodedData(new Oid(SigningCertificateV2Oid), SigningCertificateV2(Tsa)));
            cms.ComputeSignature(signer, silent: true);
            return cms.Encode();
        }

        /// <summary>SigningCertificateV2 ::= SEQUENCE { SEQUENCE OF ESSCertIDv2 { certHash (SHA-256) } }.</summary>
        private static byte[] SigningCertificateV2(X509Certificate2 certificate)
        {
            using var sha = SHA256.Create();
            var writer = new AsnWriter(AsnEncodingRules.DER);
            using (writer.PushSequence())
            using (writer.PushSequence())
            using (writer.PushSequence())
            {
                writer.WriteOctetString(sha.ComputeHash(certificate.RawData));
            }

            return writer.Encode();
        }
    }

    public static HttpClient TsaClient(LocalTsa tsa)
    {
        var client = new HttpClient(tsa);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/timestamp-reply"));
        return client;
    }
}
