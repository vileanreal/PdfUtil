using PdfUtil.Internal;
using PdfUtil.Internal.Content;
using PdfUtil.Internal.IO;

namespace PdfUtil;

/// <summary>Extracts text, with positions, fonts and colours. Implementations are stateless and thread-safe.</summary>
public interface IPdfTextExtractor
{
    /// <summary>
    /// Extracts the text of the selected pages, and a positioned model of each page: text runs with font, size,
    /// colour and bounds, and image placements. No OCR: text drawn as images or as paths is not found.
    /// </summary>
    Task<PdfTextExtractionResult> ExtractAsync(Stream input, PdfTextExtractionOptions? options = null, CancellationToken ct = default);
}

/// <summary>How the text of a page is ordered.</summary>
public enum PdfTextOrder
{
    /// <summary>In the order the document draws it, which is normally the reading order. Default.</summary>
    Content = 0,

    /// <summary>By position: lines from top to bottom, and left to right within a line. Columns are read across, line by line.</summary>
    Layout = 1,
}

/// <summary>Settings for <see cref="IPdfTextExtractor.ExtractAsync"/>.</summary>
public sealed class PdfTextExtractionOptions
{
    /// <summary>For protected documents: a password that opens the document (user or owner).</summary>
    public string? Password { get; set; }

    /// <summary>The pages to extract. Default <c>null</c>: every page.</summary>
    public PdfPageRange? Pages { get; set; }

    /// <summary>How text is ordered. Default <see cref="PdfTextOrder.Content"/>.</summary>
    public PdfTextOrder Order { get; set; }

    /// <summary>Include every glyph's position in <see cref="PdfTextRun.Glyphs"/>. Default <c>false</c>.</summary>
    public bool IncludeGlyphs { get; set; }

    /// <summary>Optional progress reporting (stage <see cref="PdfProgressStage.Reading"/>, one unit per page).</summary>
    public IProgress<PdfProgress>? Progress { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"PdfTextExtractionOptions {{ Pages = {Pages?.Text ?? "all"}, Order = {Order}, Password = {(string.IsNullOrEmpty(Password) ? "<empty>" : "<set>")} }}";
}

/// <summary>The extracted text and the positioned model of each page.</summary>
public sealed class PdfTextExtractionResult
{
    internal PdfTextExtractionResult()
    {
    }

    /// <summary>The selected pages, in order.</summary>
    public IReadOnlyList<PdfPageText> Pages { get; internal init; } = Array.Empty<PdfPageText>();

    /// <summary>The text of all selected pages, separated by a blank line.</summary>
    public string Text => string.Join("\n\n", Pages.Select(p => p.Text));

    /// <summary>Glyphs that could not be mapped to Unicode (they appear as U+FFFD).</summary>
    public int UnknownGlyphCount => Pages.Sum(p => p.UnknownGlyphCount);

    /// <summary>Non-fatal issues: undecodable streams, vertical text, missing fonts.</summary>
    public IReadOnlyList<string> Warnings { get; internal init; } = Array.Empty<string>();

    /// <inheritdoc />
    public override string ToString() => $"PdfTextExtractionResult {{ Pages = {Pages.Count}, Characters = {Pages.Sum(p => p.Text.Length)}, UnknownGlyphs = {UnknownGlyphCount} }}";
}

/// <summary>
/// The text of one page and where it is. Coordinates are in points in display space: the page as a viewer shows it
/// (crop box, rotation applied), with the origin at the bottom-left corner and y pointing up.
/// </summary>
public sealed class PdfPageText
{
    internal PdfPageText()
    {
    }

    /// <summary>Page number, from 1.</summary>
    public int Number { get; internal init; }

    /// <summary>Displayed width in points.</summary>
    public double Width { get; internal init; }

    /// <summary>Displayed height in points.</summary>
    public double Height { get; internal init; }

    /// <summary>The page's text.</summary>
    public string Text { get; internal init; } = "";

    /// <summary>Text runs: stretches of text in one font, size and colour on one baseline.</summary>
    public IReadOnlyList<PdfTextRun> Runs { get; internal init; } = Array.Empty<PdfTextRun>();

    /// <summary>Images drawn on the page.</summary>
    public IReadOnlyList<PdfImagePlacement> Images { get; internal init; } = Array.Empty<PdfImagePlacement>();

