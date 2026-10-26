using System.Text;
using PdfUtil.Conversion.Internal.Layout;
using PdfUtil.Conversion.Internal.Model;
using PdfUtil.Internal;
using PdfUtil.Internal.Content;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Parsing;

namespace PdfUtil.Conversion.Internal;

/// <summary>What rendering a document model produced.</summary>
internal sealed record RenderedPdf(byte[] Pdf, int Pages, List<string> EmbeddedFonts, int Images, int Links, int Bookmarks, bool Protected);

/// <summary>
/// The second half of HTML and Word to PDF: lays out every section of a document model, numbers the pages and adds
/// headers and footers, writes the PDF, reads it back to check it, and optionally protects it.
/// </summary>
internal sealed class ModelRenderer
{
    private readonly PdfUtilOptions _lib;
    private readonly PdfConversionLimits _limits;
    private readonly PdfLog _log;
    private readonly Action<string> _warn;

    public ModelRenderer(PdfUtilOptions lib, PdfConversionLimits limits, PdfLog log, Action<string> warn)
    {
        _lib = lib;
        _limits = limits;
        _log = log;
        _warn = warn;
    }

    public async Task<RenderedPdf> RenderAsync(DocModel model, FontProvider fonts, PdfProtectionOptions? protection, bool createBookmarks, string creator, IProgress<PdfProgress>? progressSink, CancellationToken ct)
    {
        var progress = new ProgressReporter(progressSink);
        var all = new List<(LaidOutPage Page, Section Section, bool First)>();
        foreach (var section in model.Sections)
        {
            ct.ThrowIfCancellationRequested();
            var contentWidth = section.PageWidth - section.Margins.Left - section.Margins.Right;
            var contentHeight = section.PageHeight - section.Margins.Top - section.Margins.Bottom;
            var engine = new LayoutEngine(fonts, _warn, contentHeight);
            var pages = Guard.Run(() => engine.Paginate(engine.LayoutBlocks(section.Blocks, section.Margins.Left, contentWidth), section));
            all.AddRange(pages.Select((p, i) => (p, section, i == 0)));
            progress.Report(PdfProgressStage.Writing, all.Count, all.Count);
        }

        Guard.Run(() =>
        {
            new LayoutEngine(fonts, _warn, 1000).Finish(all);
            return 0;
        });
        var laidOut = all.Select(a => a.Page).ToList();
        progress.Report(PdfProgressStage.Writing, laidOut.Count, laidOut.Count, force: true);
        if (laidOut.Sum(p => p.Ops.Count) > _limits.MaxOutputElements)
        {
            throw new PdfLimitExceededException($"The output has more drawing operations than the configured maximum of {_limits.MaxOutputElements}.");
        }

        var bookmarks = createBookmarks
            ? model.AllBlocks().OfType<Paragraph>().Where(p => p.HeadingLevel > 0 && p.Anchors.Count > 0 && p.PlainText.Trim().Length > 0).Select(p => new PdfBookmarkEntry(p.PlainText.Trim(), p.HeadingLevel, p.Anchors[0])).ToList()
            : new List<PdfBookmarkEntry>();
        var builder = new PdfBuilder(_limits.MaxImagePixels, _warn);
        var pdf = Guard.Run(() => builder.Build(laidOut, new PdfMetadata(model.Title, model.Author, model.Subject, model.Keywords, model.Language, creator), bookmarks));
        ct.ThrowIfCancellationRequested();
        Verify(pdf, laidOut, ct);

        var isProtected = false;
        if (protection is not null)
        {
            using var plain = new MemoryStream(pdf, writable: false);
            using var protectedOutput = new MemoryStream();
            var result = await new PdfProtector(_lib).ProtectAsync(plain, protectedOutput, protection, ct).ConfigureAwait(false);
            foreach (var warning in result.Warnings)
            {
                _warn(warning);
            }

            pdf = protectedOutput.ToArray();
            isProtected = true;
        }

        var anchors = laidOut.SelectMany(p => p.Anchors).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        return new RenderedPdf(pdf, laidOut.Count, builder.EmbeddedFonts.ToList(), builder.ImageCount, builder.LinkCount, bookmarks.Count(b => anchors.Contains(b.Anchor)), isProtected);
    }

    /// <summary>
    /// Reads the PDF back in strict mode: every page is there, every content stream can be interpreted, and the text a
    /// reader extracts (through each font's encoding and ToUnicode map) is exactly the text that was laid out.
    /// </summary>
    private void Verify(byte[] pdf, List<LaidOutPage> pages, CancellationToken ct)
    {
        try
        {
            using var source = new PdfSource(new MemoryStream(pdf, writable: false), ownsStream: true);
            using var doc = PdfDocument.Open(source, _lib, _log, strict: true, ct);
            var nodes = PageTreeReader.ReadPageNodes(doc, _lib.MaxNestingDepth, ct);
            if (nodes.Count != pages.Count)
            {
                throw new PdfVerificationException($"The PDF has {nodes.Count} pages instead of {pages.Count}.");
            }

            var interpreter = new ContentInterpreter(doc, _lib.MaxNestingDepth, ct);
            var extracted = new StringBuilder();
            foreach (var node in nodes)
            {
                var content = interpreter.Read(node);
                if (content.Warnings.Count > 0)
                {
                    throw new PdfVerificationException("A page of the PDF cannot be read back: " + content.Warnings[0]);
                }

                foreach (var glyph in content.Glyphs)
                {
                    extracted.Append(glyph.Text);
                }
            }

            static string Normalize(string s) => new(s.Where(c => !char.IsWhiteSpace(c) && c is not ('�' or '?' or ' ')).Order().ToArray());
            var expected = string.Concat(pages.SelectMany(p => p.Ops).OfType<TextOp>().Select(t => t.Font.Shown(t.Text)));
            if (Normalize(expected) != Normalize(extracted.ToString()))
            {
                throw new PdfVerificationException("The text read back from the PDF differs from the text that was laid out.");
            }
        }
        catch (PdfCorruptException ex)
        {
            throw new PdfVerificationException("The PDF could not be read back: " + ex.Message, ex);
        }
    }
}
