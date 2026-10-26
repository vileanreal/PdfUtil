using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfUtil.Internal;
using PdfUtil.Internal.IO;
using PdfUtil.Signatures.Internal;

namespace PdfUtil.Signatures;

/// <summary>Signs PDF documents and verifies their signatures. Implementations are stateless and thread-safe.</summary>
public interface IPdfSigner
{
    /// <summary>
    /// Signs a document with a PAdES baseline B-B signature (<c>ETSI.CAdES.detached</c>), optionally with an RFC 3161
    /// timestamp (B-T). The signature is appended as an incremental update, so earlier signatures stay valid. Only
    /// unencrypted documents can be signed.
    /// </summary>
    Task<PdfSignResult> SignAsync(Stream input, Stream output, PdfSignOptions options, CancellationToken ct = default);

    /// <summary>File version of <see cref="SignAsync"/> (atomic replace; input and output may be the same file).</summary>
    Task<PdfSignResult> SignFileAsync(string inputPath, string outputPath, PdfSignOptions options, CancellationToken ct = default);

    /// <summary>
    /// Verifies every signature and document timestamp: the byte-range digest, the CMS signature, signed and
    /// timestamped times, what changed after signing, and optionally the certificate chain.
    /// </summary>
    Task<PdfSignatureVerificationResult> VerifyAsync(Stream input, PdfSignatureVerificationOptions? options = null, CancellationToken ct = default);
}

/// <summary>Settings for <see cref="IPdfSigner.SignAsync"/>.</summary>
public sealed class PdfSignOptions
{
    /// <summary>The signing certificate (RSA or ECDSA), with its private key unless <see cref="PrivateKey"/> is set. Required.</summary>
    public X509Certificate2? Certificate { get; set; }

    /// <summary>
    /// The private key, when it is not attached to <see cref="Certificate"/> (for example a key from a hardware module
    /// or key vault exposed as <see cref="RSA"/> or <see cref="ECDsa"/>). Default <c>null</c>: the certificate's own key.
    /// </summary>
    public AsymmetricAlgorithm? PrivateKey { get; set; }

    /// <summary>Intermediate certificates to embed so that verifiers can build the chain.</summary>
    public X509Certificate2Collection? AdditionalCertificates { get; set; }

    /// <summary>
    /// The signature field: an existing unsigned signature field with this name is signed; otherwise a new field with
    /// this name is created. Default: a new field named Signature1 (or the next free number).
    /// </summary>
    public string? FieldName { get; set; }

    /// <summary>A visible signature; <c>null</c> (default) signs invisibly.</summary>
    public PdfSignatureAppearance? Appearance { get; set; }

    /// <summary>The reason for signing (<c>/Reason</c>).</summary>
    public string? Reason { get; set; }

    /// <summary>Where the document was signed (<c>/Location</c>).</summary>
    public string? Location { get; set; }

    /// <summary>How to contact the signer (<c>/ContactInfo</c>).</summary>
    public string? ContactInfo { get; set; }

    /// <summary>The signer name shown by viewers (<c>/Name</c>). Default: the certificate's subject name.</summary>
    public string? SignerName { get; set; }

    /// <summary>The digest algorithm: SHA-256 (default), SHA-384 or SHA-512.</summary>
    public HashAlgorithmName DigestAlgorithm { get; set; } = HashAlgorithmName.SHA256;

    /// <summary>An RFC 3161 time-stamping authority URL. When set, the signature gets a timestamp (PAdES B-T).</summary>
    public Uri? TimestampServer { get; set; }

    /// <summary>The HTTP client used for <see cref="TimestampServer"/>. Default: a shared client.</summary>
    public HttpClient? TimestampHttpClient { get; set; }

    /// <summary>Bytes reserved for the signature value. Default: estimated from the certificates (and grown automatically once if too small).</summary>
    public int? ReservedSize { get; set; }

    /// <summary>The signing time written to <c>/M</c> (the time the signer claims). Default: now.</summary>
    public DateTimeOffset? SigningTime { get; set; }

