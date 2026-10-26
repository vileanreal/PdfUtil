using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using PdfUtil.Internal;
using PdfUtil.Internal.Analysis;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;
using PdfUtil.Internal.Protection;
using PdfUtil.Internal.Security;

namespace PdfUtil.Signatures.Internal;

/// <summary>
/// Certificate (public-key) encryption, ISO 32000-2 §7.6.5 (F18). A random 20-byte seed and each recipient's 4-byte
/// permissions are enveloped (CMS EnvelopedData) for the recipient's certificate; the file key is SHA-1 (V4) or
/// SHA-256 (V5) of the seed followed by every recipient blob in order (and 0xFFFFFFFF when metadata is not encrypted).
/// Objects are then encrypted with the crypt filters exactly as with the Standard handler.
/// </summary>
internal sealed class CertificateSecurityEngine
{
    private const string FilterName = "DefaultCryptFilter";

    private readonly PdfUtilOptions _lib;
    private readonly PdfLog _log;

    public CertificateSecurityEngine(PdfUtilOptions lib, PdfLog log)
    {
        _lib = lib;
        _log = log;
    }

    public static void Validate(PdfCertificateProtectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Recipients is not { Count: > 0 })
        {
            throw new ArgumentException("At least one recipient is required.", nameof(options));
        }

        if (options.Algorithm is not (PdfEncryptionAlgorithm.Aes256 or PdfEncryptionAlgorithm.Aes128))
        {
            throw new ArgumentException("Certificate encryption supports Aes256 and Aes128.", nameof(options));
        }