    /// <summary>Glyphs on this page that could not be mapped to Unicode.</summary>
    public int UnknownGlyphCount { get; internal init; }
}

/// <summary>A stretch of text in one font, size and colour on one baseline.</summary>
public sealed class PdfTextRun
{
    internal PdfTextRun()
    {
    }

    /// <summary>The text, with spaces inserted where the gaps between glyphs are wide.</summary>
    public string Text { get; internal init; } = "";

    /// <summary>The font name, without a subset prefix.</summary>
    public string FontName { get; internal init; } = "";

    /// <summary>The font size as shown, in points (the text matrix and transformations applied).</summary>
    public double FontSize { get; internal init; }

    /// <summary>Whether the font is bold (from its name, flags or weight).</summary>
    public bool IsBold { get; internal init; }

    /// <summary>Whether the font is italic or oblique.</summary>
    public bool IsItalic { get; internal init; }

    /// <summary>The fill colour as <c>#RRGGBB</c> (CMYK and gray converted).</summary>
    public string Color { get; internal init; } = "#000000";

    /// <summary>The start of the baseline.</summary>
    public double X { get; internal init; }

    /// <summary>The start of the baseline.</summary>
    public double Y { get; internal init; }

    /// <summary>The length of the run along its baseline.</summary>
    public double Width { get; internal init; }

    /// <summary>The bounding box, from the font's descent to its ascent.</summary>
    public PdfRectangle Bounds { get; internal init; }

    /// <summary>The direction of the baseline in degrees, counter-clockwise (0 for normal horizontal text).</summary>
    public double Angle { get; internal init; }

    /// <summary>Each glyph's text and position, with <see cref="PdfTextExtractionOptions.IncludeGlyphs"/>.</summary>
    public IReadOnlyList<PdfGlyph> Glyphs { get; internal init; } = Array.Empty<PdfGlyph>();

    /// <inheritdoc />
    public override string ToString() => $"\"{Text}\" ({FontName} {FontSize:0.#}pt at {X:0.#},{Y:0.#})";
}

/// <summary>One glyph: its text and baseline origin in display space, and its width.</summary>
/// <param name="Text">The Unicode text (several characters for a ligature, U+FFFD when unknown).</param>
/// <param name="X">The baseline origin.</param>
/// <param name="Y">The baseline origin.</param>
/// <param name="Width">The glyph width along the baseline.</param>
public readonly record struct PdfGlyph(string Text, double X, double Y, double Width);

/// <summary>An image drawn on a page.</summary>
public sealed class PdfImagePlacement
{
    internal PdfImagePlacement()
    {
    }

    /// <summary>Where the image is drawn, in display space.</summary>
    public PdfRectangle Bounds { get; internal init; }

    /// <summary>Object number of the image XObject, or <c>null</c> for an inline image.</summary>
    public int? ObjectNumber { get; internal init; }

    /// <summary>Width in pixels.</summary>
    public int PixelWidth { get; internal init; }

    /// <summary>Height in pixels.</summary>
    public int PixelHeight { get; internal init; }

    /// <summary>Whether the image is an inline image in the content stream.</summary>
    public bool IsInline => ObjectNumber is null;
}

/// <summary>Default <see cref="IPdfTextExtractor"/>. Stateless and thread-safe; register as a singleton.</summary>
public sealed class PdfTextExtractor : IPdfTextExtractor
{
    private readonly PdfUtilOptions _options;
    private readonly PdfLog _log;

    /// <summary>Creates a text extractor with default options.</summary>
    public PdfTextExtractor() : this(null) { }

    /// <summary>Creates a text extractor. The options are copied.</summary>
    public PdfTextExtractor(PdfUtilOptions? options)
    {
        _options = (options ?? new PdfUtilOptions()).Clone();
        _log = new PdfLog(_options.Logger);
    }

    /// <inheritdoc />
    public async Task<PdfTextExtractionResult> ExtractAsync(Stream input, PdfTextExtractionOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= new PdfTextExtractionOptions();
        if (!Enum.IsDefined(options.Order))
        {
            throw new ArgumentException("Order is not a defined value.", nameof(options));
        }

        using var source = await InputBuffer.OpenAsync(input, _options, ct).ConfigureAwait(false);
        return Guard.Run(() => new TextExtractionEngine(_options, _log).Extract(source, options, ct));
    }
}
