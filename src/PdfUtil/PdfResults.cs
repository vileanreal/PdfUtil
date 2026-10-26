namespace PdfUtil;

/// <summary>Outcome of a successful protection.</summary>
public sealed class PdfProtectionResult
{
    internal PdfProtectionResult(string sourceVersion, string outputVersion, PdfEncryptionAlgorithm algorithm, int revision, IReadOnlyList<string> warnings, bool userPasswordWasNormalized)
    {
        SourceVersion = sourceVersion;
        OutputVersion = outputVersion;
        Algorithm = algorithm;
        Revision = revision;
        Warnings = warnings;
        UserPasswordWasNormalized = userPasswordWasNormalized;
    }

    /// <summary>Effective version of the input (max of header and catalog <c>/Version</c>), e.g. <c>"1.4"</c>.</summary>
    public string SourceVersion { get; }

    /// <summary>Version written to the output header, e.g. <c>"1.7"</c> or <c>"2.0"</c>.</summary>
    public string OutputVersion { get; }

    /// <summary>Algorithm used.</summary>
    public PdfEncryptionAlgorithm Algorithm { get; }

    /// <summary>Standard Security Handler revision written (3, 4 or 6).</summary>
    public int Revision { get; }

    /// <summary>
    /// <c>true</c> when SASLprep changed the user or owner password (AES-256 only), for example by mapping a
    /// non-ASCII space to U+0020 or by Unicode normalisation. Such passwords still verify with this library and with
    /// SASLprep-aware readers; readers that skip SASLprep need the normalised form.
    /// </summary>
    public bool UserPasswordWasNormalized { get; }

    /// <summary>Whether <see cref="PdfProtectionOptions.InfoChanges"/> changed the document information.</summary>
    public bool DocumentInfoChanged { get; internal init; }

    /// <summary>Whether the XMP metadata was updated to match the document information changes.</summary>
    public bool XmpUpdated { get; internal init; }

    /// <summary>Non-fatal issues (PDF/A conformance lost, signature invalidated, repairs performed, ...).</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <inheritdoc />
    public override string ToString() => $"PdfProtectionResult {{ OutputVersion = {OutputVersion}, Algorithm = {Algorithm}, R = {Revision}, Warnings = {Warnings.Count} }}";
}

/// <summary>Outcome of a password check. A wrong password is reported here, never thrown.</summary>
public sealed class PdfPasswordCheckResult
{
    internal PdfPasswordCheckResult(bool isEncrypted, bool isValid, PdfPasswordMatch matchedAs, PdfEncryptionAlgorithm algorithm, int revision, bool opensWithoutPassword)
    {
        IsEncrypted = isEncrypted;
        IsValid = isValid;
        MatchedAs = matchedAs;
        Algorithm = algorithm;
        Revision = revision;
        OpensWithoutPassword = opensWithoutPassword;
    }

    /// <summary>Whether the document is encrypted.</summary>
    public bool IsEncrypted { get; }

    /// <summary>
    /// Whether the supplied password authenticates. For an unencrypted document this is <c>true</c> for any password
    /// (no password is needed) and <see cref="MatchedAs"/> is <see cref="PdfPasswordMatch.None"/>.
    /// </summary>
    public bool IsValid { get; }

    /// <summary>Which password matched. The owner password is checked first, so a password that is both reports <see cref="PdfPasswordMatch.Owner"/>.</summary>
    public PdfPasswordMatch MatchedAs { get; }

    /// <summary>Encryption algorithm of the document.</summary>
    public PdfEncryptionAlgorithm Algorithm { get; }

    /// <summary>Standard Security Handler revision (<c>/R</c>), or 0 if not encrypted.</summary>
    public int Revision { get; }

    /// <summary>Whether the empty user password opens the document (it is encrypted only to enforce permissions).</summary>
    public bool OpensWithoutPassword { get; }

    /// <summary>Whether the document uses certificate (public-key) encryption, which no password opens; see the PdfUtil.Signatures package.</summary>
    public bool RequiresCertificate { get; internal init; }

    /// <inheritdoc />
    public override string ToString() => $"PdfPasswordCheckResult {{ IsEncrypted = {IsEncrypted}, IsValid = {IsValid}, MatchedAs = {MatchedAs}, Algorithm = {Algorithm}, R = {Revision} }}";
}

