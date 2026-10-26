using PdfUtil.Internal.Document;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;

namespace PdfUtil.Internal.Content;

/// <summary>Text extraction (F15): interprets each page and assembles runs and text.</summary>
internal sealed class TextExtractionEngine
{
    private readonly PdfUtilOptions _lib;
    private readonly PdfLog _log;

    public TextExtractionEngine(PdfUtilOptions lib, PdfLog log)
    {
        _lib = lib;
        _log = log;
    }

    /// <summary>Opens a document for reading, with any password that opens it.</summary>
    public static PdfDocument OpenForReading(PdfSource source, string? password, PdfUtilOptions lib, PdfLog log, List<string> warnings, CancellationToken ct)
    {
        var doc = PdfDocument.Open(source, lib, log, strict: false, ct);
        try
        {
            if (doc.IsEncrypted)
            {
                var match = doc.Authenticate(password);
                if (match == PdfPasswordMatch.None)
                {
                    throw new PdfInvalidPasswordException(password is null ? "The document needs a password to open." : "The password does not open the document.");
                }

                if (match == PdfPasswordMatch.User && ((PdfPermissions)doc.Security!.P & PdfPermissions.CopyContent) == 0)
                {
                    warnings.Add("The document's permissions do not allow copying its content; make sure you are entitled to extract it.");
                }
            }

            return doc;
        }
        catch
        {
            doc.Dispose();
            throw;
        }
    }

    public PdfTextExtractionResult Extract(PdfSource source, PdfTextExtractionOptions options, CancellationToken ct)
    {
        var warnings = new List<string>();
        using var doc = OpenForReading(source, options.Password, _lib, _log, warnings, ct);
        var pages = PageTreeReader.ReadPageNodes(doc, _lib.MaxNestingDepth, ct);
        var selected = (options.Pages ?? PdfPageRange.All).Resolve(pages.Count);
        var progress = new ProgressReporter(options.Progress);
        progress.Report(PdfProgressStage.Reading, 0, selected.Count, force: true);
        var interpreter = new ContentInterpreter(doc, _lib.MaxNestingDepth, ct);
        var result = new List<PdfPageText>();
        foreach (var number in selected)
        {
            ct.ThrowIfCancellationRequested();
            var content = interpreter.Read(pages[number - 1]);
            warnings.AddRange(content.Warnings.Select(w => $"Page {number}: {w}"));
            result.Add(ToPageText(doc, number, content, options));
            progress.Report(PdfProgressStage.Reading, result.Count, selected.Count);
        }

        progress.Report(PdfProgressStage.Reading, selected.Count, selected.Count, force: true);
        return new PdfTextExtractionResult { Pages = result, Warnings = warnings.Distinct().ToList() };
    }

    internal static PdfPageText ToPageText(PdfDocument doc, int number, PageContent content, PdfTextExtractionOptions options)
    {
        var runs = TextAssembler.BuildRuns(content.Glyphs);
        return new PdfPageText
        {
            Number = number,
            Width = content.Width,
            Height = content.Height,
            Text = options.Order == PdfTextOrder.Layout ? TextAssembler.LayoutOrder(runs) : TextAssembler.ContentOrder(runs),
            Runs = runs.Select(r => ToRun(r, options.IncludeGlyphs)).ToList(),
            Images = content.Images.Select(i => new PdfImagePlacement
            {
                Bounds = i.Bounds,
                ObjectNumber = i.ObjectNumber,
                PixelWidth = (int)Math.Clamp(Number(doc, i.Dictionary["Width"] ?? i.Dictionary["W"]), 0, int.MaxValue),
                PixelHeight = (int)Math.Clamp(Number(doc, i.Dictionary["Height"] ?? i.Dictionary["H"]), 0, int.MaxValue),
            }).ToList(),
            UnknownGlyphCount = content.UnknownGlyphs,
        };
    }

    private static double Number(PdfDocument doc, PdfObject? o) => doc.Resolve(o) switch
    {
        PdfInteger i => i.Value,
        PdfReal r when double.IsFinite(r.Value) => r.Value,
        _ => 0,
    };

    private static PdfTextRun ToRun(TextAssembler.Run run, bool includeGlyphs)
    {
        var f = run.First;
        var cos = Math.Cos(f.Angle);
        var sin = Math.Sin(f.Angle);
        var length = run.Along(run.EndX, run.EndY);
        var ascent = f.Font.Ascent * f.Size;
        var descent = f.Font.Descent * f.Size;
        (double X, double Y)[] corners =
        {
            (f.X - (sin * descent), f.Y + (cos * descent)),
            (f.X - (sin * ascent), f.Y + (cos * ascent)),
            (run.EndX - (sin * descent), run.EndY + (cos * descent)),
            (run.EndX - (sin * ascent), run.EndY + (cos * ascent)),
        };
        return new PdfTextRun
        {
            Text = run.Text.ToString(),
            FontName = f.Font.Name,
            FontSize = Math.Round(f.Size, 3),
            IsBold = f.Font.IsBold,
            IsItalic = f.Font.IsItalic,
            Color = f.Color,
            X = f.X,
            Y = f.Y,
            Width = length,
            Bounds = new PdfRectangle(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y)),
            Angle = Math.Round(f.Angle * 180 / Math.PI, 3),
            Glyphs = includeGlyphs ? run.Glyphs.Select(g => new PdfGlyph(g.Text, g.X, g.Y, g.Width)).ToList() : Array.Empty<PdfGlyph>(),
        };
    }
}
