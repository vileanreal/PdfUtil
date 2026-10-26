using System.Globalization;
using System.Text;
using PdfUtil.Conversion.Internal.Model;
using PdfUtil.Conversion.Internal.Pdf;

namespace PdfUtil.Conversion.Internal.Html;

/// <summary>Where images go: data URIs, files in an assets folder, or nowhere.</summary>
internal sealed class ImageSink
{
    private readonly PdfHtmlImageMode _mode;
    private readonly string? _folder;
    private readonly Dictionary<string, byte[]>? _files;
    private readonly Dictionary<ImageData, string> _written = new(ReferenceEqualityComparer.Instance);

    public ImageSink(PdfHtmlImageMode mode, string? folder, Dictionary<string, byte[]>? files)
    {
        _mode = mode;
        _folder = folder;
        _files = files;
    }

    public bool Enabled => _mode != PdfHtmlImageMode.None;

    public int ImageCount => _written.Count;

    public int PlaceholderCount { get; private set; }

    public IReadOnlyCollection<string> AssetPaths => _written.Values.Where(v => !v.StartsWith("data:", StringComparison.Ordinal)).ToList();

    /// <summary>The <c>src</c> for an image, or null for a placeholder.</summary>
    public string? Source(ImageData image)
    {
        if (image.IsPlaceholder)
        {
            PlaceholderCount++;
            return null;
        }

        if (_written.TryGetValue(image, out var existing))
        {
            return existing;
        }

        string src;
        if (_mode == PdfHtmlImageMode.SeparateFiles && _files is not null && _folder is not null)
        {
            var name = $"image-{(_written.Count + 1).ToString(CultureInfo.InvariantCulture)}.{image.Extension}";
            _files[name] = image.Data!;
            src = _folder + "/" + name;
        }
        else
        {
            src = $"data:{image.MimeType};base64,{Convert.ToBase64String(image.Data!)}";
        }

        _written[image] = src;
        return src;
    }
}

/// <summary>Settings shared by the HTML writers.</summary>
internal sealed record HtmlSettings(bool Xhtml, bool Navigation, ImageSink Images, int MaxElements);

/// <summary>The parts of an HTML document both writers share: the head and the navigation list.</summary>
internal static class HtmlDocument
{
    public static void Begin(HtmlBuilder html, HtmlSettings settings, string title, string? language, string? author, string? subject, string? keywords, string css)
    {
        if (settings.Xhtml)
        {
            html.Raw("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        }

        html.Raw("<!DOCTYPE html>\n");
        var lang = string.IsNullOrWhiteSpace(language) ? null : language.Trim();
        html.Open("html", ("xmlns", "http://www.w3.org/1999/xhtml"), ("lang", lang), ("xml:lang", settings.Xhtml ? lang : null));
        html.NewLine();
        html.Open("head");
        html.NewLine();
        html.Void("meta", ("charset", "utf-8"));
        html.NewLine();
        html.Void("meta", ("name", "viewport"), ("content", "width=device-width, initial-scale=1"));
        html.NewLine();
        html.Void("meta", ("name", "generator"), ("content", "PdfUtil"));
        html.NewLine();
        foreach (var (name, value) in new[] { ("author", author), ("description", subject), ("keywords", keywords) })
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                html.Void("meta", ("name", name), ("content", value));
                html.NewLine();
            }
        }

        html.Element("title", title);
        html.NewLine();
        // Style content is raw text in HTML but parsed text in XML; CSS without '<' and '&' reads the same in both.
        if (css.Contains('<', StringComparison.Ordinal) || css.Contains('&', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The style sheet contains characters that differ between HTML and XML.");
        }

        html.Open("style");
        html.Raw(css);
        html.Close("style");
        html.NewLine();
        html.Close("head");
        html.NewLine();
        html.Open("body");
        html.NewLine();
    }

    public static void End(HtmlBuilder html)
    {
        html.Close("body");
        html.NewLine();
        html.Close("html");
        html.NewLine();
    }

    public static void Navigation(HtmlBuilder html, IReadOnlyList<OutlineEntry> outline)
    {
        if (outline.Count == 0)
        {
            return;
        }

        html.Open("nav", ("class", "pdf-outline"), ("aria-label", "Bookmarks"));
        NavigationList(html, outline, 0);
        html.Close("nav");
        html.NewLine();
    }

    private static void NavigationList(HtmlBuilder html, IReadOnlyList<OutlineEntry> entries, int depth)
    {
        html.Open("ul");
        foreach (var entry in entries)
        {
            html.Open("li");
            var href = entry.Anchor is not null ? "#" + entry.Anchor : entry.Uri;
            if (href is not null)
            {
                html.Element("a", entry.Title, ("href", href));
            }
            else
            {
                html.Element("span", entry.Title);
            }

            if (entry.Children.Count > 0 && depth < 32)
            {
                NavigationList(html, entry.Children, depth + 1);
            }

            html.Close("li");
        }

        html.Close("ul");
    }
}

/// <summary>Writes a <see cref="DocModel"/> as reflowable HTML.</summary>
internal static class ReflowHtmlWriter
{
    private sealed record Context(HtmlSettings Settings, double BodySize, string BodyFamily);