/// <summary>A rectangle in default user space units (1/72 inch).</summary>
/// <param name="LowerLeftX">Lower-left x.</param>
/// <param name="LowerLeftY">Lower-left y.</param>
/// <param name="UpperRightX">Upper-right x.</param>
/// <param name="UpperRightY">Upper-right y.</param>
public readonly record struct PdfRectangle(double LowerLeftX, double LowerLeftY, double UpperRightX, double UpperRightY)
{
    /// <summary>Absolute width.</summary>
    public double Width => Math.Abs(UpperRightX - LowerLeftX);

    /// <summary>Absolute height.</summary>
    public double Height => Math.Abs(UpperRightY - LowerLeftY);
}

/// <summary>Geometry of one page.</summary>
public sealed class PdfPageInfo
{
    internal PdfPageInfo(int number, PdfRectangle mediaBox, PdfRectangle? cropBox, int rotate)
    {
        Number = number;
        MediaBox = mediaBox;
        CropBox = cropBox;
        Rotate = rotate;
    }

    /// <summary>1-based page number.</summary>
    public int Number { get; }

    /// <summary>Effective (possibly inherited) media box.</summary>
    public PdfRectangle MediaBox { get; }

    /// <summary>Effective (possibly inherited) crop box, if present.</summary>
    public PdfRectangle? CropBox { get; }

    /// <summary>Effective rotation, normalised to 0, 90, 180 or 270.</summary>
    public int Rotate { get; }

    /// <summary>Displayed width in points (crop box if present, else media box; swapped for 90/270 rotation).</summary>
    public double DisplayWidth => Rotate % 180 == 0 ? (CropBox ?? MediaBox).Width : (CropBox ?? MediaBox).Height;

    /// <summary>Displayed height in points.</summary>
    public double DisplayHeight => Rotate % 180 == 0 ? (CropBox ?? MediaBox).Height : (CropBox ?? MediaBox).Width;
}

/// <summary>Encryption details of a document.</summary>
public sealed class PdfEncryptionInfo
{
    internal PdfEncryptionInfo(string filter, string? subFilter, int v, int r, int keyLengthBits, int rawPermissions, PdfEncryptionAlgorithm algorithm, bool encryptMetadata, string? streamFilter, string? stringFilter, string? embeddedFileFilter, PdfPasswordMatch authenticatedAs)
    {
        Filter = filter;
        SubFilter = subFilter;
        V = v;
        R = r;
        KeyLengthBits = keyLengthBits;
        RawPermissions = rawPermissions;
        Algorithm = algorithm;
        EncryptMetadata = encryptMetadata;
        StreamFilter = streamFilter;
        StringFilter = stringFilter;
        EmbeddedFileFilter = embeddedFileFilter;
        AuthenticatedAs = authenticatedAs;
    }

    /// <summary>Security handler name (<c>/Filter</c>), normally <c>Standard</c>.</summary>
    public string Filter { get; }

    /// <summary><c>/SubFilter</c>, if any.</summary>
    public string? SubFilter { get; }

    /// <summary>Algorithm version <c>/V</c>.</summary>
    public int V { get; }

    /// <summary>Revision <c>/R</c>.</summary>
    public int R { get; }

    /// <summary>Key length in bits.</summary>
    public int KeyLengthBits { get; }

    /// <summary>The signed 32-bit <c>/P</c> value.</summary>
    public int RawPermissions { get; }

    /// <summary>The permission bits of <see cref="RawPermissions"/>.</summary>
    public PdfPermissions Permissions => (PdfPermissions)RawPermissions & PdfPermissions.All;

    /// <summary>Algorithm.</summary>
    public PdfEncryptionAlgorithm Algorithm { get; }

    /// <summary>Whether metadata streams are encrypted.</summary>
    public bool EncryptMetadata { get; }

    /// <summary><c>/StmF</c> crypt filter name (V4+).</summary>
    public string? StreamFilter { get; }

    /// <summary><c>/StrF</c> crypt filter name (V4+).</summary>
    public string? StringFilter { get; }

    /// <summary><c>/EFF</c> crypt filter name (V4+).</summary>
    public string? EmbeddedFileFilter { get; }

