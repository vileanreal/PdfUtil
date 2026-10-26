using PdfUtil.Internal;
using PdfUtil.Internal.Analysis;
using PdfUtil.Internal.IO;

namespace PdfUtil;

/// <summary>Detailed inspection and validation of PDF documents. Implementations are stateless and thread-safe.</summary>
public interface IPdfAnalyzer
{
    /// <summary>
    /// Returns everything <see cref="IPdfInspector.GetInfoAsync"/> returns, plus the details selected in
    /// <see cref="PdfInspectionOptions.Details"/>: fonts, images, attachments, bookmarks, form fields, signatures, page
    /// labels and actions.
    /// </summary>
    Task<PdfDocumentInfo> InspectAsync(Stream input, PdfInspectionOptions options, CancellationToken ct = default);

    /// <summary>
    /// Checks the document and returns a report of every problem found, with severity, code, message and object
    /// number. A damaged file that can still be read is reported, never thrown; a file that cannot be read at all gives
    /// a report with <see cref="PdfValidationReport.IsReadable"/> <c>false</c>. Throws only for exceeded limits, a wrong
    /// non-null password, and cancellation.
    /// </summary>
    Task<PdfValidationReport> ValidateAsync(Stream input, PdfValidationOptions? options = null, CancellationToken ct = default);
}

/// <summary>Default <see cref="IPdfAnalyzer"/>. Stateless and thread-safe; register as a singleton.</summary>
public sealed class PdfAnalyzer : IPdfAnalyzer
{
    private readonly PdfUtilOptions _options;
    private readonly PdfLog _log;

    /// <summary>Creates an analyzer with default options.</summary>
    public PdfAnalyzer() : this(null) { }

    /// <summary>Creates an analyzer. The options are copied.</summary>
    public PdfAnalyzer(PdfUtilOptions? options)
    {
        _options = (options ?? new PdfUtilOptions()).Clone();
        _log = new PdfLog(_options.Logger);
    }

    /// <inheritdoc />
    public async Task<PdfDocumentInfo> InspectAsync(Stream input, PdfInspectionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        if ((options.Details & ~PdfInspectionDetails.All) != 0)
        {
            throw new ArgumentException("Details contains undefined flags.", nameof(options));
        }

        using var source = await InputBuffer.OpenAsync(input, _options, ct).ConfigureAwait(false);
        return Guard.Run(() => new InspectionEngine(_options, _log).Inspect(source, options.Password, options.Details, ct));
    }

    /// <inheritdoc />
    public async Task<PdfValidationReport> ValidateAsync(Stream input, PdfValidationOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= new PdfValidationOptions();
        if (options.MaxIssues < 1)
        {
            throw new ArgumentException("MaxIssues must be at least 1.", nameof(options));
        }

        using var source = await InputBuffer.OpenAsync(input, _options, ct).ConfigureAwait(false);
        _log.Info($"Validating a {source.Length}-byte document.");
        return Guard.Run(() => new Validator(_options, _log).Validate(source, options, ct));
    }
}
