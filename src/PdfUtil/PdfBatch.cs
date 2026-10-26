using System.Diagnostics;

namespace PdfUtil;

/// <summary>Outcome of one item in a batch.</summary>
public enum PdfBatchItemStatus
{
    /// <summary>The operation succeeded.</summary>
    Succeeded = 0,

    /// <summary>The operation threw; see <see cref="PdfBatchItemResult{TItem, TResult}.Error"/>.</summary>
    Failed = 1,

    /// <summary>The item was not processed because the batch stopped after an error (<see cref="PdfBatchOptions.StopOnFirstError"/>).</summary>
    Skipped = 2,
}

/// <summary>Settings for <see cref="IPdfBatchProcessor"/>.</summary>
public sealed class PdfBatchOptions
{
    /// <summary>Maximum number of items processed at the same time. Default: the number of processors.</summary>
    public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;

    /// <summary>Stop starting new items after the first failure; unstarted items are reported as <see cref="PdfBatchItemStatus.Skipped"/>. Default <c>false</c>.</summary>
    public bool StopOnFirstError { get; set; }

    /// <summary>Optional progress reporting, once per finished item.</summary>
    public IProgress<PdfBatchProgress>? Progress { get; set; }
}

/// <summary>Batch progress.</summary>
/// <param name="Completed">Items finished (succeeded, failed or skipped).</param>
/// <param name="Total">Items in the batch.</param>
/// <param name="Succeeded">Items that succeeded so far.</param>
/// <param name="Failed">Items that failed so far.</param>
public readonly record struct PdfBatchProgress(int Completed, int Total, int Succeeded, int Failed);

/// <summary>An input file and the output file to write for it.</summary>
/// <param name="InputPath">Input PDF.</param>
/// <param name="OutputPath">Output PDF (may equal <paramref name="InputPath"/>).</param>
public sealed record PdfFileJob(string InputPath, string OutputPath);

/// <summary>The result of one item.</summary>
/// <typeparam name="TItem">Item type.</typeparam>
/// <typeparam name="TResult">Result type of the operation.</typeparam>
public sealed class PdfBatchItemResult<TItem, TResult>
{
    internal PdfBatchItemResult(int index, TItem item, PdfBatchItemStatus status, TResult? result, Exception? error, TimeSpan duration)
    {
        Index = index;
        Item = item;
        Status = status;
        Result = result;
        Error = error;
        Duration = duration;
    }

    /// <summary>Position of the item in the input sequence.</summary>
    public int Index { get; }

    /// <summary>The item.</summary>
    public TItem Item { get; }

    /// <summary>Outcome.</summary>
    public PdfBatchItemStatus Status { get; }

    /// <summary>The operation's result when it succeeded.</summary>
    public TResult? Result { get; }

    /// <summary>The exception when the operation failed. Messages never contain passwords.</summary>
    public Exception? Error { get; }

    /// <summary>Time the operation took.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Warnings of the result, when the result type has any (protection, edit and inspection results).</summary>
    public IReadOnlyList<string> Warnings => Result switch
    {
        PdfProtectionResult r => r.Warnings,
        PdfEditResult r => r.Warnings,
        PdfDocumentInfo r => r.Warnings,
        _ => Array.Empty<string>(),
    };
}

/// <summary>The results of a batch, in input order.</summary>
/// <typeparam name="TItem">Item type.</typeparam>
/// <typeparam name="TResult">Result type of the operation.</typeparam>
public sealed class PdfBatchResult<TItem, TResult>
{
    internal PdfBatchResult(IReadOnlyList<PdfBatchItemResult<TItem, TResult>> items, TimeSpan duration)
    {
        Items = items;
        Duration = duration;
    }

    /// <summary>One result per input item, in input order.</summary>
    public IReadOnlyList<PdfBatchItemResult<TItem, TResult>> Items { get; }

    /// <summary>Total time.</summary>
    public TimeSpan Duration { get; }

