using System.Security.Cryptography;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Parsing;
using PdfUtil.Internal.Security;

namespace PdfUtil.Internal.Protection;

/// <summary>Password bytes that are already known (kept from the source), used instead of encoding a string. R2-R4 only.</summary>
internal sealed record KnownPasswordBytes(byte[]? User, byte[]? Owner);

/// <summary>Protection on top of the shared <see cref="RewriteEngine"/>: password encoding, policies, new encryption.</summary>
internal sealed class ProtectionEngine
{
    private readonly PdfUtilOptions _lib;
    private readonly PdfLog _log;

    public ProtectionEngine(PdfUtilOptions lib, PdfLog log)
    {
        _lib = lib;
        _log = log;
    }

    public static int RevisionFor(PdfEncryptionAlgorithm algorithm) => algorithm switch
    {
        PdfEncryptionAlgorithm.Aes256 => 6,
        PdfEncryptionAlgorithm.Aes128 => 4,
        PdfEncryptionAlgorithm.Rc4_128 => 3,
        _ => throw new ArgumentException($"Algorithm {algorithm} cannot be used for protection; use Aes256, Aes128 or Rc4_128.", nameof(algorithm)),
    };

    public static PdfVersion RequiredVersion(PdfEncryptionAlgorithm algorithm) => algorithm switch
    {
        PdfEncryptionAlgorithm.Rc4_128 => PdfVersion.V1_4,
        PdfEncryptionAlgorithm.Aes128 => PdfVersion.V1_6,
        _ => PdfVersion.V1_7,
    };

    public static void ValidateOptions(PdfProtectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RevisionFor(options.Algorithm);
        if (string.IsNullOrEmpty(options.UserPassword) && string.IsNullOrEmpty(options.OwnerPassword))
        {
            throw new ArgumentException("At least one of UserPassword and OwnerPassword must be set.", nameof(options));
        }

        if (!options.EncryptMetadata && options.Algorithm == PdfEncryptionAlgorithm.Rc4_128)
        {
            throw new ArgumentException("EncryptMetadata = false requires Aes128 or Aes256 (it is not defined for RC4 revision 3).", nameof(options));
        }

        options.InfoChanges?.Validate();
        if ((options.Permissions & ~PdfPermissions.All) != 0)
        {
            throw new ArgumentException("Permissions contains undefined bits.", nameof(options));
        }

        if (options.AlreadyEncryptedPolicy == AlreadyEncryptedPolicy.ReEncryptWithOwnerPassword && options.CurrentOwnerPassword is null)
        {
            throw new ArgumentException("CurrentOwnerPassword is required with AlreadyEncryptedPolicy.ReEncryptWithOwnerPassword.", nameof(options));
        }
    }

    /// <summary>
    /// Writes the protected document to <paramref name="buffer"/> (seekable, empty) and verifies it. Throws if any
    /// check fails; the caller then discards the buffer.
    /// </summary>
    public PdfProtectionResult Protect(PdfSource input, Stream buffer, PdfProtectionOptions options, CancellationToken ct)
    {
        ValidateOptions(options);

        // Encode passwords first: reject unusable passwords before doing any work.
        using (EncodePasswords(options, RevisionFor(options.Algorithm), null, out _))
        {
        }

        using var doc = PdfDocument.Open(input, _lib, _log, strict: false, ct);
        return ProtectDocument(doc, buffer, options, null, ct);
    }

    /// <summary>Protects an opened document. <paramref name="known"/> supplies kept legacy password bytes (change of security).</summary>
    public PdfProtectionResult ProtectDocument(PdfDocument doc, Stream buffer, PdfProtectionOptions options, KnownPasswordBytes? known, CancellationToken ct)
    {
        ValidateOptions(options);
        var revision = RevisionFor(options.Algorithm);
        using var security = EncodePasswords(options, revision, known, out var normalized);
        var warnings = new List<string>();
        if (doc.IsEncrypted)
        {
            if (options.AlreadyEncryptedPolicy == AlreadyEncryptedPolicy.Throw)
            {
                throw new PdfAlreadyEncryptedException("The document is already encrypted. Set AlreadyEncryptedPolicy = ReEncryptWithOwnerPassword and supply CurrentOwnerPassword to re-encrypt it.");
            }

            doc.InitializeSecurity();
            if (doc.Security!.AuthenticatedAs != PdfPasswordMatch.Owner && doc.Authenticate(options.CurrentOwnerPassword) != PdfPasswordMatch.Owner)
            {
                throw new PdfInvalidPasswordException("CurrentOwnerPassword does not authenticate as the owner password of the encrypted input.");
            }

            warnings.Add("The input was encrypted; it was decrypted with the owner password and re-encrypted with the new settings.");
        }

        if (normalized)
        {
            warnings.Add("A password was normalised by SASLprep (RFC 4013). It verifies with SASLprep-aware readers and with this library; readers that skip SASLprep need the normalised form.");
        }

        if (!security.OwnerPasswordGenerated && security.UserPassword.AsSpan().SequenceEqual(security.OwnerPassword))
        {
            warnings.Add("The user and owner passwords are identical, so anyone who can open the document has full permissions.");
        }

        var outcome = new RewriteEngine(_lib, _log).Rewrite(doc, new RewriteSettings
        {
            Security = security,
            Progress = new ProgressReporter(options.Progress),
            InfoChanges = options.InfoChanges,
            MinimumVersion = RequiredVersion(options.Algorithm),
            PdfAPolicy = options.PdfAPolicy,
            Verify = options.VerifyOutput,
            UseObjectStreams = options.Write?.UseObjectStreams == true,
            InitialWarnings = warnings,
        }, buffer, ct);

        return new PdfProtectionResult(outcome.SourceVersion.ToString(), outcome.OutputVersion.ToString(), options.Algorithm, revision, outcome.Warnings, normalized)
        {
            DocumentInfoChanged = outcome.InfoChanged,
            XmpUpdated = outcome.XmpUpdated,
        };
    }

    private static NewOutputEncryption EncodePasswords(PdfProtectionOptions options, int revision, KnownPasswordBytes? known, out bool normalized)
    {
        var legacy = revision <= 4;
        var knownUser = legacy ? known?.User : null;
        var knownOwner = legacy ? known?.Owner : null;
        var userChanged = false;
        var ownerChanged = false;

        var userBytes = knownUser is not null
            ? (byte[])knownUser.Clone()
            : PasswordEncoder.EncodeForProtection(options.UserPassword, revision, options.AllowPasswordTruncation, "user", options.PasswordPolicy, out userChanged);
        var ownerGenerated = knownOwner is null && string.IsNullOrEmpty(options.OwnerPassword);
        var ownerBytes = knownOwner is not null
            ? (byte[])knownOwner.Clone()
            : ownerGenerated
                ? RandomNumberGenerator.GetBytes(32)
                : PasswordEncoder.EncodeForProtection(options.OwnerPassword, revision, options.AllowPasswordTruncation, "owner", options.PasswordPolicy, out ownerChanged);
        normalized = userChanged || ownerChanged;
        return new NewOutputEncryption
        {
            Algorithm = options.Algorithm,
            Revision = revision,
            UserPassword = userBytes,
            OwnerPassword = ownerBytes,
            OwnerPasswordGenerated = ownerGenerated,
            P = SecurityAlgorithms.ComputeP(options.Permissions),
            EncryptMetadata = options.EncryptMetadata,
            UserPasswordText = knownUser is null ? options.UserPassword ?? "" : null,
            OwnerPasswordText = knownOwner is null && !ownerGenerated ? options.OwnerPassword : null,
        };
    }
}
