using System.Security.Cryptography.X509Certificates;
using PdfUtil.Internal;
using PdfUtil.Internal.IO;
using PdfUtil.Signatures.Internal;

namespace PdfUtil.Signatures;

/// <summary>
/// Certificate (public-key) encryption, <c>/Adobe.PubSec</c> with <c>adbe.pkcs7.s5</c>: protects documents for
/// recipients' certificates and opens them with a recipient's private key. Implementations are stateless and thread-safe.
/// </summary>
public interface IPdfCertificateSecurity
{
    /// <summary>Encrypts an unencrypted document so that only the recipients can open it, each with its own permissions.</summary>
    Task<PdfCertificateProtectionResult> ProtectAsync(Stream input, Stream output, PdfCertificateProtectionOptions options, CancellationToken ct = default);

    /// <summary>File version of <see cref="ProtectAsync"/> (atomic replace).</summary>
    Task<PdfCertificateProtectionResult> ProtectFileAsync(string inputPath, string outputPath, PdfCertificateProtectionOptions options, CancellationToken ct = default);

    /// <summary>
    /// Writes an unencrypted copy of a certificate-encrypted document, opened with a recipient's certificate and private
    /// key. Recipients with restricted permissions need <see cref="PdfCertificateDecryptOptions.AllowRestrictedRecipient"/>.
    /// </summary>
    Task<PdfEditResult> DecryptAsync(Stream input, Stream output, PdfCertificateDecryptOptions options, CancellationToken ct = default);

    /// <summary>File version of <see cref="DecryptAsync"/>.</summary>
    Task<PdfEditResult> DecryptFileAsync(string inputPath, string outputPath, PdfCertificateDecryptOptions options, CancellationToken ct = default);

    /// <summary>
    /// Reads document details. With a recipient's certificate the document is opened and everything is reported;
    /// without one, the encryption details only (<see cref="PdfDocumentInfo.RequiresCertificate"/> is set).
    /// </summary>
    Task<PdfDocumentInfo> InspectAsync(Stream input, X509Certificate2? certificate, PdfInspectionDetails details = PdfInspectionDetails.None, CancellationToken ct = default);

    /// <summary>As <see cref="InspectAsync(Stream, X509Certificate2?, PdfInspectionDetails, CancellationToken)"/>, with the recipient's private key given separately.</summary>
    Task<PdfDocumentInfo> InspectAsync(Stream input, X509Certificate2 certificate, System.Security.Cryptography.RSA privateKey, PdfInspectionDetails details = PdfInspectionDetails.None, CancellationToken ct = default);
}

/// <summary>A recipient of a certificate-encrypted document.</summary>
/// <param name="Certificate">The recipient's certificate (an RSA public key is enough).</param>
/// <param name="Permissions">What the recipient may do. Default: everything.</param>
public sealed record PdfCertificateRecipient(X509Certificate2 Certificate, PdfPermissions Permissions = PdfPermissions.All);

/// <summary>Settings for <see cref="IPdfCertificateSecurity.ProtectAsync"/>.</summary>
public sealed class PdfCertificateProtectionOptions
{
    /// <summary>The recipients. At least one is required.</summary>
    public IReadOnlyList<PdfCertificateRecipient> Recipients { get; set; } = Array.Empty<PdfCertificateRecipient>();

    /// <summary><see cref="PdfEncryptionAlgorithm.Aes256"/> (default, V5) or <see cref="PdfEncryptionAlgorithm.Aes128"/> (V4).</summary>
    public PdfEncryptionAlgorithm Algorithm { get; set; } = PdfEncryptionAlgorithm.Aes256;

    /// <summary>Whether XMP metadata is encrypted. Default <c>true</c>.</summary>
    public bool EncryptMetadata { get; set; } = true;

    /// <summary>Behaviour for PDF/A input, which forbids encryption. Default <see cref="PdfAPolicy.Throw"/>.</summary>
    public PdfAPolicy PdfAPolicy { get; set; }

    /// <summary>Output layout, for example compressed object streams.</summary>
    public PdfWriteOptions? Write { get; set; }

    /// <summary>Optional progress reporting.</summary>
    public IProgress<PdfProgress>? Progress { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"PdfCertificateProtectionOptions {{ Recipients = {Recipients.Count}, Algorithm = {Algorithm} }}";
}

/// <summary>Settings for <see cref="IPdfCertificateSecurity.DecryptAsync"/>.</summary>
public sealed class PdfCertificateDecryptOptions
{
    /// <summary>A recipient's certificate, with its private key unless <see cref="PrivateKey"/> is set. Required.</summary>
    public X509Certificate2? Certificate { get; set; }