    public static string Write(DocModel model, HtmlSettings settings)
    {
        var runs = model.AllBlocks().OfType<Paragraph>().SelectMany(p => Inline.Flatten(p.Inlines)).OfType<TextInline>().ToList();
        var bodySize = runs.Where(r => r.Style.Size is not null).GroupBy(r => r.Style.Size!.Value).OrderByDescending(g => g.Sum(r => r.Text.Length)).Select(g => g.Key).FirstOrDefault(12);
        var bodyFamily = runs.GroupBy(r => (r.Style.FontFamily, r.Style.GenericFamily)).OrderByDescending(g => g.Sum(r => r.Text.Length)).Select(g => Family(g.Key.FontFamily, g.Key.GenericFamily)).FirstOrDefault(Family(null, "serif"));
        var context = new Context(settings, bodySize, bodyFamily);
        var css = $"body{{font-family:{bodyFamily};font-size:{HtmlBuilder.Num(bodySize)}pt;line-height:1.4;max-width:48em;margin:2em auto;padding:0 1em;color:#000;background:#fff}}"
            + "img{max-width:100%;height:auto}p,li{margin:0.5em 0}pre{white-space:pre-wrap;font-family:monospace}"
            + ".pdf-image{text-align:center}.pdf-missing-image{display:inline-block;max-width:100%;border:1px dashed #999;color:#666;font:0.8em sans-serif;text-align:center}"
            + ".pdf-page-break{border:0;border-top:1px dashed #bbb;margin:2em 0}.pdf-outline{border-bottom:1px solid #ddd;margin-bottom:1em}"
            + "table{border-collapse:collapse}td,th{border:1px solid #999;padding:0.2em 0.4em;vertical-align:top}";
        var html = new HtmlBuilder(settings.MaxElements);
        var title = model.Title is { Length: > 0 } t ? t : model.AllBlocks().OfType<Paragraph>().FirstOrDefault(p => p.HeadingLevel > 0)?.PlainText.Trim() is { Length: > 0 } h ? h : "Document";
        HtmlDocument.Begin(html, settings, title, model.Language, model.Author, model.Subject, model.Keywords, css);
        if (settings.Navigation)
        {
            HtmlDocument.Navigation(html, model.Outline);
        }

        html.Open("main", ("class", "pdf-document"));
        html.NewLine();
        foreach (var section in model.Sections)
        {
            Blocks(html, section.Blocks, context);
        }

        html.Close("main");
        html.NewLine();
        HtmlDocument.End(html);
        return html.ToString();
    }

    private static string Family(string? family, string? generic)
    {
        var g = generic ?? "serif";
        return family is null || family == g ? g : HtmlBuilder.CssString(family) + "," + g;
    }

