using PdfUtil.Internal.IO;

namespace PdfUtil;

/// <summary>
/// Chains operations on one PDF: each step reads the previous step's output. Steps are recorded by the fluent methods
/// and run, in order, when <see cref="RunAsync"/>, <see cref="ToBytesAsync"/>, <see cref="ToStreamAsync"/> or
/// <see cref="SaveAsync"/> is called. Every step keeps its own self-verification; if one fails, its exception
/// propagates (with the step's name in <c>Exception.Data["PdfPipelineStep"]</c>) and nothing is written.
/// </summary>
/// <remarks>
/// <para>
/// Passwords carry forward: the password given with <see cref="WithPassword"/> opens the input, and after
/// <see cref="Protect(PdfProtectionOptions)"/> (or <see cref="ChangeSecurity"/>) the new owner password opens the
/// result, so later steps keep working without repeating it. A step's own <c>Password</c> wins when it is set.
/// Options objects are not changed.
/// </para>
/// <para>
/// Intermediate results are held in memory. A pipeline can run more than once (each run reads the input again),
/// but it is not meant to be shared between threads while steps are being added.
/// </para>
/// </remarks>
public sealed class PdfPipeline
{
    private readonly Func<CancellationToken, Task<byte[]>> _source;
    private readonly PdfUtilOptions _options;
    private readonly List<Step> _steps = new();
    private string? _password;

    private delegate Task<(object? Result, string? NextPassword)> StepRun(Stream input, Stream output, string? password, CancellationToken ct);

    private sealed record Step(string Name, StepRun Run);

    private PdfPipeline(Func<CancellationToken, Task<byte[]>> source, PdfUtilOptions? options)
    {
        _source = source;
        _options = (options ?? new PdfUtilOptions()).Clone();
    }

    // ------------------------------------------------------------------ sources

    /// <summary>Starts a pipeline from a PDF in memory.</summary>
    public static PdfPipeline FromBytes(byte[] pdf, PdfUtilOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return new PdfPipeline(_ => Task.FromResult(pdf), options);
    }

    /// <summary>Starts a pipeline from a file, read when the pipeline runs.</summary>
    public static PdfPipeline FromFile(string path, PdfUtilOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new PdfPipeline(ct => File.ReadAllBytesAsync(path, ct), options);
    }

