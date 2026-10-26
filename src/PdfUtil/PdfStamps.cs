using System.Globalization;
using PdfUtil.Internal;
using PdfUtil.Internal.IO;
using PdfUtil.Internal.Pages;

namespace PdfUtil;

/// <summary>Adds text or image stamps and watermarks to pages. Implementations are stateless and thread-safe.</summary>
public interface IPdfStamper
{
    /// <summary>
    /// Stamps the selected pages, over or under their content. Existing content streams are never changed: the stamp
    /// is a new content stream added to each page. Protected documents keep their security (owner password needed).
    /// </summary>
    Task<PdfStampResult> StampAsync(Stream input, Stream output, PdfStampOptions options, CancellationToken ct = default);

    /// <summary>File version of <see cref="StampAsync"/> (atomic replace; input and output may be the same file).</summary>
    Task<PdfStampResult> StampFileAsync(string inputPath, string outputPath, PdfStampOptions options, CancellationToken ct = default);
}

/// <summary>A standard 14 font usable for text stamps without embedding (WinAnsi text only).</summary>
public enum PdfStandardFont
{
    /// <summary>Helvetica (Arial-like sans-serif). Default.</summary>
    Helvetica = 0,

    /// <summary>Helvetica Bold.</summary>
    HelveticaBold = 1,

    /// <summary>Helvetica Oblique.</summary>
    HelveticaOblique = 2,

    /// <summary>Helvetica Bold Oblique.</summary>
    HelveticaBoldOblique = 3,

    /// <summary>Times Roman (serif).</summary>
    TimesRoman = 4,

    /// <summary>Times Bold.</summary>
    TimesBold = 5,

    /// <summary>Times Italic.</summary>
    TimesItalic = 6,

    /// <summary>Times Bold Italic.</summary>
    TimesBoldItalic = 7,

    /// <summary>Courier (monospaced).</summary>
    Courier = 8,

    /// <summary>Courier Bold.</summary>
    CourierBold = 9,

    /// <summary>Courier Oblique.</summary>
    CourierOblique = 10,

    /// <summary>Courier Bold Oblique.</summary>
    CourierBoldOblique = 11,
}

/// <summary>Where a stamp goes on the page, as the page is displayed (rotation and crop box respected).</summary>
public enum PdfStampPosition
{
    /// <summary>The centre of the page. Default.</summary>
    Center = 0,

    /// <summary>Top left corner, inside the margin.</summary>
    TopLeft = 1,

    /// <summary>Top edge, centred.</summary>
    TopCenter = 2,

    /// <summary>Top right corner.</summary>
    TopRight = 3,

    /// <summary>Left edge, centred vertically.</summary>
    MiddleLeft = 4,

    /// <summary>Right edge, centred vertically.</summary>
    MiddleRight = 5,

    /// <summary>Bottom left corner.</summary>
    BottomLeft = 6,

    /// <summary>Bottom edge, centred.</summary>
    BottomCenter = 7,

    /// <summary>Bottom right corner.</summary>
    BottomRight = 8,

    /// <summary>The stamp's centre at <see cref="PdfStampOptions.X"/>, <see cref="PdfStampOptions.Y"/>.</summary>
    Custom = 9,
}

/// <summary>Whether a stamp is drawn over or under the existing content.</summary>
public enum PdfStampLayer
{
    /// <summary>Over the content (a watermark or stamp). Default.</summary>
    Over = 0,

    /// <summary>Under the content (a background).</summary>
    Under = 1,
}

/// <summary>An RGB colour with components from 0 to 1.</summary>
/// <param name="Red">Red, 0 to 1.</param>
/// <param name="Green">Green, 0 to 1.</param>
/// <param name="Blue">Blue, 0 to 1.</param>
public readonly record struct PdfColor(double Red, double Green, double Blue)
{
    /// <summary>Black.</summary>
    public static PdfColor Black => new(0, 0, 0);

    /// <summary>Mid gray.</summary>
    public static PdfColor Gray => new(0.5, 0.5, 0.5);

    /// <summary>White.</summary>
    public static PdfColor White => new(1, 1, 1);

    /// <summary>Parses <c>#RRGGBB</c> or <c>RRGGBB</c>.</summary>
    public static PdfColor FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var h = hex.TrimStart('#');
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
        {
            throw new FormatException("A colour must be written as #RRGGBB.");
        }

        return new PdfColor(((v >> 16) & 255) / 255.0, ((v >> 8) & 255) / 255.0, (v & 255) / 255.0);
    }

    internal bool IsValid => Red is >= 0 and <= 1 && Green is >= 0 and <= 1 && Blue is >= 0 and <= 1;
}

/// <summary>Settings for <see cref="IPdfStamper.StampAsync"/>. Set either <see cref="Text"/> or <see cref="Image"/>.</summary>
public sealed class PdfStampOptions
{
    /// <summary>
    /// The text to stamp; <c>\n</c> starts a new line. With a standard font, only characters in the Windows-1252
    /// (WinAnsi) set are allowed; with <see cref="FontData"/>, any character the font has.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>A JPEG (passed through unchanged) or non-interlaced PNG (transparency becomes a soft mask) to stamp.</summary>
    public byte[]? Image { get; set; }

    /// <summary>The font for text. Default <see cref="PdfStandardFont.Helvetica"/>.</summary>
    public PdfStandardFont Font { get; set; }