    /// <summary>Number of items that succeeded.</summary>
    public int Succeeded => Items.Count(i => i.Status == PdfBatchItemStatus.Succeeded);

    /// <summary>Number of items that failed.</summary>
    public int Failed => Items.Count(i => i.Status == PdfBatchItemStatus.Failed);

    /// <summary>Number of items skipped after a failure.</summary>
    public int Skipped => Items.Count(i => i.Status == PdfBatchItemStatus.Skipped);

    /// <summary>Whether every item succeeded.</summary>
    public bool AllSucceeded => Items.All(i => i.Status == PdfBatchItemStatus.Succeeded);
}

/// <summary>Runs PdfUtil operations over many documents in parallel. Implementations are thread-safe.</summary>
public interface IPdfBatchProcessor
{
    /// <summary>
    /// Runs <paramref name="operation"/> for every item with bounded parallelism. A failing item never aborts the
    /// batch unless <see cref="PdfBatchOptions.StopOnFirstError"/> is set; cancelling <paramref name="ct"/> stops the
    /// batch and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<PdfBatchResult<TItem, TResult>> RunAsync<TItem, TResult>(IEnumerable<TItem> items, Func<TItem, CancellationToken, Task<TResult>> operation, PdfBatchOptions? options = null, CancellationToken ct = default);

    /// <summary>Protects many files with the same options.</summary>
    Task<PdfBatchResult<PdfFileJob, PdfProtectionResult>> ProtectFilesAsync(IEnumerable<PdfFileJob> jobs, PdfProtectionOptions options, PdfBatchOptions? batchOptions = null, CancellationToken ct = default);

    /// <summary>Removes protection from many files that share an owner password.</summary>
    Task<PdfBatchResult<PdfFileJob, PdfEditResult>> DecryptFilesAsync(IEnumerable<PdfFileJob> jobs, PdfDecryptOptions options, PdfBatchOptions? batchOptions = null, CancellationToken ct = default);

    /// <summary>Inspects many files.</summary>
    Task<PdfBatchResult<string, PdfDocumentInfo>> InspectFilesAsync(IEnumerable<string> paths, string? password = null, PdfBatchOptions? batchOptions = null, CancellationToken ct = default);

    /// <summary>Checks one password against many files.</summary>
    Task<PdfBatchResult<string, PdfPasswordCheckResult>> VerifyPasswordFilesAsync(IEnumerable<string> paths, string? password, PdfBatchOptions? batchOptions = null, CancellationToken ct = default);
}

/// <summary>Default <see cref="IPdfBatchProcessor"/>. Stateless and thread-safe; register as a singleton.</summary>
public sealed class PdfBatchProcessor : IPdfBatchProcessor
{
    private readonly IPdfProtector _protector;
    private readonly IPdfEditor _editor;
    private readonly IPdfInspector _inspector;
    private readonly IPdfOpener _opener;

    /// <summary>Creates a batch processor with default services.</summary>
    public PdfBatchProcessor() : this((PdfUtilOptions?)null) { }

    /// <summary>Creates a batch processor whose services use <paramref name="options"/>.</summary>
    public PdfBatchProcessor(PdfUtilOptions? options)
        : this(new PdfProtector(options), new PdfEditor(options), new PdfInspector(options), new PdfOpener(options)) { }

