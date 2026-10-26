using PdfUtil.Internal;
using PdfUtil.Internal.IO;
using PdfUtil.Internal.Protection;

namespace PdfUtil;

/// <summary>Lists, adds, removes and extracts document-level attachments (embedded files). Implementations are stateless and thread-safe.</summary>
public interface IPdfAttachmentManager
{
    /// <summary>Lists the embedded files: document-level attachments, then file attachment annotations.</summary>
    Task<IReadOnlyList<PdfAttachmentInfo>> ListAttachmentsAsync(Stream input, string? password = null, CancellationToken ct = default);

    /// <summary>
    /// Adds a document-level attachment. Protected documents keep exactly the same security (this needs the owner
    /// password in <see cref="PdfAddAttachmentOptions.Password"/>).
    /// </summary>
    Task<PdfEditResult> AddAttachmentAsync(Stream input, Stream output, PdfAddAttachmentOptions options, CancellationToken ct = default);

    /// <summary>File version of <see cref="AddAttachmentAsync"/>: temporary file and atomic replace; input and output may be the same file.</summary>
    Task<PdfEditResult> AddAttachmentFileAsync(string inputPath, string outputPath, PdfAddAttachmentOptions options, CancellationToken ct = default);

    /// <summary>Removes a document-level attachment. Throws <see cref="PdfAttachmentNotFoundException"/> if there is none with that name.</summary>
    Task<PdfEditResult> RemoveAttachmentAsync(Stream input, Stream output, PdfRemoveAttachmentOptions options, CancellationToken ct = default);

    /// <summary>File version of <see cref="RemoveAttachmentAsync"/>.</summary>
    Task<PdfEditResult> RemoveAttachmentFileAsync(string inputPath, string outputPath, PdfRemoveAttachmentOptions options, CancellationToken ct = default);

    /// <summary>
    /// Returns the decoded bytes of an embedded file, found by <see cref="PdfAttachmentInfo.Name"/> (document-level
    /// attachments first, then file attachment annotations). For protected documents any password that opens the
    /// document works. Throws <see cref="PdfAttachmentNotFoundException"/> if there is none with that name.
    /// </summary>
    Task<byte[]> ExtractAttachmentAsync(Stream input, string name, string? password = null, CancellationToken ct = default);
}

/// <summary>How an attachment relates to the document (<c>/AFRelationship</c>, PDF 2.0 and PDF/A-3).</summary>
public enum PdfAttachmentRelationship
{
    /// <summary>The original source of the document's content (for example the spreadsheet a table came from).</summary>
    Source = 0,

    /// <summary>Data the document's content was derived from (for example the XML of an e-invoice).</summary>
    Data = 1,

    /// <summary>An alternative representation of the content (for example audio).</summary>
    Alternative = 2,

    /// <summary>A supplemental representation of the content.</summary>
    Supplement = 3,

    /// <summary>An encrypted payload document.</summary>
    EncryptedPayload = 4,

    /// <summary>Data of a form.</summary>
    FormData = 5,

    /// <summary>A schema definition.</summary>
    Schema = 6,

    /// <summary>The relationship is not known or does not fit the others.</summary>
    Unspecified = 7,
}

/// <summary>Settings for <see cref="IPdfAttachmentManager.AddAttachmentAsync"/>.</summary>
public sealed class PdfAddAttachmentOptions
{
    /// <summary>The attachment name, which is also its file name, for example <c>invoice.xml</c>. Required.</summary>
    public string Name { get; set; } = "";

    /// <summary>The file content. Required.</summary>
    public byte[] Content { get; set; } = Array.Empty<byte>();

    /// <summary>Optional description shown by viewers.</summary>
    public string? Description { get; set; }

    /// <summary>Optional MIME type, for example <c>application/xml</c>.</summary>
    public string? MimeType { get; set; }

    /// <summary>Creation date stored in the attachment's parameters. Default: now.</summary>
    public DateTimeOffset? CreationDate { get; set; }

    /// <summary>Modification date stored in the attachment's parameters. Default: now.</summary>
    public DateTimeOffset? ModificationDate { get; set; }