    private static void Blocks(HtmlBuilder html, IEnumerable<Block> blocks, Context context)
    {
        foreach (var block in blocks)
        {
            Block(html, block, context);
            html.NewLine();
        }
    }

    private static string? Id(Block block) => block.Anchors.Count > 0 ? block.Anchors[0] : null;

    private static void ExtraAnchors(HtmlBuilder html, Block block)
    {
        foreach (var anchor in block.Anchors.Skip(1))
        {
            html.Element("span", null, ("id", anchor));
        }
    }

    private static void Block(HtmlBuilder html, Block block, Context context)
    {
        switch (block)
        {
            case AnchorBlock:
                html.Element("div", null, ("id", Id(block)), ("class", "pdf-page-start"));
                ExtraAnchors(html, block);
                break;
            case ContainerBlock container:
                html.Open("div", ("id", Id(container)));
                ExtraAnchors(html, container);
                foreach (var child in container.Children)
                {
                    Block(html, child, context);
                }

                html.Close("div");
                break;
            case Paragraph p:
                var tag = p.HeadingLevel > 0 ? "h" + Math.Clamp(p.HeadingLevel, 1, 6).ToString(CultureInfo.InvariantCulture) : p.IsPreformatted ? "pre" : "p";
                var baseStyle = BaseStyle(p);
                html.Open(tag, ("id", Id(p)), ("style", ParagraphCss(p, baseStyle, context)));
                ExtraAnchors(html, p);
                Inlines(html, p.Inlines, baseStyle, p, context);
                html.Close(tag);
                break;
            case ListBlock list:
                var listTag = list.IsOrdered ? "ol" : "ul";
                var type = list.Kind switch
                {
                    ListKind.LowerAlpha => "a",
                    ListKind.UpperAlpha => "A",
                    ListKind.LowerRoman => "i",
                    ListKind.UpperRoman => "I",
                    _ => null,
                };
                html.Open(listTag, ("id", Id(list)), ("type", type), ("start", list.IsOrdered && list.Start != 1 ? list.Start.ToString(CultureInfo.InvariantCulture) : null));
                ExtraAnchors(html, list);
                foreach (var item in list.Items)
                {
                    html.NewLine();
                    html.Open("li");
                    var rest = item.Blocks;
                    if (item.Blocks.Count > 0 && item.Blocks[0] is Paragraph { HeadingLevel: 0, IsPreformatted: false } first && first.Anchors.Count == 0)
                    {
                        Inlines(html, first.Inlines, BaseStyle(first), first, context);
                        rest = item.Blocks.Skip(1).ToList();
                    }

                    foreach (var child in rest)
                    {
                        Block(html, child, context);
                    }

                    html.Close("li");
                }

                html.NewLine();
                html.Close(listTag);
                break;
            case TableBlock table:
                html.Open("table", ("id", Id(table)));
                ExtraAnchors(html, table);
                for (var r = 0; r < table.Rows.Count; r++)
                {
                    html.NewLine();
                    html.Open("tr");
                    foreach (var cell in table.Rows[r].Cells)
                    {
                        var cellTag = r < table.HeaderRows ? "th" : "td";
                        html.Open(cellTag, ("colspan", cell.ColumnSpan > 1 ? cell.ColumnSpan.ToString(CultureInfo.InvariantCulture) : null), ("rowspan", cell.RowSpan > 1 ? cell.RowSpan.ToString(CultureInfo.InvariantCulture) : null), ("style", cell.Background is null ? null : "background-color:" + cell.Background));
                        foreach (var child in cell.Blocks)
                        {
                            Block(html, child, context);
                        }

                        html.Close(cellTag);
                    }

                    html.Close("tr");
                }

                html.NewLine();
                html.Close("table");
                break;
            case ImageBlock image:
                if (!context.Settings.Images.Enabled)
                {
                    break;
                }

                html.Open("p", ("id", Id(image)), ("class", "pdf-image"));
                ExtraAnchors(html, image);
                Image(html, image.Image, image.Width, image.Height, image.AltText, context.Settings.Images);
                html.Close("p");
                break;
            case RuleBlock:
                html.Void("hr", ("id", Id(block)));
                break;
            case PageBreakBlock:
                html.Void("hr", ("id", Id(block)), ("class", "pdf-page-break"));
                break;
        }
    }