    /// <summary>
    /// A TrueType font (<c>.ttf</c>, or the first face of a <c>.ttc</c>) for the text, replacing <see cref="Font"/>. It is
    /// embedded as a subset with a Unicode map, so any script the font covers can be stamped and the text can be copied.
    /// Fonts with PostScript outlines and fonts whose license forbids embedding are refused.
    /// </summary>
    public byte[]? FontData { get; set; }

    /// <summary>Font size in points. Default 48.</summary>
    public double FontSize { get; set; } = 48;

    /// <summary>Text colour. Default gray.</summary>
    public PdfColor Color { get; set; } = PdfColor.Gray;

    /// <summary>Opacity from 0 (invisible) to 1 (opaque). Default 0.3.</summary>
    public double Opacity { get; set; } = 0.3;

    /// <summary>Counter-clockwise rotation in degrees, around the stamp's centre. Default 45.</summary>
    public double Rotation { get; set; } = 45;

    /// <summary>Where to put the stamp. Default <see cref="PdfStampPosition.Center"/>.</summary>
    public PdfStampPosition Position { get; set; }

    /// <summary>With <see cref="PdfStampPosition.Custom"/>: the stamp centre, in points from the left edge of the page as displayed.</summary>
    public double X { get; set; }

    /// <summary>With <see cref="PdfStampPosition.Custom"/>: the stamp centre, in points from the bottom edge of the page as displayed.</summary>
    public double Y { get; set; }

    /// <summary>Distance from the page edges for the corner and edge positions, in points. Default 36 (half an inch).</summary>
    public double Margin { get; set; } = 36;

    /// <summary>Image width in points; the height keeps the aspect ratio. Default <c>null</c>: one point per pixel (72 dpi).</summary>
    public double? ImageWidth { get; set; }

    /// <summary>Over or under the content. Default <see cref="PdfStampLayer.Over"/>.</summary>
    public PdfStampLayer Layer { get; set; }

    /// <summary>The pages to stamp. Default <c>null</c>: every page.</summary>
    public PdfPageRange? Pages { get; set; }

    /// <summary>For protected documents: the owner password, needed to re-encrypt with the same settings.</summary>
    public string? Password { get; set; }

    /// <summary>Output layout, for example compressed object streams.</summary>
    public PdfWriteOptions? Write { get; set; }

    /// <summary>Optional progress reporting.</summary>
    public IProgress<PdfProgress>? Progress { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"PdfStampOptions {{ Text = {(Text is null ? "-" : "\"" + Text + "\"")}, Image = {Image?.Length ?? 0} bytes, Position = {Position}, Layer = {Layer}, Pages = {Pages?.Text ?? "all"} }}";
}

/// <summary>Outcome of stamping.</summary>
public sealed class PdfStampResult
{
    internal PdfStampResult()
    {
    }

    /// <summary>Effective input version.</summary>
    public string SourceVersion { get; internal init; } = "";

    /// <summary>Version written to the output header (at least 1.4 when the stamp is transparent).</summary>
    public string OutputVersion { get; internal init; } = "";

    /// <summary>Encryption algorithm of the output (the same as the input), or <see cref="PdfEncryptionAlgorithm.None"/>.</summary>
    public PdfEncryptionAlgorithm Algorithm { get; internal init; } = PdfEncryptionAlgorithm.None;

    /// <summary>Whether the output is encrypted.</summary>
    public bool IsEncrypted => Algorithm != PdfEncryptionAlgorithm.None;

    /// <summary>The pages that were stamped.</summary>
    public IReadOnlyList<int> StampedPages { get; internal init; } = Array.Empty<int>();

    /// <summary>Non-fatal issues.</summary>
    public IReadOnlyList<string> Warnings { get; internal init; } = Array.Empty<string>();

    /// <inheritdoc />
    public override string ToString() => $"PdfStampResult {{ StampedPages = {StampedPages.Count} }}";
}

/// <summary>Default <see cref="IPdfStamper"/>. Stateless and thread-safe; register as a singleton.</summary>
public sealed class PdfStamper : IPdfStamper
{
    private readonly PdfUtilOptions _options;
    private readonly PdfLog _log;

    /// <summary>Creates a stamper with default options.</summary>
    public PdfStamper() : this(null) { }

    /// <summary>Creates a stamper. The options are copied.</summary>
    public PdfStamper(PdfUtilOptions? options)
    {
        _options = (options ?? new PdfUtilOptions()).Clone();
        _log = new PdfLog(_options.Logger);
    }

    /// <inheritdoc />
    public Task<PdfStampResult> StampAsync(Stream input, Stream output, PdfStampOptions options, CancellationToken ct = default)
    {
        var stamp = StampEngine.Prepare(options, _options);
        return OutputRunner.ToStreamAsync(input, output, _options, _log, "Stamping a {size}-byte document.", (source, buffer) => new StampEngine(_options, _log).Stamp(source, buffer, options, stamp, ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfStampResult> StampFileAsync(string inputPath, string outputPath, PdfStampOptions options, CancellationToken ct = default)
    {
        var stamp = StampEngine.Prepare(options, _options);
        return OutputRunner.ToFileAsync(inputPath, outputPath, _options, _log, "Stamping a {size}-byte document.", (source, buffer) => new StampEngine(_options, _log).Stamp(source, buffer, options, stamp, ct), ct);
    }
}
