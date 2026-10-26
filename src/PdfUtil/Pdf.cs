namespace PdfUtil;

/// <summary>
/// Static, synchronous facade over <see cref="PdfProtector"/>, <see cref="PdfEditor"/>, <see cref="PdfOpener"/>, <see cref="PdfInspector"/>,
/// <see cref="PdfAnalyzer"/>, <see cref="PdfCleaner"/> and <see cref="PdfAttachmentManager"/>
/// with default options. Thread-safe. For dependency injection, logging or custom limits use the services directly
/// (or <c>services.AddPdfUtil()</c> from PdfUtil.Extensions.DependencyInjection).
/// </summary>
public static class Pdf
{
    private static readonly PdfProtector Protector = new();
    private static readonly PdfOpener Opener = new();
    private static readonly PdfInspector Inspector = new();
    private static readonly PdfEditor Editor = new();
    private static readonly PdfAnalyzer Analyzer = new();
    private static readonly PdfCleaner Cleaner = new();
    private static readonly PdfAttachmentManager Attachments = new();
    private static readonly PdfPageEditor PageEditor = new();
    private static readonly PdfTextExtractor TextExtractor = new();
    private static readonly PdfStamper Stamper = new();

    /// <summary>Protects a file (atomic replace; <paramref name="inputPath"/> may equal <paramref name="outputPath"/>).</summary>
    public static PdfProtectionResult Protect(string inputPath, string outputPath, PdfProtectionOptions options) =>
        Protector.ProtectFileAsync(inputPath, outputPath, options).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Protects a PDF read from <paramref name="input"/> into <paramref name="output"/>.</summary>
    public static PdfProtectionResult Protect(Stream input, Stream output, PdfProtectionOptions options) =>
        Protector.ProtectAsync(input, output, options).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Protects an in-memory PDF and returns the protected bytes.</summary>
    public static byte[] Protect(byte[] pdf, PdfProtectionOptions options) => Protect(pdf, options, out _);

