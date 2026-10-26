using PdfUtil.Conversion.Internal.Docx;
using PdfUtil.Conversion.Internal.Layout;
using PdfUtil.Conversion.Internal.Model;
using PdfUtil.Internal;

namespace PdfUtil.Conversion.Internal;

/// <summary>Word to PDF (F22): read the package safely, build the document model, then lay it out and write it like HTML.</summary>
internal sealed class DocxToPdfEngine
{
    private readonly PdfUtilOptions _lib;
    private readonly PdfConversionLimits _limits;
    private readonly PdfLog _log;
    private readonly List<string> _warnings = new();

    public DocxToPdfEngine(PdfUtilOptions lib, PdfConversionLimits limits, PdfLog log)
    {
        _lib = lib;
        _limits = limits;
        _log = log;
    }

    private void Warn(string message)
    {
        if (!_warnings.Contains(message))
        {
            _warnings.Add(message);
        }
    }

    public async Task<(byte[] Pdf, DocxToPdfResult Result)> ConvertAsync(byte[] docx, DocxToPdfOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (docx.Length >= 8 && docx[0] == 0xD0 && docx[1] == 0xCF && docx[2] == 0x11 && docx[3] == 0xE0)
        {
            throw new PdfCorruptException("The file is a legacy Word document (.doc) or a password-protected one; only unprotected .docx files are supported.");
        }

        var model = Guard.Run(() =>
        {
            var package = DocxPackage.Read(docx, _limits.MaxPackageEntries, _limits.MaxPackageBytes);
            return new DocxReader(package, Warn, _limits.MaxImagePixels, _limits.MaxElements, _limits.MaxNestingDepth).Read();
        });
        ct.ThrowIfCancellationRequested();
        HtmlToPdfEngine.WarnComplexScripts(string.Concat(model.AllBlocks().OfType<Paragraph>().Select(p => p.PlainText)), Warn);

        var sources = options.Fonts.Select((f, i) => (f.Family, f.Data, "supplied font " + (f.Family ?? (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToList<(string?, byte[], string)>();
        sources.AddRange(FontSources.FromDirectories(options.FontDirectories, options.UseSystemFonts, Warn));
        var fonts = new FontProvider(FontProvider.Load(sources, Warn), options.DefaultFontFamily, Warn);
        var rendered = await new ModelRenderer(_lib, _limits, _log, Warn).RenderAsync(model, fonts, options.Protection, options.CreateBookmarks, "PdfUtil Word to PDF", options.Progress, ct).ConfigureAwait(false);
        _log.Info($"Converted a Word document to a {rendered.Pages}-page, {rendered.Pdf.Length}-byte PDF.");
        return (rendered.Pdf, new DocxToPdfResult
        {
            PageCount = rendered.Pages,
            SectionCount = model.Sections.Count,
            EmbeddedFonts = rendered.EmbeddedFonts,
            ImageCount = rendered.Images,
            LinkCount = rendered.Links,
            BookmarkCount = rendered.Bookmarks,
            MissingCharacters = fonts.MissingCharacters,
            IsProtected = rendered.Protected,
            PdfLength = rendered.Pdf.Length,
            Warnings = _warnings.ToList(),
        });
    }
}
