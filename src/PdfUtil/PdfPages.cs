using PdfUtil.Internal;
using PdfUtil.Internal.IO;
using PdfUtil.Internal.Pages;

namespace PdfUtil;

/// <summary>Rotates, deletes, merges and splits pages. Implementations are stateless and thread-safe.</summary>
public interface IPdfPageEditor
{
    /// <summary>Rotates the selected pages by a multiple of 90 degrees, on top of their current (possibly inherited) rotation. Protected documents keep their security (owner password needed).</summary>
    Task<PdfPageEditResult> RotatePagesAsync(Stream input, Stream output, PdfRotatePagesOptions options, CancellationToken ct = default);

    /// <summary>File version of <see cref="RotatePagesAsync"/> (atomic replace; input and output may be the same file).</summary>
    Task<PdfPageEditResult> RotatePagesFileAsync(string inputPath, string outputPath, PdfRotatePagesOptions options, CancellationToken ct = default);

    /// <summary>
    /// Deletes the selected pages. Links, bookmark destinations, named destinations, form fields and other references
    /// that pointed at deleted pages are removed, and each removal is reported. Page labels are renumbered.
    /// </summary>
    Task<PdfPageEditResult> DeletePagesAsync(Stream input, Stream output, PdfDeletePagesOptions options, CancellationToken ct = default);

    /// <summary>File version of <see cref="DeletePagesAsync"/>.</summary>
    Task<PdfPageEditResult> DeletePagesFileAsync(string inputPath, string outputPath, PdfDeletePagesOptions options, CancellationToken ct = default);

    /// <summary>
    /// Merges documents in order: pages (optionally a range of each), bookmarks, named destinations, form fields and
    /// attachments. The output is unencrypted unless <see cref="PdfMergeOptions.Protection"/> is set.
    /// </summary>
    Task<PdfMergeResult> MergeAsync(IReadOnlyList<PdfMergeInput> inputs, Stream output, PdfMergeOptions? options = null, CancellationToken ct = default);

    /// <summary>File version of <see cref="MergeAsync"/> (atomic replace).</summary>
    Task<PdfMergeResult> MergeFileAsync(IReadOnlyList<PdfMergeInput> inputs, string outputPath, PdfMergeOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Splits a document into parts. Each part is written to the stream that <paramref name="createOutput"/> returns
    /// for it (PdfUtil writes and flushes it, but does not dispose it), only after the part has been verified.
    /// </summary>
    Task<PdfSplitResult> SplitAsync(Stream input, PdfSplitOptions options, Func<PdfSplitPart, Stream> createOutput, CancellationToken ct = default);

    /// <summary>
    /// Splits a file into files named by <paramref name="outputPathPattern"/>, where <c>{0}</c> is replaced by the part
    /// number (1, 2, ...), for example <c>"chapter-{0}.pdf"</c>. Each file is written atomically.
    /// </summary>
    Task<PdfSplitResult> SplitFileAsync(string inputPath, string outputPathPattern, PdfSplitOptions options, CancellationToken ct = default);
}

/// <summary>Settings for <see cref="IPdfPageEditor.RotatePagesAsync"/>.</summary>
public sealed class PdfRotatePagesOptions
{
    /// <summary>Clockwise rotation in degrees: a multiple of 90, negative for counter-clockwise. Default 90.</summary>
    public int Angle { get; set; } = 90;

    /// <summary>The pages to rotate. Default <c>null</c>: every page.</summary>
    public PdfPageRange? Pages { get; set; }

    /// <summary>For protected documents: the owner password, needed to re-encrypt with the same settings.</summary>
    public string? Password { get; set; }

    /// <summary>Output layout, for example compressed object streams.</summary>
    public PdfWriteOptions? Write { get; set; }

    /// <summary>Optional progress reporting.</summary>
    public IProgress<PdfProgress>? Progress { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"PdfRotatePagesOptions {{ Angle = {Angle}, Pages = {Pages?.Text ?? "all"}, Password = {(string.IsNullOrEmpty(Password) ? "<empty>" : "<set>")} }}";
}

/// <summary>Settings for <see cref="IPdfPageEditor.DeletePagesAsync"/>.</summary>
public sealed class PdfDeletePagesOptions
{
    /// <summary>The pages to delete. Required; it may not select every page.</summary>
    public PdfPageRange? Pages { get; set; }

    /// <summary>For protected documents: the owner password, needed to re-encrypt with the same settings.</summary>
    public string? Password { get; set; }

    /// <summary>Output layout, for example compressed object streams.</summary>
    public PdfWriteOptions? Write { get; set; }