    /// <summary>Optional progress reporting.</summary>
    public IProgress<PdfProgress>? Progress { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"PdfSignOptions {{ Certificate = {Certificate?.Subject}, FieldName = {FieldName}, Visible = {Appearance is not null}, Timestamp = {TimestampServer is not null} }}";
}

/// <summary>Where and how a visible signature is shown.</summary>
public sealed class PdfSignatureAppearance
{
    /// <summary>The page, from 1. Default 1.</summary>
    public int Page { get; set; } = 1;

    /// <summary>The rectangle, in points on the page as displayed (origin bottom-left, rotation and crop box respected). Default 200 × 60 at (36, 36).</summary>
    public PdfRectangle Bounds { get; set; } = new(36, 36, 236, 96);

    /// <summary>The text; <c>null</c> (default) shows the signer, date, reason and location. Only Windows-1252 characters are shown; others become '?'.</summary>
    public string? Text { get; set; }
}

/// <summary>Outcome of signing.</summary>
public sealed class PdfSignResult
{
    internal PdfSignResult()
    {
    }

    /// <summary>The name of the signed field.</summary>
    public string FieldName { get; internal init; } = "";

    /// <summary>The signed byte range as (offset, length) pairs.</summary>
    public IReadOnlyList<long> ByteRange { get; internal init; } = Array.Empty<long>();

    /// <summary>Size of the signature value (CMS) in bytes, and the space reserved for it.</summary>
    public int SignatureSize { get; internal init; }

    /// <summary>Bytes reserved for the signature value.</summary>
    public int ReservedSize { get; internal init; }

    /// <summary>The time of the RFC 3161 timestamp, when one was requested.</summary>
    public DateTimeOffset? TimestampTime { get; internal init; }

    /// <summary>Non-fatal issues.</summary>
    public IReadOnlyList<string> Warnings { get; internal init; } = Array.Empty<string>();

    /// <inheritdoc />
    public override string ToString() => $"PdfSignResult {{ FieldName = {FieldName}, SignatureSize = {SignatureSize} }}";
}

/// <summary>Settings for <see cref="IPdfSigner.VerifyAsync"/>.</summary>
public sealed class PdfSignatureVerificationOptions
{
    /// <summary>For protected documents: a password that opens the document.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Build and validate each signer's certificate chain with this policy (trust anchors, revocation mode,
    /// verification time). Default <c>null</c>: the chain is not checked, only the signatures and digests.
    /// </summary>
    public X509ChainPolicy? ChainPolicy { get; set; }
}

/// <summary>The verification of every signature in a document.</summary>
public sealed class PdfSignatureVerificationResult
{
    internal PdfSignatureVerificationResult()
    {
    }

    /// <summary>Each signature and document timestamp, in field order.</summary>
    public IReadOnlyList<PdfSignatureValidation> Signatures { get; internal init; } = Array.Empty<PdfSignatureValidation>();

    /// <summary>Whether there is at least one signature and every one is valid.</summary>
    public bool AllValid => Signatures.Count > 0 && Signatures.All(s => s.IsValid);

    /// <summary>Non-fatal issues.</summary>
    public IReadOnlyList<string> Warnings { get; internal init; } = Array.Empty<string>();

    /// <inheritdoc />
    public override string ToString() => $"PdfSignatureVerificationResult {{ Signatures = {Signatures.Count}, AllValid = {AllValid} }}";
}

/// <summary>The verification of one signature or document timestamp.</summary>
public sealed class PdfSignatureValidation
{
    internal PdfSignatureValidation()
    {
    }

    /// <summary>The fully qualified name of the signature field.</summary>
    public string FieldName { get; internal init; } = "";

    /// <summary>The signature format: <c>adbe.pkcs7.detached</c>, <c>ETSI.CAdES.detached</c>, <c>adbe.pkcs7.sha1</c> or <c>ETSI.RFC3161</c>.</summary>
    public string? SubFilter { get; internal init; }

    /// <summary>Whether this is a document timestamp rather than a signature.</summary>
    public bool IsDocumentTimestamp { get; internal init; }

    /// <summary>Whether the signature is valid: the digest matches, the signature verifies, and the chain (when checked) is valid.</summary>
    public bool IsValid => DigestMatches && SignatureIsValid && ChainIsValid != false;

