// RFC 3161 timestamps for .NET Framework, whose System.Security.Cryptography.Pkcs has no Rfc3161TimestampRequest,
// Rfc3161TimestampToken or SignerInfo.AddUnsignedAttribute. The token's CMS signature is still checked by
// SignedCms; this file only reads and writes the RFC 3161 and CMS structures around it. Only the .NET Framework
// build uses it, but it is compiled for every target so the .NET 8 tests can check it against the BCL classes.
using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace PdfUtil.Signatures.Internal;

/// <summary>A decoded RFC 3161 timestamp token: the CMS <c>SignedData</c> and its <c>TSTInfo</c>.</summary>
internal sealed class TimestampToken
{
    private const string TstInfoOid = "1.2.840.113549.1.9.16.1.4";
    private const string SigningCertificateOid = "1.2.840.113549.1.9.16.2.12";
    private const string SigningCertificateV2Oid = "1.2.840.113549.1.9.16.2.47";

    private TimestampToken(SignedCms cms, byte[] encoded, string hashAlgorithmId, byte[] messageHash, DateTimeOffset timestamp, BigInteger? nonce)
    {
        Cms = cms;
        Encoded = encoded;
        HashAlgorithmId = hashAlgorithmId;
        MessageHash = messageHash;
        Timestamp = timestamp;
        Nonce = nonce;
    }

    public SignedCms Cms { get; }

    /// <summary>The token as a DER <c>ContentInfo</c>, the value of the signature-time-stamp attribute.</summary>
    public byte[] Encoded { get; }

    public string HashAlgorithmId { get; }

    public byte[] MessageHash { get; }

    public DateTimeOffset Timestamp { get; }

    public BigInteger? Nonce { get; }

    public static bool TryDecode(byte[] encoded, out TimestampToken? token)
    {
        token = null;
        try
        {
            var cms = new SignedCms();
            cms.Decode(encoded);
            if (cms.ContentInfo.ContentType.Value != TstInfoOid)
            {
                return false;
            }

            var tst = new AsnReader(cms.ContentInfo.Content, AsnEncodingRules.DER).ReadSequence();
            tst.ReadInteger();
            tst.ReadObjectIdentifier();
            var imprint = tst.ReadSequence();
            var algorithm = imprint.ReadSequence().ReadObjectIdentifier();
            var hash = imprint.ReadOctetString();
            tst.ReadInteger();
            var time = tst.ReadGeneralizedTime();
            if (tst.HasData && tst.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
            {
                tst.ReadEncodedValue();
            }

            if (tst.HasData && tst.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean))
            {
                tst.ReadBoolean();
            }

            BigInteger? nonce = tst.HasData && tst.PeekTag().HasSameClassAndValue(Asn1Tag.Integer) ? tst.ReadInteger() : null;

            // Like Rfc3161TimestampToken.TryDecode: the TSA certificate, when included, must be the one named by the
            // ESS signing-certificate attribute and must be valid at the timestamp.
            var signer = cms.SignerInfos.Count == 1 ? cms.SignerInfos[0] : null;
            if (signer is null || (signer.Certificate is { } cert && (!NamesCertificate(signer, cert) || time < cert.NotBefore || time > cert.NotAfter)))
            {
                return false;
            }

            token = new TimestampToken(cms, encoded, algorithm, hash, time, nonce);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or AsnContentException)
        {
            return false;
        }
    }

    /// <summary>Whether the token is correctly signed and timestamps <paramref name="data"/>.</summary>
    public bool VerifySignatureForData(byte[] data, out X509Certificate2? signer) =>
        VerifySignatureForHash(Hash(HashAlgorithmId, data), out signer);

    /// <summary>Whether the token is correctly signed and timestamps the signature value <paramref name="signature"/>.</summary>
    public bool VerifySignatureForSignature(byte[] signature, out X509Certificate2? signer) =>
        VerifySignatureForHash(Hash(HashAlgorithmId, signature), out signer);

