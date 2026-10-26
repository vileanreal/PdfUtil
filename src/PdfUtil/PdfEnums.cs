namespace PdfUtil;

/// <summary>
/// Document permissions for the Standard Security Handler (ISO 32000-1, Table 22).
/// Values are the bit masks of the <c>/P</c> entry; reserved bits are managed internally.
/// </summary>
[Flags]
public enum PdfPermissions
{
    /// <summary>No permission is granted.</summary>
    None = 0,

    /// <summary>Bit 3: print the document (possibly at low quality, see <see cref="PrintHighQuality"/>).</summary>
    Print = 1 << 2,

    /// <summary>Bit 4: modify the contents of the document by operations other than those controlled by other bits.</summary>
    ModifyContents = 1 << 3,

    /// <summary>Bit 5: copy or otherwise extract text and graphics.</summary>
    CopyContent = 1 << 4,

    /// <summary>Bit 6: add or modify annotations, fill in interactive form fields.</summary>
    ModifyAnnotations = 1 << 5,

    /// <summary>Bit 9: fill in existing interactive form fields (including signature fields).</summary>
    FillForms = 1 << 8,

    /// <summary>Bit 10: extract text and graphics for accessibility purposes.</summary>
    ExtractForAccessibility = 1 << 9,

    /// <summary>Bit 11: assemble the document (insert, rotate, delete pages, create bookmarks or thumbnails).</summary>
    AssembleDocument = 1 << 10,

    /// <summary>Bit 12: print the document to a representation from which a faithful digital copy could be generated.</summary>
    PrintHighQuality = 1 << 11,

    /// <summary>All permissions.</summary>
    All = Print | ModifyContents | CopyContent | ModifyAnnotations | FillForms | ExtractForAccessibility | AssembleDocument | PrintHighQuality,
}

/// <summary>Encryption algorithm used when protecting a document.</summary>
public enum PdfEncryptionAlgorithm
{
    /// <summary>AES-256 (Standard Security Handler V5 / R6, ISO 32000-2). Default. Requires PDF 1.7 with Adobe extension level 8, or PDF 2.0.</summary>
    Aes256 = 0,

    /// <summary>AES-128 (V4 / R4, AESV2 crypt filter). Requires PDF 1.6. Use for readers released before ~2008.</summary>
    Aes128 = 1,

    /// <summary>RC4 with a 128-bit key (V2 / R3). Requires PDF 1.4. Legacy; weak by modern standards.</summary>
    Rc4_128 = 2,

    /// <summary>RC4 with a 40-bit key (V1 / R2). Read-only: reported for existing documents, never written.</summary>
    Rc4_40 = 10,

    /// <summary>RC4 inside a V4 crypt filter (V4 / R4, CFM V2). Read-only.</summary>
    Rc4_128CryptFilter = 11,

    /// <summary>AES-256 from Adobe extension level 3 (V5 / R5, deprecated). Read-only, never written.</summary>
    Aes256R5 = 12,

    /// <summary>The document is not encrypted, or the algorithm is not known.</summary>
    None = 99,
}

/// <summary>What to do when the input declares PDF/A conformance, which forbids encryption.</summary>
public enum PdfAPolicy
{
    /// <summary>Throw <see cref="PdfComplianceException"/>. Default.</summary>
    Throw = 0,

    /// <summary>Encrypt anyway and add an entry to <see cref="PdfProtectionResult.Warnings"/>. The output is no longer PDF/A conformant.</summary>
    Proceed = 1,
}

/// <summary>What to do when the input document is already encrypted.</summary>
public enum AlreadyEncryptedPolicy
{
    /// <summary>Throw <see cref="PdfAlreadyEncryptedException"/>. Default.</summary>
    Throw = 0,

    /// <summary>
    /// Decrypt with <see cref="PdfProtectionOptions.CurrentOwnerPassword"/> (which must authenticate as the
    /// owner password) and re-encrypt with the new settings.
    /// </summary>
    ReEncryptWithOwnerPassword = 1,
}

/// <summary>Which passwords <see cref="IPdfProtector"/> accepts.</summary>
public enum PdfPasswordPolicy
{
    /// <summary>
    /// Default. Accept only passwords that every major reader turns into the same bytes, so the correct password
    /// opens the file everywhere. RC4/AES-128: printable ASCII. AES-256: Basic Multilingual Plane text that SASLprep
    /// leaves unchanged and that has no canonical decomposition (Cyrillic, Greek, CJK ideographs, Arabic, Hebrew are
    /// fine; accented Latin letters such as 'é', Hangul syllables and emoji are not, because Apple PDFKit and pdf.js
    /// encode them differently).
    /// </summary>
    Interoperable = 0,

    /// <summary>
    /// Apply the ISO 32000 rules only: PDFDocEncoding for RC4/AES-128, SASLprep + UTF-8 for AES-256. Passwords with
    /// non-ASCII (legacy) or composed (AES-256) characters then open in spec-conformant readers such as Acrobat and
    /// in this library, but may be rejected by some browsers and Apple viewers.
    /// </summary>
    Specification = 1,
}

/// <summary>Which password a check matched.</summary>
public enum PdfPasswordMatch
{
    /// <summary>The password did not authenticate (or the document is not encrypted).</summary>
    None = 0,

    /// <summary>The password authenticated as the user (open) password.</summary>
    User = 1,

    /// <summary>The password authenticated as the owner (permissions) password.</summary>
    Owner = 2,
}

/// <summary>Severity of an internal log message.</summary>
public enum PdfLogLevel
{
    /// <summary>Verbose diagnostic detail.</summary>
    Debug = 0,

    /// <summary>Normal operational message.</summary>
    Information = 1,

    /// <summary>Something unexpected that was tolerated (for example a repaired cross-reference table).</summary>
    Warning = 2,

    /// <summary>An operation failed.</summary>
    Error = 3,
}
