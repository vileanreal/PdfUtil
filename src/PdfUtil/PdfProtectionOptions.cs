namespace PdfUtil;

/// <summary>Settings for <see cref="IPdfProtector"/>.</summary>
public sealed class PdfProtectionOptions
{
    /// <summary>
    /// Password needed to open the document. <c>null</c> or empty means the document opens without a password
    /// (permissions still apply).
    /// </summary>
    public string? UserPassword { get; set; }

    /// <summary>
    /// Password that grants full access. When <c>null</c> or empty, a random owner password is generated and
    /// discarded, so the permissions cannot be lifted with a password. At least one of
    /// <see cref="UserPassword"/> and <see cref="OwnerPassword"/> must be non-empty.
    /// </summary>
    public string? OwnerPassword { get; set; }

    /// <summary>Permissions granted to users who open the document with the user password. Default: <see cref="PdfPermissions.All"/>.</summary>
    public PdfPermissions Permissions { get; set; } = PdfPermissions.All;

    /// <summary>Encryption algorithm. Default: <see cref="PdfEncryptionAlgorithm.Aes256"/>.</summary>
    public PdfEncryptionAlgorithm Algorithm { get; set; } = PdfEncryptionAlgorithm.Aes256;

    /// <summary>Behaviour for PDF/A input. Default: <see cref="PdfAPolicy.Throw"/>.</summary>
    public PdfAPolicy PdfAPolicy { get; set; } = PdfAPolicy.Throw;

    /// <summary>Behaviour for already-encrypted input. Default: <see cref="AlreadyEncryptedPolicy.Throw"/>.</summary>
    public AlreadyEncryptedPolicy AlreadyEncryptedPolicy { get; set; } = AlreadyEncryptedPolicy.Throw;

    /// <summary>
    /// The current owner password of an already-encrypted input. Required with
    /// <see cref="AlreadyEncryptedPolicy.ReEncryptWithOwnerPassword"/>.
    /// </summary>
    public string? CurrentOwnerPassword { get; set; }

    /// <summary>
    /// Whether the document-level XMP metadata stream is encrypted. Default <c>true</c> (the <c>/EncryptMetadata</c>
    /// entry is then omitted). Setting <c>false</c> is only valid for <see cref="PdfEncryptionAlgorithm.Aes128"/> and
    /// <see cref="PdfEncryptionAlgorithm.Aes256"/>.
    /// </summary>
    public bool EncryptMetadata { get; set; } = true;

    /// <summary>Which passwords are accepted. Default: <see cref="PdfPasswordPolicy.Interoperable"/>.</summary>
    public PdfPasswordPolicy PasswordPolicy { get; set; } = PdfPasswordPolicy.Interoperable;

    /// <summary>
    /// Optional changes to the document information (Title, Author, ...) applied while protecting. Default
    /// <c>null</c>: the document information is copied unchanged.
    /// </summary>
    public PdfInfoChanges? InfoChanges { get; set; }

    /// <summary>
    /// RC4/AES-128 only: allow passwords longer than 32 bytes to be truncated to 32 bytes (as the specification
    /// does). Default <c>false</c>: such passwords are rejected so that a user never types a password that only
    /// partially matters.
    /// </summary>
    public bool AllowPasswordTruncation { get; set; }

    /// <summary>Output layout, for example compressed object streams. Default <c>null</c>: the most compatible layout.</summary>
    public PdfWriteOptions? Write { get; set; }

    /// <summary>Optional progress reporting (reading, writing, verifying).</summary>
    public IProgress<PdfProgress>? Progress { get; set; }

    /// <summary>
    /// Run the post-protection self-verification. Default <c>true</c>. Disabling it is strongly discouraged and is
    /// intended only for benchmarking.
    /// </summary>
    public bool VerifyOutput { get; set; } = true;

    /// <inheritdoc />
    public override string ToString() =>
        $"PdfProtectionOptions {{ Algorithm = {Algorithm}, Permissions = {Permissions}, UserPassword = {(string.IsNullOrEmpty(UserPassword) ? "<empty>" : "<set>")}, OwnerPassword = {(string.IsNullOrEmpty(OwnerPassword) ? "<empty>" : "<set>")}, PdfAPolicy = {PdfAPolicy}, AlreadyEncryptedPolicy = {AlreadyEncryptedPolicy}, EncryptMetadata = {EncryptMetadata}, PasswordPolicy = {PasswordPolicy} }}";
}

/// <summary>Library-wide settings: hostile-input limits, buffering and logging.</summary>
public sealed class PdfUtilOptions
{
    /// <summary>Maximum accepted input size in bytes. Default 2 GiB.</summary>
    public long MaxFileSizeBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Maximum number of indirect objects (cross-reference entries). Default 5,000,000.</summary>
    public int MaxObjectCount { get; set; } = 5_000_000;

    /// <summary>Maximum nesting depth of arrays and dictionaries, and of the page tree. Default 256.</summary>
    public int MaxNestingDepth { get; set; } = 256;

    /// <summary>Maximum size of a single decompressed stream in bytes (zip-bomb protection). Default 256 MiB.</summary>
    public long MaxDecompressedStreamBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Maximum number of cross-reference sections followed through <c>/Prev</c>. Default 4096.</summary>
    public int MaxXrefSections { get; set; } = 4096;

    /// <summary>
    /// Inputs and outputs up to this size are buffered in memory; larger ones use temporary files. Default 32 MiB.
    /// </summary>
    public long InMemoryBufferThresholdBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>Directory for temporary files. Default: <see cref="Path.GetTempPath"/>.</summary>
    public string? TempDirectory { get; set; }

    /// <summary>
    /// Optional log callback. Messages never contain passwords, keys or document content.
    /// The callback must be thread-safe when services are shared.
    /// </summary>
    public Action<PdfLogLevel, string>? Logger { get; set; }

    internal PdfUtilOptions Clone() => (PdfUtilOptions)MemberwiseClone();
}