    /// <summary>Which password the inspection authenticated with (<see cref="PdfPasswordMatch.None"/> if none worked).</summary>
    public PdfPasswordMatch AuthenticatedAs { get; }

    /// <summary>For certificate (public-key) encryption: the number of PKCS#7 recipient entries; 0 for password encryption.</summary>
    public int RecipientCount { get; internal init; }
}

/// <summary>Values of the document information dictionary (<c>/Info</c>).</summary>
public sealed class PdfInfoDictionary
{
    internal PdfInfoDictionary(IReadOnlyDictionary<string, string> entries, DateTimeOffset? creationDate, DateTimeOffset? modDate)
    {
        Entries = entries;
        CreationDate = creationDate;
        ModDate = modDate;
    }

    /// <summary>All entries, decoded as text (names are given without the slash). Includes custom keys.</summary>
    public IReadOnlyDictionary<string, string> Entries { get; }

    /// <summary><c>/Title</c>.</summary>
    public string? Title => Get("Title");

    /// <summary><c>/Author</c>.</summary>
    public string? Author => Get("Author");

    /// <summary><c>/Subject</c>.</summary>
    public string? Subject => Get("Subject");

    /// <summary><c>/Keywords</c>.</summary>
    public string? Keywords => Get("Keywords");

    /// <summary><c>/Creator</c>.</summary>
    public string? Creator => Get("Creator");

    /// <summary><c>/Producer</c>.</summary>
    public string? Producer => Get("Producer");

    /// <summary><c>/Trapped</c>.</summary>
    public string? Trapped => Get("Trapped");

    /// <summary><c>/CreationDate</c> parsed from the PDF date format.</summary>
    public DateTimeOffset? CreationDate { get; }

    /// <summary><c>/ModDate</c> parsed from the PDF date format.</summary>
    public DateTimeOffset? ModDate { get; }

    /// <summary>Entries other than the standard keys.</summary>
    public IReadOnlyDictionary<string, string> CustomEntries =>
        Entries.Where(e => !StandardKeys.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);

    private static readonly HashSet<string> StandardKeys = new(StringComparer.Ordinal)
    {
        "Title", "Author", "Subject", "Keywords", "Creator", "Producer", "CreationDate", "ModDate", "Trapped",
    };

    private string? Get(string key) => Entries.TryGetValue(key, out var v) ? v : null;
}

/// <summary>Parsed XMP metadata.</summary>
public sealed class PdfXmpMetadata
{
    internal PdfXmpMetadata(string rawPacket)
    {
        RawPacket = rawPacket;
    }

    /// <summary>The raw XMP packet as text.</summary>
    public string RawPacket { get; }

    /// <summary>Whether the packet was well-formed XML.</summary>
    public bool IsWellFormed { get; internal set; }

    /// <summary><c>dc:title</c> (default language).</summary>
    public string? Title { get; internal set; }

    /// <summary><c>dc:creator</c> entries.</summary>
    public IReadOnlyList<string> Creators { get; internal set; } = Array.Empty<string>();

    /// <summary><c>dc:description</c> (default language).</summary>
    public string? Description { get; internal set; }

    /// <summary><c>dc:subject</c> entries.</summary>
    public IReadOnlyList<string> Subjects { get; internal set; } = Array.Empty<string>();

    /// <summary><c>xmp:CreatorTool</c>.</summary>
    public string? CreatorTool { get; internal set; }

    /// <summary><c>xmp:CreateDate</c>.</summary>
    public DateTimeOffset? CreateDate { get; internal set; }

    /// <summary><c>xmp:ModifyDate</c>.</summary>
    public DateTimeOffset? ModifyDate { get; internal set; }

    /// <summary><c>xmp:MetadataDate</c>.</summary>
    public DateTimeOffset? MetadataDate { get; internal set; }

    /// <summary><c>pdf:Producer</c>.</summary>
    public string? Producer { get; internal set; }

    /// <summary><c>pdf:Keywords</c>.</summary>
    public string? Keywords { get; internal set; }

    /// <summary><c>pdfaid:part</c> (e.g. 1, 2, 3, 4).</summary>
    public int? PdfAPart { get; internal set; }