    /// <summary>Optional progress reporting.</summary>
    public IProgress<PdfProgress>? Progress { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"PdfDeletePagesOptions {{ Pages = {Pages?.Text}, Password = {(string.IsNullOrEmpty(Password) ? "<empty>" : "<set>")} }}";
}

/// <summary>Outcome of rotating or deleting pages.</summary>
public sealed class PdfPageEditResult
{
    internal PdfPageEditResult()
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

    /// <summary>Pages in the input.</summary>
    public int SourcePageCount { get; internal init; }

    /// <summary>Pages in the output.</summary>
    public int PageCount { get; internal init; }

    /// <summary>Everything that was removed because it pointed at a deleted page, one sentence each.</summary>
    public IReadOnlyList<string> Removed { get; internal init; } = Array.Empty<string>();

    /// <summary>Non-fatal issues.</summary>
    public IReadOnlyList<string> Warnings { get; internal init; } = Array.Empty<string>();

    /// <inheritdoc />
    public override string ToString() => $"PdfPageEditResult {{ PageCount = {PageCount}, Removed = {Removed.Count} }}";
}

/// <summary>One input of a merge: a PDF, its password if it is protected, and optionally which pages to take.</summary>
public sealed class PdfMergeInput
{
    private PdfMergeInput(Func<Stream> open, string? name)
    {
        Open = open;
        Name = name;
    }

    internal Func<Stream> Open { get; }

    /// <summary>A file. Its name (without extension) is the default bookmark title.</summary>
    public static PdfMergeInput FromFile(string path, string? password = null, PdfPageRange? pages = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new PdfMergeInput(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess), Path.GetFileNameWithoutExtension(path)) { Password = password, Pages = pages };
    }

    /// <summary>A stream, read from its current position. It is not disposed.</summary>
    public static PdfMergeInput FromStream(Stream stream, string? password = null, PdfPageRange? pages = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new PdfMergeInput(() => new NonDisposingStream(stream), null) { Password = password, Pages = pages };
    }

    /// <summary>An in-memory PDF.</summary>
    public static PdfMergeInput FromBytes(byte[] pdf, string? password = null, PdfPageRange? pages = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return new PdfMergeInput(() => new MemoryStream(pdf, writable: false), null) { Password = password, Pages = pages };
    }

    /// <summary>The owner password of a protected input (or the password to open it, with <see cref="PdfMergeOptions.AllowUserPassword"/>).</summary>
    public string? Password { get; set; }

    /// <summary>The pages to take. Default <c>null</c>: every page.</summary>
    public PdfPageRange? Pages { get; set; }

    /// <summary>The title of this document's bookmark with <see cref="PdfMergeOutlineMode.PerDocument"/>. Default: the file name, the document title, or "Document n".</summary>
    public string? Name { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"PdfMergeInput {{ Name = {Name}, Pages = {Pages?.Text ?? "all"}, Password = {(string.IsNullOrEmpty(Password) ? "<empty>" : "<set>")} }}";
}

/// <summary>How bookmarks are combined when merging.</summary>
public enum PdfMergeOutlineMode
{
    /// <summary>One top-level bookmark per input document, with that document's bookmarks below it. Default.</summary>
    PerDocument = 0,

    /// <summary>The bookmarks of all inputs, one after the other, at the top level.</summary>
    Flat = 1,

    /// <summary>No bookmarks.</summary>
    None = 2,
}

/// <summary>Settings for <see cref="IPdfPageEditor.MergeAsync"/>.</summary>
public sealed class PdfMergeOptions
{
    /// <summary>How bookmarks are combined. Default <see cref="PdfMergeOutlineMode.PerDocument"/>.</summary>
    public PdfMergeOutlineMode Outlines { get; set; }

    /// <summary>Store identical fonts, images and other shared resources once (compared by content). Default <c>false</c>.</summary>
    public bool DeduplicateResources { get; set; }

    /// <summary>Accept the password to open a protected input instead of its owner password. Default <c>false</c>.</summary>
    public bool AllowUserPassword { get; set; }

    /// <summary>Protect the output with these settings. Default <c>null</c>: the output is not encrypted.</summary>
    public PdfProtectionOptions? Protection { get; set; }

    /// <summary>Output layout, for example compressed object streams.</summary>
    public PdfWriteOptions? Write { get; set; }

    /// <summary>Optional progress reporting.</summary>
    public IProgress<PdfProgress>? Progress { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"PdfMergeOptions {{ Outlines = {Outlines}, DeduplicateResources = {DeduplicateResources}, Protection = {(Protection is null ? "none" : Protection.Algorithm.ToString())} }}";
}

/// <summary>Outcome of a merge.</summary>
public sealed class PdfMergeResult
{
    internal PdfMergeResult()
    {
    }

