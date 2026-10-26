using Microsoft.Extensions.DependencyInjection;
using PdfUtil.Extensions.DependencyInjection;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>F4 batch processing and F5 progress reporting.</summary>
public class BatchAndProgressTests
{
    /// <summary>Records reports synchronously (unlike <see cref="Progress{T}"/>, which posts to a context).</summary>
    private sealed class Recorder<T> : IProgress<T>
    {
        public List<T> Reports { get; } = new();

        public void Report(T value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }
        }
    }

    private static string NewDirectory() => Directory.CreateTempSubdirectory("pdfutil-batch-").FullName;

    [Fact]
    public async Task ProtectFiles_OneBadFileDoesNotStopTheBatch()
    {
        var dir = NewDirectory();
        try
        {
            var jobs = new List<PdfFileJob>();
            for (var i = 0; i < 6; i++)
            {
                var input = Path.Combine(dir, $"in{i}.pdf");
                await File.WriteAllBytesAsync(input, i == 3 ? "not a pdf"u8.ToArray() : new TestPdfBuilder { PageCount = 1 + i }.Build());
                jobs.Add(new PdfFileJob(input, Path.Combine(dir, $"out{i}.pdf")));
            }

            var progress = new Recorder<PdfBatchProgress>();
            var result = await new PdfBatchProcessor().ProtectFilesAsync(jobs, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256), new PdfBatchOptions { MaxDegreeOfParallelism = 3, Progress = progress });

            Assert.Equal(6, result.Items.Count);
            Assert.Equal(5, result.Succeeded);
            Assert.Equal(1, result.Failed);
            Assert.False(result.AllSucceeded);
            Assert.Equal(Enumerable.Range(0, 6), result.Items.Select(i => i.Index));
            Assert.Equal(PdfBatchItemStatus.Failed, result.Items[3].Status);
            Assert.IsAssignableFrom<PdfCorruptException>(result.Items[3].Error);
            Assert.False(File.Exists(jobs[3].OutputPath));
            foreach (var item in result.Items.Where(i => i.Status == PdfBatchItemStatus.Succeeded))
            {
                Assert.True(Pdf.VerifyPassword(item.Item.OutputPath, "user").IsValid);
                Assert.Equal(item.Index + 1, Pdf.GetInfo(item.Item.OutputPath, "user").PageCount);
                Assert.True(item.Duration > TimeSpan.Zero);
            }

            Assert.Equal(6, progress.Reports.Count);
            Assert.Equal(new PdfBatchProgress(6, 6, 5, 1), progress.Reports.MaxBy(r => r.Completed));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task StopOnFirstError_SkipsTheRest()
    {
        var items = Enumerable.Range(0, 10).ToList();
        var result = await new PdfBatchProcessor().RunAsync(items, (i, _) => i == 0 ? throw new PdfCorruptException("bad") : Task.FromResult(i), new PdfBatchOptions { MaxDegreeOfParallelism = 1, StopOnFirstError = true });
        Assert.Equal(PdfBatchItemStatus.Failed, result.Items[0].Status);
        Assert.Equal(9, result.Skipped);
        Assert.Equal(0, result.Succeeded);
    }

    [Fact]
    public async Task RunAsync_IsParallel_AndOrdered()
    {
        var running = 0;
        var maxRunning = 0;
        var result = await new PdfBatchProcessor().RunAsync(Enumerable.Range(0, 20), async (i, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            await Task.Delay(20, ct);
            Interlocked.Decrement(ref running);
            return i * 2;
        }, new PdfBatchOptions { MaxDegreeOfParallelism = 4 });

        Assert.True(result.AllSucceeded);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => i * 2), result.Items.Select(i => i.Result));
        Assert.InRange(maxRunning, 2, 4);

        static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }

    [Fact]
    public async Task Cancellation_StopsTheBatch()
    {
        using var cts = new CancellationTokenSource();
        var task = new PdfBatchProcessor().RunAsync(Enumerable.Range(0, 100), async (i, ct) =>
        {
            if (i == 5)
            {
                cts.Cancel();
            }

            await Task.Delay(10, ct);
            return i;
        }, new PdfBatchOptions { MaxDegreeOfParallelism = 2 }, cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task DecryptInspectAndVerifyFiles()
    {
        var dir = NewDirectory();
        try
        {
            var jobs = new List<PdfFileJob>();
            for (var i = 0; i < 4; i++)
            {
                var path = Path.Combine(dir, $"enc{i}.pdf");
                await File.WriteAllBytesAsync(path, DecryptTests.Encrypted(i % 2 == 0 ? 6 : 4));
                jobs.Add(new PdfFileJob(path, Path.Combine(dir, $"dec{i}.pdf")));
            }

            var batch = new PdfBatchProcessor();
            var verify = await batch.VerifyPasswordFilesAsync(jobs.Select(j => j.InputPath), "u-pass");
            Assert.All(verify.Items, i => Assert.Equal(PdfPasswordMatch.User, i.Result!.MatchedAs));

            var decrypted = await batch.DecryptFilesAsync(jobs, new PdfDecryptOptions { Password = "o-pass" });
            Assert.True(decrypted.AllSucceeded);

            var inspected = await batch.InspectFilesAsync(jobs.Select(j => j.OutputPath));
            Assert.All(inspected.Items, i =>
            {
                Assert.False(i.Result!.IsEncrypted);
                Assert.Equal(2, i.Result.PageCount);
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Di_RegistersEditorAndBatchProcessor()
    {
        var services = new ServiceCollection();
        services.AddPdfUtil();
        using var sp = services.BuildServiceProvider();
        var editor = sp.GetRequiredService<IPdfEditor>();
        Assert.Same(editor, sp.GetRequiredService<IPdfEditor>());
        var batch = sp.GetRequiredService<IPdfBatchProcessor>();
        var output = new MemoryStream();
        await editor.DecryptAsync(new MemoryStream(DecryptTests.Encrypted(4)), output, new PdfDecryptOptions { Password = "o-pass" });
        Assert.False(Pdf.IsEncrypted(output.ToArray()));
        var result = await batch.RunAsync(new[] { 1, 2 }, (i, _) => Task.FromResult(i));
        Assert.True(result.AllSucceeded);
    }

    [Fact]
    public void Progress_ReportsEveryStageInOrder()
    {
        var progress = new Recorder<PdfProgress>();
        var options = TestHelpers.Options(PdfEncryptionAlgorithm.Aes256);
        options.Progress = progress;
        Pdf.Protect(new TestPdfBuilder { PageCount = 40 }.Build(), options);

        var stages = progress.Reports.Select(r => r.Stage).Distinct().ToList();
        Assert.Equal(new[] { PdfProgressStage.Reading, PdfProgressStage.Writing, PdfProgressStage.Verifying }, stages);
        foreach (var stage in stages)
        {
            var reports = progress.Reports.Where(r => r.Stage == stage).ToList();
            Assert.Equal(0, reports[0].Completed);
            Assert.Equal(reports[^1].Total, reports[^1].Completed);
            Assert.Equal(1.0, reports[^1].Fraction);
            Assert.True(reports.Zip(reports.Skip(1)).All(p => p.Second.Completed >= p.First.Completed));
        }

        // Throttled: far fewer reports than objects written (40 pages are well over 100 objects).
        Assert.True(progress.Reports.Count < 20, $"{progress.Reports.Count} reports");
    }

    [Fact]
    public async Task Progress_ForEditOperations()
    {
        var decryptProgress = new Recorder<PdfProgress>();
        Pdf.Decrypt(DecryptTests.Encrypted(6), new PdfDecryptOptions { Password = "o-pass", Progress = decryptProgress }, out _);
        Assert.Contains(decryptProgress.Reports, r => r.Stage == PdfProgressStage.Verifying && r.Fraction == 1.0);

        var infoProgress = new Recorder<PdfProgress>();
        await new PdfEditor().EditInfoAsync(new MemoryStream(new TestPdfBuilder().Build()), new MemoryStream(), new PdfEditInfoOptions { Changes = new PdfInfoChanges { Title = "t" }, Progress = infoProgress });
        Assert.Contains(infoProgress.Reports, r => r.Stage == PdfProgressStage.Writing);
    }

    [Fact]
    public void ThrowingProgressCallback_DoesNotBreakTheOperation()
    {
        var options = TestHelpers.Options(PdfEncryptionAlgorithm.Aes128);
        options.Progress = new ThrowingProgress();
        var (output, _) = TestHelpers.Protect(new TestPdfBuilder().Build(), options);
        Assert.True(Pdf.VerifyPassword(output, "user").IsValid);
    }

    private sealed class ThrowingProgress : IProgress<PdfProgress>
    {
        public void Report(PdfProgress value) => throw new InvalidOperationException("callback failure");
    }
}