    /// <summary><c>pdfaid:conformance</c> (e.g. A, B, U, E, F).</summary>
    public string? PdfAConformance { get; internal set; }

    /// <summary><c>pdfuaid:part</c>.</summary>
    public int? PdfUAPart { get; internal set; }

    /// <summary><c>pdfxid:GTS_PDFXVersion</c> or <c>pdfx:GTS_PDFXVersion</c>.</summary>
    public string? PdfXVersion { get; internal set; }

    /// <summary>Whether the packet declares PDF/A conformance.</summary>
    public bool IsPdfA => PdfAPart.HasValue;

    /// <summary>PDF/A level such as <c>"PDF/A-2B"</c>, if declared.</summary>
    public string? PdfALevel => PdfAPart.HasValue ? $"PDF/A-{PdfAPart}{PdfAConformance}" : null;
}

/// <summary>A developer extension declared in the catalog <c>/Extensions</c> dictionary.</summary>
/// <param name="Prefix">Developer prefix, e.g. <c>ADBE</c>.</param>
/// <param name="BaseVersion">Base version, e.g. <c>1.7</c>.</param>
/// <param name="ExtensionLevel">Extension level.</param>
public sealed record PdfExtension(string Prefix, string? BaseVersion, int? ExtensionLevel);

/// <summary>Document details returned by <see cref="IPdfInspector"/>.</summary>
public sealed class PdfDocumentInfo
{
    /// <summary>Version in the <c>%PDF-x.y</c> header.</summary>
    public string HeaderVersion { get; internal set; } = "";

    /// <summary>Catalog <c>/Version</c>, if present.</summary>
    public string? CatalogVersion { get; internal set; }

    /// <summary>Effective version: the later of the header and catalog versions.</summary>
    public string EffectiveVersion { get; internal set; } = "";

    /// <summary>Developer extensions declared in the catalog.</summary>
    public IReadOnlyList<PdfExtension> Extensions { get; internal set; } = Array.Empty<PdfExtension>();

    /// <summary>
    /// Whether the document is encrypted but could not be opened (no password, or the empty user password did not
    /// work). Page and metadata fields are then <c>null</c>.
    /// </summary>
    public bool RequiresPassword { get; internal set; }

    /// <summary>
    /// Whether the document uses certificate (public-key) encryption and could not be opened. Page and metadata fields
    /// are then <c>null</c>; open it with the PdfUtil.Signatures package and a recipient's certificate.
    /// </summary>
    public bool RequiresCertificate { get; internal set; }

    /// <summary>Number of pages, or <c>null</c> when <see cref="RequiresPassword"/> is set.</summary>
    public int? PageCount { get; internal set; }

    /// <summary>Page geometry.</summary>
    public IReadOnlyList<PdfPageInfo> Pages { get; internal set; } = Array.Empty<PdfPageInfo>();

    /// <summary>Encryption details, or <c>null</c> if not encrypted.</summary>
    public PdfEncryptionInfo? Encryption { get; internal set; }

    /// <summary>Whether the document is encrypted.</summary>
    public bool IsEncrypted => Encryption is not null;

    /// <summary>The document information dictionary, if present and readable.</summary>
    public PdfInfoDictionary? Info { get; internal set; }

    /// <summary>The catalog XMP metadata, if present and readable.</summary>
    public PdfXmpMetadata? Xmp { get; internal set; }

    /// <summary>Whether the XMP declares PDF/A conformance.</summary>
    public bool IsPdfA => Xmp?.IsPdfA ?? false;

    /// <summary>First trailer <c>/ID</c> element (permanent identifier) as raw bytes, never text-decoded.</summary>
    public byte[]? PermanentId { get; internal set; }

    /// <summary>Second trailer <c>/ID</c> element (changing identifier) as raw bytes.</summary>
    public byte[]? ChangingId { get; internal set; }

    /// <summary><see cref="PermanentId"/> as upper-case hexadecimal.</summary>
    public string? PermanentIdHex => PermanentId is null ? null : Convert.ToHexString(PermanentId);

    /// <summary><see cref="ChangingId"/> as upper-case hexadecimal.</summary>
    public string? ChangingIdHex => ChangingId is null ? null : Convert.ToHexString(ChangingId);