    /// <summary>Version written to the output header: the highest input version (or what protection requires).</summary>
    public string OutputVersion { get; internal init; } = "";

    /// <summary>Pages in the output.</summary>
    public int PageCount { get; internal init; }

    /// <summary>Pages taken from each input, in input order.</summary>
    public IReadOnlyList<int> PagesPerInput { get; internal init; } = Array.Empty<int>();

    /// <summary>Whether the output is encrypted.</summary>
    public bool IsEncrypted { get; internal init; }

    /// <summary>Form fields, named destinations and attachments that were renamed because the name was taken, one sentence each.</summary>
    public IReadOnlyList<string> Renamed { get; internal init; } = Array.Empty<string>();

    /// <summary>Links and destinations that were dropped because their target page is not in the output, one sentence each.</summary>
    public IReadOnlyList<string> Removed { get; internal init; } = Array.Empty<string>();

    /// <summary>Objects saved by <see cref="PdfMergeOptions.DeduplicateResources"/>.</summary>
    public int DeduplicatedObjects { get; internal init; }

    /// <summary>Non-fatal issues.</summary>
    public IReadOnlyList<string> Warnings { get; internal init; } = Array.Empty<string>();

    /// <inheritdoc />
    public override string ToString() => $"PdfMergeResult {{ PageCount = {PageCount}, Inputs = {PagesPerInput.Count} }}";
}

/// <summary>Settings for <see cref="IPdfPageEditor.SplitAsync"/>. Set either <see cref="Ranges"/> or <see cref="EveryNPages"/>.</summary>
public sealed class PdfSplitOptions
{
    /// <summary>One part per range, in order. Ranges may overlap.</summary>
    public IReadOnlyList<PdfPageRange>? Ranges { get; set; }

    /// <summary>Consecutive parts of this many pages (the last part may be shorter).</summary>
    public int? EveryNPages { get; set; }

    /// <summary>The owner password of a protected input (or the password to open it, with <see cref="AllowUserPassword"/>).</summary>
    public string? Password { get; set; }

    /// <summary>Accept the password to open a protected input instead of its owner password. Default <c>false</c>.</summary>
    public bool AllowUserPassword { get; set; }

    /// <summary>Protect every part with these settings. Default <c>null</c>: the parts are not encrypted.</summary>
    public PdfProtectionOptions? Protection { get; set; }

    /// <summary>Output layout, for example compressed object streams.</summary>
    public PdfWriteOptions? Write { get; set; }

    /// <summary>Optional progress reporting.</summary>
    public IProgress<PdfProgress>? Progress { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"PdfSplitOptions {{ Ranges = {(Ranges is null ? "-" : string.Join("; ", Ranges))}, EveryNPages = {EveryNPages}, Password = {(string.IsNullOrEmpty(Password) ? "<empty>" : "<set>")} }}";
}

/// <summary>One part of a split.</summary>
/// <param name="Number">Part number, from 1.</param>
/// <param name="Pages">The source page numbers in the part, in order.</param>
public sealed record PdfSplitPart(int Number, IReadOnlyList<int> Pages);

/// <summary>Outcome of a split.</summary>
public sealed class PdfSplitResult
{
    internal PdfSplitResult()
    {
    }

    /// <summary>The parts written, in order.</summary>
    public IReadOnlyList<PdfSplitPart> Parts { get; internal init; } = Array.Empty<PdfSplitPart>();

    /// <summary>The files written by <see cref="IPdfPageEditor.SplitFileAsync"/>, in part order; empty for streams.</summary>
    public IReadOnlyList<string> Files { get; internal init; } = Array.Empty<string>();

    /// <summary>Links and destinations dropped because their target page went to another part, one sentence each.</summary>
    public IReadOnlyList<string> Removed { get; internal init; } = Array.Empty<string>();

    /// <summary>Non-fatal issues.</summary>
    public IReadOnlyList<string> Warnings { get; internal init; } = Array.Empty<string>();

    /// <inheritdoc />
    public override string ToString() => $"PdfSplitResult {{ Parts = {Parts.Count} }}";
}

/// <summary>Default <see cref="IPdfPageEditor"/>. Stateless and thread-safe; register as a singleton.</summary>
public sealed class PdfPageEditor : IPdfPageEditor
{
    private readonly PdfUtilOptions _options;
    private readonly PdfLog _log;

