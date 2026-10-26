using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PdfUtil.Extensions.DependencyInjection;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

public class DiTests
{
    [Fact]
    public async Task AddPdfUtil_RegistersSingletons_AndBridgesILogger()
    {
        var provider = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Debug));
        services.AddPdfUtil(o => o.MaxNestingDepth = 128);
        using var sp = services.BuildServiceProvider();

        var protector = sp.GetRequiredService<IPdfProtector>();
        Assert.Same(protector, sp.GetRequiredService<IPdfProtector>());
        Assert.Equal(128, sp.GetRequiredService<PdfUtilOptions>().MaxNestingDepth);

        var output = new MemoryStream();
        await protector.ProtectAsync(new MemoryStream(new TestPdfBuilder().Build()), output, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256, "dipass", "diowner"));
        var check = await sp.GetRequiredService<IPdfOpener>().VerifyPasswordAsync(new MemoryStream(output.ToArray()), "dipass");
        Assert.True(check.IsValid);
        var info = await sp.GetRequiredService<IPdfInspector>().GetInfoAsync(new MemoryStream(output.ToArray()), "diowner");
        Assert.Equal(2, info.PageCount);

        Assert.Contains(provider.Messages, m => m.Category == PdfUtilServiceCollectionExtensions.LoggerCategory);
        Assert.All(provider.Messages, m => Assert.DoesNotContain("dipass", m.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddPdfUtil_RegistersPhase2Services()
    {
        var services = new ServiceCollection();
        services.AddPdfUtil(o => o.MaxNestingDepth = 100);
        using var sp = services.BuildServiceProvider();
        var analyzer = sp.GetRequiredService<IPdfAnalyzer>();
        var cleaner = sp.GetRequiredService<IPdfCleaner>();
        var attachments = sp.GetRequiredService<IPdfAttachmentManager>();
        Assert.Same(sp.GetRequiredService<IPdfPageEditor>(), sp.GetRequiredService<IPdfPageEditor>());
        Assert.Same(sp.GetRequiredService<IPdfTextExtractor>(), sp.GetRequiredService<IPdfTextExtractor>());
        Assert.Same(sp.GetRequiredService<IPdfStamper>(), sp.GetRequiredService<IPdfStamper>());
        Assert.Same(analyzer, sp.GetRequiredService<IPdfAnalyzer>());
        Assert.Same(cleaner, sp.GetRequiredService<IPdfCleaner>());
        Assert.Same(attachments, sp.GetRequiredService<IPdfAttachmentManager>());

        var input = new TestPdfBuilder { Rich = true }.Build();
        Assert.True((await analyzer.ValidateAsync(new MemoryStream(input))).IsValid);
        Assert.NotEmpty((await analyzer.InspectAsync(new MemoryStream(input), new PdfInspectionOptions())).Fonts);
        var sanitized = new MemoryStream();
        await cleaner.SanitizeAsync(new MemoryStream(input), sanitized, new PdfSanitizeOptions());
        Assert.Equal(4, (await attachments.ListAttachmentsAsync(new MemoryStream(sanitized.ToArray()))).Count);
    }

    [Fact]
    public async Task Phase2Services_AreThreadSafe()
    {
        var analyzer = new PdfAnalyzer();
        var cleaner = new PdfCleaner();
        var input = new TestPdfBuilder { Rich = true, BreakXrefOffsets = true }.Build();
        var tasks = Enumerable.Range(0, 16).Select(async i =>
        {
            var output = new MemoryStream();
            var result = await cleaner.RepairAsync(new MemoryStream(input), output);
            var info = await analyzer.InspectAsync(new MemoryStream(output.ToArray()), new PdfInspectionOptions());
            return (result.WasDamaged, info.FormFields.Count);
        });
        Assert.All(await Task.WhenAll(tasks), r => Assert.Equal((true, 8), r));
    }

    [Fact]
    public async Task ServicesAreThreadSafe()
    {
        var protector = new PdfProtector();
        var input = new TestPdfBuilder().Build();
        var tasks = Enumerable.Range(0, 32).Select(async i =>
        {
            var output = new MemoryStream();
            var algorithm = TestHelpers.WritableAlgorithms[i % 3];
            await protector.ProtectAsync(new MemoryStream(input), output, TestHelpers.Options(algorithm, "u" + i, "o" + i));
            Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output.ToArray(), "u" + i).MatchedAs);
        });
        await Task.WhenAll(tasks);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(string Category, string Message)> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose() { }

        private sealed class Logger : ILogger
        {
            private readonly CapturingLoggerProvider _owner;
            private readonly string _category;

            public Logger(CapturingLoggerProvider owner, string category)
            {
                _owner = owner;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (_owner.Messages)
                {
                    _owner.Messages.Add((_category, formatter(state, exception)));
                }
            }
        }
    }
}
