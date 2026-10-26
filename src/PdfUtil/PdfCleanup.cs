using PdfUtil.Internal;
using PdfUtil.Internal.IO;
using PdfUtil.Internal.Protection;

namespace PdfUtil;

/// <summary>Repairs damaged documents and removes content such as scripts, attachments and metadata. Implementations are stateless and thread-safe.</summary>
public interface IPdfCleaner
{
    /// <summary>
    /// Writes a clean, single-revision copy through the recovery reader: damaged cross-reference data, wrong stream
    /// lengths and page tree problems are fixed, incremental updates are merged, and unreachable objects are dropped.
    /// Protected documents keep exactly the same security (this needs the owner password), unless
    /// <see cref="PdfRepairOptions.RemoveProtection"/> is set. The result lists every repair and reports any page that
    /// could not be recovered.
    /// </summary>
    Task<PdfRepairResult> RepairAsync(Stream input, Stream output, PdfRepairOptions? options = null, CancellationToken ct = default);

    /// <summary>File version of <see cref="RepairAsync"/>: temporary file and atomic replace; input and output may be the same file.</summary>
    Task<PdfRepairResult> RepairFileAsync(string inputPath, string outputPath, PdfRepairOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Removes the content selected in <paramref name="options"/>. Protected documents keep exactly the same security
    /// (this needs the owner password). The result lists everything that was removed.
    /// </summary>
    Task<PdfSanitizeResult> SanitizeAsync(Stream input, Stream output, PdfSanitizeOptions options, CancellationToken ct = default);

    /// <summary>File version of <see cref="SanitizeAsync"/>.</summary>
    Task<PdfSanitizeResult> SanitizeFileAsync(string inputPath, string outputPath, PdfSanitizeOptions options, CancellationToken ct = default);
}

/// <summary>Settings for <see cref="IPdfCleaner.RepairAsync"/>.</summary>
public sealed class PdfRepairOptions
{
    /// <summary>For protected documents: the owner password, to re-encrypt the repaired copy with the same settings (or to remove protection).</summary>
    public string? Password { get; set; }

    /// <summary>Write the repaired copy unencrypted. Default <c>false</c> (keep the protection). Needs the owner password.</summary>
    public bool RemoveProtection { get; set; }

    /// <summary>Output layout, for example compressed object streams. Default <c>null</c>: the most compatible layout.</summary>
    public PdfWriteOptions? Write { get; set; }

    /// <summary>Optional progress reporting.</summary>
    public IProgress<PdfProgress>? Progress { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"PdfRepairOptions {{ Password = {(string.IsNullOrEmpty(Password) ? "<empty>" : "<set>")}, RemoveProtection = {RemoveProtection} }}";
}

/// <summary>Outcome of a successful repair.</summary>
public sealed class PdfRepairResult
{
    internal PdfRepairResult()
    {
    }

    /// <summary>Effective input version.</summary>
    public string SourceVersion { get; internal init; } = "";

    /// <summary>Version written to the output header.</summary>
    public string OutputVersion { get; internal init; } = "";

    /// <summary>Encryption algorithm of the output, or <see cref="PdfEncryptionAlgorithm.None"/>.</summary>
    public PdfEncryptionAlgorithm Algorithm { get; internal init; } = PdfEncryptionAlgorithm.None;

    /// <summary>Security handler revision of the output, or 0 if not encrypted.</summary>
    public int Revision { get; internal init; }

    /// <summary>Whether the output is encrypted.</summary>
    public bool IsEncrypted => Algorithm != PdfEncryptionAlgorithm.None;

    /// <summary>
    /// Everything that was repaired or normalized, one sentence each: reconstructed cross-reference data, corrected
    /// offsets and stream lengths, page tree fixes, merged incremental updates, dropped unreachable objects, and so on.
    /// </summary>
    public IReadOnlyList<string> Repairs { get; internal init; } = Array.Empty<string>();

    /// <summary>Whether the input was damaged (as opposed to only normalized).</summary>
    public bool WasDamaged { get; internal init; }

    /// <summary>The page count declared by the page tree root (<c>/Count</c>), if it has one.</summary>
    public int? DeclaredPageCount { get; internal init; }

    /// <summary>Number of pages in the output (equal to the number of pages that could be read from the input).</summary>
    public int PageCount { get; internal init; }