    /// <summary>Whether the digest of the signed byte ranges equals the digest in the signature.</summary>
    public bool DigestMatches { get; internal init; }

    /// <summary>Whether the CMS signature verifies with the signer's public key.</summary>
    public bool SignatureIsValid { get; internal init; }

    /// <summary>Whether the byte range covers the whole file except the signature value.</summary>
    public bool CoversWholeFile { get; internal init; }

    /// <summary>The revision the signature covers, from 1.</summary>
    public int Revision { get; internal init; }

    /// <summary>Whether revisions were appended after this signature.</summary>
    public bool ModifiedAfterSigning { get; internal init; }

    /// <summary>What the later revisions changed, for example "signatures added" or "annotations added or changed".</summary>
    public IReadOnlyList<string> ChangesAfterSigning { get; internal init; } = Array.Empty<string>();

    /// <summary>The signer's certificate.</summary>
    public X509Certificate2? Signer { get; internal init; }

    /// <summary>The signer's name, from the certificate.</summary>
    public string? SignerName { get; internal init; }

    /// <summary>The digest algorithm, for example SHA256.</summary>
    public string? DigestAlgorithm { get; internal init; }

    /// <summary>The signing time from the signed attributes, if present.</summary>
    public DateTimeOffset? SigningTime { get; internal init; }

    /// <summary>The time the signer claims in <c>/M</c>.</summary>
    public DateTimeOffset? ClaimedTime { get; internal init; }

    /// <summary>The time of the signature's timestamp token (or of the document timestamp).</summary>
    public DateTimeOffset? TimestampTime { get; internal init; }

    /// <summary>Whether the timestamp token verifies, when there is one.</summary>
    public bool? TimestampIsValid { get; internal init; }

    /// <summary>Whether the certificate chain is valid; <c>null</c> when no chain policy was given.</summary>
    public bool? ChainIsValid { get; internal init; }

    /// <summary>The chain status messages, when the chain was checked.</summary>
    public IReadOnlyList<string> ChainStatus { get; internal init; } = Array.Empty<string>();

    /// <summary>Why the signature is not valid, or anything unusual about it.</summary>
    public IReadOnlyList<string> Problems { get; internal init; } = Array.Empty<string>();

    /// <inheritdoc />
    public override string ToString() => $"{FieldName}: {(IsValid ? "valid" : "INVALID")} ({SubFilter}, {SignerName})";
}

/// <summary>Default <see cref="IPdfSigner"/>. Stateless and thread-safe; register as a singleton.</summary>
public sealed class PdfSigner : IPdfSigner
{
    private readonly PdfUtilOptions _options;
    private readonly PdfLog _log;

    /// <summary>Creates a signer with default options.</summary>
    public PdfSigner() : this(null) { }

    /// <summary>Creates a signer. The options are copied.</summary>
    public PdfSigner(PdfUtilOptions? options)
    {
        _options = (options ?? new PdfUtilOptions()).Clone();
        _log = new PdfLog(_options.Logger);
    }

    /// <inheritdoc />
    public Task<PdfSignResult> SignAsync(Stream input, Stream output, PdfSignOptions options, CancellationToken ct = default)
    {
        SigningEngine.Validate(options);
        return OutputRunner.ToStreamAsync(input, output, _options, _log, "Signing a {size}-byte document.", (source, buffer) => new SigningEngine(_options, _log).Sign(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfSignResult> SignFileAsync(string inputPath, string outputPath, PdfSignOptions options, CancellationToken ct = default)
    {
        SigningEngine.Validate(options);
        return OutputRunner.ToFileAsync(inputPath, outputPath, _options, _log, "Signing a {size}-byte document.", (source, buffer) => new SigningEngine(_options, _log).Sign(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public async Task<PdfSignatureVerificationResult> VerifyAsync(Stream input, PdfSignatureVerificationOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var source = await InputBuffer.OpenAsync(input, _options, ct).ConfigureAwait(false);
        return Guard.Run(() => new SignatureVerifier(_options, _log).Verify(source, options ?? new PdfSignatureVerificationOptions(), ct));
    }
}