    internal static void Image(HtmlBuilder html, ImageData image, double width, double height, string? alt, ImageSink sink)
    {
        var src = sink.Source(image);
        var size = $"width:{HtmlBuilder.Num(width)}pt;height:{HtmlBuilder.Num(height)}pt";
        if (src is null)
        {
            html.Element("span", "Image not converted", ("class", "pdf-missing-image"), ("style", size), ("title", image.Placeholder));
        }
        else
        {
            html.Void("img", ("src", src), ("alt", alt ?? ""), ("style", "width:" + HtmlBuilder.Num(width) + "pt"));
        }
    }

    /// <summary>The style most of a paragraph's text uses; runs only spell out what differs from it.</summary>
    private static RunStyle BaseStyle(Paragraph p)
    {
        var runs = Inline.Flatten(p.Inlines).OfType<TextInline>().ToList();
        if (runs.Count == 0)
        {
            return RunStyle.Plain;
        }

        var main = runs.GroupBy(r => (r.Style.FontFamily, r.Style.GenericFamily, r.Style.Size, r.Style.Bold, r.Style.Italic, r.Style.Color)).OrderByDescending(g => g.Sum(r => r.Text.Length)).First().Key;
        return new RunStyle { FontFamily = main.FontFamily, GenericFamily = main.GenericFamily, Size = main.Size, Bold = main.Bold, Italic = main.Italic, Color = main.Color };
    }

    private static string? ParagraphCss(Paragraph p, RunStyle style, Context context)
    {
        var css = new StringBuilder();
        if (style.Size is double size && (p.HeadingLevel > 0 || Math.Abs(size - context.BodySize) > 0.5))
        {
            css.Append("font-size:").Append(HtmlBuilder.Num(size)).Append("pt;");
        }

        var family = Family(style.FontFamily, style.GenericFamily);
        if (style.FontFamily is not null && family != context.BodyFamily && !p.IsPreformatted)
        {
            css.Append("font-family:").Append(family).Append(';');
        }

        if (p.HeadingLevel > 0 && !style.Bold)
        {
            css.Append("font-weight:normal;");
        }
        else if (p.HeadingLevel == 0 && style.Bold)
        {
            css.Append("font-weight:bold;");
        }

        if (style.Italic)
        {
            css.Append("font-style:italic;");
        }

        if (style.Color is not null)
        {
            css.Append("color:").Append(style.Color).Append(';');
        }

        if (p.Alignment != TextAlignment.Left)
        {
            css.Append("text-align:").Append(p.Alignment.ToString().ToLowerInvariant()).Append(';');
        }

        if (p.IndentLeft > 0)
        {
            css.Append("margin-left:").Append(HtmlBuilder.Num(p.IndentLeft)).Append("pt;");
        }

        return css.Length == 0 ? null : css.ToString().TrimEnd(';');
    }

    private static void Inlines(HtmlBuilder html, IEnumerable<Inline> inlines, RunStyle baseStyle, Paragraph p, Context context)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case TextInline text:
                    Text(html, text, baseStyle, p);
                    break;
                case LinkInline link:
                    html.Open("a", ("href", link.Anchor is not null ? "#" + link.Anchor : link.Uri));
                    Inlines(html, link.Children, baseStyle, p, context);
                    html.Close("a");
                    break;
                case BreakInline:
                    if (p.IsPreformatted)
                    {
                        html.Text("\n");
                    }
                    else
                    {
                        html.Void("br");
                    }

