using System.Diagnostics;

namespace PdfUtil.Internal;

/// <summary>
/// Throttles progress reports for one operation: at most one report about every 100 ms, plus the first and last
/// report of each stage. Not thread-safe; one instance per operation.
/// </summary>
internal sealed class ProgressReporter
{
    private static readonly long IntervalTicks = Stopwatch.Frequency / 10;

    private readonly IProgress<PdfProgress>? _progress;
    private long _lastReport;
    private PdfProgressStage? _lastStage;

    public ProgressReporter(IProgress<PdfProgress>? progress) => _progress = progress;

    public static ProgressReporter None { get; } = new(null);

    /// <summary>Reports progress; <paramref name="force"/> bypasses throttling (use for stage start and end).</summary>
    public void Report(PdfProgressStage stage, long completed, long total, bool force = false)
    {
        if (_progress is null)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (!force && stage == _lastStage && now - _lastReport < IntervalTicks)
        {
            return;
        }

        _lastReport = now;
        _lastStage = stage;
        try
        {
            _progress.Report(new PdfProgress(stage, Math.Min(completed, total), total));
        }
        catch (Exception)
        {
            // A failing progress callback must never break document processing.
        }
    }
}
