using System.Security.Cryptography;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;
using PdfUtil.Internal.Security;
using PdfUtil.Internal.Text;

namespace PdfUtil.Internal.Protection;

/// <summary>Decrypt, edit document information and change security, on top of the shared <see cref="RewriteEngine"/>.</summary>
internal sealed class EditEngine
{
    private readonly PdfUtilOptions _lib;
    private readonly PdfLog _log;

    public EditEngine(PdfUtilOptions lib, PdfLog log)
    {
        _lib = lib;
        _log = log;
    }

    public static void Validate(PdfDecryptOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.InfoChanges?.Validate();
    }

    public static void Validate(PdfEditInfoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Changes is null)
        {
            throw new ArgumentException("Changes is required.", nameof(options));
        }

        options.Changes.Validate();
        ValidateSaveMode(options.SaveMode, options.Write, nameof(options));
    }

    /// <summary>Incremental saves cannot be combined with object streams (the update matches the source's layout).</summary>
    internal static void ValidateSaveMode(PdfSaveMode mode, PdfWriteOptions? write, string parameter)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentException("SaveMode is not a defined value.", parameter);
        }

        if (mode == PdfSaveMode.Incremental && write?.UseObjectStreams == true)
        {
            throw new ArgumentException("UseObjectStreams cannot be combined with PdfSaveMode.Incremental: an incremental update keeps the source's layout.", parameter);
        }
    }

    public static void Validate(PdfSecurityChanges changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.CurrentOwnerPassword is null)
        {
            throw new ArgumentException("CurrentOwnerPassword is required to change the security of a document.", nameof(changes));
        }

        if (changes.Permissions is { } p && (p & ~PdfPermissions.All) != 0)
        {
            throw new ArgumentException("Permissions contains undefined bits.", nameof(changes));
        }

        if (changes.Algorithm is { } algorithm)
        {
            ProtectionEngine.RevisionFor(algorithm);
        }

        changes.InfoChanges?.Validate();
    }

    /// <summary>F1: writes an unencrypted copy.</summary>
    public PdfEditResult Decrypt(PdfSource input, Stream buffer, PdfDecryptOptions options, CancellationToken ct)
    {
        Validate(options);
        using var doc = PdfDocument.Open(input, _lib, _log, strict: false, ct);
        var warnings = new List<string>();
        if (doc.IsEncrypted)
        {
            doc.InitializeSecurity();
            var match = doc.Authenticate(options.Password);
            if (match == PdfPasswordMatch.None)
            {
                throw new PdfInvalidPasswordException("The password does not open the document.");
            }

            if (match == PdfPasswordMatch.User && !options.AllowUserPassword)
            {
                throw new PdfInvalidPasswordException("This is the password to open the document, not the owner password. Removing protection also removes the owner's restrictions, so it requires the owner password (or AllowUserPassword = true).");
            }

            if (match == PdfPasswordMatch.User)
            {
                warnings.Add("Protection was removed with the user password (AllowUserPassword), which also removed the owner's permission restrictions.");
            }
        }
        else
        {
            warnings.Add("The document is not encrypted; it was rewritten without any change to its security.");
        }

        var outcome = new RewriteEngine(_lib, _log).Rewrite(doc, new RewriteSettings
        {
            Security = NoOutputEncryption.Instance,
            Progress = new ProgressReporter(options.Progress),
            InfoChanges = options.InfoChanges,
            InitialWarnings = warnings,
            UseObjectStreams = options.Write?.UseObjectStreams == true,
        }, buffer, ct);
        return new PdfEditResult(outcome.SourceVersion.ToString(), outcome.OutputVersion.ToString(), PdfEncryptionAlgorithm.None, 0, outcome.InfoChanged, outcome.XmpUpdated, outcome.Warnings);
    }

    /// <summary>F3: changes the document information and keeps the security exactly as it is.</summary>
    public PdfEditResult EditInfo(PdfSource input, Stream buffer, PdfEditInfoOptions options, CancellationToken ct)
    {
        Validate(options);
        using var doc = PdfDocument.Open(input, _lib, _log, strict: false, ct);
        if (options.SaveMode == PdfSaveMode.Incremental)
        {
            var update = new IncrementalEngine(_lib, _log).Append(doc, new IncrementalSettings
            {
                Progress = new ProgressReporter(options.Progress),
                InfoChanges = options.Changes,
            }, buffer, ct);
            return new PdfEditResult(update.SourceVersion.ToString(), update.OutputVersion.ToString(), PdfEncryptionAlgorithm.None, 0, update.InfoChanged, update.XmpUpdated, update.Warnings)
            {
                SaveMode = PdfSaveMode.Incremental,
            };
        }

        using var security = doc.IsEncrypted ? PreserveSecurity(doc, options.Password) : (OutputSecurity)NoOutputEncryption.Instance;
        var outcome = new RewriteEngine(_lib, _log).Rewrite(doc, new RewriteSettings
        {
            Security = security,
            Progress = new ProgressReporter(options.Progress),
            InfoChanges = options.Changes,
            UseObjectStreams = options.Write?.UseObjectStreams == true,
        }, buffer, ct);
        var handler = doc.Security;
        return new PdfEditResult(
            outcome.SourceVersion.ToString(),
            outcome.OutputVersion.ToString(),
            handler?.Algorithm ?? PdfEncryptionAlgorithm.None,
            handler?.R ?? 0,
            outcome.InfoChanged,
            outcome.XmpUpdated,
            outcome.Warnings);
    }

    /// <summary>F2: changes passwords, permissions or algorithm, keeping what is not specified.</summary>
    public PdfProtectionResult ChangeSecurity(PdfSource input, Stream buffer, PdfSecurityChanges changes, CancellationToken ct)
    {
        Validate(changes);
        using var doc = PdfDocument.Open(input, _lib, _log, strict: false, ct);
        if (!doc.IsEncrypted)
        {
            throw new PdfNotEncryptedException("The document is not encrypted. Use IPdfProtector to protect it.");
        }

        var handler = doc.InitializeSecurity();
        var ownerBytes = FindOwnerPassword(doc, handler, changes.CurrentOwnerPassword, "CurrentOwnerPassword", "changing the security");
        try
        {
            var warnings = new List<string>();

            // Algorithm: keep the current one, upgrading the ones PdfUtil does not write.
            var algorithm = changes.Algorithm ?? handler.Algorithm switch
            {
                PdfEncryptionAlgorithm.Rc4_40 => Warn(PdfEncryptionAlgorithm.Rc4_128, "RC4 40-bit is not written by PdfUtil; the document was upgraded to RC4 128-bit."),
                PdfEncryptionAlgorithm.Aes256R5 => Warn(PdfEncryptionAlgorithm.Aes256, "AES-256 revision 5 is deprecated; the document was upgraded to AES-256 revision 6."),
                PdfEncryptionAlgorithm.Rc4_128CryptFilter => PdfEncryptionAlgorithm.Rc4_128,
                var current => current,
            };
            var targetRevision = ProtectionEngine.RevisionFor(algorithm);
            var legacyTarget = targetRevision <= 4;

            PdfEncryptionAlgorithm Warn(PdfEncryptionAlgorithm value, string message)
            {
                warnings.Add(message);
                return value;
            }

            // User password: new, or kept.
            string userText;
            byte[]? keptUser = null;
            if (changes.UserPassword is not null)
            {
                userText = changes.UserPassword;
            }
            else if (handler.R <= 4)
            {
                keptUser = handler.RecoverUserPassword(ownerBytes) ?? throw new PdfCorruptException("The current user password could not be recovered from the owner password; the encryption dictionary is inconsistent.");
                userText = PdfDocEncoding.Decode(keptUser);
                if (!legacyTarget && userText.Length > 0)
                {
                    try
                    {
                        PasswordEncoder.EncodeForProtection(userText, targetRevision, false, "user", changes.PasswordPolicy, out _);
                    }
                    catch (PdfPasswordEncodingException)
                    {
                        throw new PdfPasswordEncodingException("The current password to open the document cannot be carried over to AES-256 under the selected password policy. Set UserPassword to choose a new one.");
                    }
                }
            }
            else
            {
                // AES-256: the user password cannot be derived from the owner password; it can be kept only when empty.
                var emptyOpens = handler.Authenticate(ReadOnlySpan<byte>.Empty) != PdfPasswordMatch.None;
                doc.Authenticate(new[] { ownerBytes });
                if (!emptyOpens)
                {
                    throw new ArgumentException("The current password to open an AES-256 document cannot be recovered from the owner password. Set UserPassword (to the current one, to keep it, or to a new one).", nameof(changes));
                }

                userText = "";
            }

            // Owner password: new, or the current one (its exact bytes when staying on a legacy algorithm).
            var ownerText = changes.OwnerPassword ?? changes.CurrentOwnerPassword!;
            var keptOwner = changes.OwnerPassword is null && legacyTarget && handler.R <= 4 ? ownerBytes : null;

            // Metadata encryption is only defined for AES (and V4 RC4); RC4 revision 3 always encrypts it.
            var encryptMetadata = changes.EncryptMetadata ?? handler.EncryptMetadata;
            if (!encryptMetadata && algorithm == PdfEncryptionAlgorithm.Rc4_128 && changes.EncryptMetadata is null)
            {
                encryptMetadata = true;
                warnings.Add("RC4 128-bit (revision 3) cannot leave metadata unencrypted; metadata is now encrypted.");
            }

            var options = new PdfProtectionOptions
            {
                UserPassword = userText,
                OwnerPassword = ownerText,
                Permissions = changes.Permissions ?? ((PdfPermissions)handler.P & PdfPermissions.All),
                Algorithm = algorithm,
                EncryptMetadata = encryptMetadata,
                PasswordPolicy = changes.PasswordPolicy,
                AllowPasswordTruncation = changes.AllowPasswordTruncation,
                AlreadyEncryptedPolicy = AlreadyEncryptedPolicy.ReEncryptWithOwnerPassword,
                CurrentOwnerPassword = changes.CurrentOwnerPassword,
                PdfAPolicy = PdfAPolicy.Proceed,
                InfoChanges = changes.InfoChanges,
                Progress = changes.Progress,
                Write = changes.Write,
            };
            var result = new ProtectionEngine(_lib, _log).ProtectDocument(doc, buffer, options, new KnownPasswordBytes(keptUser, keptOwner), ct);
            if (keptUser is not null)
            {
                CryptographicOperations.ZeroMemory(keptUser);
            }

            // Re-encryption is the point of this operation, so the protector's "input was encrypted" note is dropped.
            var resultWarnings = warnings.Concat(result.Warnings.Where(w => !w.StartsWith("The input was encrypted;", StringComparison.Ordinal))).ToArray();
            return new PdfProtectionResult(result.SourceVersion, result.OutputVersion, result.Algorithm, result.Revision, resultWarnings, result.UserPasswordWasNormalized)
            {
                DocumentInfoChanged = result.DocumentInfoChanged,
                XmpUpdated = result.XmpUpdated,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ownerBytes);
        }
    }

    /// <summary>For an operation that keeps the security as it is: no encryption, or the source's exact encryption (needs the owner password).</summary>
    internal static OutputSecurity KeepSecurity(PdfDocument doc, string? password) =>
        doc.IsEncrypted ? PreserveSecurity(doc, password) : NoOutputEncryption.Instance;

    /// <summary>Authenticates with the owner password and keeps the document's encryption exactly.</summary>
    internal static PreservedOutputEncryption PreserveSecurity(PdfDocument doc, string? password)
    {
        var handler = doc.InitializeSecurity();
        if (handler.R is 2 or 5)
        {
            throw new PdfUnsupportedEncryptionException($"The document uses a deprecated encryption revision (R{handler.R}) that PdfUtil does not write. Use IPdfEditor.ChangeSecurityAsync to re-encrypt it with a current algorithm.");
        }

        var ownerBytes = FindOwnerPassword(doc, handler, password, "Password", "keeping the protection while editing");
        try
        {
            return new PreservedOutputEncryption
            {
                Handler = handler,
                EncryptDictionary = (PdfDictionary)ObjectGraph.ResolveDeep(doc.Trailer[PdfName.Encrypt], doc.Resolve),
                OwnerPasswordText = password ?? "",
                RecoveredUserPassword = handler.RecoverUserPassword(ownerBytes),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ownerBytes);
        }
    }

    /// <summary>Finds the encoding of <paramref name="password"/> that authenticates as the owner, and leaves the document authenticated with it.</summary>
    internal static byte[] FindOwnerPassword(PdfDocument doc, StandardSecurityHandler handler, string? password, string parameter, string purpose)
    {
        var userMatched = false;
        foreach (var candidate in PasswordEncoder.EncodeForVerification(password, handler.R))
        {
            switch (handler.Authenticate(candidate))
            {
                case PdfPasswordMatch.Owner:
                    doc.Authenticate(new[] { candidate });
                    return candidate;
                case PdfPasswordMatch.User:
                    userMatched = true;
                    break;
            }
        }

        throw new PdfInvalidPasswordException(userMatched
            ? $"{parameter} is the password to open the document, not the owner password; {purpose} requires the owner password."
            : $"{parameter} does not authenticate as the owner password of the document.");
    }
}
