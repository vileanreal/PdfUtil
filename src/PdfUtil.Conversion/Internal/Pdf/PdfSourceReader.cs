using PdfUtil.Conversion.Internal.Model;
using PdfUtil.Internal;
using PdfUtil.Internal.Analysis;
using PdfUtil.Internal.Content;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;
using PdfUtil.Internal.Text;

namespace PdfUtil.Conversion.Internal.Pdf;

/// <summary>A page as read for conversion, in display space (points, origin at the bottom-left, y up).</summary>
internal sealed class SourcePage
{
    public required int Number { get; init; }

    public required double Width { get; init; }

    public required double Height { get; init; }

    public required List<TextAssembler.Run> Runs { get; init; }

    public List<SourceImage> Images { get; } = new();

    public List<SourceLink> Links { get; } = new();

    public required List<RuleInfo> Rules { get; init; }

    public int UnknownGlyphs { get; init; }
}

internal sealed record SourceImage(PdfRectangle Bounds, ImageData Image);

/// <summary>A link area: an external URI (already checked to be safe) or a page of this document.</summary>
internal sealed record SourceLink(PdfRectangle Bounds, string? Uri, int? Page);

internal sealed class SourceDocument
{
    public required PdfDocumentInfo Info { get; init; }

    public required int TotalPages { get; init; }

    public required List<SourcePage> Pages { get; init; }

    public string? Title => Info.Info?.Title is { Length: > 0 } t ? t : Info.Xmp?.Title;
}

/// <summary>Reads what conversion needs from a PDF: text runs (F15), images, links, rules, bookmarks and metadata.</summary>
internal static class PdfSourceReader
{
    public static SourceDocument Read(PdfSource source, string? password, PdfPageRange? range, bool readImages, PdfUtilOptions lib, PdfConversionLimits limits, PdfLog log, List<string> warnings, IProgress<PdfProgress>? progressSink, CancellationToken ct)
    {
        using var doc = TextExtractionEngine.OpenForReading(source, password, lib, log, warnings, ct);
        var info = new InspectionEngine(lib, log).Inspect(doc, source.Length, password, PdfInspectionDetails.Bookmarks, null, ct);
        var nodes = PageTreeReader.ReadPageNodes(doc, lib.MaxNestingDepth, ct);
        var selected = (range ?? PdfPageRange.All).Resolve(nodes.Count);
        var progress = new ProgressReporter(progressSink);
        progress.Report(PdfProgressStage.Reading, 0, selected.Count, force: true);
        var interpreter = new ContentInterpreter(doc, lib.MaxNestingDepth, ct);
        var images = new ImageExtractor(doc, limits.MaxImagePixels, lib.MaxDecompressedStreamBytes, warnings);
        var details = new DetailReader(doc, nodes, lib, ct);
        var pages = new List<SourcePage>();
        foreach (var number in selected)
        {
            ct.ThrowIfCancellationRequested();
            var node = nodes[number - 1];
            var content = interpreter.Read(node);
            warnings.AddRange(content.Warnings.Select(w => $"Page {number}: {w}").Where(w => !warnings.Contains(w)));
            var page = new SourcePage
            {
                Number = number,
                Width = content.Width,
                Height = content.Height,
                Runs = TextAssembler.BuildRuns(content.Glyphs),
                Rules = content.Rules,
                UnknownGlyphs = content.UnknownGlyphs,
            };
            if (readImages)
            {
                foreach (var image in content.Images)
                {
                    if (image.Bounds.Width >= 0.5 && image.Bounds.Height >= 0.5)
                    {
                        page.Images.Add(new SourceImage(Clip(image.Bounds, page.Width, page.Height), images.Extract(image)));
                    }
                }
            }

            ReadLinks(doc, node, details, page, warnings);
            pages.Add(page);
            progress.Report(PdfProgressStage.Reading, pages.Count, selected.Count);
        }

        progress.Report(PdfProgressStage.Reading, selected.Count, selected.Count, force: true);
        return new SourceDocument { Info = info, TotalPages = nodes.Count, Pages = pages };
    }

    private static PdfRectangle Clip(PdfRectangle r, double width, double height) =>
        new(Math.Max(r.LowerLeftX, 0), Math.Max(r.LowerLeftY, 0), Math.Min(r.UpperRightX, width), Math.Min(r.UpperRightY, height));

    private static void ReadLinks(PdfDocument doc, PageNode node, DetailReader details, SourcePage page, List<string> warnings)
    {
        if (doc.Resolve(node.Page["Annots"]) is not PdfArray annots)
        {
            return;
        }

        var toDisplay = PageGeometry.DisplayToUser(node.Info.Rotate, PageGeometry.VisibleBox(node.Info)).Inverse();
        foreach (var item in annots.Items)
        {
            if (doc.ResolveDictionary(item) is not { } annot || (doc.Resolve(annot["Subtype"]) as PdfName)?.Value != "Link" || doc.Resolve(annot["Rect"]) is not PdfArray { Count: 4 } rect)
            {
                continue;
            }

            var n = rect.Items.Select(o => doc.Resolve(o) switch { PdfInteger i => i.Value, PdfReal r when double.IsFinite(r.Value) => r.Value, _ => 0.0 }).ToArray();
            var corners = new[] { toDisplay.Transform(n[0], n[1]), toDisplay.Transform(n[2], n[3]), toDisplay.Transform(n[0], n[3]), toDisplay.Transform(n[2], n[1]) };
            var bounds = new PdfRectangle(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                continue;
            }

            if (annot["Dest"] is { } dest)
            {
                if (details.DestinationPage(dest, 0) is int target)
                {
                    page.Links.Add(new SourceLink(bounds, null, target));
                }

                continue;
            }

            if (doc.ResolveDictionary(annot["A"]) is not { } action)
            {
                continue;
            }

            switch ((doc.Resolve(action["S"]) as PdfName)?.Value)
            {
                case "GoTo":
                    if (details.DestinationPage(action["D"], 0) is int goTo)
                    {
                        page.Links.Add(new SourceLink(bounds, null, goTo));
                    }

                    break;
                case "URI":
                    var uri = doc.Resolve(action["URI"]) is PdfString s ? TextString.Decode(s.Value) : null;
                    if (UrlSafety.Check(uri) is { } safe)
                    {
                        page.Links.Add(new SourceLink(bounds, safe, null));
                    }
                    else if (uri is not null)
                    {
                        Warn(warnings, "A link with an unsafe or unsupported address was dropped.");
                    }

                    break;
                case var other:
                    Warn(warnings, $"A link with a {other ?? "missing"} action was dropped; only web addresses and links within the document are converted.");
                    break;
            }
        }
    }

    private static void Warn(List<string> warnings, string message)
    {
        if (!warnings.Contains(message))
        {
            warnings.Add(message);
        }
    }
}
