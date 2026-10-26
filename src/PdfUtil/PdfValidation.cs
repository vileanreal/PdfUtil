namespace PdfUtil;

/// <summary>Severity of a <see cref="PdfValidationIssue"/>.</summary>
public enum PdfValidationSeverity
{
    /// <summary>Not a problem: a fact worth knowing (incremental updates, linearization, content that was not checked).</summary>
    Info = 0,

    /// <summary>A deviation from the specification that readers normally tolerate.</summary>
    Warning = 1,

    /// <summary>Damage or an inconsistency that some readers may fail on, or that PdfUtil had to repair to read the file.</summary>
    Error = 2,
}

/// <summary>The codes used in <see cref="PdfValidationIssue.Code"/>. Codes are stable; messages may change.</summary>
public static class PdfValidationCodes
{
    /// <summary>The file cannot be read at all.</summary>
    public const string Unreadable = "Unreadable";

    /// <summary>Data precedes the <c>%PDF-</c> header.</summary>
    public const string HeaderJunk = "HeaderJunk";

    /// <summary>The cross-reference information was damaged and had to be rebuilt by scanning the file.</summary>
    public const string XrefReconstructed = "XrefReconstructed";

    /// <summary>A cross-reference offset does not point at its object.</summary>
    public const string XrefOffsetWrong = "XrefOffsetWrong";

    /// <summary>The <c>/Prev</c> chain of cross-reference sections loops.</summary>
    public const string XrefPrevLoop = "XrefPrevLoop";

    /// <summary>The <c>/XRefStm</c> stream of a hybrid-reference file is damaged.</summary>
    public const string XrefStreamDamaged = "XrefStreamDamaged";

    /// <summary>The trailer <c>/Size</c> does not cover every object number.</summary>
    public const string TrailerSizeWrong = "TrailerSizeWrong";

    /// <summary>An object stream cannot be read.</summary>
    public const string ObjectStreamDamaged = "ObjectStreamDamaged";

    /// <summary>The cross-reference data lists an object that is not in the file.</summary>
    public const string ObjectMissing = "ObjectMissing";

    /// <summary>An object cannot be parsed.</summary>
    public const string ObjectUnreadable = "ObjectUnreadable";

    /// <summary>A stream's <c>/Length</c> is wrong.</summary>
    public const string StreamLengthWrong = "StreamLengthWrong";

    /// <summary>A reference points to an object that does not exist.</summary>
    public const string DanglingReference = "DanglingReference";

    /// <summary>The trailer has no <c>/ID</c> (required for encrypted files and PDF 2.0).</summary>
    public const string MissingId = "MissingId";

    /// <summary>A date string does not follow the PDF date format.</summary>
    public const string InvalidDate = "InvalidDate";

    /// <summary>The catalog is missing or is not <c>/Type /Catalog</c>.</summary>
    public const string CatalogInvalid = "CatalogInvalid";

    /// <summary>The catalog has no page tree.</summary>
    public const string PageTreeMissing = "PageTreeMissing";

    /// <summary>The page tree contains a cycle.</summary>
    public const string PageTreeCycle = "PageTreeCycle";

    /// <summary>A page tree node's <c>/Count</c> does not match the pages below it.</summary>
    public const string PageTreeCountWrong = "PageTreeCountWrong";

    /// <summary>A page tree node is not a dictionary, has the wrong <c>/Type</c>, or a wrong <c>/Parent</c>.</summary>
    public const string PageTreeNodeInvalid = "PageTreeNodeInvalid";

    /// <summary>A page has no (inherited) <c>/MediaBox</c>.</summary>
    public const string PageMediaBoxMissing = "PageMediaBoxMissing";

    /// <summary>The <c>/Encrypt</c> dictionary has missing or inconsistent entries.</summary>
    public const string EncryptionInconsistent = "EncryptionInconsistent";

    /// <summary>The AES-256 <c>/Perms</c> entry does not match <c>/P</c>.</summary>
    public const string EncryptionPermsMismatch = "EncryptionPermsMismatch";

    /// <summary>The document is encrypted and no working password was given, so objects and streams were not checked.</summary>
    public const string EncryptedContentNotChecked = "EncryptedContentNotChecked";

    /// <summary>A FlateDecode stream cannot be decompressed at all.</summary>
    public const string FlateDecodeFailed = "FlateDecodeFailed";