    /// <summary>Page objects found in the file that are not part of the page tree, and so are not in the output.</summary>
    public int OrphanedPageCount { get; internal init; }

    /// <summary>Whether pages may be missing: the declared count is higher than the pages found, or page objects are outside the page tree.</summary>
    public bool PagesMayBeMissing => DeclaredPageCount > PageCount || OrphanedPageCount > 0;

    /// <summary>Non-fatal issues.</summary>
    public IReadOnlyList<string> Warnings { get; internal init; } = Array.Empty<string>();

    /// <inheritdoc />
    public override string ToString() => $"PdfRepairResult {{ Repairs = {Repairs.Count}, PageCount = {PageCount}, PagesMayBeMissing = {PagesMayBeMissing} }}";
}

/// <summary>Settings for <see cref="IPdfCleaner.SanitizeAsync"/>. Each switch is independent.</summary>
public sealed class PdfSanitizeOptions
{
    /// <summary>Remove the document information dictionary (title, author, dates, ...).</summary>
    public bool RemoveDocumentInfo { get; set; }

    /// <summary>Remove XMP metadata streams: the document's and those of pages, images and other objects.</summary>
    public bool RemoveXmp { get; set; }

    /// <summary>Remove embedded files: the <c>/EmbeddedFiles</c> tree, file attachment annotations, associated files (<c>/AF</c>) and portfolios.</summary>
    public bool RemoveAttachments { get; set; }

    /// <summary>Remove JavaScript: document scripts (<c>/Names /JavaScript</c>) and JavaScript actions of the document, pages, annotations, form fields and bookmarks. Default <c>true</c>.</summary>
    public bool RemoveJavaScript { get; set; } = true;

    /// <summary>Remove the open action (<c>/OpenAction</c>) and every additional-actions dictionary (<c>/AA</c>), whatever their type.</summary>
    public bool RemoveActions { get; set; }

    /// <summary>Remove annotations (and with them the interactive form, whose fields are widget annotations).</summary>
    public bool RemoveAnnotations { get; set; }

    /// <summary>With <see cref="RemoveAnnotations"/>: keep link annotations. Default <c>true</c>.</summary>
    public bool KeepLinks { get; set; } = true;

    /// <summary>For protected documents: the owner password, needed to re-encrypt with the same settings.</summary>
    public string? Password { get; set; }

    /// <summary>Output layout, for example compressed object streams. Default <c>null</c>: the most compatible layout.</summary>
    public PdfWriteOptions? Write { get; set; }

    /// <summary>Optional progress reporting.</summary>
    public IProgress<PdfProgress>? Progress { get; set; }

    /// <summary>Options with every removal switched on (links are kept).</summary>
    public static PdfSanitizeOptions All() => new()
    {
        RemoveDocumentInfo = true,
        RemoveXmp = true,
        RemoveAttachments = true,
        RemoveJavaScript = true,
        RemoveActions = true,
        RemoveAnnotations = true,
    };

    internal bool RemovesAnything => RemoveDocumentInfo || RemoveXmp || RemoveAttachments || RemoveJavaScript || RemoveActions || RemoveAnnotations;

    /// <inheritdoc />
    public override string ToString() =>
        $"PdfSanitizeOptions {{ Info = {RemoveDocumentInfo}, Xmp = {RemoveXmp}, Attachments = {RemoveAttachments}, JavaScript = {RemoveJavaScript}, Actions = {RemoveActions}, Annotations = {RemoveAnnotations}, Password = {(string.IsNullOrEmpty(Password) ? "<empty>" : "<set>")} }}";
}

/// <summary>What kind of content <see cref="IPdfCleaner.SanitizeAsync"/> removed.</summary>
public enum PdfSanitizeItemKind
{
    /// <summary>The document information dictionary.</summary>
    DocumentInfo = 0,

    /// <summary>An XMP metadata stream.</summary>
    XmpMetadata = 1,

    /// <summary>An embedded file, file attachment annotation, associated-files array or portfolio.</summary>
    Attachment = 2,

    /// <summary>A JavaScript action or document script.</summary>
    JavaScript = 3,

    /// <summary>The document's open action.</summary>
    OpenAction = 4,

    /// <summary>An additional-actions dictionary (<c>/AA</c>).</summary>
    AdditionalActions = 5,