    /// <summary>Creates a page editor with default options.</summary>
    public PdfPageEditor() : this(null) { }

    /// <summary>Creates a page editor. The options are copied.</summary>
    public PdfPageEditor(PdfUtilOptions? options)
    {
        _options = (options ?? new PdfUtilOptions()).Clone();
        _log = new PdfLog(_options.Logger);
    }

    /// <inheritdoc />
    public Task<PdfPageEditResult> RotatePagesAsync(Stream input, Stream output, PdfRotatePagesOptions options, CancellationToken ct = default)
    {
        PageEditEngine.Validate(options);
        return OutputRunner.ToStreamAsync(input, output, _options, _log, "Rotating pages of a {size}-byte document.", (source, buffer) => Pages.Rotate(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfPageEditResult> RotatePagesFileAsync(string inputPath, string outputPath, PdfRotatePagesOptions options, CancellationToken ct = default)
    {
        PageEditEngine.Validate(options);
        return OutputRunner.ToFileAsync(inputPath, outputPath, _options, _log, "Rotating pages of a {size}-byte document.", (source, buffer) => Pages.Rotate(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfPageEditResult> DeletePagesAsync(Stream input, Stream output, PdfDeletePagesOptions options, CancellationToken ct = default)
    {
        PageEditEngine.Validate(options);
        return OutputRunner.ToStreamAsync(input, output, _options, _log, "Deleting pages of a {size}-byte document.", (source, buffer) => Pages.Delete(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfPageEditResult> DeletePagesFileAsync(string inputPath, string outputPath, PdfDeletePagesOptions options, CancellationToken ct = default)
    {
        PageEditEngine.Validate(options);
        return OutputRunner.ToFileAsync(inputPath, outputPath, _options, _log, "Deleting pages of a {size}-byte document.", (source, buffer) => Pages.Delete(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public async Task<PdfMergeResult> MergeAsync(IReadOnlyList<PdfMergeInput> inputs, Stream output, PdfMergeOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var sources = await ComposeEngine.OpenInputsAsync(inputs, _options, ct).ConfigureAwait(false);
        try
        {
            return await OutputRunner.WriteAsync(output, _options, (buffer) => Composer.Merge(sources, buffer, options ?? new PdfMergeOptions(), ct), ct).ConfigureAwait(false);
        }
        finally
        {
            ComposeEngine.Dispose(sources);
        }
    }

    /// <inheritdoc />
    public async Task<PdfMergeResult> MergeFileAsync(IReadOnlyList<PdfMergeInput> inputs, string outputPath, PdfMergeOptions? options = null, CancellationToken ct = default)
    {
        var sources = await ComposeEngine.OpenInputsAsync(inputs, _options, ct).ConfigureAwait(false);
        try
        {
            return await OutputRunner.WriteFileAsync(outputPath, (buffer) => Composer.Merge(sources, buffer, options ?? new PdfMergeOptions(), ct), ct).ConfigureAwait(false);
        }
        finally
        {
            ComposeEngine.Dispose(sources);
        }
    }

    /// <inheritdoc />
    public async Task<PdfSplitResult> SplitAsync(Stream input, PdfSplitOptions options, Func<PdfSplitPart, Stream> createOutput, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(createOutput);
        ComposeEngine.Validate(options);
        using var source = await InputBuffer.OpenAsync(input, _options, ct).ConfigureAwait(false);
        return Guard.Run(() => Composer.Split(source, options, (part, buffer) =>
        {
            var target = createOutput(part);
            buffer.Position = 0;
            buffer.CopyTo(target);
            target.Flush();
            return null;
        }, ct));
    }

    /// <inheritdoc />
    public async Task<PdfSplitResult> SplitFileAsync(string inputPath, string outputPathPattern, PdfSplitOptions options, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPathPattern);
        if (!outputPathPattern.Contains("{0}", StringComparison.Ordinal))
        {
            throw new ArgumentException("The output path pattern must contain {0}, which is replaced by the part number.", nameof(outputPathPattern));
        }

        ComposeEngine.Validate(options);
        await using var stream = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess);
        using var source = await InputBuffer.OpenAsync(stream, _options, ct).ConfigureAwait(false);
        return Guard.Run(() => Composer.Split(source, options, (part, buffer) =>
        {
            var path = string.Format(System.Globalization.CultureInfo.InvariantCulture, outputPathPattern, part.Number);
            OutputRunner.CommitFile(buffer, path);
            return Path.GetFullPath(path);
        }, ct));
    }

    private PageEditEngine Pages => new(_options, _log);

    private ComposeEngine Composer => new(_options, _log);
}
