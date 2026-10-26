using System.Globalization;
using System.Text;
using PdfUtil.Conversion.Internal.Css;
using PdfUtil.Conversion.Internal.Html;
using PdfUtil.Conversion.Internal.Layout;
using PdfUtil.Conversion.Internal.Model;
using PdfUtil.Internal;
using PdfUtil.Internal.Content;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Parsing;

namespace PdfUtil.Conversion.Internal;

/// <summary>
/// HTML to PDF (F21): parse the HTML, load style sheets, images and fonts through the caller's resolver, compute
/// styles, build the document model, lay it out, write the PDF, check it by reading it back, and optionally protect it.
/// </summary>
internal sealed class HtmlToPdfEngine
{
    /// <summary>The user-agent style sheet: HTML's default rendering, adapted for print.</summary>
    internal const string UserAgentCss = """
        html, address, blockquote, body, center, dd, details, dialog, dir, div, dl, dt, fieldset, figcaption, figure, footer, form, header, hgroup, hr, legend, li, main, menu, nav, ol, p, pre, section, summary, ul, article, aside, h1, h2, h3, h4, h5, h6 { display: block }
        head, script, style, title, meta, link, template, noscript, base, datalist, area, param, source, track { display: none }
        [hidden] { display: none }
        p, dl, figure, blockquote, pre { margin-top: 1em; margin-bottom: 1em }
        blockquote, figure { margin-left: 40px; margin-right: 40px }
        h1 { font-size: 2em; margin-top: 0.67em; margin-bottom: 0.67em; font-weight: bold }
        h2 { font-size: 1.5em; margin-top: 0.83em; margin-bottom: 0.83em; font-weight: bold }
        h3 { font-size: 1.17em; margin-top: 1em; margin-bottom: 1em; font-weight: bold }
        h4 { margin-top: 1.33em; margin-bottom: 1.33em; font-weight: bold }
        h5 { font-size: 0.83em; margin-top: 1.67em; margin-bottom: 1.67em; font-weight: bold }
        h6 { font-size: 0.67em; margin-top: 2.33em; margin-bottom: 2.33em; font-weight: bold }
        ul, ol, menu, dir { margin-top: 1em; margin-bottom: 1em; padding-left: 40px }
        ul ul, ul ol, ol ul, ol ol { margin-top: 0; margin-bottom: 0 }
        ul, menu, dir { list-style-type: disc }
        ol { list-style-type: decimal }
        ul ul, ol ul { list-style-type: circle }
        ul ul ul, ul ol ul, ol ul ul, ol ol ul { list-style-type: square }
        li { display: list-item }
        dd { margin-left: 40px }
        pre, code, kbd, samp, tt { font-family: monospace }
        pre { white-space: pre }
        b, strong, th { font-weight: bold }
        i, em, cite, var, dfn, address { font-style: italic }
        u, ins { text-decoration: underline }
        s, strike, del { text-decoration: line-through }
        sub { vertical-align: sub; font-size: smaller }
        sup { vertical-align: super; font-size: smaller }
        small { font-size: smaller }
        big { font-size: larger }
        mark { background-color: yellow }
        center { text-align: center }
        hr { margin-top: 0.5em; margin-bottom: 0.5em; border-top: 1px solid #808080 }
        table { display: table; border-spacing: 2px }
        caption { display: table-caption; text-align: center }
        thead { display: table-header-group }
        tbody { display: table-row-group }
        tfoot { display: table-footer-group }
        tr { display: table-row }
        td, th { display: table-cell; padding: 1px }
        th { text-align: center }
        a:link { color: #0000EE; text-decoration: underline }
        """;

    private readonly PdfUtilOptions _lib;
    private readonly PdfConversionLimits _limits;
    private readonly PdfLog _log;
    private readonly List<string> _warnings = new();
    private readonly HashSet<string> _skippedResources = new(StringComparer.Ordinal);
    private int _resources;

    public HtmlToPdfEngine(PdfUtilOptions lib, PdfConversionLimits limits, PdfLog log)
    {
        _lib = lib;
        _limits = limits;
        _log = log;
    }

    public static void Validate(HtmlToPdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var (w, h) = (options.PageSize.Width, options.PageSize.Height);
        if (!double.IsFinite(w) || !double.IsFinite(h) || w < 36 || h < 36 || w > 14400 || h > 14400)
        {
            throw new ArgumentException("PageSize must be between 36 and 14400 points on each side.", nameof(options));
        }

        var m = options.Margins;
        if (new[] { m.Top, m.Right, m.Bottom, m.Left }.Any(v => !double.IsFinite(v) || v < 0) || m.Left + m.Right > w - 36 || m.Top + m.Bottom > h - 36)
        {
            throw new ArgumentException("Margins must be non-negative and leave at least 36 points for content.", nameof(options));
        }

        if (!double.IsFinite(options.DefaultFontSize) || options.DefaultFontSize is < 1 or > 500)
        {
            throw new ArgumentException("DefaultFontSize must be between 1 and 500 points.", nameof(options));
        }
    }

