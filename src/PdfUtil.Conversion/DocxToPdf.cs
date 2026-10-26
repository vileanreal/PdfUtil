namespace PdfUtil.Conversion;

/// <summary>Settings for Word (DOCX) to PDF conversion.</summary>
public sealed class DocxToPdfOptions
{
    /// <summary>TrueType fonts to use, by family name (for the document's fonts and for characters the standard fonts lack).</summary>
    public IList<PdfFontSource> Fonts { get; } = new List<PdfFontSource>();

    /// <summary>Folders whose <c>.ttf</c> and <c>.ttc</c> fonts are available, by their family names.</summary>
    public IList<string> FontDirectories { get; } = new List<string>();

    /// <summary>Also use the operating system's fonts (results then depend on the machine). Default <c>false</c>.</summary>
    public bool UseSystemFonts { get; set; }

    /// <summary>The font family for fonts that are not available (a CSS family list). Default <c>sans-serif</c>.</summary>
    public string DefaultFontFamily { get; set; } = "sans-serif";

    /// <summary>Create bookmarks from the headings. Default <c>true</c>.</summary>
    public bool CreateBookmarks { get; set; } = true;

    /// <summary>Protect the PDF in the same call; null for none.</summary>
    public PdfProtectionOptions? Protection { get; set; }

    /// <summary>Optional progress reporting (stage <see cref="PdfProgressStage.Writing"/>, one unit per page).</summary>
    public IProgress<PdfProgress>? Progress { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"DocxToPdfOptions {{ Fonts = {Fonts.Count}, Protected = {Protection is not null} }}";
}

/// <summary>Outcome of a Word to PDF conversion.</summary>
public sealed class DocxToPdfResult
{
    internal DocxToPdfResult()
    {
    }

    /// <summary>Number of pages.</summary>
    public int PageCount { get; internal init; }

    /// <summary>Number of sections with their own page setup.</summary>
    public int SectionCount { get; internal init; }

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

    /// <summary>Whether the PDF was protected with <see cref="DocxToPdfOptions.Protection"/>.</summary>
    public bool IsProtected { get; internal init; }

    /// <summary>Size of the PDF in bytes.</summary>
    public long PdfLength { get; internal init; }

    /// <summary>Non-fatal issues: content that is not converted (one warning per kind), missing glyphs, oversized content.</summary>
    public IReadOnlyList<string> Warnings { get; internal init; } = Array.Empty<string>();

    /// <inheritdoc />
    public override string ToString() => $"DocxToPdfResult {{ Pages = {PageCount}, Sections = {SectionCount}, Fonts = {EmbeddedFonts.Count}, Images = {ImageCount}, Links = {LinkCount}, Bytes = {PdfLength} }}";
}