    /// <summary>Input size in bytes.</summary>
    public long FileSize { get; internal set; }

    /// <summary>Whether the first object is a linearization parameter dictionary.</summary>
    public bool IsLinearized { get; internal set; }

    /// <summary>Whether the catalog <c>/MarkInfo /Marked</c> is true.</summary>
    public bool IsTagged { get; internal set; }

    /// <summary>Number of incremental updates (cross-reference sections beyond the original, excluding linearization).</summary>
    public int IncrementalUpdateCount { get; internal set; }

    /// <summary>Whether the file uses cross-reference streams.</summary>
    public bool HasXrefStreams { get; internal set; }

    /// <summary>Whether the file uses object streams.</summary>
    public bool HasObjectStreams { get; internal set; }

    /// <summary>Whether the file appears to be digitally signed.</summary>
    public bool IsSigned { get; internal set; }

    /// <summary>Whether the cross-reference information had to be reconstructed.</summary>
    public bool WasRepaired { get; internal set; }

    /// <summary>Non-fatal issues found while reading.</summary>
    public IReadOnlyList<string> Warnings { get; internal set; } = Array.Empty<string>();

    /// <summary>The catalog <c>/PageLayout</c>, for example <c>SinglePage</c> or <c>TwoColumnLeft</c>.</summary>
    public string? PageLayout { get; internal set; }

    /// <summary>The catalog <c>/PageMode</c>, for example <c>UseOutlines</c> or <c>FullScreen</c>.</summary>
    public string? PageMode { get; internal set; }

    /// <summary>The document language (catalog <c>/Lang</c>), for example <c>en-US</c>.</summary>
    public string? Language { get; internal set; }

    /// <summary>Which optional details were collected (<see cref="PdfInspectionDetails.None"/> for <see cref="IPdfInspector.GetInfoAsync"/>).</summary>
    public PdfInspectionDetails InspectedDetails { get; internal set; }

    /// <summary>Fonts (with <see cref="PdfInspectionDetails.Fonts"/>).</summary>
    public IReadOnlyList<PdfFontInfo> Fonts { get; internal set; } = Array.Empty<PdfFontInfo>();

    /// <summary>Images (with <see cref="PdfInspectionDetails.Images"/>).</summary>
    public IReadOnlyList<PdfImageInfo> Images { get; internal set; } = Array.Empty<PdfImageInfo>();

    /// <summary>Embedded files (with <see cref="PdfInspectionDetails.Attachments"/>).</summary>
    public IReadOnlyList<PdfAttachmentInfo> Attachments { get; internal set; } = Array.Empty<PdfAttachmentInfo>();

    /// <summary>Top-level bookmarks; each has its children (with <see cref="PdfInspectionDetails.Bookmarks"/>).</summary>
    public IReadOnlyList<PdfBookmark> Bookmarks { get; internal set; } = Array.Empty<PdfBookmark>();

    /// <summary>Form fields (with <see cref="PdfInspectionDetails.FormFields"/>).</summary>
    public IReadOnlyList<PdfFormField> FormFields { get; internal set; } = Array.Empty<PdfFormField>();

    /// <summary>Signatures (with <see cref="PdfInspectionDetails.Signatures"/>).</summary>
    public IReadOnlyList<PdfSignatureInfo> Signatures { get; internal set; } = Array.Empty<PdfSignatureInfo>();

    /// <summary>The label of each page, in page order (with <see cref="PdfInspectionDetails.PageLabels"/>); empty when the document defines no page labels.</summary>
    public IReadOnlyList<string> PageLabels { get; internal set; } = Array.Empty<string>();

    /// <summary>Whether the document contains JavaScript anywhere (with <see cref="PdfInspectionDetails.Actions"/>).</summary>
    public bool HasJavaScript { get; internal set; }

    /// <summary>Names of the document-level scripts (the <c>/Names /JavaScript</c> tree), never their source.</summary>
    public IReadOnlyList<string> JavaScriptNames { get; internal set; } = Array.Empty<string>();

    /// <summary>Every action found, with its location and type (with <see cref="PdfInspectionDetails.Actions"/>).</summary>
    public IReadOnlyList<PdfActionInfo> Actions { get; internal set; } = Array.Empty<PdfActionInfo>();
}
