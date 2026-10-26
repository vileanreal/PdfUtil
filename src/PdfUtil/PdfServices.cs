using System.Security.Cryptography;
using PdfUtil.Internal;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.IO;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;
using PdfUtil.Internal.Protection;
using PdfUtil.Internal.Analysis;
using PdfUtil.Internal.Security;

namespace PdfUtil;

/// <summary>Default <see cref="IPdfProtector"/>. Stateless and thread-safe; register as a singleton.</summary>
public sealed class PdfProtector : IPdfProtector
{
    private readonly PdfUtilOptions _options;
    private readonly PdfLog _log;

    /// <summary>Creates a protector with default options.</summary>
    public PdfProtector() : this(null) { }

    /// <summary>Creates a protector. The options are copied; later changes to <paramref name="options"/> have no effect.</summary>
    public PdfProtector(PdfUtilOptions? options)
    {
        _options = (options ?? new PdfUtilOptions()).Clone();
        _log = new PdfLog(_options.Logger);
    }

    /// <inheritdoc />
    public Task<PdfProtectionResult> ProtectAsync(Stream input, Stream output, PdfProtectionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ProtectionEngine.ValidateOptions(options);
        return OutputRunner.ToStreamAsync(input, output, _options, _log, $"Protecting a {{size}}-byte document with {options.Algorithm}.", (source, buffer) => new ProtectionEngine(_options, _log).Protect(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfProtectionResult> ProtectFileAsync(string inputPath, string outputPath, PdfProtectionOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ProtectionEngine.ValidateOptions(options);
        return OutputRunner.ToFileAsync(inputPath, outputPath, _options, _log, $"Protecting a {{size}}-byte document with {options.Algorithm}.", (source, buffer) => new ProtectionEngine(_options, _log).Protect(source, buffer, options, ct), ct);
    }
}

/// <summary>Default <see cref="IPdfEditor"/>. Stateless and thread-safe; register as a singleton.</summary>
public sealed class PdfEditor : IPdfEditor
{
    private readonly PdfUtilOptions _options;
    private readonly PdfLog _log;

    /// <summary>Creates an editor with default options.</summary>
    public PdfEditor() : this(null) { }

    /// <summary>Creates an editor. The options are copied; later changes to <paramref name="options"/> have no effect.</summary>
    public PdfEditor(PdfUtilOptions? options)
    {
        _options = (options ?? new PdfUtilOptions()).Clone();
        _log = new PdfLog(_options.Logger);
    }

    /// <inheritdoc />
    public Task<PdfEditResult> DecryptAsync(Stream input, Stream output, PdfDecryptOptions options, CancellationToken ct = default)
    {
        EditEngine.Validate(options);
        return OutputRunner.ToStreamAsync(input, output, _options, _log, "Removing protection from a {size}-byte document.", (source, buffer) => Engine.Decrypt(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfEditResult> DecryptFileAsync(string inputPath, string outputPath, PdfDecryptOptions options, CancellationToken ct = default)
    {
        EditEngine.Validate(options);
        return OutputRunner.ToFileAsync(inputPath, outputPath, _options, _log, "Removing protection from a {size}-byte document.", (source, buffer) => Engine.Decrypt(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfProtectionResult> ChangeSecurityAsync(Stream input, Stream output, PdfSecurityChanges changes, CancellationToken ct = default)
    {
        EditEngine.Validate(changes);
        return OutputRunner.ToStreamAsync(input, output, _options, _log, "Changing the security of a {size}-byte document.", (source, buffer) => Engine.ChangeSecurity(source, buffer, changes, ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfProtectionResult> ChangeSecurityFileAsync(string inputPath, string outputPath, PdfSecurityChanges changes, CancellationToken ct = default)
    {
        EditEngine.Validate(changes);
        return OutputRunner.ToFileAsync(inputPath, outputPath, _options, _log, "Changing the security of a {size}-byte document.", (source, buffer) => Engine.ChangeSecurity(source, buffer, changes, ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfEditResult> EditInfoAsync(Stream input, Stream output, PdfEditInfoOptions options, CancellationToken ct = default)
    {
        EditEngine.Validate(options);
        return OutputRunner.ToStreamAsync(input, output, _options, _log, "Editing the document information of a {size}-byte document.", (source, buffer) => Engine.EditInfo(source, buffer, options, ct), ct);
    }

    /// <inheritdoc />
    public Task<PdfEditResult> EditInfoFileAsync(string inputPath, string outputPath, PdfEditInfoOptions options, CancellationToken ct = default)
    {
        EditEngine.Validate(options);
        return OutputRunner.ToFileAsync(inputPath, outputPath, _options, _log, "Editing the document information of a {size}-byte document.", (source, buffer) => Engine.EditInfo(source, buffer, options, ct), ct);
    }

    private EditEngine Engine => new(_options, _log);
}

/// <summary>Default <see cref="IPdfOpener"/>. Stateless and thread-safe; register as a singleton.</summary>
public sealed class PdfOpener : IPdfOpener
{
    private readonly PdfUtilOptions _options;
    private readonly PdfLog _log;

    /// <summary>Creates an opener with default options.</summary>
    public PdfOpener() : this(null) { }

    /// <summary>Creates an opener. The options are copied.</summary>
    public PdfOpener(PdfUtilOptions? options)
    {
        _options = (options ?? new PdfUtilOptions()).Clone();
        _log = new PdfLog(_options.Logger);
    }

    /// <inheritdoc />
    public async Task<PdfPasswordCheckResult> VerifyPasswordAsync(Stream input, string? password, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var source = await InputBuffer.OpenAsync(input, _options, ct).ConfigureAwait(false);
        return Guard.Run(() =>
        {
            using var doc = PdfDocument.Open(source, _options, _log, strict: false, ct);
            if (!doc.IsEncrypted)
            {
                return new PdfPasswordCheckResult(false, true, PdfPasswordMatch.None, PdfEncryptionAlgorithm.None, 0, opensWithoutPassword: true);
            }

            if (doc.SecurityFilter == "Adobe.PubSec")
            {
                var pubSec = PubSecDictionary.Parse(doc.GetEncryptDictionary()!, doc.Resolve);
                return new PdfPasswordCheckResult(true, false, PdfPasswordMatch.None, pubSec.Algorithm, 0, opensWithoutPassword: false) { RequiresCertificate = true };
            }

            var handler = doc.InitializeSecurity();
            var match = handler.Authenticate(PasswordEncoder.EncodeForVerification(password, handler.R));
            var opensWithoutPassword = (match == PdfPasswordMatch.User && string.IsNullOrEmpty(password))
                || handler.Authenticate(PasswordEncoder.EncodeForVerification(null, handler.R)) != PdfPasswordMatch.None;
            _log.Debug($"Password check on an R{handler.R} document: {match}.");
            return new PdfPasswordCheckResult(true, match != PdfPasswordMatch.None, match, handler.Algorithm, handler.R, opensWithoutPassword);
        });
    }

    /// <inheritdoc />
    public async Task<bool> IsEncryptedAsync(Stream input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var source = await InputBuffer.OpenAsync(input, _options, ct).ConfigureAwait(false);
        return Guard.Run(() =>
        {
            using var doc = PdfDocument.Open(source, _options, _log, strict: false, ct);
            return doc.IsEncrypted;
        });
    }
}

/// <summary>Default <see cref="IPdfInspector"/>. Stateless and thread-safe; register as a singleton.</summary>
public sealed class PdfInspector : IPdfInspector
{
    private readonly PdfUtilOptions _options;
    private readonly PdfLog _log;

    /// <summary>Creates an inspector with default options.</summary>
    public PdfInspector() : this(null) { }

    /// <summary>Creates an inspector. The options are copied.</summary>
    public PdfInspector(PdfUtilOptions? options)
    {
        _options = (options ?? new PdfUtilOptions()).Clone();
        _log = new PdfLog(_options.Logger);
    }

    /// <inheritdoc />
    public async Task<PdfDocumentInfo> GetInfoAsync(Stream input, string? password = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var source = await InputBuffer.OpenAsync(input, _options, ct).ConfigureAwait(false);
        return Guard.Run(() => new InspectionEngine(_options, _log).Inspect(source, password, PdfInspectionDetails.None, ct));
    }
}

/// <summary>Maps unexpected failures on hostile input to <see cref="PdfCorruptException"/> instead of crashing callers.</summary>
internal static class Guard
{
    public static T Run<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidCastException or NullReferenceException or OverflowException or FormatException or InvalidDataException or CryptographicException or EndOfStreamException or KeyNotFoundException)
        {
            throw new PdfCorruptException("The document is malformed and cannot be processed.", ex);
        }
        catch (InsufficientExecutionStackException ex)
        {
            throw new PdfLimitExceededException("The document is nested too deeply to process. " + ex.Message);
        }
    }
}