    /// <summary>The recipient's RSA private key, when it is not attached to <see cref="Certificate"/>.</summary>
    public System.Security.Cryptography.RSA? PrivateKey { get; set; }

    /// <summary>
    /// Also remove protection for a recipient whose permissions are restricted. Default <c>false</c>: removing
    /// protection lifts the restrictions, so only a recipient with every permission may do it.
    /// </summary>
    public bool AllowRestrictedRecipient { get; set; }

    /// <summary>Output layout, for example compressed object streams.</summary>
    public PdfWriteOptions? Write { get; set; }

    /// <summary>Optional progress reporting.</summary>
    public IProgress<PdfProgress>? Progress { get; set; }
}

/// <summary>Outcome of certificate protection.</summary>
public sealed class PdfCertificateProtectionResult
{
    internal PdfCertificateProtectionResult()
    {
    }

    /// <summary>Effective input version.</summary>
    public string SourceVersion { get; internal init; } = "";

    /// <summary>Version written to the output header.</summary>
    public string OutputVersion { get; internal init; } = "";

    /// <summary>The algorithm used.</summary>
    public PdfEncryptionAlgorithm Algorithm { get; internal init; }

    /// <summary>Number of recipients.</summary>
    public int RecipientCount { get; internal init; }

    /// <summary>Non-fatal issues.</summary>
    public IReadOnlyList<string> Warnings { get; internal init; } = Array.Empty<string>();

    /// <inheritdoc />
    public override string ToString() => $"PdfCertificateProtectionResult {{ Algorithm = {Algorithm}, Recipients = {RecipientCount} }}";
}

/// <summary>Default <see cref="IPdfCertificateSecurity"/>. Stateless and thread-safe; register as a singleton.</summary>
public sealed class PdfCertificateSecurity : IPdfCertificateSecurity
{
    private readonly PdfUtilOptions _options;
    private readonly PdfLog _log;

    /// <summary>Creates the service with default options.</summary>
    public PdfCertificateSecurity() : this(null) { }

    /// <summary>Creates the service. The options are copied.</summary>
    public PdfCertificateSecurity(PdfUtilOptions? options)
    {
        _options = (options ?? new PdfUtilOptions()).Clone();
        _log = new PdfLog(_options.Logger);
    }

    /// <inheritdoc />
    public Task<PdfCertificateProtectionResult> ProtectAsync(Stream input, Stream output, PdfCertificateProtectionOptions options, CancellationToken ct = default)
    {
        CertificateSecurityEngine.Validate(options);
        return OutputRunner.ToStreamAsync(input, output, _options, _log, "Protecting a {size}-byte document with certificates.", (source, buffer) => Engine.Protect(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfCertificateProtectionResult> ProtectFileAsync(string inputPath, string outputPath, PdfCertificateProtectionOptions options, CancellationToken ct = default)
    {
        CertificateSecurityEngine.Validate(options);
        return OutputRunner.ToFileAsync(inputPath, outputPath, _options, _log, "Protecting a {size}-byte document with certificates.", (source, buffer) => Engine.Protect(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfEditResult> DecryptAsync(Stream input, Stream output, PdfCertificateDecryptOptions options, CancellationToken ct = default)
    {
        CertificateSecurityEngine.Validate(options);
        return OutputRunner.ToStreamAsync(input, output, _options, _log, "Removing certificate protection from a {size}-byte document.", (source, buffer) => Engine.Decrypt(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfEditResult> DecryptFileAsync(string inputPath, string outputPath, PdfCertificateDecryptOptions options, CancellationToken ct = default)
    {
        CertificateSecurityEngine.Validate(options);
        return OutputRunner.ToFileAsync(inputPath, outputPath, _options, _log, "Removing certificate protection from a {size}-byte document.", (source, buffer) => Engine.Decrypt(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public async Task<PdfDocumentInfo> InspectAsync(Stream input, X509Certificate2? certificate, PdfInspectionDetails details = PdfInspectionDetails.None, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var source = await InputBuffer.OpenAsync(input, _options, ct).ConfigureAwait(false);
        return Guard.Run(() => Engine.Inspect(source, certificate, null, details, ct));
    }

    /// <inheritdoc />
    public async Task<PdfDocumentInfo> InspectAsync(Stream input, X509Certificate2 certificate, System.Security.Cryptography.RSA privateKey, PdfInspectionDetails details = PdfInspectionDetails.None, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(privateKey);
        using var source = await InputBuffer.OpenAsync(input, _options, ct).ConfigureAwait(false);
        return Guard.Run(() => Engine.Inspect(source, certificate, privateKey, details, ct));
    }

    private CertificateSecurityEngine Engine => new(_options, _log);
}
