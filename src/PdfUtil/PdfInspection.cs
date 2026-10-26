namespace PdfUtil;

/// <summary>The optional details that <see cref="IPdfAnalyzer.InspectAsync"/> collects, on top of the basic inspection.</summary>
[Flags]
public enum PdfInspectionDetails
{
    /// <summary>Only the basic details of <see cref="IPdfInspector.GetInfoAsync"/>.</summary>
    None = 0,

    /// <summary>Fonts used by pages, form XObjects and Type 3 fonts.</summary>
    Fonts = 1 << 0,

    /// <summary>Image XObjects used by pages and form XObjects.</summary>
    Images = 1 << 1,

    /// <summary>Embedded files: document-level attachments and file attachment annotations.</summary>
    Attachments = 1 << 2,

    /// <summary>The outline (bookmark) tree.</summary>
    Bookmarks = 1 << 3,

    /// <summary>Interactive form fields.</summary>
    FormFields = 1 << 4,

    /// <summary>Digital signatures (without cryptographic validation).</summary>
    Signatures = 1 << 5,

    /// <summary>Page labels.</summary>
    PageLabels = 1 << 6,

    /// <summary>JavaScript and other actions.</summary>
    Actions = 1 << 7,

    /// <summary>Everything.</summary>
    All = Fonts | Images | Attachments | Bookmarks | FormFields | Signatures | PageLabels | Actions,
}

/// <summary>Settings for <see cref="IPdfAnalyzer.InspectAsync"/>.</summary>
public sealed class PdfInspectionOptions
{
    /// <summary>
    /// For encrypted documents: the password (user or owner). <c>null</c> tries the empty user password; if that does
    /// not open the document, a partial result with <see cref="PdfDocumentInfo.RequiresPassword"/> is returned.
    /// A wrong non-null password throws <see cref="PdfInvalidPasswordException"/>.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>Which details to collect. Default <see cref="PdfInspectionDetails.All"/>.</summary>
    public PdfInspectionDetails Details { get; set; } = PdfInspectionDetails.All;

    /// <inheritdoc />
    public override string ToString() => $"PdfInspectionOptions {{ Password = {(string.IsNullOrEmpty(Password) ? "<empty>" : "<set>")}, Details = {Details} }}";
}

/// <summary>A font used in the document.</summary>
public sealed class PdfFontInfo
{
    internal PdfFontInfo()
    {
    }

    /// <summary>The font name (<c>/BaseFont</c>) without a subset prefix, or the resource name for Type 3 fonts without a name.</summary>
    public string Name { get; internal init; } = "";

    /// <summary>The name exactly as stored, including any subset prefix such as <c>ABCDEF+</c>.</summary>
    public string? BaseFont { get; internal init; }

    /// <summary>Font type: <c>Type1</c>, <c>MMType1</c>, <c>TrueType</c>, <c>Type3</c>, or <c>Type0</c>.</summary>
    public string Subtype { get; internal init; } = "";

    /// <summary>For <c>Type0</c> fonts: the descendant font type (<c>CIDFontType0</c> or <c>CIDFontType2</c>).</summary>
    public string? DescendantSubtype { get; internal init; }

    /// <summary>Whether the font program is embedded (always <c>true</c> for Type 3 fonts, whose glyphs are part of the PDF).</summary>
    public bool IsEmbedded { get; internal init; }

    /// <summary>Whether only a subset of the font is embedded (the name has a six-letter <c>ABCDEF+</c> prefix).</summary>
    public bool IsSubset { get; internal init; }

    /// <summary>The embedded font file type: <c>FontFile</c> (Type 1), <c>FontFile2</c> (TrueType) or <c>FontFile3</c> (CFF/OpenType).</summary>
    public string? FontFileType { get; internal init; }

    /// <summary>The encoding: a name such as <c>WinAnsiEncoding</c> or <c>Identity-H</c>, <c>Custom</c> for a differences encoding (with its base, if any), or <c>null</c> for the font's built-in encoding.</summary>
    public string? Encoding { get; internal init; }

    /// <summary>Whether the font has a <c>/ToUnicode</c> map (needed for reliable text extraction).</summary>
    public bool HasToUnicode { get; internal init; }

    /// <summary>The pages that use the font, directly or through form XObjects.</summary>
    public IReadOnlyList<int> Pages { get; internal init; } = Array.Empty<int>();

    /// <summary>Object number of the font dictionary, or <c>null</c> for a direct dictionary.</summary>
    public int? ObjectNumber { get; internal init; }

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({Subtype}{(IsEmbedded ? IsSubset ? ", embedded subset" : ", embedded" : "")})";
}

/// <summary>An image XObject used in the document.</summary>
public sealed class PdfImageInfo
{
    internal PdfImageInfo()
    {
    }

    /// <summary>The pages that show the image, directly or through form XObjects.</summary>
    public IReadOnlyList<int> Pages { get; internal init; } = Array.Empty<int>();