    private bool VerifySignatureForHash(byte[]? expected, out X509Certificate2? signer)
    {
        signer = Cms.SignerInfos.Count > 0 ? Cms.SignerInfos[0].Certificate : null;
        if (expected is null || !CryptographicOperations.FixedTimeEquals(expected, MessageHash))
        {
            return false;
        }

        try
        {
            Cms.CheckSignature(verifySignatureOnly: true);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Whether the signing-certificate (v1, SHA-1) or signing-certificate-v2 attribute names <paramref name="cert"/>.</summary>
    private static bool NamesCertificate(SignerInfo signer, X509Certificate2 cert)
    {
        foreach (var attribute in signer.SignedAttributes)
        {
            var v2 = attribute.Oid.Value == SigningCertificateV2Oid;
            if (!v2 && attribute.Oid.Value != SigningCertificateOid)
            {
                continue;
            }

            // SigningCertificate(V2) ::= SEQUENCE { certs SEQUENCE OF ESSCertID(v2), ... }; the first one is the signer.
            var certId = new AsnReader(attribute.Values[0].RawData, AsnEncodingRules.DER).ReadSequence().ReadSequence().ReadSequence();
            var hashOid = "1.3.14.3.2.26";
            if (v2)
            {
                hashOid = "2.16.840.1.101.3.4.2.1";
                if (certId.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
                {
                    hashOid = certId.ReadSequence().ReadObjectIdentifier();
                }
            }

            return Hash(hashOid, cert.RawData) is { } actual && CryptographicOperations.FixedTimeEquals(actual, certId.ReadOctetString());
        }

        return false;
    }

    internal static byte[]? Hash(string oid, byte[] data) => oid switch
    {
        "1.3.14.3.2.26" => SHA1.HashData(data),
        "2.16.840.1.101.3.4.2.1" => SHA256.HashData(data),
        "2.16.840.1.101.3.4.2.2" => SHA384.HashData(data),
        "2.16.840.1.101.3.4.2.3" => SHA512.HashData(data),
        _ => null,
    };
}

/// <summary>Builds RFC 3161 requests, reads responses, and edits the CMS encoding where .NET Framework has no API.</summary>
internal static class Rfc3161
{
    private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";

    /// <summary>A <c>TimeStampReq</c> for the SHA-256 hash of <paramref name="signature"/>, asking for the TSA's certificate.</summary>
    public static byte[] CreateRequest(byte[] signature, byte[] nonce)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteInteger(1);
            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(Sha256Oid);
                    writer.WriteNull();
                }

                writer.WriteOctetString(SHA256.HashData(signature));
            }

            writer.WriteInteger(Nonce(nonce));
            writer.WriteBoolean(true);
        }