    /// <summary>
    /// Optional relationship to the document. When set, it is written as <c>/AFRelationship</c> and the attachment is
    /// also listed in the catalog's associated files (<c>/AF</c>), as PDF/A-3 and PDF 2.0 require.
    /// </summary>
    public PdfAttachmentRelationship? Relationship { get; set; }

    /// <summary>Replace an existing attachment with the same name. Default <c>false</c> (an existing name throws <see cref="ArgumentException"/>).</summary>
    public bool ReplaceExisting { get; set; }

    /// <summary>Compress the content with FlateDecode. Default <c>true</c>.</summary>
    public bool Compress { get; set; } = true;

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
    public override string ToString() => $"PdfAddAttachmentOptions {{ Name = {Name}, Content = {Content?.Length ?? 0} bytes, Password = {(string.IsNullOrEmpty(Password) ? "<empty>" : "<set>")} }}";
}

/// <summary>Settings for <see cref="IPdfAttachmentManager.RemoveAttachmentAsync"/>.</summary>
public sealed class PdfRemoveAttachmentOptions
{
    /// <summary>The name of the attachment to remove (its name tree key or file name). Required.</summary>
    public string Name { get; set; } = "";

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
    public override string ToString() => $"PdfRemoveAttachmentOptions {{ Name = {Name}, Password = {(string.IsNullOrEmpty(Password) ? "<empty>" : "<set>")} }}";
}

/// <summary>Default <see cref="IPdfAttachmentManager"/>. Stateless and thread-safe; register as a singleton.</summary>
public sealed class PdfAttachmentManager : IPdfAttachmentManager
{
    private readonly PdfUtilOptions _options;
    private readonly PdfLog _log;

    /// <summary>Creates an attachment manager with default options.</summary>
    public PdfAttachmentManager() : this(null) { }

    /// <summary>Creates an attachment manager. The options are copied.</summary>
    public PdfAttachmentManager(PdfUtilOptions? options)
    {
        _options = (options ?? new PdfUtilOptions()).Clone();
        _log = new PdfLog(_options.Logger);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PdfAttachmentInfo>> ListAttachmentsAsync(Stream input, string? password = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var source = await InputBuffer.OpenAsync(input, _options, ct).ConfigureAwait(false);
        return Guard.Run(() => Engine.List(source, password, ct));
    }

    /// <inheritdoc />
    public Task<PdfEditResult> AddAttachmentAsync(Stream input, Stream output, PdfAddAttachmentOptions options, CancellationToken ct = default)
    {
        AttachmentEngine.Validate(options);
        return OutputRunner.ToStreamAsync(input, output, _options, _log, "Adding an attachment to a {size}-byte document.", (source, buffer) => Engine.Add(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfEditResult> AddAttachmentFileAsync(string inputPath, string outputPath, PdfAddAttachmentOptions options, CancellationToken ct = default)
    {
        AttachmentEngine.Validate(options);
        return OutputRunner.ToFileAsync(inputPath, outputPath, _options, _log, "Adding an attachment to a {size}-byte document.", (source, buffer) => Engine.Add(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfEditResult> RemoveAttachmentAsync(Stream input, Stream output, PdfRemoveAttachmentOptions options, CancellationToken ct = default)
    {
        AttachmentEngine.Validate(options);
        return OutputRunner.ToStreamAsync(input, output, _options, _log, "Removing an attachment from a {size}-byte document.", (source, buffer) => Engine.Remove(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfEditResult> RemoveAttachmentFileAsync(string inputPath, string outputPath, PdfRemoveAttachmentOptions options, CancellationToken ct = default)
    {
        AttachmentEngine.Validate(options);
        return OutputRunner.ToFileAsync(inputPath, outputPath, _options, _log, "Removing an attachment from a {size}-byte document.", (source, buffer) => Engine.Remove(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public async Task<byte[]> ExtractAttachmentAsync(Stream input, string name, string? password = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrEmpty(name);
        using var source = await InputBuffer.OpenAsync(input, _options, ct).ConfigureAwait(false);
        return Guard.Run(() => Engine.Extract(source, name, password, ct));
    }

    private AttachmentEngine Engine => new(_options, _log);
}