    /// <summary>Width in pixels.</summary>
    public int Width { get; internal init; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; internal init; }

    /// <summary>Bits per colour component, or <c>null</c> when not given (JPEG 2000).</summary>
    public int? BitsPerComponent { get; internal init; }

    /// <summary>The colour space, for example <c>DeviceRGB</c>, <c>ICCBased (3 components)</c> or <c>Indexed (DeviceRGB, 256 colours)</c>; <c>ImageMask</c> for stencil masks.</summary>
    public string? ColorSpace { get; internal init; }

    /// <summary>The filters, in order (the last is normally the image codec, such as <c>DCTDecode</c> for JPEG).</summary>
    public IReadOnlyList<string> Filters { get; internal init; } = Array.Empty<string>();

    /// <summary>Whether the image has a soft mask (<c>/SMask</c>) for transparency.</summary>
    public bool HasSoftMask { get; internal init; }

    /// <summary>Size of the stored (compressed) image data in bytes.</summary>
    public long StoredLength { get; internal init; }

    /// <summary>Object number of the image stream.</summary>
    public int ObjectNumber { get; internal init; }

    /// <inheritdoc />
    public override string ToString() => $"{Width}x{Height} {ColorSpace} {string.Join("/", Filters)}";
}

/// <summary>An embedded file.</summary>
public sealed class PdfAttachmentInfo
{
    internal PdfAttachmentInfo()
    {
    }

    /// <summary>
    /// The name used to find the attachment: the key in the <c>/EmbeddedFiles</c> name tree, or for file attachment
    /// annotations the file name.
    /// </summary>
    public string Name { get; internal init; } = "";

    /// <summary>The file name (<c>/UF</c>, else <c>/F</c>).</summary>
    public string? FileName { get; internal init; }

    /// <summary>The description (<c>/Desc</c>).</summary>
    public string? Description { get; internal init; }

    /// <summary>The uncompressed size in bytes from <c>/Params /Size</c>, if given.</summary>
    public long? Size { get; internal init; }

    /// <summary>The MIME type (the embedded file's <c>/Subtype</c>), for example <c>application/xml</c>.</summary>
    public string? MimeType { get; internal init; }

    /// <summary>Creation date from <c>/Params</c>.</summary>
    public DateTimeOffset? CreationDate { get; internal init; }

    /// <summary>Modification date from <c>/Params</c>.</summary>
    public DateTimeOffset? ModificationDate { get; internal init; }

    /// <summary>The MD5 checksum from <c>/Params /CheckSum</c> as upper-case hexadecimal.</summary>
    public string? CheckSum { get; internal init; }

    /// <summary>The PDF 2.0 / PDF/A-3 relationship (<c>/AFRelationship</c>), for example <c>Source</c> or <c>Data</c>.</summary>
    public string? Relationship { get; internal init; }

    /// <summary>For file attachment annotations: the page number; <c>null</c> for document-level attachments.</summary>
    public int? Page { get; internal init; }

    /// <summary>Whether the file data is present (a file specification may only refer to an external file).</summary>
    public bool IsEmbedded { get; internal init; }

    /// <summary>Object number of the file specification, or <c>null</c> for a direct dictionary.</summary>
    public int? ObjectNumber { get; internal init; }

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({MimeType ?? "unknown type"}, {(Size is long s ? s + " bytes" : "size unknown")})";
}

/// <summary>An outline item (bookmark).</summary>
public sealed class PdfBookmark
{
    internal PdfBookmark()
    {
    }

    /// <summary>The title, decoded.</summary>
    public string Title { get; internal init; } = "";

    /// <summary>The destination page number, when the bookmark goes to a page of this document.</summary>
    public int? Page { get; internal init; }

    /// <summary>The action type when the bookmark does something other than go to a page (<c>URI</c>, <c>GoToR</c>, <c>Launch</c>, <c>JavaScript</c>, ...).</summary>
    public string? Action { get; internal init; }

    /// <summary>The target of a <c>URI</c> action.</summary>
    public string? Uri { get; internal init; }

    /// <summary>Whether the item is shown expanded (a positive <c>/Count</c>).</summary>
    public bool IsOpen { get; internal init; }

    /// <summary>Child items.</summary>
    public IReadOnlyList<PdfBookmark> Children { get; internal set; } = Array.Empty<PdfBookmark>();

    /// <inheritdoc />
    public override string ToString() => Page is int p ? $"{Title} (page {p})" : Title;
}

/// <summary>The type of an interactive form field.</summary>
public enum PdfFormFieldType
{
    /// <summary>The type is missing or unknown.</summary>
    Unknown = 0,

    /// <summary>A text field.</summary>
    Text = 1,

    /// <summary>A check box.</summary>
    CheckBox = 2,

    /// <summary>A group of radio buttons.</summary>
    RadioButton = 3,

    /// <summary>A push button.</summary>
    PushButton = 4,