        foreach (var r in options.Recipients)
        {
            if (r?.Certificate?.GetRSAPublicKey() is null)
            {
                throw new ArgumentException("Every recipient needs a certificate with an RSA public key.", nameof(options));
            }

            if ((r.Permissions & ~PdfPermissions.All) != 0)
            {
                throw new ArgumentException("Permissions contains undefined bits.", nameof(options));
            }
        }
    }

    public static void Validate(PdfCertificateDecryptOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Certificate is null || (options.PrivateKey is null && !options.Certificate.HasPrivateKey))
        {
            throw new ArgumentException("A recipient certificate with its private key (attached, or in PrivateKey) is required.", nameof(options));
        }
    }

    // ------------------------------------------------------------------ key derivation

    private static byte[] DeriveKey(byte[] seed, IReadOnlyList<byte[]> recipients, bool encryptMetadata, int v, int keyBytes)
    {
        using var input = new MemoryStream();
        input.Write(seed);
        foreach (var r in recipients)
        {
            input.Write(r);
        }

        if (!encryptMetadata)
        {
            input.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        }

        var hash = v >= 5 ? SHA256.HashData(input.ToArray()) : SHA1.HashData(input.ToArray());
        return hash.AsSpan(0, keyBytes).ToArray();
    }

    /// <summary>Opens a recipient blob with the certificate: returns the seed and permissions, or null if the certificate is not this recipient.</summary>
    private static (byte[] Seed, int Permissions)? OpenEnvelope(byte[] blob, X509Certificate2 certificate, RSA? privateKey)
    {
        try
        {
            var envelope = new EnvelopedCms();
            envelope.Decode(blob);
            if (privateKey is null)
            {
                envelope.Decrypt(new X509Certificate2Collection(certificate));
            }
            else
            {
                var recipient = envelope.RecipientInfos.Cast<RecipientInfo>().FirstOrDefault(r => Matches(r.RecipientIdentifier, certificate));
                if (recipient is null)
                {
                    return null;
                }

#if NETFRAMEWORK
                // .NET Framework's EnvelopedCms decrypts only with a certificate that carries its key.
                throw new PlatformNotSupportedException("On .NET Framework the private key must be attached to the certificate; PrivateKey is supported on .NET 8 and later.");
#else
                envelope.Decrypt(recipient, privateKey);
#endif
            }

            var content = envelope.ContentInfo.Content;
            if (content.Length < 24)
            {
                return null;
            }

            var permissions = (content[20] << 24) | (content[21] << 16) | (content[22] << 8) | content[23];
            return (content.AsSpan(0, 20).ToArray(), permissions);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static bool Matches(SubjectIdentifier id, X509Certificate2 certificate) => id.Value switch
    {
        X509IssuerSerial issuerSerial => issuerSerial.IssuerName == certificate.Issuer && string.Equals(issuerSerial.SerialNumber, certificate.SerialNumber, StringComparison.OrdinalIgnoreCase),
        string ski => certificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Any(e => string.Equals(e.SubjectKeyIdentifier, ski, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    /// <summary>Opens a certificate-encrypted document with <paramref name="certificate"/>; returns the recipient's permissions, or null.</summary>
    internal static int? Unlock(PdfDocument doc, X509Certificate2 certificate, RSA? privateKey = null)
    {
        if (doc.SecurityFilter != "Adobe.PubSec" || doc.GetEncryptDictionary() is not { } dict)
        {
            return null;
        }

        var pubSec = PubSecDictionary.Parse(dict, doc.Resolve);
        if (pubSec.SubFilter is not ("adbe.pkcs7.s5" or "adbe.pkcs7.s4" or null))
        {
            throw new PdfUnsupportedEncryptionException($"Public-key encryption with /SubFilter /{pubSec.SubFilter} is not supported; only adbe.pkcs7.s5 and adbe.pkcs7.s4 are.");
        }

        foreach (var blob in pubSec.Recipients)
        {
            if (OpenEnvelope(blob, certificate, privateKey) is { } opened)
            {
                var key = DeriveKey(opened.Seed, pubSec.Recipients, pubSec.EncryptMetadata, pubSec.V, pubSec.KeyLengthBits / 8);
                var filters = pubSec.V >= 4 ? pubSec.Filters : new Dictionary<string, CryptMethod>();
                doc.InstallSecurity(StandardSecurityHandler.FromFileKey(pubSec.V, pubSec.KeyLengthBits, key, filters, pubSec.StreamFilter, pubSec.StringFilter, pubSec.EmbeddedFileFilter, opened.Permissions, pubSec.EncryptMetadata));
                CryptographicOperations.ZeroMemory(key);
                return opened.Permissions;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ operations

    public PdfCertificateProtectionResult Protect(PdfSource input, Stream buffer, PdfCertificateProtectionOptions options, CancellationToken ct)
    {
        Validate(options);
        using var doc = PdfDocument.Open(input, _lib, _log, strict: false, ct);
        if (doc.IsEncrypted)
        {
            throw new PdfAlreadyEncryptedException("The document is already encrypted; remove its protection first.");
        }

        var aes256 = options.Algorithm == PdfEncryptionAlgorithm.Aes256;
        var v = aes256 ? 5 : 4;
        var keyBytes = aes256 ? 32 : 16;
        var seed = RandomNumberGenerator.GetBytes(20);
        var recipients = new List<byte[]>();
        foreach (var recipient in options.Recipients)
        {
            var content = new byte[24];
            seed.CopyTo(content, 0);
            var p = SecurityAlgorithms.ComputeP(recipient.Permissions);
            content[20] = (byte)(p >> 24);
            content[21] = (byte)(p >> 16);
            content[22] = (byte)(p >> 8);
            content[23] = (byte)p;
            var envelope = new EnvelopedCms(new ContentInfo(content), new AlgorithmIdentifier(new Oid(aes256 ? "2.16.840.1.101.3.4.1.42" : "2.16.840.1.101.3.4.1.2")));
            envelope.Encrypt(new CmsRecipient(SubjectIdentifierType.IssuerAndSerialNumber, recipient.Certificate));
            recipients.Add(envelope.Encode());
        }

        var key = DeriveKey(seed, recipients, options.EncryptMetadata, v, keyBytes);
        var method = aes256 ? CryptMethod.AesV3 : CryptMethod.AesV2;
        var cryptFilter = new PdfDictionary
        {
            [PdfName.Type] = PdfName.Get("CryptFilter"),
            ["CFM"] = PdfName.Get(aes256 ? "AESV3" : "AESV2"),
            ["AuthEvent"] = PdfName.Get("DocOpen"),
            [PdfName.Length] = new PdfInteger(keyBytes),
            ["Recipients"] = new PdfArray(recipients.Select(r => (PdfObject)new PdfString(r, isHex: true))),
        };
        if (!options.EncryptMetadata)
        {
            cryptFilter["EncryptMetadata"] = PdfBoolean.False;
        }

        var encryptDict = new PdfDictionary
        {
            [PdfName.Filter] = PdfName.Get("Adobe.PubSec"),
            ["SubFilter"] = PdfName.Get("adbe.pkcs7.s5"),
            ["V"] = new PdfInteger(v),
            [PdfName.Length] = new PdfInteger(keyBytes * 8),
            ["CF"] = new PdfDictionary { [FilterName] = cryptFilter },
            ["StmF"] = PdfName.Get(FilterName),
            ["StrF"] = PdfName.Get(FilterName),
        };
        if (!options.EncryptMetadata)
        {
            encryptDict["EncryptMetadata"] = PdfBoolean.False;
        }

        var filters = new Dictionary<string, CryptMethod> { [FilterName] = method };
        var handler = StandardSecurityHandler.FromFileKey(v, keyBytes * 8, key, filters, FilterName, FilterName, FilterName, -4, options.EncryptMetadata);
        var decryptable = options.Recipients.Select(r => r.Certificate).FirstOrDefault(c => c.HasPrivateKey);
        using var security = new KeyedOutputEncryption
        {
            Handler = handler,
            EncryptDictionary = encryptDict,
            Reauthenticate = written =>
            {
                // Derive the key again from what was written; with a private key at hand, open a recipient's envelope too.
                var parsed = PubSecDictionary.Parse(written.GetEncryptDictionary()!, written.Resolve);
                var writtenSeed = seed;
                if (decryptable is not null)
                {
                    var opened = parsed.Recipients.Select(blob => OpenEnvelope(blob, decryptable, null)).FirstOrDefault(o => o is not null);
                    if (opened is null || !opened.Value.Seed.AsSpan().SequenceEqual(seed))
                    {
                        return null;
                    }
                }

                var derived = DeriveKey(writtenSeed, parsed.Recipients, parsed.EncryptMetadata, parsed.V, parsed.KeyLengthBits / 8);
                var reopened = StandardSecurityHandler.FromFileKey(parsed.V, parsed.KeyLengthBits, derived, parsed.Filters, parsed.StreamFilter, parsed.StringFilter, parsed.EmbeddedFileFilter, -4, parsed.EncryptMetadata);
                CryptographicOperations.ZeroMemory(derived);
                return reopened;
            },
        };
        CryptographicOperations.ZeroMemory(key);
        var outcome = new RewriteEngine(_lib, _log).Rewrite(doc, new RewriteSettings
        {
            Security = security,
            Progress = new ProgressReporter(options.Progress),
            MinimumVersion = aes256 ? PdfVersion.V1_7 : PdfVersion.V1_6,
            PdfAPolicy = options.PdfAPolicy,
            UseObjectStreams = options.Write?.UseObjectStreams == true,
        }, buffer, ct);
        return new PdfCertificateProtectionResult
        {
            SourceVersion = outcome.SourceVersion.ToString(),
            OutputVersion = outcome.OutputVersion.ToString(),
            Algorithm = options.Algorithm,
            RecipientCount = recipients.Count,
            Warnings = outcome.Warnings,
        };
    }

    public PdfEditResult Decrypt(PdfSource input, Stream buffer, PdfCertificateDecryptOptions options, CancellationToken ct)
    {
        Validate(options);
        using var doc = PdfDocument.Open(input, _lib, _log, strict: false, ct);
        if (doc.SecurityFilter != "Adobe.PubSec")
        {
            throw new PdfNotEncryptedException(doc.IsEncrypted ? "The document is protected with a password, not with certificates; use IPdfEditor.DecryptAsync." : "The document is not encrypted.");
        }

        var permissions = Unlock(doc, options.Certificate!, options.PrivateKey) ?? throw new PdfInvalidPasswordException("The certificate is not a recipient of this document, or its private key is not available.");
        var warnings = new List<string>();
        if (((PdfPermissions)permissions & PdfPermissions.All) != PdfPermissions.All)
        {
            if (!options.AllowRestrictedRecipient)
            {
                throw new PdfInvalidPasswordException("This recipient's permissions are restricted; removing protection lifts the restrictions, so it needs a recipient with every permission (or AllowRestrictedRecipient = true).");
            }

            warnings.Add("Protection was removed by a recipient with restricted permissions (AllowRestrictedRecipient).");
        }

        var outcome = new RewriteEngine(_lib, _log).Rewrite(doc, new RewriteSettings
        {
            Security = NoOutputEncryption.Instance,
            Progress = new ProgressReporter(options.Progress),
            UseObjectStreams = options.Write?.UseObjectStreams == true,
            InitialWarnings = warnings,
        }, buffer, ct);
        return new PdfEditResult(outcome.SourceVersion.ToString(), outcome.OutputVersion.ToString(), PdfEncryptionAlgorithm.None, 0, false, false, outcome.Warnings);
    }

    public PdfDocumentInfo Inspect(PdfSource source, X509Certificate2? certificate, RSA? privateKey, PdfInspectionDetails details, CancellationToken ct)
    {
        using var doc = PdfDocument.Open(source, _lib, _log, strict: false, ct);
        return new InspectionEngine(_lib, _log).Inspect(doc, source.Length, null, details, certificate is null ? null : d => Unlock(d, certificate, privateKey) is not null, ct);
    }
}
