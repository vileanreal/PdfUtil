namespace PdfUtil;

/// <summary>How a written PDF is laid out. The defaults give the most compatible output.</summary>
public sealed class PdfWriteOptions
{
    /// <summary>
    /// Pack objects into compressed object streams and write a cross-reference stream instead of a table, which makes
    /// files smaller (often 10-30%). Requires PDF 1.5, so the output version is raised to at least 1.5; readers older
    /// than about 2003 cannot open such files. Default <c>false</c>.
    /// </summary>
    public bool UseObjectStreams { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"PdfWriteOptions {{ UseObjectStreams = {UseObjectStreams} }}";
}

/// <summary>How an edit is saved.</summary>
public enum PdfSaveMode
{
    /// <summary>The whole document is rewritten as a clean, single revision. Default.</summary>
    Rewrite = 0,

    /// <summary>
    /// The changes are appended as an incremental update, so every original byte is preserved (existing signatures
    /// stay valid for the bytes they cover). Only for unencrypted, undamaged documents. Earlier revisions, including
    /// anything removed by the edit, remain in the file.
    /// </summary>
    Incremental = 1,
}