    private void Warn(string message)
    {
        if (!_warnings.Contains(message))
        {
            _warnings.Add(message);
        }
    }

    public async Task<(byte[] Pdf, HtmlToPdfResult Result)> ConvertAsync(string html, HtmlToPdfOptions options, CancellationToken ct)
    {
        if (html.Length > _limits.MaxInputBytes)
        {
            throw new PdfLimitExceededException($"The HTML is larger than the configured maximum of {_limits.MaxInputBytes} characters.");
        }

        var dom = Guard.Run(() => HtmlParser.Parse(html, _limits.MaxElements, _limits.MaxNestingDepth));
        ct.ThrowIfCancellationRequested();

        // Base address: <base href> (absolute only), or the option.
        var baseUri = options.BaseUri;
        if (dom.Head.Descendants().FirstOrDefault(e => e.Name == "base")?.Attr("href") is { } baseHref && Uri.TryCreate(baseHref, UriKind.Absolute, out var baseAbsolute))
        {
            baseUri = baseAbsolute;
        }

        // Style sheets: <link rel=stylesheet>, <style>, and their @imports.
        var order = 0;
        var sheets = new List<StyleSheet> { CssParser.Parse(UserAgentCss, 0, ref order, _ => { }) };
        foreach (var element in dom.Root.Descendants())
        {
            ct.ThrowIfCancellationRequested();
            if (element.Name == "style" && Media(element.Attr("media")))
            {
                var sheet = CssParser.Parse(element.TextContent, 1, ref order, Warn);
                sheets.Add(sheet);
                await Imports(sheet, baseUri, options, sheets, 0, ct).ConfigureAwait(false);
            }
            else if (element.Name == "link" && (element.Attr("rel") ?? "").Split(' ').Contains("stylesheet", StringComparer.OrdinalIgnoreCase) && Media(element.Attr("media")) && element.Attr("href") is { } href)
            {
                if (await Fetch(href, baseUri, options, ct).ConfigureAwait(false) is { } css)
                {
                    var sheet = CssParser.Parse(Encoding.UTF8.GetString(css), 1, ref order, Warn);
                    sheets.Add(sheet);
                    await Imports(sheet, baseUri, options, sheets, 0, ct).ConfigureAwait(false);
                }
            }
        }

        // Page setup: options, then @page.
        var section = new Section
        {
            PageWidth = options.PageSize.Width,
            PageHeight = options.PageSize.Height,
            Margins = new Margins(options.Margins.Top, options.Margins.Right, options.Margins.Bottom, options.Margins.Left),
        };
        ApplyPage(section, sheets.SelectMany(s => s.PageDeclarations), options.DefaultFontSize);
        section.HeaderDistance = Math.Min(Math.Max(12, section.Margins.Top / 3), 36);
        var contentWidth = section.PageWidth - section.Margins.Left - section.Margins.Right;

        // Fonts: supplied, folders, system, @font-face.
        var fontSources = new List<(string?, byte[], string)>();
        foreach (var font in options.Fonts)
        {
            fontSources.Add((font.Family, font.Data, "supplied font " + (font.Family ?? (fontSources.Count + 1).ToString(CultureInfo.InvariantCulture))));
        }

        fontSources.AddRange(FontSources.FromDirectories(options.FontDirectories, options.UseSystemFonts, Warn));

        foreach (var face in sheets.SelectMany(s => s.FontFaces))
        {
            foreach (var source in face.Sources)
            {
                if (await Fetch(source, baseUri, options, ct).ConfigureAwait(false) is { } data)
                {
                    fontSources.Add((face.Family, data, "@font-face " + face.Family));
                    break;
                }
            }
        }

        var faces = FontProvider.Load(fontSources, Warn);
        var fonts = new FontProvider(faces, options.DefaultFontFamily, Warn);

        // Images.
        var images = new Dictionary<string, ImageData?>(StringComparer.Ordinal);
        foreach (var src in dom.Root.Descendants().Where(e => e.Name == "img").Select(e => e.Attr("src")?.Trim()).OfType<string>().Distinct())
        {
            var data = await Fetch(src, baseUri, options, ct).ConfigureAwait(false);
            images[src] = data is null ? null : HtmlModelBuilder.Describe(data, _limits.MaxImagePixels, Warn);
        }

        // Styles and model.
        var initial = new ComputedStyle
        {
            Display = "block",
            FontSize = options.DefaultFontSize,
            FontFamily = CssParser.SplitTopLevel(options.DefaultFontFamily, ',').Select(f => CssParser.Unquote(f.Trim())).ToList(),
        };
        var cascade = new Cascade(sheets, Warn, options.DefaultFontSize);
        cascade.Compute(dom.Root, initial);
        var builder = new HtmlModelBuilder(cascade, src => images.GetValueOrDefault(src), Warn, contentWidth) { BaseUri = baseUri };
        section.Blocks.AddRange(builder.Build(dom.Body));
        var model = new DocModel
        {
            Title = options.Title ?? dom.Head.Descendants().FirstOrDefault(e => e.Name == "title")?.TextContent.Trim(),
            Language = dom.Root.Attr("lang") ?? dom.Root.Attr("xml:lang"),
            Author = Meta(dom, "author"),
            Subject = Meta(dom, "description"),
            Keywords = Meta(dom, "keywords"),
        };
        model.Sections.Add(section);
        foreach (var (template, target) in new[] { (options.HeaderHtml, section.Header), (options.FooterHtml, section.Footer) })
        {
            if (!string.IsNullOrWhiteSpace(template))
            {
                var fragment = HtmlParser.Parse(template, _limits.MaxElements, _limits.MaxNestingDepth);
                var fragmentStyles = new Cascade(sheets, Warn, options.DefaultFontSize);
                fragmentStyles.Compute(fragment.Root, initial);
                target.AddRange(new HtmlModelBuilder(fragmentStyles, src => null, Warn, contentWidth, fields: true) { BaseUri = baseUri }.Build(fragment.Body));
            }
        }

        WarnComplexScripts(html, Warn);
        ct.ThrowIfCancellationRequested();

        var rendered = await new ModelRenderer(_lib, _limits, _log, Warn).RenderAsync(model, fonts, options.Protection, options.CreateBookmarks, "PdfUtil HTML to PDF", options.Progress, ct).ConfigureAwait(false);
        _log.Info($"Converted HTML to a {rendered.Pages}-page, {rendered.Pdf.Length}-byte PDF.");
        return (rendered.Pdf, new HtmlToPdfResult
        {
            PageCount = rendered.Pages,
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

    private static bool Media(string? media)
    {
        if (string.IsNullOrWhiteSpace(media))
        {
            return true;
        }

        var lower = media.ToLowerInvariant();
        return lower.Contains("print", StringComparison.Ordinal) || lower.Contains("all", StringComparison.Ordinal);
    }

    private static string? Meta(HtmlDom dom, string name) =>
        dom.Head.Descendants().FirstOrDefault(e => e.Name == "meta" && string.Equals(e.Attr("name"), name, StringComparison.OrdinalIgnoreCase))?.Attr("content");

    private async Task Imports(StyleSheet sheet, Uri? baseUri, HtmlToPdfOptions options, List<StyleSheet> sheets, int depth, CancellationToken ct)
    {
        if (depth > 4)
        {
            return;
        }

        foreach (var import in sheet.Imports)
        {
            if (await Fetch(import, baseUri, options, ct).ConfigureAwait(false) is { } css)
            {
                // Imported rules come before the importing sheet's own in cascade order; their order here is close enough.
                var order = -100_000 * (depth + 1);
                var imported = CssParser.Parse(Encoding.UTF8.GetString(css), 1, ref order, Warn);
                sheets.Insert(1, imported);
                await Imports(imported, baseUri, options, sheets, depth + 1, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Loads a resource: data URIs directly, everything else only through the caller's resolver.</summary>
    private async Task<byte[]?> Fetch(string reference, Uri? baseUri, HtmlToPdfOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var value = reference.Trim();
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return DataUri(value);
        }

        if (++_resources > _limits.MaxResources)
        {
            Warn($"The document refers to more than the configured maximum of {_limits.MaxResources} resources; the rest were skipped.");
            return null;
        }

        Uri? address = null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            address = absolute;
        }
        else if (baseUri is not null && Uri.TryCreate(baseUri, value, out var combined))
        {
            address = combined;
        }

        if (options.ResourceResolver is null || address is null)
        {
            if (_skippedResources.Add(value))
            {
                Warn(options.ResourceResolver is null
                    ? "External resources (style sheets, images, fonts) are not loaded without a ResourceResolver; they were skipped."
                    : "A relative resource address cannot be resolved without a BaseUri; it was skipped.");
            }

            return null;
        }

        byte[]? data;
        try
        {
            data = await options.ResourceResolver(address, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Warn($"A resource could not be loaded ({ex.GetType().Name}); it was skipped.");
            return null;
        }

        if (data is null)
        {
            Warn("Some resources were not found by the resolver and were skipped.");
            return null;
        }

        if (data.LongLength > _limits.MaxResourceBytes)
        {
            Warn($"A resource is larger than the configured maximum of {_limits.MaxResourceBytes} bytes and was skipped.");
            return null;
        }

        return data;
    }

    private byte[]? DataUri(string uri)
    {
        var comma = uri.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return null;
        }

        var header = uri[5..comma];
        var payload = uri[(comma + 1)..];
        if (payload.Length > _limits.MaxResourceBytes * 2)
        {
            Warn("A data URI is larger than the configured maximum resource size and was skipped.");
            return null;
        }

        try
        {
            return header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(payload.Replace(" ", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal))
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }
        catch (FormatException)
        {
            Warn("A data URI is damaged and was skipped.");
            return null;
        }
    }

    private void ApplyPage(Section section, IEnumerable<Declaration> declarations, double fontSize)
    {
        foreach (var d in declarations)
        {
            var value = d.Value.Trim().ToLowerInvariant();
            switch (d.Property)
            {
                case "size":
                    var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var landscape = parts.Contains("landscape");
                    var named = parts.FirstOrDefault(p => p is "a3" or "a4" or "a5" or "letter" or "legal") switch
                    {
                        "a3" => PdfPageSize.A3,
                        "a4" => PdfPageSize.A4,
                        "a5" => PdfPageSize.A5,
                        "letter" => PdfPageSize.Letter,
                        "legal" => PdfPageSize.Legal,
                        _ => (PdfPageSize?)null,
                    };
                    var lengths = parts.Select(p => CssValues.ParseLength(p, fontSize, fontSize)).Where(l => l is { IsPercent: false } && l.Value.Value > 0).Select(l => l!.Value.Value).ToList();
                    var size = named ?? (lengths.Count >= 2 ? new PdfPageSize(lengths[0], lengths[1]) : lengths.Count == 1 ? new PdfPageSize(lengths[0], lengths[0]) : new PdfPageSize(section.PageWidth, section.PageHeight));
                    if (landscape)
                    {
                        size = size.Landscape;
                    }
                    else if (parts.Contains("portrait") && size.Width > size.Height)
                    {
                        size = new PdfPageSize(size.Height, size.Width);
                    }

                    if (size.Width is >= 36 and <= 14400 && size.Height is >= 36 and <= 14400)
                    {
                        (section.PageWidth, section.PageHeight) = (size.Width, size.Height);
                    }

                    break;
                case "margin":
                    var m = CssParser.SplitTopLevel(value, ' ').Select(p => CssValues.ParseLength(p, fontSize, fontSize)?.Resolve(section.PageWidth) ?? 0).ToList();
                    if (m.Count > 0)
                    {
                        var (t, r, b, l) = m.Count switch
                        {
                            1 => (m[0], m[0], m[0], m[0]),
                            2 => (m[0], m[1], m[0], m[1]),
                            3 => (m[0], m[1], m[2], m[1]),
                            _ => (m[0], m[1], m[2], m[3]),
                        };
                        section.Margins = new Margins(t, r, b, l);
                    }

                    break;
                case "margin-top" or "margin-right" or "margin-bottom" or "margin-left":
                    var v = CssValues.ParseLength(value, fontSize, fontSize)?.Resolve(section.PageWidth) ?? 0;
                    section.Margins = d.Property[7] switch
                    {
                        't' => section.Margins with { Top = v },
                        'r' => section.Margins with { Right = v },
                        'b' => section.Margins with { Bottom = v },
                        _ => section.Margins with { Left = v },
                    };
                    break;
                default:
                    Warn($"The @page property '{d.Property}' is not supported and was ignored.");
                    break;
            }
        }

        // Margins always leave room for content.
        var mm = section.Margins;
        section.Margins = new Margins(
            Math.Clamp(mm.Top, 0, section.PageHeight / 3),
            Math.Clamp(mm.Right, 0, section.PageWidth / 3),
            Math.Clamp(mm.Bottom, 0, section.PageHeight / 3),
            Math.Clamp(mm.Left, 0, section.PageWidth / 3));
    }

    internal static void WarnComplexScripts(string text, Action<string> warn)
    {
        foreach (var c in text)
        {
            if (c is (>= '֐' and <= 'ࣿ') or (>= 'יִ' and <= '﷿') or (>= 'ﹰ' and <= '﻿'))
            {
                warn("Right-to-left text (Hebrew, Arabic) is laid out left to right without shaping.");
                return;
            }

            if (c is (>= 'ऀ' and <= '෿') or (>= '฀' and <= '໿') or (>= 'က' and <= '႟') or (>= 'ក' and <= '៿'))
            {
                warn("Complex scripts (Indic, Thai and others) are laid out without shaping.");
                return;
            }
        }
    }
}