    /// <summary>A drop-down list.</summary>
    ComboBox = 5,

    /// <summary>A scrollable list.</summary>
    ListBox = 6,

    /// <summary>A signature field.</summary>
    Signature = 7,
}

/// <summary>An interactive form field (a terminal field of the AcroForm field tree).</summary>
public sealed class PdfFormField
{
    internal PdfFormField()
    {
    }

    /// <summary>The fully qualified name: the partial names (<c>/T</c>) of the field and its ancestors joined with periods.</summary>
    public string FullName { get; internal init; } = "";

    /// <summary>The field type.</summary>
    public PdfFormFieldType Type { get; internal init; }

    /// <summary>The value as text (option names for buttons, several values joined with <c>"; "</c>), or <c>null</c>.</summary>
    public string? Value { get; internal init; }

    /// <summary>The raw field flags (<c>/Ff</c>), possibly inherited.</summary>
    public int Flags { get; internal init; }

    /// <summary>Flag bit 1: the field cannot be changed.</summary>
    public bool IsReadOnly => (Flags & 1) != 0;

    /// <summary>Flag bit 2: the field must have a value when the form is submitted.</summary>
    public bool IsRequired => (Flags & 2) != 0;

    /// <summary>Flag bit 3: the field is not submitted.</summary>
    public bool IsNoExport => (Flags & 4) != 0;

    /// <summary>The tooltip (<c>/TU</c>).</summary>
    public string? AlternateName { get; internal init; }

    /// <summary>The pages that show the field's widgets.</summary>
    public IReadOnlyList<int> Pages { get; internal init; } = Array.Empty<int>();

    /// <summary>Object number of the field dictionary, or <c>null</c> for a direct dictionary.</summary>
    public int? ObjectNumber { get; internal init; }

    /// <inheritdoc />
    public override string ToString() => $"{FullName} ({Type}) = {Value ?? "<empty>"}";
}

/// <summary>A digital signature, as stored. The signature is not validated cryptographically.</summary>
public sealed class PdfSignatureInfo
{
    internal PdfSignatureInfo()
    {
    }

    /// <summary>The fully qualified name of the signature field.</summary>
    public string FieldName { get; internal init; } = "";

    /// <summary>The signer name stored in the signature dictionary (<c>/Name</c>); the certificate is not read.</summary>
    public string? SignerName { get; internal init; }

    /// <summary>The signing time stored in <c>/M</c> (not a trusted timestamp).</summary>
    public DateTimeOffset? SigningTime { get; internal init; }

    /// <summary>The reason (<c>/Reason</c>).</summary>
    public string? Reason { get; internal init; }

    /// <summary>The location (<c>/Location</c>).</summary>
    public string? Location { get; internal init; }

    /// <summary>The contact information (<c>/ContactInfo</c>).</summary>
    public string? ContactInfo { get; internal init; }

    /// <summary>The signature handler (<c>/Filter</c>), for example <c>Adobe.PPKLite</c>.</summary>
    public string? Filter { get; internal init; }

    /// <summary>The signature format (<c>/SubFilter</c>), for example <c>adbe.pkcs7.detached</c> or <c>ETSI.CAdES.detached</c>.</summary>
    public string? SubFilter { get; internal init; }

    /// <summary>Whether this is a document timestamp (<c>/Type /DocTimeStamp</c>) rather than a signature.</summary>
    public bool IsDocumentTimestamp { get; internal init; }

    /// <summary>The signed byte ranges as (offset, length) pairs, flattened.</summary>
    public IReadOnlyList<long> ByteRange { get; internal init; } = Array.Empty<long>();

    /// <summary>
    /// Whether the byte range covers the whole file except the signature value: it starts at 0, ends at the end of
    /// the file, and the single gap is exactly the hexadecimal <c>/Contents</c> string. <c>false</c> usually means the
    /// file was changed after signing (for example by an incremental update), or the signature is malformed.
    /// </summary>
    public bool CoversWholeFile { get; internal init; }

    /// <summary>Size of the signature value (<c>/Contents</c>) in bytes.</summary>
    public int ContentsLength { get; internal init; }

    /// <summary>The page that shows the signature widget, if any.</summary>
    public int? Page { get; internal init; }

    /// <inheritdoc />
    public override string ToString() => $"{FieldName} ({SubFilter}, {(CoversWholeFile ? "covers the whole file" : "does not cover the whole file")})";
}

/// <summary>An action found in the document. Only the type and where it is are reported, never script source.</summary>
/// <param name="Location">Where the action is, for example <c>OpenAction</c>, <c>Page 2 /AA /O</c>, <c>Annotation on page 1</c>, <c>Field "name" /AA /K</c> or <c>Named JavaScript "init"</c>.</param>
/// <param name="Type">The action type (<c>/S</c>), for example <c>JavaScript</c>, <c>URI</c>, <c>Launch</c>, <c>SubmitForm</c>.</param>
public sealed record PdfActionInfo(string Location, string Type);
