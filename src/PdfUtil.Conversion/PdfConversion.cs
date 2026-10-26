using PdfUtil.Conversion.Internal;
using PdfUtil.Internal;
using PdfUtil.Internal.IO;

namespace PdfUtil.Conversion;

/// <summary>
/// Converts PDF documents to HTML and Word, and HTML and Word documents to PDF. Implementations are stateless and
/// thread-safe. Conversion never fetches network resources and never runs scripts.
/// </summary>
public interface IPdfConverter
{
    /// <summary>
    /// Converts a PDF to a single, self-contained HTML file (images as data URIs). Only a converted and structurally
    /// validated document reaches <paramref name="output"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><see cref="PdfToHtmlOptions.Images"/> is <see cref="PdfHtmlImageMode.SeparateFiles"/>, which needs file output.</exception>
    Task<PdfToHtmlResult> ConvertToHtmlAsync(Stream input, Stream output, PdfToHtmlOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Converts a PDF file to an HTML file. With <see cref="PdfHtmlImageMode.SeparateFiles"/>, images are written to a
    /// folder next to the HTML file (<see cref="PdfToHtmlOptions.AssetsFolderName"/>). The HTML file is replaced
    /// atomically, and the assets folder is replaced only once the conversion has succeeded.
    /// </summary>
    Task<PdfToHtmlResult> ConvertToHtmlFileAsync(string inputPath, string outputPath, PdfToHtmlOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Converts a PDF to a Word document (DOCX), using the same structure analysis as reflowable HTML. Only a converted
    /// and structurally validated package reaches <paramref name="output"/>.
    /// </summary>
    Task<PdfToDocxResult> ConvertToDocxAsync(Stream input, Stream output, PdfToDocxOptions? options = null, CancellationToken ct = default);

    /// <summary>Converts a PDF file to a Word file; the output file is replaced atomically.</summary>
    Task<PdfToDocxResult> ConvertToDocxFileAsync(string inputPath, string outputPath, PdfToDocxOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Converts HTML (with its CSS) to PDF. The input's encoding comes from a byte order mark or a <c>meta charset</c>,
    /// else UTF-8. The PDF is checked by reading it back before it reaches <paramref name="output"/>.
    /// </summary>
    Task<HtmlToPdfResult> ConvertFromHtmlAsync(Stream htmlInput, Stream output, HtmlToPdfOptions? options = null, CancellationToken ct = default);

    /// <summary>Converts an HTML string to PDF.</summary>
    Task<HtmlToPdfResult> ConvertFromHtmlStringAsync(string html, Stream output, HtmlToPdfOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Converts an HTML file to a PDF file (replaced atomically). Relative addresses resolve against the HTML file's
    /// folder; they are loaded only with a <see cref="HtmlToPdfOptions.ResourceResolver"/> (for example
    /// <see cref="HtmlResources.FromDirectory"/>).
    /// </summary>
    Task<HtmlToPdfResult> ConvertFromHtmlFileAsync(string inputPath, string outputPath, HtmlToPdfOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Converts a Word document (DOCX) to PDF with the same layout engine as HTML. The package is read with limits on
    /// entries, sizes and names; the PDF is checked by reading it back before it reaches <paramref name="output"/>.
    /// </summary>
    Task<DocxToPdfResult> ConvertFromDocxAsync(Stream docxInput, Stream output, DocxToPdfOptions? options = null, CancellationToken ct = default);

    /// <summary>Converts a Word file to a PDF file (replaced atomically).</summary>
    Task<DocxToPdfResult> ConvertFromDocxFileAsync(string inputPath, string outputPath, DocxToPdfOptions? options = null, CancellationToken ct = default);
}

/// <summary>Limits applied by conversion, on top of the <see cref="PdfUtilOptions"/> limits for reading PDFs.</summary>
public sealed class PdfConversionLimits
{
    /// <summary>Maximum pixel count (width × height) of a single image; larger images are skipped with a warning. Default 100,000,000.</summary>
    public long MaxImagePixels { get; set; } = 100_000_000;

    /// <summary>Maximum number of elements written to an output document (or drawing operations in a PDF). Default 2,000,000.</summary>
    public int MaxOutputElements { get; set; } = 2_000_000;

    /// <summary>Maximum size of an HTML or DOCX input in bytes (characters for HTML strings). Default 64 MiB.</summary>
    public long MaxInputBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Maximum number of elements in an HTML input. Default 500,000.</summary>
    public int MaxElements { get; set; } = 500_000;

    /// <summary>Maximum nesting depth of HTML elements. Default 256.</summary>
    public int MaxNestingDepth { get; set; } = 256;

    /// <summary>Maximum number of external resources (style sheets, images, fonts) an HTML input may load. Default 500.</summary>
    public int MaxResources { get; set; } = 500;

    /// <summary>Maximum size of one external resource in bytes. Default 32 MiB.</summary>
    public long MaxResourceBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>Maximum number of entries in a DOCX package. Default 10,000.</summary>
    public int MaxPackageEntries { get; set; } = 10_000;

    /// <summary>Maximum total uncompressed size of a DOCX package in bytes (zip-bomb protection). Default 512 MiB.</summary>
    public long MaxPackageBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Creates a copy.</summary>
    public PdfConversionLimits Clone() => (PdfConversionLimits)MemberwiseClone();

    internal void Validate()
    {
        if (MaxImagePixels <= 0 || MaxOutputElements <= 0 || MaxInputBytes <= 0 || MaxElements <= 0 || MaxNestingDepth <= 0 || MaxResources < 0 || MaxResourceBytes <= 0 || MaxPackageEntries <= 0 || MaxPackageBytes <= 0)
        {
            throw new ArgumentException("Conversion limits must be positive.");
        }
    }
}

/// <summary>How a PDF is laid out in HTML.</summary>
public enum PdfHtmlLayout
{
    /// <summary>
    /// Reflowable HTML: paragraphs, headings and lists reconstructed from the page layout, in reading order (columns
    /// are read one after the other where they can be detected). Default.
    /// </summary>
    Reflow = 0,

    /// <summary>One positioned container per page, with text runs and images where the PDF draws them.</summary>
    Fixed = 1,
}

/// <summary>How images are written.</summary>
public enum PdfHtmlImageMode
{
    /// <summary>Inside the HTML file, as data URIs. Default.</summary>
    Embedded = 0,

    /// <summary>As separate files in a folder next to the HTML file (file output only).</summary>
    SeparateFiles = 1,

    /// <summary>Left out.</summary>
    None = 2,
}

/// <summary>Settings for PDF to HTML conversion.</summary>
public sealed class PdfToHtmlOptions
{
    /// <summary>For protected documents: a password that opens the document (user or owner).</summary>
    public string? Password { get; set; }

    /// <summary>The pages to convert. Default <c>null</c>: every page.</summary>
    public PdfPageRange? Pages { get; set; }

    /// <summary>Reflowable or fixed layout. Default <see cref="PdfHtmlLayout.Reflow"/>.</summary>
    public PdfHtmlLayout Layout { get; set; }

    /// <summary>How images are written. Default <see cref="PdfHtmlImageMode.Embedded"/>.</summary>
    public PdfHtmlImageMode Images { get; set; }

    /// <summary>
    /// Write XHTML: an XML declaration and <c>xml:lang</c>, for consumers that parse the output as XML. The default
    /// output is HTML5 that is also well-formed XML.
    /// </summary>
    public bool Xhtml { get; set; }

    /// <summary>Add a navigation list built from the document's bookmarks. Default <c>true</c>.</summary>
    public bool IncludeNavigation { get; set; } = true;

    /// <summary>In reflow layout, mark where each PDF page ends with a separator line. Default <c>false</c>.</summary>
    public bool PageSeparators { get; set; }

    /// <summary>
    /// The folder for <see cref="PdfHtmlImageMode.SeparateFiles"/>, relative to the HTML file. Default: the HTML file's
    /// name without extension plus <c>_files</c>. Must be a single folder name.
    /// </summary>
    public string? AssetsFolderName { get; set; }

    /// <summary>Optional progress reporting (stage <see cref="PdfProgressStage.Reading"/>, one unit per page).</summary>
    public IProgress<PdfProgress>? Progress { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"PdfToHtmlOptions {{ Layout = {Layout}, Images = {Images}, Pages = {Pages?.Text ?? "all"}, Password = {(string.IsNullOrEmpty(Password) ? "<empty>" : "<set>")} }}";
}

/// <summary>Outcome of a PDF to HTML conversion.</summary>
public sealed class PdfToHtmlResult
{
    internal PdfToHtmlResult()
    {
    }

    /// <summary>Number of pages converted.</summary>
    public int PageCount { get; internal init; }

    /// <summary>Number of images written (each distinct image once).</summary>
    public int ImageCount { get; internal init; }

    /// <summary>Images that could not be converted and were replaced by a placeholder.</summary>
    public int PlaceholderCount { get; internal init; }

    /// <summary>Number of links written.</summary>
    public int LinkCount { get; internal init; }

    /// <summary>Number of headings detected (reflow layout).</summary>
    public int HeadingCount { get; internal init; }

    /// <summary>Number of lists detected (reflow layout).</summary>
    public int ListCount { get; internal init; }

    /// <summary>Glyphs that could not be mapped to Unicode (they appear as U+FFFD).</summary>
    public int UnknownGlyphCount { get; internal init; }

    /// <summary>Size of the HTML file in bytes.</summary>
    public long HtmlLength { get; internal init; }

    /// <summary>With <see cref="PdfHtmlImageMode.SeparateFiles"/>: the asset files written, relative to the HTML file.</summary>
    public IReadOnlyList<string> AssetFiles { get; internal init; } = Array.Empty<string>();

    /// <summary>Non-fatal issues: images that could not be converted, unsafe links that were dropped, damaged content.</summary>
    public IReadOnlyList<string> Warnings { get; internal init; } = Array.Empty<string>();

    /// <inheritdoc />
    public override string ToString() => $"PdfToHtmlResult {{ Pages = {PageCount}, Images = {ImageCount}, Links = {LinkCount}, Headings = {HeadingCount}, Bytes = {HtmlLength} }}";
}

/// <summary>Settings for PDF to Word conversion.</summary>
public sealed class PdfToDocxOptions
{
    /// <summary>For protected documents: a password that opens the document (user or owner).</summary>
    public string? Password { get; set; }

    /// <summary>The pages to convert. Default <c>null</c>: every page.</summary>
    public PdfPageRange? Pages { get; set; }

    /// <summary>
    /// Start each PDF page on a new page in Word. Default <c>false</c>: text flows freely, and paragraphs broken by a
    /// PDF page break are joined.
    /// </summary>
    public bool PageBreaks { get; set; }

    /// <summary>Include images. Default <c>true</c>.</summary>
    public bool IncludeImages { get; set; } = true;

    /// <summary>Optional progress reporting (stage <see cref="PdfProgressStage.Reading"/>, one unit per page).</summary>
    public IProgress<PdfProgress>? Progress { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"PdfToDocxOptions {{ Pages = {Pages?.Text ?? "all"}, PageBreaks = {PageBreaks}, Password = {(string.IsNullOrEmpty(Password) ? "<empty>" : "<set>")} }}";
}

/// <summary>Outcome of a PDF to Word conversion.</summary>
public sealed class PdfToDocxResult
{
    internal PdfToDocxResult()
    {
    }

    /// <summary>Number of pages converted.</summary>
    public int PageCount { get; internal init; }

    /// <summary>Number of paragraphs written (headings included, list items and table cells included).</summary>
    public int ParagraphCount { get; internal init; }

    /// <summary>Number of headings detected.</summary>
    public int HeadingCount { get; internal init; }

    /// <summary>Number of lists detected.</summary>
    public int ListCount { get; internal init; }

    /// <summary>Number of tables detected (from ruling lines with text that fits the grid).</summary>
    public int TableCount { get; internal init; }

    /// <summary>Number of distinct images written.</summary>
    public int ImageCount { get; internal init; }

    /// <summary>Number of hyperlinks written.</summary>
    public int LinkCount { get; internal init; }

    /// <summary>Glyphs that could not be mapped to Unicode (they appear as U+FFFD).</summary>
    public int UnknownGlyphCount { get; internal init; }

    /// <summary>Size of the DOCX file in bytes.</summary>
    public long DocxLength { get; internal init; }

    /// <summary>Non-fatal issues: images that could not be converted, unsafe links that were dropped, damaged content.</summary>
    public IReadOnlyList<string> Warnings { get; internal init; } = Array.Empty<string>();

    /// <inheritdoc />
    public override string ToString() => $"PdfToDocxResult {{ Pages = {PageCount}, Paragraphs = {ParagraphCount}, Headings = {HeadingCount}, Lists = {ListCount}, Tables = {TableCount}, Images = {ImageCount}, Bytes = {DocxLength} }}";
}

/// <summary>Default <see cref="IPdfConverter"/>. Stateless and thread-safe; register as a singleton.</summary>
public sealed class PdfConverter : IPdfConverter
{
    private readonly PdfUtilOptions _options;
    private readonly PdfConversionLimits _limits;
    private readonly PdfLog _log;

    /// <summary>Creates a converter with default options and limits.</summary>
    public PdfConverter() : this(null) { }

    /// <summary>Creates a converter. The options and limits are copied.</summary>
    public PdfConverter(PdfUtilOptions? options, PdfConversionLimits? limits = null)
    {
        _options = (options ?? new PdfUtilOptions()).Clone();
        _limits = (limits ?? new PdfConversionLimits()).Clone();
        _limits.Validate();
        _log = new PdfLog(_options.Logger);
    }

    /// <inheritdoc />
    public Task<PdfToHtmlResult> ConvertToHtmlAsync(Stream input, Stream output, PdfToHtmlOptions? options = null, CancellationToken ct = default)
    {
        options ??= new PdfToHtmlOptions();
        HtmlConversionEngine.Validate(options, fileOutput: false);
        return OutputRunner.ToStreamAsync(input, output, _options, _log, "Converting a {size}-byte PDF to HTML.", (source, buffer) =>
        {
            var (html, result) = Engine.Convert(source, options, null, ct);
            buffer.Write(html);
            return result;
        }, ct);
    }

    /// <inheritdoc />
    public Task<PdfToHtmlResult> ConvertToHtmlFileAsync(string inputPath, string outputPath, PdfToHtmlOptions? options = null, CancellationToken ct = default)
    {
        options ??= new PdfToHtmlOptions();
        HtmlConversionEngine.Validate(options, fileOutput: true);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        var folder = options.Images == PdfHtmlImageMode.SeparateFiles ? HtmlConversionEngine.AssetsFolderFor(outputPath, options) : null;
        return ConversionOutput.ToFilesAsync(inputPath, outputPath, folder, _options, _log, "Converting a {size}-byte PDF to HTML.", (source, assets) => Engine.Convert(source, options, assets is null ? null : (folder!, assets), ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfToDocxResult> ConvertToDocxAsync(Stream input, Stream output, PdfToDocxOptions? options = null, CancellationToken ct = default)
    {
        options ??= new PdfToDocxOptions();
        return OutputRunner.ToStreamAsync(input, output, _options, _log, "Converting a {size}-byte PDF to Word.", (source, buffer) =>
        {
            var (docx, result) = DocxEngine.Convert(source, options, ct);
            buffer.Write(docx);
            return result;
        }, ct);
    }

    /// <inheritdoc />
    public Task<PdfToDocxResult> ConvertToDocxFileAsync(string inputPath, string outputPath, PdfToDocxOptions? options = null, CancellationToken ct = default)
    {
        options ??= new PdfToDocxOptions();
        return ConversionOutput.ToFilesAsync(inputPath, outputPath, null, _options, _log, "Converting a {size}-byte PDF to Word.", (source, _) => DocxEngine.Convert(source, options, ct), ct);
    }

    /// <inheritdoc />
    public async Task<HtmlToPdfResult> ConvertFromHtmlAsync(Stream htmlInput, Stream output, HtmlToPdfOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(htmlInput);
        var data = await ReadLimitedAsync(htmlInput, ct).ConfigureAwait(false);
        return await ConvertFromHtmlStringAsync(PdfUtil.Conversion.Internal.Html.HtmlParser.Decode(data), output, options, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HtmlToPdfResult> ConvertFromHtmlStringAsync(string html, Stream output, HtmlToPdfOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(output);
        options ??= new HtmlToPdfOptions();
        HtmlToPdfEngine.Validate(options);
        var (pdf, result) = await new HtmlToPdfEngine(_options, _limits, _log).ConvertAsync(html, options, ct).ConfigureAwait(false);
        return await OutputRunner.WriteAsync(output, _options, buffer =>
        {
            buffer.Write(pdf);
            return result;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<HtmlToPdfResult> ConvertFromHtmlFileAsync(string inputPath, string outputPath, HtmlToPdfOptions? options = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        options ??= new HtmlToPdfOptions();
        HtmlToPdfEngine.Validate(options);
        var fullInput = Path.GetFullPath(inputPath);
        if (!File.Exists(fullInput))
        {
            throw new FileNotFoundException("The input file does not exist.", fullInput);
        }

        byte[] data;
        await using (var input = new FileStream(fullInput, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
        {
            data = await ReadLimitedAsync(input, ct).ConfigureAwait(false);
        }

        var effective = options;
        if (options.BaseUri is null)
        {
            effective = Copy(options);
            effective.BaseUri = new Uri(fullInput);
        }

        var (pdf, result) = await new HtmlToPdfEngine(_options, _limits, _log).ConvertAsync(PdfUtil.Conversion.Internal.Html.HtmlParser.Decode(data), effective, ct).ConfigureAwait(false);
        return await OutputRunner.WriteFileAsync(outputPath, buffer =>
        {
            buffer.Write(pdf);
            return result;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DocxToPdfResult> ConvertFromDocxAsync(Stream docxInput, Stream output, DocxToPdfOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(docxInput);
        ArgumentNullException.ThrowIfNull(output);
        var data = await ReadLimitedAsync(docxInput, ct).ConfigureAwait(false);
        var (pdf, result) = await new DocxToPdfEngine(_options, _limits, _log).ConvertAsync(data, options ?? new DocxToPdfOptions(), ct).ConfigureAwait(false);
        return await OutputRunner.WriteAsync(output, _options, buffer =>
        {
            buffer.Write(pdf);
            return result;
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DocxToPdfResult> ConvertFromDocxFileAsync(string inputPath, string outputPath, DocxToPdfOptions? options = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        var fullInput = Path.GetFullPath(inputPath);
        if (!File.Exists(fullInput))
        {
            throw new FileNotFoundException("The input file does not exist.", fullInput);
        }

        byte[] data;
        await using (var input = new FileStream(fullInput, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
        {
            data = await ReadLimitedAsync(input, ct).ConfigureAwait(false);
        }

        var (pdf, result) = await new DocxToPdfEngine(_options, _limits, _log).ConvertAsync(data, options ?? new DocxToPdfOptions(), ct).ConfigureAwait(false);
        return await OutputRunner.WriteFileAsync(outputPath, buffer =>
        {
            buffer.Write(pdf);
            return result;
        }, ct).ConfigureAwait(false);
    }

    private static HtmlToPdfOptions Copy(HtmlToPdfOptions o)
    {
        var copy = new HtmlToPdfOptions
        {
            PageSize = o.PageSize,
            Margins = o.Margins,
            BaseUri = o.BaseUri,
            ResourceResolver = o.ResourceResolver,
            UseSystemFonts = o.UseSystemFonts,
            DefaultFontFamily = o.DefaultFontFamily,
            DefaultFontSize = o.DefaultFontSize,
            HeaderHtml = o.HeaderHtml,
            FooterHtml = o.FooterHtml,
            CreateBookmarks = o.CreateBookmarks,
            Title = o.Title,
            Protection = o.Protection,
            Progress = o.Progress,
        };
        foreach (var font in o.Fonts)
        {
            copy.Fonts.Add(font);
        }

        foreach (var directory in o.FontDirectories)
        {
            copy.FontDirectories.Add(directory);
        }

        return copy;
    }

    private async Task<byte[]> ReadLimitedAsync(Stream input, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > _limits.MaxInputBytes)
            {
                throw new PdfLimitExceededException($"The input is larger than the configured maximum of {_limits.MaxInputBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private HtmlConversionEngine Engine => new(_options, _limits, _log);

    private DocxConversionEngine DocxEngine => new(_options, _limits, _log);
}