    /// <summary>A FlateDecode stream is truncated or has trailing garbage; only part of it decompresses.</summary>
    public const string FlateDecodeTruncated = "FlateDecodeTruncated";

    /// <summary>A stream is too large to decompress within the configured limits, so it was not checked.</summary>
    public const string StreamNotChecked = "StreamNotChecked";

    /// <summary>The XMP metadata is not well-formed XML.</summary>
    public const string XmpMalformed = "XmpMalformed";

    /// <summary>The file has incremental updates.</summary>
    public const string IncrementalUpdates = "IncrementalUpdates";

    /// <summary>The issue limit was reached; further issues were not reported.</summary>
    public const string TooManyIssues = "TooManyIssues";
}

/// <summary>One problem found by validation.</summary>
/// <param name="Severity">How serious the problem is.</param>
/// <param name="Code">A stable code from <see cref="PdfValidationCodes"/>.</param>
/// <param name="Message">A readable description. Never contains passwords or document content beyond names and numbers.</param>
/// <param name="ObjectNumber">The object concerned, when there is one.</param>
public sealed record PdfValidationIssue(PdfValidationSeverity Severity, string Code, string Message, int? ObjectNumber = null)
{
    /// <inheritdoc />
    public override string ToString() => ObjectNumber is int n ? $"{Severity} {Code} (object {n}): {Message}" : $"{Severity} {Code}: {Message}";
}

/// <summary>Settings for <see cref="IPdfAnalyzer.ValidateAsync"/>.</summary>
public sealed class PdfValidationOptions
{
    /// <summary>For encrypted documents: a password (user or owner) to check objects and streams. Without one, only the structure is checked.</summary>
    public string? Password { get; set; }

    /// <summary>Decompress every FlateDecode stream to check it. Default <c>true</c>; turn off for a faster structural check.</summary>
    public bool CheckStreams { get; set; } = true;

    /// <summary>Maximum number of issues to report. Default 1000.</summary>
    public int MaxIssues { get; set; } = 1000;

    /// <summary>Optional progress reporting (stage <see cref="PdfProgressStage.Reading"/>).</summary>
    public IProgress<PdfProgress>? Progress { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"PdfValidationOptions {{ Password = {(string.IsNullOrEmpty(Password) ? "<empty>" : "<set>")}, CheckStreams = {CheckStreams} }}";
}

/// <summary>The result of <see cref="IPdfAnalyzer.ValidateAsync"/>.</summary>
public sealed class PdfValidationReport
{
    internal PdfValidationReport(bool isReadable, int? pageCount, int objectCount, IReadOnlyList<PdfValidationIssue> issues)
    {
        IsReadable = isReadable;
        PageCount = pageCount;
        ObjectCount = objectCount;
        Issues = issues;
    }

    /// <summary>Whether the file could be read (possibly after repairs). When <c>false</c>, <see cref="Issues"/> says why.</summary>
    public bool IsReadable { get; }

    /// <summary>Whether no issue has <see cref="PdfValidationSeverity.Error"/> severity.</summary>
    public bool IsValid => IsReadable && !Issues.Any(i => i.Severity == PdfValidationSeverity.Error);

    /// <summary>Number of pages found, or <c>null</c> when the page tree could not be read.</summary>
    public int? PageCount { get; }

    /// <summary>Number of objects checked.</summary>
    public int ObjectCount { get; }

    /// <summary>Every issue found, most severe first, then in the order found.</summary>
    public IReadOnlyList<PdfValidationIssue> Issues { get; }

    /// <summary>The issues with <see cref="PdfValidationSeverity.Error"/> severity.</summary>
    public IEnumerable<PdfValidationIssue> Errors => Issues.Where(i => i.Severity == PdfValidationSeverity.Error);

    /// <summary>The issues with <see cref="PdfValidationSeverity.Warning"/> severity.</summary>
    public IEnumerable<PdfValidationIssue> Warnings => Issues.Where(i => i.Severity == PdfValidationSeverity.Warning);

    /// <inheritdoc />
    public override string ToString() => $"PdfValidationReport {{ IsValid = {IsValid}, Errors = {Errors.Count()}, Warnings = {Warnings.Count()}, Issues = {Issues.Count} }}";
}
