namespace PdfUtil;

/// <summary>Encrypts PDF documents with the Standard Security Handler. Implementations are stateless and thread-safe.</summary>
public interface IPdfProtector
{
    /// <summary>
    /// Reads a PDF from <paramref name="input"/> and writes a password-protected copy to <paramref name="output"/>.
    /// The copy is fully verified before the first byte is written to <paramref name="output"/>.
    /// </summary>
    /// <param name="input">Source PDF. Seekable streams are read from their current position; non-seekable streams are buffered.</param>
    /// <param name="output">Destination stream (writable).</param>
    /// <param name="options">Protection settings.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Details of the protection, including warnings.</returns>
    Task<PdfProtectionResult> ProtectAsync(Stream input, Stream output, PdfProtectionOptions options, CancellationToken ct = default);

    /// <summary>
    /// Protects a file. The output is written to a temporary file next to <paramref name="outputPath"/>, verified,
    /// and then moved into place, so a failed protection never leaves a partial or invalid file behind.
    /// <paramref name="inputPath"/> and <paramref name="outputPath"/> may be the same file.
    /// </summary>
    Task<PdfProtectionResult> ProtectFileAsync(string inputPath, string outputPath, PdfProtectionOptions options, CancellationToken ct = default);
}

/// <summary>Opens PDF documents to check passwords. Implementations are stateless and thread-safe.</summary>
public interface IPdfOpener
{
    /// <summary>
    /// Checks whether <paramref name="password"/> opens the document and whether it is the user or owner password.
    /// A wrong password is reported in the result, never thrown.
    /// </summary>
    Task<PdfPasswordCheckResult> VerifyPasswordAsync(Stream input, string? password, CancellationToken ct = default);

    /// <summary>Whether the document has an <c>/Encrypt</c> dictionary.</summary>
    Task<bool> IsEncryptedAsync(Stream input, CancellationToken ct = default);
}

/// <summary>Reads document details and metadata. Implementations are stateless and thread-safe.</summary>
public interface IPdfInspector
{
    /// <summary>
    /// Returns version, page, encryption, Info, XMP and identifier details. For encrypted documents the password
    /// (or, when <c>null</c>, the empty user password) is used to decrypt metadata; if a non-null password is wrong,
    /// <see cref="PdfInvalidPasswordException"/> is thrown, and if <c>null</c> does not open the document, a partial
    /// result with <see cref="PdfDocumentInfo.RequiresPassword"/> set is returned.
    /// </summary>
    Task<PdfDocumentInfo> GetInfoAsync(Stream input, string? password = null, CancellationToken ct = default);
}
