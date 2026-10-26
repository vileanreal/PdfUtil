using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PdfUtil.Signatures.Internal;

namespace PdfUtil.Tests.Infrastructure;

/// <summary>Test certificates made with <see cref="CertificateRequest"/>, and a fake RFC 3161 time-stamping authority.</summary>
internal static class TestCertificates
{
    // On some macOS versions .NET cannot attach an RSA private key to a certificate (the keychain rejects it), so RSA
    // certificates here carry no key; the key is kept alongside and passed as PdfSignOptions.PrivateKey. ECDSA keys are
    // attached normally, so both ways of supplying a key are tested.
    private static readonly Dictionary<X509Certificate2, AsymmetricAlgorithm> Keys = new(ReferenceEqualityComparer.Instance);
    private static readonly RSA RootKey = RSA.Create(2048);
    private static readonly RSA TsaKey = RSA.Create(2048);

    private static readonly Lazy<X509Certificate2> LazyRoot = new(() =>
    {
        var request = new CertificateRequest("CN=PdfUtil Test Root CA", RootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.Create(request.SubjectName, X509SignatureGenerator.CreateForRSA(RootKey, RSASignaturePadding.Pkcs1), DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5), new byte[] { 1 });
    });

    public static X509Certificate2 Root => LazyRoot.Value;

    /// <summary>The private key of a certificate made here that has no attached key.</summary>
    public static AsymmetricAlgorithm? KeyFor(X509Certificate2 certificate)
    {
        lock (Keys)
        {
            return Keys.TryGetValue(certificate, out var key) ? key : null;
        }
    }

    public static X509Certificate2 Rsa(string name = "Jane Signer") => Issue(name, RSA.Create(2048));

    public static X509Certificate2 Ecdsa(string name = "Erin Ecdsa") => Issue(name, ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>A certificate issued by <see cref="Root"/>: ECDSA with its key attached, RSA with its key in <see cref="KeyFor"/>.</summary>
    private static X509Certificate2 Issue(string name, AsymmetricAlgorithm key)
    {
        var request = key switch
        {
            RSA rsa => new CertificateRequest($"CN={name}, O=PdfUtil Tests", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            ECDsa ec => new CertificateRequest($"CN={name}, O=PdfUtil Tests", ec, HashAlgorithmName.SHA256),
            _ => throw new ArgumentException("Unsupported key.", nameof(key)),
        };
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Root, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        var serial = RandomNumberGenerator.GetBytes(8);
        serial[0] &= 0x7F;
        var issued = request.Create(Root.SubjectName, X509SignatureGenerator.CreateForRSA(RootKey, RSASignaturePadding.Pkcs1), DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddYears(1), serial);
        if (key is ECDsa ecdsa)
        {
            return issued.CopyWithPrivateKey(ecdsa);
        }

        lock (Keys)
        {
            Keys[issued] = key;
        }

        return issued;
    }

    private static readonly Lazy<X509Certificate2> LazyTsa = new(() =>
    {
        var request = new CertificateRequest("CN=PdfUtil Test TSA", TsaKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.8") }, true));
        return request.Create(request.SubjectName, X509SignatureGenerator.CreateForRSA(TsaKey, RSASignaturePadding.Pkcs1), DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), new byte[] { 2 });
    });

    public static X509Certificate2 Tsa => LazyTsa.Value;

    /// <summary>A timestamp token for a message hash, signed by <see cref="Tsa"/>.</summary>
    public static byte[] TimestampToken(Oid hashAlgorithm, ReadOnlyMemory<byte> hash, ReadOnlyMemory<byte>? nonce, DateTimeOffset time)
    {
        var info = new Rfc3161TimestampTokenInfo(new Oid("1.2.3.4.1"), hashAlgorithm, hash, RandomNumberGenerator.GetBytes(8), time, nonce: nonce);
        var cms = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, Tsa, TsaKey) { IncludeOption = X509IncludeOption.EndCertOnly };
        signer.SignedAttributes.Add(new AsnEncodedData(new Oid("1.2.840.113549.1.9.16.2.47"), SigningEngine.SigningCertificateV2(Tsa)));
        cms.ComputeSignature(signer, silent: true);
        return cms.Encode();
    }

    /// <summary>An HTTP handler that answers RFC 3161 requests like a time-stamping authority.</summary>
    public sealed class FakeTsa : HttpMessageHandler
    {
        public int Requests { get; private set; }

        // Within the TSA certificate's validity, which a token must be.
        public DateTimeOffset Time { get; } = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            if (request.Content.Headers.ContentType?.MediaType != "application/timestamp-query" || !Rfc3161TimestampRequest.TryDecode(body, out var tsRequest, out _))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            var token = TimestampToken(tsRequest.HashAlgorithmId, tsRequest.GetMessageHash(), tsRequest.GetNonce(), Time);
            var writer = new AsnWriter(AsnEncodingRules.DER);
            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                {
                    writer.WriteInteger(0);
                }

                writer.WriteEncodedValue(token);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(writer.Encode()) };
        }
    }
}
