using PdfUtil.Conversion.Internal.Docx;
using PdfUtil.Conversion.Internal.Model;
using PdfUtil.Conversion.Internal.Pdf;
using PdfUtil.Internal;
using PdfUtil.Internal.Parsing;

namespace PdfUtil.Conversion.Internal;

/// <summary>PDF to Word (F20): read the PDF, analyse its structure (as for reflowable HTML), write OOXML, validate it.</summary>
internal sealed class DocxConversionEngine
{
    private readonly PdfUtilOptions _lib;
    private readonly PdfConversionLimits _limits;
    private readonly PdfLog _log;

    public DocxConversionEngine(PdfUtilOptions lib, PdfConversionLimits limits, PdfLog log)
    {
        _lib = lib;
        _limits = limits;
        _log = log;
    }

    public (byte[] Docx, PdfToDocxResult Result) Convert(PdfSource source, PdfToDocxOptions options, CancellationToken ct)
    {
        var warnings = new List<string>();
        var document = PdfSourceReader.Read(source, options.Password, options.Pages, options.IncludeImages, _lib, _limits, _log, warnings, options.Progress, ct);
        ct.ThrowIfCancellationRequested();
        var model = ReflowAnalyzer.Analyze(document, options.PageBreaks);
        var docx = DocxWriter.Write(model, _limits.MaxOutputElements, out var writer);
        ct.ThrowIfCancellationRequested();
        DocxValidator.Validate(docx);
        _log.Info($"Converted {document.Pages.Count} pages to a {docx.Length}-byte Word document.");
        var blocks = model.AllBlocks().ToList();
        return (docx, new PdfToDocxResult
        {
            PageCount = document.Pages.Count,
            ParagraphCount = blocks.OfType<Paragraph>().Count(),
            HeadingCount = blocks.OfType<Paragraph>().Count(p => p.HeadingLevel > 0),
            ListCount = blocks.OfType<ListBlock>().Count(),
            TableCount = blocks.OfType<TableBlock>().Count(),
            ImageCount = writer.ImageCount,
            LinkCount = writer.LinkCount,
            UnknownGlyphCount = document.Pages.Sum(p => p.UnknownGlyphs),
            DocxLength = docx.Length,
            Warnings = warnings.Distinct().ToList(),
        });
    }
}
