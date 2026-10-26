using System.Text;
using PdfUtil.Conversion.Internal.Html;
using PdfUtil.Conversion.Internal.Model;
using PdfUtil.Conversion.Internal.Pdf;
using PdfUtil.Internal;
using PdfUtil.Internal.Parsing;

namespace PdfUtil.Conversion.Internal;

/// <summary>PDF to HTML (F19): read the PDF, analyse or position it, write HTML, validate it.</summary>
internal sealed class HtmlConversionEngine
{
    private readonly PdfUtilOptions _lib;
    private readonly PdfConversionLimits _limits;
    private readonly PdfLog _log;

    public HtmlConversionEngine(PdfUtilOptions lib, PdfConversionLimits limits, PdfLog log)
    {
        _lib = lib;
        _limits = limits;
        _log = log;
    }

    public static void Validate(PdfToHtmlOptions options, bool fileOutput)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Layout) || !Enum.IsDefined(options.Images))
        {
            throw new ArgumentException("Layout or Images is not a defined value.", nameof(options));
        }

        if (options.Images == PdfHtmlImageMode.SeparateFiles && !fileOutput)
        {
            throw new ArgumentException("Images as separate files need file output (ConvertToHtmlFileAsync); stream output embeds them.", nameof(options));
        }

        if (options.AssetsFolderName is { } folder && (folder.Length == 0 || folder is "." or ".." || folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || folder.IndexOfAny(new[] { '/', '\\', ':' }) >= 0))
        {
            throw new ArgumentException("AssetsFolderName must be a single folder name.", nameof(options));
        }
    }

    public static string AssetsFolderFor(string outputPath, PdfToHtmlOptions options) =>
        options.AssetsFolderName ?? Path.GetFileNameWithoutExtension(outputPath) + "_files";

    public (byte[] Html, PdfToHtmlResult Result) Convert(PdfSource source, PdfToHtmlOptions options, (string Folder, Dictionary<string, byte[]> Files)? assets, CancellationToken ct)
    {
        var warnings = new List<string>();
        var document = PdfSourceReader.Read(source, options.Password, options.Pages, options.Images != PdfHtmlImageMode.None, _lib, _limits, _log, warnings, options.Progress, ct);
        ct.ThrowIfCancellationRequested();
        var model = ReflowAnalyzer.Analyze(document, options.PageSeparators);
        var sink = new ImageSink(options.Images, assets?.Folder, assets?.Files);
        var settings = new HtmlSettings(options.Xhtml, options.IncludeNavigation, sink, _limits.MaxOutputElements);
        string html;
        int links;
        if (options.Layout == PdfHtmlLayout.Fixed)
        {
            html = FixedHtmlWriter.Write(document, model, settings, out links);
        }
        else
        {
            html = ReflowHtmlWriter.Write(model, settings);
            links = model.AllBlocks().OfType<Paragraph>().Sum(p => p.Inlines.OfType<LinkInline>().Count());
        }

        ct.ThrowIfCancellationRequested();
        HtmlValidator.Validate(html, sink.AssetPaths);
        if (document.Pages.Any(p => p.Runs.Count == 0) && document.Pages.All(p => p.Runs.Count == 0 || p.Images.Count > 0))
        {
            warnings.Add("Some pages have no text (they may be scanned images); PdfUtil does not run OCR.");
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        _log.Info($"Converted {document.Pages.Count} pages to {bytes.Length} bytes of HTML.");
        var blocks = model.AllBlocks().ToList();
        return (bytes, new PdfToHtmlResult
        {
            PageCount = document.Pages.Count,
            ImageCount = sink.ImageCount,
            PlaceholderCount = sink.PlaceholderCount,
            LinkCount = links,
            HeadingCount = options.Layout == PdfHtmlLayout.Reflow ? blocks.OfType<Paragraph>().Count(p => p.HeadingLevel > 0) : 0,
            ListCount = options.Layout == PdfHtmlLayout.Reflow ? blocks.OfType<ListBlock>().Count() : 0,
            UnknownGlyphCount = document.Pages.Sum(p => p.UnknownGlyphs),
            HtmlLength = bytes.Length,
            AssetFiles = sink.AssetPaths.ToList(),
            Warnings = warnings.Distinct().ToList(),
        });
    }
}
