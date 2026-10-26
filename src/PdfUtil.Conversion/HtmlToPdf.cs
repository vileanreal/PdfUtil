namespace PdfUtil.Conversion;

/// <summary>A page size in points (1/72 inch).</summary>
/// <param name="Width">Width in points.</param>
/// <param name="Height">Height in points.</param>
public readonly record struct PdfPageSize(double Width, double Height)
{
    /// <summary>ISO A4, 210 × 297 mm.</summary>
    public static PdfPageSize A4 => new(595.28, 841.89);

    /// <summary>ISO A3, 297 × 420 mm.</summary>
    public static PdfPageSize A3 => new(841.89, 1190.55);

    /// <summary>ISO A5, 148 × 210 mm.</summary>
    public static PdfPageSize A5 => new(419.53, 595.28);

    /// <summary>US Letter, 8.5 × 11 in.</summary>
    public static PdfPageSize Letter => new(612, 792);

    /// <summary>US Legal, 8.5 × 14 in.</summary>
    public static PdfPageSize Legal => new(612, 1008);

    /// <summary>The same size turned sideways.</summary>
    public PdfPageSize Landscape => Width >= Height ? this : new(Height, Width);
}

/// <summary>Page margins in points.</summary>
/// <param name="Top">Top margin.</param>
/// <param name="Right">Right margin.</param>
/// <param name="Bottom">Bottom margin.</param>
/// <param name="Left">Left margin.</param>
public readonly record struct PdfMargins(double Top, double Right, double Bottom, double Left)
{
    /// <summary>The same margin on every side.</summary>
    public static PdfMargins All(double margin) => new(margin, margin, margin, margin);
}

/// <summary>A TrueType font supplied by the caller.</summary>
/// <param name="Data">The font file (<c>.ttf</c>, or a <c>.ttc</c> collection). Fonts with PostScript outlines are not supported.</param>
/// <param name="Family">The family name CSS uses for it; default: the name inside the font.</param>
public sealed record PdfFontSource(byte[] Data, string? Family = null);

/// <summary>Loads a resource (style sheet, image or font) for HTML conversion; returns null when it is not available.</summary>
/// <param name="address">The absolute address (relative addresses are resolved against the base URI first).</param>
/// <param name="ct">Cancellation.</param>
public delegate Task<byte[]?> HtmlResourceResolver(Uri address, CancellationToken ct);

/// <summary>Ready-made resource resolvers.</summary>
public static class HtmlResources
{
    /// <summary>
    /// Serves <c>file:</c> addresses inside <paramref name="root"/> (and its subfolders) only; anything else,
    /// including paths that leave the folder, is refused. Network addresses are never fetched.
    /// </summary>
    public static HtmlResourceResolver FromDirectory(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        var full = Path.GetFullPath(root);
        var prefix = full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
        return async (address, ct) =>
        {
            if (!address.IsAbsoluteUri || !address.IsFile)
            {
                return null;
            }

            var path = Path.GetFullPath(address.LocalPath);
            if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !File.Exists(path))
            {
                return null;
            }

            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        };
    }
}

/// <summary>Settings for HTML to PDF conversion.</summary>
public sealed class HtmlToPdfOptions
{
    /// <summary>Page size. Default A4. A CSS <c>@page { size }</c> rule overrides it.</summary>
    public PdfPageSize PageSize { get; set; } = PdfPageSize.A4;

    /// <summary>Page margins. Default 54 pt (0.75 in) on every side. A CSS <c>@page { margin }</c> rule overrides them.</summary>
    public PdfMargins Margins { get; set; } = PdfMargins.All(54);

    /// <summary>The address relative links, images and style sheets are resolved against. A <c>&lt;base href&gt;</c> in the document wins.</summary>
    public Uri? BaseUri { get; set; }

    /// <summary>
    /// Loads external style sheets, images and fonts. Without one, nothing is loaded (each skipped resource is a
    /// warning); <c>data:</c> addresses always work. PdfUtil never fetches from the network itself.
    /// </summary>
    public HtmlResourceResolver? ResourceResolver { get; set; }

    /// <summary>TrueType fonts to use (for CSS families by their name, and for characters the standard fonts lack).</summary>
    public IList<PdfFontSource> Fonts { get; } = new List<PdfFontSource>();

    /// <summary>Folders whose <c>.ttf</c> and <c>.ttc</c> fonts are available, by their family names.</summary>
    public IList<string> FontDirectories { get; } = new List<string>();

    /// <summary>Also use the operating system's fonts (results then depend on the machine). Default <c>false</c>.</summary>
    public bool UseSystemFonts { get; set; }

    /// <summary>The font family for text without one (a CSS family list). Default <c>serif</c>.</summary>
    public string DefaultFontFamily { get; set; } = "serif";

    /// <summary>The font size for text without one, in points. Default 12.</summary>
    public double DefaultFontSize { get; set; } = 12;

    /// <summary>
    /// HTML repeated at the top of every page, inside the top margin. <c>{page}</c> and <c>{pages}</c> (or elements
    /// with class <c>pageNumber</c> and <c>totalPages</c>) become the page number and count. The document's styles apply.
    /// </summary>
    public string? HeaderHtml { get; set; }

    /// <summary>HTML repeated at the bottom of every page, inside the bottom margin; see <see cref="HeaderHtml"/>.</summary>
    public string? FooterHtml { get; set; }

    /// <summary>Create bookmarks from the headings (h1–h6). Default <c>true</c>.</summary>
    public bool CreateBookmarks { get; set; } = true;

    /// <summary>The document title; default the <c>&lt;title&gt;</c> element.</summary>
    public string? Title { get; set; }

    /// <summary>Protect the PDF in the same call (passwords, permissions, algorithm); null for none.</summary>
    public PdfProtectionOptions? Protection { get; set; }

    /// <summary>Optional progress reporting (stage <see cref="PdfProgressStage.Writing"/>, one unit per page).</summary>
    public IProgress<PdfProgress>? Progress { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"HtmlToPdfOptions {{ PageSize = {PageSize}, Fonts = {Fonts.Count}, Protected = {Protection is not null} }}";
}

/// <summary>Outcome of an HTML to PDF conversion.</summary>
public sealed class HtmlToPdfResult
{
    internal HtmlToPdfResult()
    {
    }

    /// <summary>Number of pages.</summary>
    public int PageCount { get; internal init; }

    /// <summary>The TrueType font families embedded (as subsets).</summary>
    public IReadOnlyList<string> EmbeddedFonts { get; internal init; } = Array.Empty<string>();

    /// <summary>Number of distinct images placed.</summary>
    public int ImageCount { get; internal init; }

    /// <summary>Number of link annotations.</summary>
    public int LinkCount { get; internal init; }

    /// <summary>Number of bookmarks created from headings.</summary>
    public int BookmarkCount { get; internal init; }

    /// <summary>Characters no available font could show.</summary>
    public int MissingCharacters { get; internal init; }

    /// <summary>Whether the PDF was protected with <see cref="HtmlToPdfOptions.Protection"/>.</summary>
    public bool IsProtected { get; internal init; }

    /// <summary>Size of the PDF in bytes.</summary>
    public long PdfLength { get; internal init; }

    /// <summary>
    /// Non-fatal issues: unsupported CSS (one warning per kind), resources that were not loaded, missing glyphs,
    /// content that does not fit a page.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; internal init; } = Array.Empty<string>();

    /// <inheritdoc />
    public override string ToString() => $"HtmlToPdfResult {{ Pages = {PageCount}, Fonts = {EmbeddedFonts.Count}, Images = {ImageCount}, Links = {LinkCount}, Bytes = {PdfLength} }}";
}