        return writer.Encode();
    }

    /// <summary>Reads a <c>TimeStampResp</c> and checks that its token is granted, signed, and for this request.</summary>
    public static TimestampToken ProcessResponse(byte[] response, byte[] signature, byte[] nonce)
    {
        byte[] encodedToken;
        try
        {
            var resp = new AsnReader(response, AsnEncodingRules.BER).ReadSequence();
            var status = resp.ReadSequence();
            var code = (int)status.ReadInteger();
            if (code is not (0 or 1))
            {
                throw new CryptographicException($"The timestamp request was rejected (status {code}).");
            }

            if (!resp.HasData)
            {
                throw new CryptographicException("The response has no timestamp token.");
            }

            encodedToken = resp.ReadEncodedValue().ToArray();
        }
        catch (AsnContentException ex)
        {
            throw new CryptographicException("The response is not a TimeStampResp.", ex);
        }

        if (!TimestampToken.TryDecode(encodedToken, out var token))
        {
            throw new CryptographicException("The response does not contain an RFC 3161 timestamp token.");
        }

        if (token!.Nonce != Nonce(nonce))
        {
            throw new CryptographicException("The timestamp token's nonce does not match the request.");
        }

        if (token.HashAlgorithmId != Sha256Oid || !token.VerifySignatureForSignature(signature, out var signer))
        {
            throw new CryptographicException("The timestamp token does not verify for this signature.");
        }

        if (signer is null)
        {
            throw new CryptographicException("The timestamp token does not contain the TSA certificate.");
        }

        return token;
    }

    /// <summary>The <c>signature</c> value of the first <c>SignerInfo</c> of a CMS <c>ContentInfo</c>.</summary>
    public static byte[] SignatureValue(byte[] contentInfo)
    {
        var signerInfo = FirstSignerInfo(contentInfo);
        signerInfo.ReadInteger();
        signerInfo.ReadEncodedValue();
        signerInfo.ReadEncodedValue();
        if (signerInfo.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            signerInfo.ReadEncodedValue();
        }

        signerInfo.ReadEncodedValue();
        return signerInfo.ReadOctetString();
    }

    /// <summary>Re-encodes a CMS <c>ContentInfo</c> with an unsigned attribute added to its first <c>SignerInfo</c>.</summary>
    public static byte[] AddUnsignedAttribute(byte[] contentInfo, string oid, byte[] value)
    {
        var outer = new AsnReader(contentInfo, AsnEncodingRules.BER).ReadSequence();
        var contentType = outer.ReadObjectIdentifier();
        var signedData = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)).ReadSequence();

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(contentType);
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)))
            using (writer.PushSequence())
            {
                // signerInfos is the last field of SignedData; everything before it is copied as is.
                var fields = new List<ReadOnlyMemory<byte>>();
                while (signedData.HasData)
                {
                    fields.Add(signedData.ReadEncodedValue());
                }

                foreach (var field in fields.Take(fields.Count - 1))
                {
                    writer.WriteEncodedValue(field.Span);
                }

                var signerInfos = new AsnReader(fields[fields.Count - 1], AsnEncodingRules.BER).ReadSetOf();
                using (writer.PushSetOf())
                {
                    WriteSignerInfo(writer, signerInfos.ReadSequence(), oid, value);
                    while (signerInfos.HasData)
                    {
                        writer.WriteEncodedValue(signerInfos.ReadEncodedValue().Span);
                    }
                }
            }
        }

        return writer.Encode();
    }

    private static void WriteSignerInfo(AsnWriter writer, AsnReader signerInfo, string oid, byte[] value)
    {
        var unsignedTag = new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: true);
        using (writer.PushSequence())
        {
            AsnReader? existing = null;
            while (signerInfo.HasData)
            {
                if (signerInfo.PeekTag().HasSameClassAndValue(unsignedTag))
                {
                    existing = signerInfo.ReadSetOf(unsignedTag);
                    continue;
                }

                writer.WriteEncodedValue(signerInfo.ReadEncodedValue().Span);
            }

            using (writer.PushSetOf(unsignedTag))
            {
                while (existing?.HasData == true)
                {
                    writer.WriteEncodedValue(existing.ReadEncodedValue().Span);
                }

                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(oid);
                    using (writer.PushSetOf())
                    {
                        writer.WriteEncodedValue(value);
                    }
                }
            }
        }
    }

    private static AsnReader FirstSignerInfo(byte[] contentInfo)
    {
        var outer = new AsnReader(contentInfo, AsnEncodingRules.BER).ReadSequence();
        outer.ReadObjectIdentifier();
        var signedData = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)).ReadSequence();
        ReadOnlyMemory<byte> last = default;
        while (signedData.HasData)
        {
            last = signedData.ReadEncodedValue();
        }

        return new AsnReader(last, AsnEncodingRules.BER).ReadSetOf().ReadSequence();
    }

    private static BigInteger Nonce(byte[] nonce)
    {
        // Big-endian and positive, as Rfc3161TimestampRequest encodes it.
        var littleEndian = new byte[nonce.Length + 1];
        for (var i = 0; i < nonce.Length; i++)
        {
            littleEndian[i] = nonce[nonce.Length - 1 - i];
        }

        return new BigInteger(littleEndian);
    }
}