                    break;
                case ImageInline image when context.Settings.Images.Enabled:
                    Image(html, image.Image, image.Width, image.Height, image.AltText, context.Settings.Images);
                    break;
                case AnchorInline anchor:
                    html.Element("span", null, ("id", anchor.Name));
                    break;
            }
        }
    }

    private static void Text(HtmlBuilder html, TextInline text, RunStyle baseStyle, Paragraph p)
    {
        var s = text.Style;
        var tags = new List<(string Tag, string? Style)>();
        if (s.Position == VerticalPosition.Superscript)
        {
            tags.Add(("sup", null));
        }
        else if (s.Position == VerticalPosition.Subscript)
        {
            tags.Add(("sub", null));
        }

        if (s.Bold && !baseStyle.Bold && p.HeadingLevel == 0)
        {
            tags.Add(("strong", null));
        }

        if (s.Italic && !baseStyle.Italic)
        {
            tags.Add(("em", null));
        }

        if (s.Underline)
        {
            tags.Add(("u", null));
        }

        if (s.Strike)
        {
            tags.Add(("s", null));
        }

        var monospace = s.GenericFamily == "monospace" && baseStyle.GenericFamily != "monospace";
        if (monospace)
        {
            tags.Add(("code", null));
        }

        var css = new StringBuilder();
        if (!s.Bold && baseStyle.Bold)
        {
            css.Append("font-weight:normal;");
        }
        else if (s.Bold && p.HeadingLevel > 0 && !baseStyle.Bold)
        {
            css.Append("font-weight:bold;");
        }

        if (!s.Italic && baseStyle.Italic)
        {
            css.Append("font-style:normal;");
        }

        if (s.Color != baseStyle.Color)
        {
            css.Append("color:").Append(s.Color ?? "#000000").Append(';');
        }

        if (s.Size is double size && baseStyle.Size is double baseSize && s.Position == VerticalPosition.Baseline && Math.Abs(size - baseSize) > 0.1 * baseSize)
        {
            css.Append("font-size:").Append(HtmlBuilder.Num(size)).Append("pt;");
        }

        if (!monospace && s.FontFamily is not null && s.FontFamily != baseStyle.FontFamily && s.GenericFamily != "monospace")
        {
            css.Append("font-family:").Append(Family(s.FontFamily, s.GenericFamily)).Append(';');
        }

        if (s.Background is not null)
        {
            css.Append("background-color:").Append(s.Background).Append(';');
        }

        if (css.Length > 0)
        {
            tags.Add(("span", css.ToString().TrimEnd(';')));
        }

        foreach (var (tag, style) in tags)
        {
            html.Open(tag, ("style", style));
        }

        html.Text(text.Text);
        for (var i = tags.Count - 1; i >= 0; i--)
        {
            html.Close(tags[i].Tag);
        }
    }
}

/// <summary>Writes pages as positioned containers: text runs and images where the PDF draws them, links as overlays.</summary>
internal static class FixedHtmlWriter
{
    /// <summary>Where a browser puts the baseline of a line box with <c>line-height:1</c>, as a fraction of the font size (typical for common fonts).</summary>
    private const double Baseline = 0.84;