    /// <summary>
    /// Starts a pipeline from a stream, read (from its current position) when the pipeline runs. The stream is not
    /// disposed; a pipeline from a stream that cannot seek runs only once.
    /// </summary>
    public static PdfPipeline FromStream(Stream input, PdfUtilOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var start = input.CanSeek ? input.Position : -1;
        return new PdfPipeline(async ct =>
        {
            if (start >= 0)
            {
                input.Position = start;
            }

            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, ct).ConfigureAwait(false);
            return buffer.ToArray();
        }, options);
    }

    /// <summary>The options the steps run with (for extension steps in other packages).</summary>
    internal PdfUtilOptions Options => _options;

    /// <summary>The names of the steps added so far, in order.</summary>
    public IReadOnlyList<string> Steps => _steps.Select(s => s.Name).ToList();

    // ------------------------------------------------------------------ steps

    /// <summary>The password that opens the input (user or owner, as the first steps need). It is carried forward to later steps.</summary>
    public PdfPipeline WithPassword(string? password)
    {
        _password = password;
        return this;
    }

    /// <summary>Adds a text or image stamp (watermark).</summary>
    public PdfPipeline Stamp(PdfStampOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Add("Stamp", async (input, output, password, ct) =>
        {
            var service = new PdfStamper(_options);
            return (await WithOpenPassword(options, password, () => service.StampAsync(input, output, options, ct)).ConfigureAwait(false), password);
        });
    }

    /// <summary>Adds a text watermark with default styling (large, grey, diagonal, 30% opaque).</summary>
    public PdfPipeline Watermark(string text) => Stamp(new PdfStampOptions { Text = text });

    /// <summary>Protects the document with passwords. Later steps open the result with the owner password.</summary>
    public PdfPipeline Protect(PdfProtectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Add("Protect", async (input, output, password, ct) =>
        {
            var restore = options.CurrentOwnerPassword;
            options.CurrentOwnerPassword ??= password;
            try
            {
                var result = await new PdfProtector(_options).ProtectAsync(input, output, options, ct).ConfigureAwait(false);
                return (result, options.OwnerPassword);
            }
            finally
            {
                options.CurrentOwnerPassword = restore;
            }
        });
    }

    /// <summary>Protects the document with a user (open) password and an owner password, AES-256 and all permissions.</summary>
    public PdfPipeline Protect(string? userPassword, string ownerPassword) =>
        Protect(new PdfProtectionOptions { UserPassword = userPassword, OwnerPassword = ownerPassword });

    /// <summary>Removes the protection. Later steps need no password.</summary>
    public PdfPipeline Decrypt(PdfDecryptOptions? options = null)
    {
        options ??= new PdfDecryptOptions();
        return Add("Decrypt", async (input, output, password, ct) =>
        {
            var restore = options.Password;
            options.Password ??= password;
            try
            {
                return (await new PdfEditor(_options).DecryptAsync(input, output, options, ct).ConfigureAwait(false), null);
            }
            finally
            {
                options.Password = restore;
            }
        });
    }

    /// <summary>Changes passwords, permissions or the algorithm of a protected document.</summary>
    public PdfPipeline ChangeSecurity(PdfSecurityChanges changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return Add("ChangeSecurity", async (input, output, password, ct) =>
        {
            var restore = changes.CurrentOwnerPassword;
            changes.CurrentOwnerPassword ??= password;
            try
            {
                var result = await new PdfEditor(_options).ChangeSecurityAsync(input, output, changes, ct).ConfigureAwait(false);
                return (result, changes.OwnerPassword ?? changes.CurrentOwnerPassword);
            }
            finally
            {
                changes.CurrentOwnerPassword = restore;
            }
        });
    }

    /// <summary>Changes the document information (and its XMP metadata).</summary>
    public PdfPipeline EditInfo(PdfEditInfoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Add("EditInfo", async (input, output, password, ct) =>
            (await WithOpenPassword(options, password, () => new PdfEditor(_options).EditInfoAsync(input, output, options, ct)).ConfigureAwait(false), password));
    }

    /// <summary>Changes the document information, for example <c>.EditInfo(i =&gt; i.Title = "Report")</c>.</summary>
    public PdfPipeline EditInfo(Action<PdfInfoChanges> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var changes = new PdfInfoChanges();
        change(changes);
        return EditInfo(new PdfEditInfoOptions { Changes = changes });
    }

    /// <summary>Rotates pages.</summary>
    public PdfPipeline RotatePages(PdfRotatePagesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Add("RotatePages", async (input, output, password, ct) =>
            (await WithOpenPassword(options, password, () => new PdfPageEditor(_options).RotatePagesAsync(input, output, options, ct)).ConfigureAwait(false), password));
    }

    /// <summary>Rotates pages by <paramref name="angle"/> degrees (a multiple of 90), all or those in <paramref name="pages"/> (for example <c>"1-3,odd"</c>).</summary>
    public PdfPipeline RotatePages(int angle, string? pages = null) =>
        RotatePages(new PdfRotatePagesOptions { Angle = angle, Pages = pages is null ? null : PdfPageRange.Parse(pages) });

    /// <summary>Deletes pages.</summary>
    public PdfPipeline DeletePages(PdfDeletePagesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Add("DeletePages", async (input, output, password, ct) =>
            (await WithOpenPassword(options, password, () => new PdfPageEditor(_options).DeletePagesAsync(input, output, options, ct)).ConfigureAwait(false), password));
    }

    /// <summary>Deletes the pages in <paramref name="pages"/> (for example <c>"2,last"</c>).</summary>
    public PdfPipeline DeletePages(string pages) => DeletePages(new PdfDeletePagesOptions { Pages = PdfPageRange.Parse(pages) });

    /// <summary>
    /// Appends other documents after the current one (a merge). The result is not protected; add
    /// <see cref="Protect(PdfProtectionOptions)"/> afterwards if needed.
    /// </summary>
    public PdfPipeline Append(IEnumerable<PdfMergeInput> documents, PdfMergeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var others = documents.ToList();
        return Add("Append", async (input, output, password, ct) =>
        {
            using var current = new MemoryStream();
            await input.CopyToAsync(current, ct).ConfigureAwait(false);
            var inputs = new List<PdfMergeInput> { PdfMergeInput.FromBytes(current.ToArray(), password) };
            inputs.AddRange(others);
            var merge = options ?? new PdfMergeOptions();
            var restore = merge.AllowUserPassword;
            merge.AllowUserPassword = true;
            try
            {
                return (await new PdfPageEditor(_options).MergeAsync(inputs, output, merge, ct).ConfigureAwait(false), null);
            }
            finally
            {
                merge.AllowUserPassword = restore;
            }
        });
    }

    /// <summary>Appends other PDF files after the current document.</summary>
    public PdfPipeline Append(params string[] paths) => Append(paths.Select(p => PdfMergeInput.FromFile(p)));

    /// <summary>Repairs a damaged document.</summary>
    public PdfPipeline Repair(PdfRepairOptions? options = null)
    {
        options ??= new PdfRepairOptions();
        return Add("Repair", async (input, output, password, ct) =>
        {
            var result = await WithOpenPassword(options, password, () => new PdfCleaner(_options).RepairAsync(input, output, options, ct)).ConfigureAwait(false);
            return (result, options.RemoveProtection ? null : password);
        });
    }

    /// <summary>Removes JavaScript and, as chosen, other active or hidden content.</summary>
    public PdfPipeline Sanitize(PdfSanitizeOptions? options = null)
    {
        options ??= new PdfSanitizeOptions();
        return Add("Sanitize", async (input, output, password, ct) =>
            (await WithOpenPassword(options, password, () => new PdfCleaner(_options).SanitizeAsync(input, output, options, ct)).ConfigureAwait(false), password));
    }

    /// <summary>Embeds a file.</summary>
    public PdfPipeline AddAttachment(PdfAddAttachmentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Add("AddAttachment", async (input, output, password, ct) =>
            (await WithOpenPassword(options, password, () => new PdfAttachmentManager(_options).AddAttachmentAsync(input, output, options, ct)).ConfigureAwait(false), password));
    }

    /// <summary>Embeds a file under <paramref name="name"/>.</summary>
    public PdfPipeline AddAttachment(string name, byte[] content, string? description = null) =>
        AddAttachment(new PdfAddAttachmentOptions { Name = name, Content = content, Description = description });

    /// <summary>Removes an attachment.</summary>
    public PdfPipeline RemoveAttachment(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Add("RemoveAttachment", async (input, output, password, ct) =>
            (await new PdfAttachmentManager(_options).RemoveAttachmentAsync(input, output, new PdfRemoveAttachmentOptions { Name = name, Password = password }, ct).ConfigureAwait(false), password));
    }

    /// <summary>
    /// Adds a custom step: read the PDF from the input stream and write the new one to the output stream. The
    /// password that currently opens the document is passed in; the step's result (if any) appears in
    /// <see cref="PdfPipelineResult.Steps"/>. Extension methods in other packages (for example <c>Sign</c> in
    /// PdfUtil.Signatures) use this.
    /// </summary>
    public PdfPipeline Then(string name, Func<Stream, Stream, string?, CancellationToken, Task<object?>> step)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(step);
        return Add(name, async (input, output, password, ct) => (await step(input, output, password, ct).ConfigureAwait(false), password));
    }

    private PdfPipeline Add(string name, StepRun run)
    {
        _steps.Add(new Step(name, run));
        return this;
    }

    /// <summary>Runs a service call with the pipeline's password on the options when the caller did not set one.</summary>
    private static async Task<T> WithOpenPassword<T>(object options, string? password, Func<Task<T>> call)
    {
        var property = options.GetType().GetProperty("Password");
        var original = property?.GetValue(options) as string;
        var fill = property is not null && original is null && password is not null;
        if (fill)
        {
            property!.SetValue(options, password);
        }

        try
        {
            return await call().ConfigureAwait(false);
        }
        finally
        {
            if (fill)
            {
                property!.SetValue(options, original);
            }
        }
    }

    // ------------------------------------------------------------------ running

    /// <summary>Runs every step and returns the final document and each step's result.</summary>
    public async Task<PdfPipelineResult> RunAsync(CancellationToken ct = default)
    {
        var current = await _source(ct).ConfigureAwait(false);
        if (current.LongLength > _options.MaxFileSizeBytes)
        {
            throw new PdfLimitExceededException($"The input is larger than the configured maximum of {_options.MaxFileSizeBytes} bytes.");
        }

        var password = _password;
        var steps = new List<PdfPipelineStepResult>();
        for (var i = 0; i < _steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = _steps[i];
            try
            {
                using var input = new MemoryStream(current, writable: false);
                using var output = new MemoryStream();
                var (result, next) = await step.Run(input, output, password, ct).ConfigureAwait(false);
                current = output.ToArray();
                password = next;
                steps.Add(new PdfPipelineStepResult(step.Name, result, Warnings(result)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ex.Data["PdfPipelineStep"] = $"{i + 1}: {step.Name}";
                throw;
            }
        }

        return new PdfPipelineResult(current, steps);
    }

    /// <summary>Runs the pipeline and returns the final PDF.</summary>
    public async Task<byte[]> ToBytesAsync(CancellationToken ct = default) => (await RunAsync(ct).ConfigureAwait(false)).Pdf;

    /// <summary>Runs the pipeline and writes the final PDF to <paramref name="output"/> (nothing is written if a step fails).</summary>
    public async Task<PdfPipelineResult> ToStreamAsync(Stream output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var result = await RunAsync(ct).ConfigureAwait(false);
        await output.WriteAsync(result.Pdf, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Runs the pipeline and saves the final PDF; the file is replaced atomically and only on success.</summary>
    public async Task<PdfPipelineResult> SaveAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var result = await RunAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream(result.Pdf, writable: false);
        OutputRunner.CommitFile(buffer, path);
        return result;
    }

    private static IReadOnlyList<string> Warnings(object? result) =>
        result?.GetType().GetProperty("Warnings")?.GetValue(result) as IReadOnlyList<string> ?? Array.Empty<string>();
}

/// <summary>What one pipeline step did.</summary>
/// <param name="Name">The step, for example <c>Stamp</c> or <c>Protect</c>.</param>
/// <param name="Result">The operation's own result (for example a <see cref="PdfStampResult"/>), or null.</param>
/// <param name="Warnings">The step's warnings.</param>
public sealed record PdfPipelineStepResult(string Name, object? Result, IReadOnlyList<string> Warnings);

/// <summary>The outcome of a pipeline: the final PDF and each step's result.</summary>
public sealed class PdfPipelineResult
{
    internal PdfPipelineResult(byte[] pdf, IReadOnlyList<PdfPipelineStepResult> steps)
    {
        Pdf = pdf;
        Steps = steps;
    }

    /// <summary>The final PDF.</summary>
    public byte[] Pdf { get; }

    /// <summary>Each step's result, in order.</summary>
    public IReadOnlyList<PdfPipelineStepResult> Steps { get; }

    /// <summary>All steps' warnings, each prefixed with its step.</summary>
    public IReadOnlyList<string> Warnings => Steps.SelectMany(s => s.Warnings.Select(w => $"{s.Name}: {w}")).ToList();

    /// <summary>The result of the first step with this name, typed, or null.</summary>
    public T? Get<T>(string stepName)
        where T : class => Steps.FirstOrDefault(s => s.Name == stepName)?.Result as T;

    /// <inheritdoc />
    public override string ToString() => $"PdfPipelineResult {{ Steps = {string.Join(" → ", Steps.Select(s => s.Name))}, Bytes = {Pdf.Length} }}";
}

/// <summary>Creates pipelines with the application's <see cref="PdfUtilOptions"/>; register as a singleton.</summary>
public interface IPdfPipelineFactory
{
    /// <summary>Starts a pipeline from a PDF in memory.</summary>
    PdfPipeline FromBytes(byte[] pdf);

    /// <summary>Starts a pipeline from a file.</summary>
    PdfPipeline FromFile(string path);

    /// <summary>Starts a pipeline from a stream (read when the pipeline runs).</summary>
    PdfPipeline FromStream(Stream input);
}

/// <summary>Default <see cref="IPdfPipelineFactory"/>. Stateless and thread-safe.</summary>
public sealed class PdfPipelineFactory : IPdfPipelineFactory
{
    private readonly PdfUtilOptions _options;

    /// <summary>Creates a factory with default options.</summary>
    public PdfPipelineFactory() : this(null) { }

    /// <summary>Creates a factory. The options are copied.</summary>
    public PdfPipelineFactory(PdfUtilOptions? options) => _options = (options ?? new PdfUtilOptions()).Clone();

    /// <inheritdoc />
    public PdfPipeline FromBytes(byte[] pdf) => PdfPipeline.FromBytes(pdf, _options);

    /// <inheritdoc />
    public PdfPipeline FromFile(string path) => PdfPipeline.FromFile(path, _options);

    /// <inheritdoc />
    public PdfPipeline FromStream(Stream input) => PdfPipeline.FromStream(input, _options);
}
