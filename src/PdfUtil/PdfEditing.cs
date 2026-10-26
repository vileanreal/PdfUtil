namespace PdfUtil;

/// <summary>Edits existing PDF documents: removes or changes protection, and changes document information. Implementations are stateless and thread-safe.</summary>
public interface IPdfEditor
{
    /// <summary>Writes an unencrypted copy of a protected document. Requires the owner password unless <see cref="PdfDecryptOptions.AllowUserPassword"/> is set.</summary>
    Task<PdfEditResult> DecryptAsync(Stream input, Stream output, PdfDecryptOptions options, CancellationToken ct = default);

    /// <summary>File version of <see cref="DecryptAsync"/>: temporary file and atomic replace; input and output may be the same file.</summary>
    Task<PdfEditResult> DecryptFileAsync(string inputPath, string outputPath, PdfDecryptOptions options, CancellationToken ct = default);

    /// <summary>Changes the passwords, permissions or algorithm of a protected document. Unspecified settings are kept where possible.</summary>
    Task<PdfProtectionResult> ChangeSecurityAsync(Stream input, Stream output, PdfSecurityChanges changes, CancellationToken ct = default);

    /// <summary>File version of <see cref="ChangeSecurityAsync"/>.</summary>
    Task<PdfProtectionResult> ChangeSecurityFileAsync(string inputPath, string outputPath, PdfSecurityChanges changes, CancellationToken ct = default);

    /// <summary>
    /// Changes the document information without changing protection: unencrypted documents stay unencrypted, and
    /// protected documents keep exactly the same passwords, permissions and algorithm (this needs the owner password).
    /// </summary>
    Task<PdfEditResult> EditInfoAsync(Stream input, Stream output, PdfEditInfoOptions options, CancellationToken ct = default);

    /// <summary>File version of <see cref="EditInfoAsync"/>.</summary>
    Task<PdfEditResult> EditInfoFileAsync(string inputPath, string outputPath, PdfEditInfoOptions options, CancellationToken ct = default);
}

/// <summary>Settings for <see cref="IPdfEditor.DecryptAsync"/>.</summary>
public sealed class PdfDecryptOptions
{
    /// <summary>The owner password of the document (or the user password, with <see cref="AllowUserPassword"/>).</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Also accept the user password. Default <c>false</c>: removing protection lifts the owner's restrictions, so by
    /// default only the owner password may do it. Enable only when the caller is entitled to an unrestricted copy.
    /// </summary>
    public bool AllowUserPassword { get; set; }

    /// <summary>Optional document information changes applied in the same step.</summary>
    public PdfInfoChanges? InfoChanges { get; set; }

    /// <summary>Output layout, for example compressed object streams. Default <c>null</c>: the most compatible layout.</summary>
    public PdfWriteOptions? Write { get; set; }

    /// <summary>Optional progress reporting.</summary>
    public IProgress<PdfProgress>? Progress { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"PdfDecryptOptions {{ Password = {(string.IsNullOrEmpty(Password) ? "<empty>" : "<set>")}, AllowUserPassword = {AllowUserPassword} }}";
}

/// <summary>Settings for <see cref="IPdfEditor.EditInfoAsync"/>.</summary>
public sealed class PdfEditInfoOptions
{
    /// <summary>The changes to apply. Required.</summary>
    public PdfInfoChanges Changes { get; set; } = new();

    /// <summary>For protected documents: the owner password, needed to re-encrypt with the same settings.</summary>
    public string? Password { get; set; }

    /// <summary>Output layout, for example compressed object streams. Default <c>null</c>: the most compatible layout.</summary>
    public PdfWriteOptions? Write { get; set; }

    /// <summary>
    /// <see cref="PdfSaveMode.Incremental"/> appends the change to the original bytes instead of rewriting the
    /// document. Only for unencrypted, undamaged documents, and not together with <see cref="PdfWriteOptions.UseObjectStreams"/>.
    /// </summary>
    public PdfSaveMode SaveMode { get; set; }

    /// <summary>Optional progress reporting.</summary>
    public IProgress<PdfProgress>? Progress { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"PdfEditInfoOptions {{ Password = {(string.IsNullOrEmpty(Password) ? "<empty>" : "<set>")} }}";
}

/// <summary>
/// Settings for <see cref="IPdfEditor.ChangeSecurityAsync"/>. Properties left <c>null</c> keep the document's
/// current setting.
/// </summary>
public sealed class PdfSecurityChanges
{
    /// <summary>The document's current owner password. Required.</summary>
    public string? CurrentOwnerPassword { get; set; }