    /// <summary>An annotation.</summary>
    Annotation = 6,

    /// <summary>The interactive form.</summary>
    Form = 7,
}

/// <summary>One item removed by <see cref="IPdfCleaner.SanitizeAsync"/>.</summary>
/// <param name="Kind">What kind of content it was.</param>
/// <param name="Description">What and where, for example <c>JavaScript action of annotation /Link on page 2</c>.</param>
/// <param name="ObjectNumber">The object that contained it (or was removed), when known.</param>
public sealed record PdfRemovedItem(PdfSanitizeItemKind Kind, string Description, int? ObjectNumber = null);

/// <summary>Outcome of a successful sanitize.</summary>
public sealed class PdfSanitizeResult
{
    internal PdfSanitizeResult()
    {
    }

    /// <summary>Effective input version.</summary>
    public string SourceVersion { get; internal init; } = "";

    /// <summary>Version written to the output header.</summary>
    public string OutputVersion { get; internal init; } = "";

    /// <summary>Encryption algorithm of the output (the same as the input), or <see cref="PdfEncryptionAlgorithm.None"/>.</summary>
    public PdfEncryptionAlgorithm Algorithm { get; internal init; } = PdfEncryptionAlgorithm.None;

    /// <summary>Security handler revision of the output, or 0 if not encrypted.</summary>
    public int Revision { get; internal init; }

    /// <summary>Whether the output is encrypted.</summary>
    public bool IsEncrypted => Algorithm != PdfEncryptionAlgorithm.None;

    /// <summary>Everything that was removed, in the order found.</summary>
    public IReadOnlyList<PdfRemovedItem> Removed { get; internal init; } = Array.Empty<PdfRemovedItem>();

    /// <summary>Non-fatal issues.</summary>
    public IReadOnlyList<string> Warnings { get; internal init; } = Array.Empty<string>();

    /// <summary>Number of removed items of one kind.</summary>
    public int Count(PdfSanitizeItemKind kind) => Removed.Count(r => r.Kind == kind);

    /// <inheritdoc />
    public override string ToString() => $"PdfSanitizeResult {{ Removed = {Removed.Count}, Warnings = {Warnings.Count} }}";
}

/// <summary>Default <see cref="IPdfCleaner"/>. Stateless and thread-safe; register as a singleton.</summary>
public sealed class PdfCleaner : IPdfCleaner
{
    private readonly PdfUtilOptions _options;
    private readonly PdfLog _log;

    /// <summary>Creates a cleaner with default options.</summary>
    public PdfCleaner() : this(null) { }

    /// <summary>Creates a cleaner. The options are copied.</summary>
    public PdfCleaner(PdfUtilOptions? options)
    {
        _options = (options ?? new PdfUtilOptions()).Clone();
        _log = new PdfLog(_options.Logger);
    }

    /// <inheritdoc />
    public Task<PdfRepairResult> RepairAsync(Stream input, Stream output, PdfRepairOptions? options = null, CancellationToken ct = default)
    {
        options ??= new PdfRepairOptions();
        return OutputRunner.ToStreamAsync(input, output, _options, _log, "Repairing a {size}-byte document.", (source, buffer) => Engine.Repair(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfRepairResult> RepairFileAsync(string inputPath, string outputPath, PdfRepairOptions? options = null, CancellationToken ct = default)
    {
        options ??= new PdfRepairOptions();
        return OutputRunner.ToFileAsync(inputPath, outputPath, _options, _log, "Repairing a {size}-byte document.", (source, buffer) => Engine.Repair(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfSanitizeResult> SanitizeAsync(Stream input, Stream output, PdfSanitizeOptions options, CancellationToken ct = default)
    {
        CleanupEngine.Validate(options);
        return OutputRunner.ToStreamAsync(input, output, _options, _log, "Sanitizing a {size}-byte document.", (source, buffer) => Engine.Sanitize(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfSanitizeResult> SanitizeFileAsync(string inputPath, string outputPath, PdfSanitizeOptions options, CancellationToken ct = default)
    {
        CleanupEngine.Validate(options);
        return OutputRunner.ToFileAsync(inputPath, outputPath, _options, _log, "Sanitizing a {size}-byte document.", (source, buffer) => Engine.Sanitize(source, buffer, options, ct), ct);
    }

    private CleanupEngine Engine => new(_options, _log);
}
