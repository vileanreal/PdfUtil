namespace PdfUtil;

/// <summary>
/// Changes to the document information (Title, Author, ...) applied while protecting a document, through
/// <see cref="PdfProtectionOptions.InfoChanges"/>. Properties left <c>null</c> keep their current value.
/// The Info dictionary is updated, and when the document has XMP metadata the matching XMP properties are updated
/// too (see <see cref="UpdateXmp"/>), so that viewers show the new values consistently.
/// </summary>
public sealed class PdfInfoChanges
{
    /// <summary>Keys of the standard Info entries, which have typed properties.</summary>
    internal static readonly HashSet<string> StandardKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "Title", "Author", "Subject", "Keywords", "Creator", "Producer", "CreationDate", "ModDate", "Trapped",
    };

    /// <summary>New <c>/Title</c> (XMP <c>dc:title</c>). <c>null</c> keeps the current value.</summary>
    public string? Title { get; set; }

    /// <summary>New <c>/Author</c> (XMP <c>dc:creator</c>). <c>null</c> keeps the current value.</summary>
    public string? Author { get; set; }

    /// <summary>New <c>/Subject</c> (XMP <c>dc:description</c>). <c>null</c> keeps the current value.</summary>
    public string? Subject { get; set; }

    /// <summary>New <c>/Keywords</c> (XMP <c>pdf:Keywords</c>). <c>null</c> keeps the current value.</summary>
    public string? Keywords { get; set; }

    /// <summary>New <c>/Creator</c>, the application that created the original content (XMP <c>xmp:CreatorTool</c>).</summary>
    public string? Creator { get; set; }

    /// <summary>New <c>/Producer</c>, the application that produced the PDF (XMP <c>pdf:Producer</c>).</summary>
    public string? Producer { get; set; }

    /// <summary>New <c>/CreationDate</c> (XMP <c>xmp:CreateDate</c>). <c>null</c> keeps the current value.</summary>
    public DateTimeOffset? CreationDate { get; set; }

    /// <summary>New <c>/ModDate</c> (XMP <c>xmp:ModifyDate</c>). Takes precedence over <see cref="UpdateModificationDate"/>.</summary>
    public DateTimeOffset? ModificationDate { get; set; }

    /// <summary>Set <c>/ModDate</c> to the current time. Default <c>false</c>.</summary>
    public bool UpdateModificationDate { get; set; }

    /// <summary>
    /// Custom Info entries to add or replace, by key (without the slash). Keys must be printable ASCII without
    /// spaces, and must not be one of the standard keys (use the typed properties for those).
    /// </summary>
    public IDictionary<string, string> CustomProperties { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Keys of entries to remove, standard (for example <c>"Keywords"</c>) or custom. A key that is also given a new
    /// value is set, not removed.
    /// </summary>
    public ISet<string> Remove { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Also update the document's XMP metadata, if it has any. Default <c>true</c>. Custom properties exist only in
    /// the Info dictionary.
    /// </summary>
    public bool UpdateXmp { get; set; } = true;

    /// <summary>Whether any change is requested.</summary>
    public bool HasChanges =>
        Title is not null || Author is not null || Subject is not null || Keywords is not null || Creator is not null
        || Producer is not null || CreationDate is not null || ModificationDate is not null || UpdateModificationDate
        || CustomProperties.Count > 0 || Remove.Count > 0;

    internal void Validate()
    {
        foreach (var key in CustomProperties.Keys.Concat(Remove))
        {
            if (string.IsNullOrEmpty(key) || key.Length > 127 || key.Any(c => c is < '!' or > '~' || c == '/'))
            {
                throw new ArgumentException("Document information keys must be 1-127 printable ASCII characters without spaces or '/'.", nameof(CustomProperties));
            }
        }

        foreach (var key in CustomProperties.Keys)
        {
            if (key == "Trapped")
            {
                throw new ArgumentException("'Trapped' holds a name, not text, and cannot be set as a custom property; it can only be removed.", nameof(CustomProperties));
            }

            if (StandardKeys.Contains(key))
            {
                throw new ArgumentException($"'{key}' is a standard document information entry; set it with the {(key == "ModDate" ? "ModificationDate" : key)} property instead of CustomProperties.", nameof(CustomProperties));
            }

            if (CustomProperties[key] is null)
            {
                throw new ArgumentException($"The value of custom property '{key}' is null; add the key to Remove to delete it.", nameof(CustomProperties));
            }
        }
    }
}