    /// <summary>Creates a batch processor over existing services (used by dependency injection).</summary>
    public PdfBatchProcessor(IPdfProtector protector, IPdfEditor editor, IPdfInspector inspector, IPdfOpener opener)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _opener = opener ?? throw new ArgumentNullException(nameof(opener));
    }

    /// <inheritdoc />
    public async Task<PdfBatchResult<TItem, TResult>> RunAsync<TItem, TResult>(IEnumerable<TItem> items, Func<TItem, CancellationToken, Task<TResult>> operation, PdfBatchOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(operation);
        options ??= new PdfBatchOptions();
        if (options.MaxDegreeOfParallelism < 1)
        {
            throw new ArgumentException("MaxDegreeOfParallelism must be at least 1.", nameof(options));
        }

        var list = items.ToList();
        var results = new PdfBatchItemResult<TItem, TResult>?[list.Count];
        var total = Stopwatch.StartNew();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var gate = new object();
        int completed = 0, succeeded = 0, failed = 0;

        void Record(int index, PdfBatchItemResult<TItem, TResult> result)
        {
            results[index] = result;
            PdfBatchProgress progress;
            lock (gate)
            {
                completed++;
                if (result.Status == PdfBatchItemStatus.Succeeded)
                {
                    succeeded++;
                }
                else if (result.Status == PdfBatchItemStatus.Failed)
                {
                    failed++;
                }

                progress = new PdfBatchProgress(completed, list.Count, succeeded, failed);
            }

            try
            {
                options.Progress?.Report(progress);
            }
            catch (Exception)
            {
                // A failing progress callback must never break the batch.
            }
        }

        var parallel = new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism, CancellationToken = ct };
        await Parallel.ForEachAsync(Enumerable.Range(0, list.Count), parallel, async (index, _) =>
        {
            var item = list[index];
            if (stop.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                Record(index, new(index, item, PdfBatchItemStatus.Skipped, default, null, TimeSpan.Zero));
                return;
            }

            var watch = Stopwatch.StartNew();
            try
            {
                var result = await operation(item, stop.Token).ConfigureAwait(false);
                Record(index, new(index, item, PdfBatchItemStatus.Succeeded, result, null, watch.Elapsed));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                Record(index, new(index, item, PdfBatchItemStatus.Skipped, default, null, watch.Elapsed));
            }
            catch (Exception ex)
            {
                Record(index, new(index, item, PdfBatchItemStatus.Failed, default, ex, watch.Elapsed));
                if (options.StopOnFirstError)
                {
                    stop.Cancel();
                }
            }
        }).ConfigureAwait(false);

        return new PdfBatchResult<TItem, TResult>(results.Select(r => r!).ToArray(), total.Elapsed);
    }

    /// <inheritdoc />
    public Task<PdfBatchResult<PdfFileJob, PdfProtectionResult>> ProtectFilesAsync(IEnumerable<PdfFileJob> jobs, PdfProtectionOptions options, PdfBatchOptions? batchOptions = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return RunAsync(jobs, (job, token) => _protector.ProtectFileAsync(job.InputPath, job.OutputPath, options, token), batchOptions, ct);
    }

    /// <inheritdoc />
    public Task<PdfBatchResult<PdfFileJob, PdfEditResult>> DecryptFilesAsync(IEnumerable<PdfFileJob> jobs, PdfDecryptOptions options, PdfBatchOptions? batchOptions = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return RunAsync(jobs, (job, token) => _editor.DecryptFileAsync(job.InputPath, job.OutputPath, options, token), batchOptions, ct);
    }

    /// <inheritdoc />
    public Task<PdfBatchResult<string, PdfDocumentInfo>> InspectFilesAsync(IEnumerable<string> paths, string? password = null, PdfBatchOptions? batchOptions = null, CancellationToken ct = default) =>
        RunAsync(paths, async (path, token) =>
        {
#if NETFRAMEWORK
            using var stream = OpenRead(path);
#else
            await using var stream = OpenRead(path);
#endif
            return await _inspector.GetInfoAsync(stream, password, token).ConfigureAwait(false);
        }, batchOptions, ct);

    /// <inheritdoc />
    public Task<PdfBatchResult<string, PdfPasswordCheckResult>> VerifyPasswordFilesAsync(IEnumerable<string> paths, string? password, PdfBatchOptions? batchOptions = null, CancellationToken ct = default) =>
        RunAsync(paths, async (path, token) =>
        {
#if NETFRAMEWORK
            using var stream = OpenRead(path);
#else
            await using var stream = OpenRead(path);
#endif
            return await _opener.VerifyPasswordAsync(stream, password, token).ConfigureAwait(false);
        }, batchOptions, ct);

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess);
}