    public static string Write(SourceDocument source, DocModel model, HtmlSettings settings, out int linkCount)
    {
        var sheet = "body{margin:0;padding:1em 0;background:#e6e6e6}"
            + ".pdf-page{position:relative;overflow:hidden;margin:0 auto 1em auto;background:#fff;box-shadow:0 0 4px rgba(0,0,0,0.3)}"
            + ".pdf-page>*{position:absolute;margin:0}"
            + $".pdf-text{{white-space:pre;line-height:1;transform-origin:0 {HtmlBuilder.Num(Baseline)}em}}"
            + ".pdf-hidden{color:transparent}.pdf-link{display:block;z-index:2}"
            + ".pdf-missing-image{box-sizing:border-box;border:1px dashed #999;color:#666;font:8pt sans-serif;text-align:center;overflow:hidden}"
            + ".pdf-outline{background:#fff;max-width:40em;margin:0 auto 1em auto;padding:0.5em 1em;font-family:sans-serif}";
        var html = new HtmlBuilder(settings.MaxElements);
        var title = model.Title is { Length: > 0 } t ? t : "Document";
        HtmlDocument.Begin(html, settings, title, model.Language, model.Author, model.Subject, model.Keywords, sheet);
        if (settings.Navigation)
        {
            HtmlDocument.Navigation(html, model.Outline);
        }

        var converted = source.Pages.Select(p => p.Number).ToHashSet();
        linkCount = 0;
        html.Open("main", ("class", "pdf-document pdf-fixed"));
        html.NewLine();
        foreach (var page in source.Pages)
        {
            html.Open("div", ("class", "pdf-page"), ("id", ReflowAnalyzer.PageAnchor(page.Number)), ("style", $"width:{HtmlBuilder.Num(page.Width)}pt;height:{HtmlBuilder.Num(page.Height)}pt"));
            html.NewLine();
            if (settings.Images.Enabled)
            {
                foreach (var image in page.Images)
                {
                    var b = image.Bounds;
                    var box = $"left:{HtmlBuilder.Num(b.LowerLeftX)}pt;top:{HtmlBuilder.Num(page.Height - b.UpperRightY)}pt;width:{HtmlBuilder.Num(b.Width)}pt;height:{HtmlBuilder.Num(b.Height)}pt";
                    var src = settings.Images.Source(image.Image);
                    if (src is null)
                    {
                        html.Element("div", "Image not converted", ("class", "pdf-missing-image"), ("style", box), ("title", image.Image.Placeholder));
                    }
                    else
                    {
                        html.Void("img", ("src", src), ("alt", ""), ("style", box));
                    }

                    html.NewLine();
                }
            }

            foreach (var run in page.Runs)
            {
                var text = run.Text.ToString();
                if (text.Trim().Length == 0)
                {
                    continue;
                }

                var f = run.First;
                var css = new StringBuilder();
                var angle = f.Angle * 180 / Math.PI;
                css.Append("left:").Append(HtmlBuilder.Num(f.X)).Append("pt;top:").Append(HtmlBuilder.Num(page.Height - f.Y - (Baseline * run.Size))).Append("pt;");
                css.Append("font-size:").Append(HtmlBuilder.Num(run.Size)).Append("pt;font-family:").Append(HtmlBuilder.CssString(ReflowAnalyzer.FamilyName(f.Font.Name))).Append(',').Append(f.Font.IsMonospace ? "monospace" : f.Font.IsSerif ? "serif" : "sans-serif").Append(';');
                if (f.Font.IsBold)
                {
                    css.Append("font-weight:bold;");
                }

                if (f.Font.IsItalic)
                {
                    css.Append("font-style:italic;");
                }

                if (f.Color != "#000000" && !f.IsInvisible)
                {
                    css.Append("color:").Append(f.Color).Append(';');
                }

                if (Math.Abs(angle) > 0.1)
                {
                    css.Append("transform:rotate(").Append(HtmlBuilder.Num(-angle)).Append("deg);");
                }

                html.Element("span", text, ("class", f.IsInvisible ? "pdf-text pdf-hidden" : "pdf-text"), ("style", css.ToString().TrimEnd(';')));
                html.NewLine();
            }

            foreach (var link in page.Links)
            {
                var href = link.Uri ?? (link.Page is int p && converted.Contains(p) ? "#" + ReflowAnalyzer.PageAnchor(p) : null);
                if (href is null)
                {
                    continue;
                }

                var b = link.Bounds;
                var label = link.Uri ?? $"Page {link.Page!.Value.ToString(CultureInfo.InvariantCulture)}";
                html.Element("a", null, ("class", "pdf-link"), ("href", href), ("title", label), ("aria-label", label), ("style", $"left:{HtmlBuilder.Num(b.LowerLeftX)}pt;top:{HtmlBuilder.Num(page.Height - b.UpperRightY)}pt;width:{HtmlBuilder.Num(b.Width)}pt;height:{HtmlBuilder.Num(b.Height)}pt"));
                html.NewLine();
                linkCount++;
            }

            html.Close("div");
            html.NewLine();
        }

        html.Close("main");
        html.NewLine();
        HtmlDocument.End(html);
        return html.ToString();
    }
}