    /// <summary>Protects an in-memory PDF and returns the protected bytes and the result details.</summary>
    public static byte[] Protect(byte[] pdf, PdfProtectionOptions options, out PdfProtectionResult result)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        result = Protect(input, output, options);
        return output.ToArray();
    }

    /// <summary>Removes protection from a file, using its owner password (atomic replace).</summary>
    public static PdfEditResult Decrypt(string inputPath, string outputPath, string ownerPassword) =>
        Editor.DecryptFileAsync(inputPath, outputPath, new PdfDecryptOptions { Password = ownerPassword }).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Removes protection from a file (atomic replace).</summary>
    public static PdfEditResult Decrypt(string inputPath, string outputPath, PdfDecryptOptions options) =>
        Editor.DecryptFileAsync(inputPath, outputPath, options).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Removes protection from an in-memory PDF, using its owner password.</summary>
    public static byte[] Decrypt(byte[] pdf, string ownerPassword) => Decrypt(pdf, new PdfDecryptOptions { Password = ownerPassword }, out _);

    /// <summary>Removes protection from an in-memory PDF.</summary>
    public static byte[] Decrypt(byte[] pdf, PdfDecryptOptions options, out PdfEditResult result)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        result = Editor.DecryptAsync(input, output, options).ConfigureAwait(false).GetAwaiter().GetResult();
        return output.ToArray();
    }

    /// <summary>Changes the passwords, permissions or algorithm of a protected file (atomic replace).</summary>
    public static PdfProtectionResult ChangeSecurity(string inputPath, string outputPath, PdfSecurityChanges changes) =>
        Editor.ChangeSecurityFileAsync(inputPath, outputPath, changes).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Changes the passwords, permissions or algorithm of an in-memory protected PDF.</summary>
    public static byte[] ChangeSecurity(byte[] pdf, PdfSecurityChanges changes, out PdfProtectionResult result)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        result = Editor.ChangeSecurityAsync(input, output, changes).ConfigureAwait(false).GetAwaiter().GetResult();
        return output.ToArray();
    }

    /// <summary>Changes the document information of a file, keeping its protection (atomic replace).</summary>
    public static PdfEditResult EditInfo(string inputPath, string outputPath, PdfInfoChanges changes, string? ownerPassword = null) =>
        Editor.EditInfoFileAsync(inputPath, outputPath, new PdfEditInfoOptions { Changes = changes, Password = ownerPassword }).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Changes the document information of a file, keeping its protection; <paramref name="options"/> can select an incremental save.</summary>
    public static PdfEditResult EditInfo(string inputPath, string outputPath, PdfEditInfoOptions options) =>
        Editor.EditInfoFileAsync(inputPath, outputPath, options).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Changes the document information of an in-memory PDF; <paramref name="options"/> can select an incremental save.</summary>
    public static byte[] EditInfo(byte[] pdf, PdfEditInfoOptions options, out PdfEditResult result) =>
        InMemory(pdf, (input, output) => Editor.EditInfoAsync(input, output, options), out result);

    /// <summary>Changes the document information of an in-memory PDF, keeping its protection.</summary>
    public static byte[] EditInfo(byte[] pdf, PdfInfoChanges changes, string? ownerPassword, out PdfEditResult result)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        result = Editor.EditInfoAsync(input, output, new PdfEditInfoOptions { Changes = changes, Password = ownerPassword }).ConfigureAwait(false).GetAwaiter().GetResult();
        return output.ToArray();
    }

    /// <summary>Checks a password against a PDF file.</summary>
    public static PdfPasswordCheckResult VerifyPassword(string path, string? password)
    {
        using var stream = OpenRead(path);
        return VerifyPassword(stream, password);
    }

    /// <summary>Checks a password against an in-memory PDF.</summary>
    public static PdfPasswordCheckResult VerifyPassword(byte[] pdf, string? password)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var stream = new MemoryStream(pdf, writable: false);
        return VerifyPassword(stream, password);
    }

    /// <summary>Checks a password against a PDF stream.</summary>
    public static PdfPasswordCheckResult VerifyPassword(Stream input, string? password) =>
        Opener.VerifyPasswordAsync(input, password).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Whether a PDF file is encrypted.</summary>
    public static bool IsEncrypted(string path)
    {
        using var stream = OpenRead(path);
        return IsEncrypted(stream);
    }

    /// <summary>Whether an in-memory PDF is encrypted.</summary>
    public static bool IsEncrypted(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var stream = new MemoryStream(pdf, writable: false);
        return IsEncrypted(stream);
    }

    /// <summary>Whether a PDF stream is encrypted.</summary>
    public static bool IsEncrypted(Stream input) => Opener.IsEncryptedAsync(input).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Reads details and metadata of a PDF file.</summary>
    public static PdfDocumentInfo GetInfo(string path, string? password = null)
    {
        using var stream = OpenRead(path);
        return GetInfo(stream, password);
    }

    /// <summary>Reads details and metadata of an in-memory PDF.</summary>
    public static PdfDocumentInfo GetInfo(byte[] pdf, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var stream = new MemoryStream(pdf, writable: false);
        return GetInfo(stream, password);
    }

    /// <summary>Reads details and metadata of a PDF stream.</summary>
    public static PdfDocumentInfo GetInfo(Stream input, string? password = null) =>
        Inspector.GetInfoAsync(input, password).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Reads details of a PDF file, including the optional details selected in <paramref name="options"/> (all by default).</summary>
    public static PdfDocumentInfo Inspect(string path, PdfInspectionOptions? options = null)
    {
        using var stream = OpenRead(path);
        return Analyzer.InspectAsync(stream, options ?? new PdfInspectionOptions()).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>Reads details of an in-memory PDF, including the optional details selected in <paramref name="options"/> (all by default).</summary>
    public static PdfDocumentInfo Inspect(byte[] pdf, PdfInspectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var stream = new MemoryStream(pdf, writable: false);
        return Analyzer.InspectAsync(stream, options ?? new PdfInspectionOptions()).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>Checks a PDF file and reports every problem found.</summary>
    public static PdfValidationReport Validate(string path, PdfValidationOptions? options = null)
    {
        using var stream = OpenRead(path);
        return Analyzer.ValidateAsync(stream, options).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>Checks an in-memory PDF and reports every problem found.</summary>
    public static PdfValidationReport Validate(byte[] pdf, PdfValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var stream = new MemoryStream(pdf, writable: false);
        return Analyzer.ValidateAsync(stream, options).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>Writes a repaired, single-revision copy of a file (atomic replace).</summary>
    public static PdfRepairResult Repair(string inputPath, string outputPath, PdfRepairOptions? options = null) =>
        Cleaner.RepairFileAsync(inputPath, outputPath, options).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Repairs an in-memory PDF and returns the repaired bytes.</summary>
    public static byte[] Repair(byte[] pdf, PdfRepairOptions? options, out PdfRepairResult result) =>
        InMemory(pdf, (input, output) => Cleaner.RepairAsync(input, output, options), out result);

    /// <summary>Removes scripts, attachments, metadata, actions or annotations from a file (atomic replace).</summary>
    public static PdfSanitizeResult Sanitize(string inputPath, string outputPath, PdfSanitizeOptions options) =>
        Cleaner.SanitizeFileAsync(inputPath, outputPath, options).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Removes scripts, attachments, metadata, actions or annotations from an in-memory PDF.</summary>
    public static byte[] Sanitize(byte[] pdf, PdfSanitizeOptions options, out PdfSanitizeResult result) =>
        InMemory(pdf, (input, output) => Cleaner.SanitizeAsync(input, output, options), out result);

    /// <summary>Lists the embedded files of a PDF file.</summary>
    public static IReadOnlyList<PdfAttachmentInfo> ListAttachments(string path, string? password = null)
    {
        using var stream = OpenRead(path);
        return Attachments.ListAttachmentsAsync(stream, password).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>Lists the embedded files of an in-memory PDF.</summary>
    public static IReadOnlyList<PdfAttachmentInfo> ListAttachments(byte[] pdf, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var stream = new MemoryStream(pdf, writable: false);
        return Attachments.ListAttachmentsAsync(stream, password).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>Adds an attachment to a file (atomic replace).</summary>
    public static PdfEditResult AddAttachment(string inputPath, string outputPath, PdfAddAttachmentOptions options) =>
        Attachments.AddAttachmentFileAsync(inputPath, outputPath, options).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Adds an attachment to an in-memory PDF.</summary>
    public static byte[] AddAttachment(byte[] pdf, PdfAddAttachmentOptions options) => AddAttachment(pdf, options, out _);

    /// <summary>Adds an attachment to an in-memory PDF and returns the result details.</summary>
    public static byte[] AddAttachment(byte[] pdf, PdfAddAttachmentOptions options, out PdfEditResult result) =>
        InMemory(pdf, (input, output) => Attachments.AddAttachmentAsync(input, output, options), out result);

    /// <summary>Removes an attachment from a file (atomic replace).</summary>
    public static PdfEditResult RemoveAttachment(string inputPath, string outputPath, string name, string? ownerPassword = null) =>
        Attachments.RemoveAttachmentFileAsync(inputPath, outputPath, new PdfRemoveAttachmentOptions { Name = name, Password = ownerPassword }).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Removes an attachment from an in-memory PDF.</summary>
    public static byte[] RemoveAttachment(byte[] pdf, string name, string? ownerPassword = null) =>
        InMemory(pdf, (input, output) => Attachments.RemoveAttachmentAsync(input, output, new PdfRemoveAttachmentOptions { Name = name, Password = ownerPassword }), out _);

    /// <summary>Removes an attachment from a file (atomic replace); <paramref name="options"/> can select an incremental save.</summary>
    public static PdfEditResult RemoveAttachment(string inputPath, string outputPath, PdfRemoveAttachmentOptions options) =>
        Attachments.RemoveAttachmentFileAsync(inputPath, outputPath, options).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Removes an attachment from an in-memory PDF; <paramref name="options"/> can select an incremental save.</summary>
    public static byte[] RemoveAttachment(byte[] pdf, PdfRemoveAttachmentOptions options, out PdfEditResult result) =>
        InMemory(pdf, (input, output) => Attachments.RemoveAttachmentAsync(input, output, options), out result);

    /// <summary>Returns the decoded bytes of an embedded file in a PDF file.</summary>
    public static byte[] ExtractAttachment(string path, string name, string? password = null)
    {
        using var stream = OpenRead(path);
        return Attachments.ExtractAttachmentAsync(stream, name, password).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>Returns the decoded bytes of an embedded file in an in-memory PDF.</summary>
    public static byte[] ExtractAttachment(byte[] pdf, string name, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var stream = new MemoryStream(pdf, writable: false);
        return Attachments.ExtractAttachmentAsync(stream, name, password).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>Rotates pages of a file (atomic replace).</summary>
    public static PdfPageEditResult RotatePages(string inputPath, string outputPath, PdfRotatePagesOptions options) =>
        PageEditor.RotatePagesFileAsync(inputPath, outputPath, options).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Rotates pages of an in-memory PDF.</summary>
    public static byte[] RotatePages(byte[] pdf, PdfRotatePagesOptions options, out PdfPageEditResult result) =>
        InMemory(pdf, (input, output) => PageEditor.RotatePagesAsync(input, output, options), out result);

    /// <summary>Deletes pages of a file (atomic replace).</summary>
    public static PdfPageEditResult DeletePages(string inputPath, string outputPath, PdfDeletePagesOptions options) =>
        PageEditor.DeletePagesFileAsync(inputPath, outputPath, options).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Deletes pages of an in-memory PDF.</summary>
    public static byte[] DeletePages(byte[] pdf, PdfDeletePagesOptions options, out PdfPageEditResult result) =>
        InMemory(pdf, (input, output) => PageEditor.DeletePagesAsync(input, output, options), out result);

    /// <summary>Merges documents into a file (atomic replace).</summary>
    public static PdfMergeResult Merge(IReadOnlyList<PdfMergeInput> inputs, string outputPath, PdfMergeOptions? options = null) =>
        PageEditor.MergeFileAsync(inputs, outputPath, options).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Merges documents and returns the merged bytes.</summary>
    public static byte[] Merge(IReadOnlyList<PdfMergeInput> inputs, PdfMergeOptions? options, out PdfMergeResult result)
    {
        using var output = new MemoryStream();
        result = PageEditor.MergeAsync(inputs, output, options).ConfigureAwait(false).GetAwaiter().GetResult();
        return output.ToArray();
    }

    /// <summary>Splits a file into files named by <paramref name="outputPathPattern"/> (<c>{0}</c> is the part number).</summary>
    public static PdfSplitResult Split(string inputPath, string outputPathPattern, PdfSplitOptions options) =>
        PageEditor.SplitFileAsync(inputPath, outputPathPattern, options).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Splits an in-memory PDF and returns the parts' bytes in order.</summary>
    public static IReadOnlyList<byte[]> Split(byte[] pdf, PdfSplitOptions options, out PdfSplitResult result)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        var parts = new List<MemoryStream>();
        using var input = new MemoryStream(pdf, writable: false);
        result = PageEditor.SplitAsync(input, options, _ =>
        {
            var part = new MemoryStream();
            parts.Add(part);
            return part;
        }).ConfigureAwait(false).GetAwaiter().GetResult();
        return parts.Select(p => p.ToArray()).ToList();
    }

    /// <summary>Stamps text or an image on the pages of a file (atomic replace).</summary>
    public static PdfStampResult Stamp(string inputPath, string outputPath, PdfStampOptions options) =>
        Stamper.StampFileAsync(inputPath, outputPath, options).ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>Stamps text or an image on the pages of an in-memory PDF.</summary>
    public static byte[] Stamp(byte[] pdf, PdfStampOptions options, out PdfStampResult result) =>
        InMemory(pdf, (input, output) => Stamper.StampAsync(input, output, options), out result);

    /// <summary>Extracts the text of a PDF file, with positions.</summary>
    public static PdfTextExtractionResult ExtractText(string path, PdfTextExtractionOptions? options = null)
    {
        using var stream = OpenRead(path);
        return TextExtractor.ExtractAsync(stream, options).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>Extracts the text of an in-memory PDF, with positions.</summary>
    public static PdfTextExtractionResult ExtractText(byte[] pdf, PdfTextExtractionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var stream = new MemoryStream(pdf, writable: false);
        return TextExtractor.ExtractAsync(stream, options).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    private static byte[] InMemory<T>(byte[] pdf, Func<Stream, Stream, Task<T>> operation, out T result)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        result = operation(input, output).ConfigureAwait(false).GetAwaiter().GetResult();
        return output.ToArray();
    }

    private static FileStream OpenRead(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.RandomAccess);
    }
}