    /// <summary>
    /// New password to open the document; <c>""</c> removes it (the document then opens without a password).
    /// <c>null</c> keeps the current one. For AES-256 documents the current user password cannot be recovered from
    /// the owner password, so it can only be kept when it is empty; otherwise set this property.
    /// </summary>
    public string? UserPassword { get; set; }

    /// <summary>New owner password. <c>null</c> keeps <see cref="CurrentOwnerPassword"/>.</summary>
    public string? OwnerPassword { get; set; }

    /// <summary>New permissions. <c>null</c> keeps the current ones.</summary>
    public PdfPermissions? Permissions { get; set; }

    /// <summary>
    /// New algorithm. <c>null</c> keeps the current one; deprecated ones that PdfUtil does not write (RC4 40-bit,
    /// AES-256 revision 5) are upgraded to RC4-128 and AES-256 revision 6 respectively, with a warning.
    /// </summary>
    public PdfEncryptionAlgorithm? Algorithm { get; set; }

    /// <summary>Whether XMP metadata is encrypted. <c>null</c> keeps the current setting.</summary>
    public bool? EncryptMetadata { get; set; }

    /// <summary>Which new passwords are accepted. Default <see cref="PdfPasswordPolicy.Interoperable"/>.</summary>
    public PdfPasswordPolicy PasswordPolicy { get; set; } = PdfPasswordPolicy.Interoperable;

    /// <summary>RC4/AES-128 only: allow new passwords longer than 32 bytes to be truncated.</summary>
    public bool AllowPasswordTruncation { get; set; }

    /// <summary>Optional document information changes applied in the same step.</summary>
    public PdfInfoChanges? InfoChanges { get; set; }

    /// <summary>Output layout, for example compressed object streams. Default <c>null</c>: the most compatible layout.</summary>
    public PdfWriteOptions? Write { get; set; }

    /// <summary>Optional progress reporting.</summary>
    public IProgress<PdfProgress>? Progress { get; set; }

    /// <inheritdoc />
    public override string ToString() =>
        $"PdfSecurityChanges {{ UserPassword = {Describe(UserPassword)}, OwnerPassword = {Describe(OwnerPassword)}, Permissions = {Permissions?.ToString() ?? "<keep>"}, Algorithm = {Algorithm?.ToString() ?? "<keep>"} }}";

    private static string Describe(string? password) => password is null ? "<keep>" : password.Length == 0 ? "<empty>" : "<set>";
}

/// <summary>Outcome of a successful edit.</summary>
public sealed class PdfEditResult
{
    internal PdfEditResult(string sourceVersion, string outputVersion, PdfEncryptionAlgorithm algorithm, int revision, bool documentInfoChanged, bool xmpUpdated, IReadOnlyList<string> warnings)
    {
        SourceVersion = sourceVersion;
        OutputVersion = outputVersion;
        Algorithm = algorithm;
        Revision = revision;
        DocumentInfoChanged = documentInfoChanged;
        XmpUpdated = xmpUpdated;
        Warnings = warnings;
    }

    /// <summary>Effective input version.</summary>
    public string SourceVersion { get; }

    /// <summary>Version written to the output header.</summary>
    public string OutputVersion { get; }

    /// <summary>Whether the output is encrypted.</summary>
    public bool IsEncrypted => Algorithm != PdfEncryptionAlgorithm.None;

    /// <summary>Encryption algorithm of the output, or <see cref="PdfEncryptionAlgorithm.None"/>.</summary>
    public PdfEncryptionAlgorithm Algorithm { get; }

    /// <summary>Security handler revision of the output, or 0 if not encrypted.</summary>
    public int Revision { get; }

    /// <summary>Whether the document information was changed.</summary>
    public bool DocumentInfoChanged { get; }

    /// <summary>Whether the XMP metadata was updated to match.</summary>
    public bool XmpUpdated { get; }

    /// <summary>Whether the change was appended as an incremental update or the document was rewritten.</summary>
    public PdfSaveMode SaveMode { get; internal init; }

    /// <summary>Non-fatal issues.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <inheritdoc />
    public override string ToString() => $"PdfEditResult {{ OutputVersion = {OutputVersion}, Algorithm = {Algorithm}, Warnings = {Warnings.Count} }}";
}
