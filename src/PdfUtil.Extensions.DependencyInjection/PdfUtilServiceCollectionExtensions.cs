using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace PdfUtil.Extensions.DependencyInjection;

/// <summary>Registers PdfUtil services with <see cref="IServiceCollection"/>.</summary>
public static class PdfUtilServiceCollectionExtensions
{
    /// <summary>The <see cref="ILogger"/> category used for PdfUtil log messages.</summary>
    public const string LoggerCategory = "PdfUtil";

    /// <summary>
    /// Registers <see cref="IPdfProtector"/>, <see cref="IPdfEditor"/>, <see cref="IPdfOpener"/>, <see cref="IPdfInspector"/>,
    /// <see cref="IPdfAnalyzer"/>, <see cref="IPdfCleaner"/>, <see cref="IPdfAttachmentManager"/>, <see cref="IPdfPageEditor"/>, <see cref="IPdfTextExtractor"/>, <see cref="IPdfStamper"/> and
    /// <see cref="IPdfBatchProcessor"/> as singletons.
    /// If an <see cref="ILoggerFactory"/> is registered, PdfUtil's log callback is bridged to an <see cref="ILogger"/>
    /// with category <see cref="LoggerCategory"/> (unless <paramref name="configure"/> sets its own callback).
    /// Log messages never contain passwords, keys or document content.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration of limits and buffering.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddPdfUtil(this IServiceCollection services, Action<PdfUtilOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(sp =>
        {
            var options = new PdfUtilOptions();
            configure?.Invoke(options);
            if (options.Logger is null && sp.GetService<ILoggerFactory>() is { } factory)
            {
                options.Logger = CreateBridge(factory.CreateLogger(LoggerCategory));
            }

            return options;
        });
        services.TryAddSingleton<IPdfProtector>(sp => new PdfProtector(sp.GetRequiredService<PdfUtilOptions>()));
        services.TryAddSingleton<IPdfOpener>(sp => new PdfOpener(sp.GetRequiredService<PdfUtilOptions>()));
        services.TryAddSingleton<IPdfInspector>(sp => new PdfInspector(sp.GetRequiredService<PdfUtilOptions>()));
        services.TryAddSingleton<IPdfEditor>(sp => new PdfEditor(sp.GetRequiredService<PdfUtilOptions>()));
        services.TryAddSingleton<IPdfAnalyzer>(sp => new PdfAnalyzer(sp.GetRequiredService<PdfUtilOptions>()));
        services.TryAddSingleton<IPdfCleaner>(sp => new PdfCleaner(sp.GetRequiredService<PdfUtilOptions>()));
        services.TryAddSingleton<IPdfAttachmentManager>(sp => new PdfAttachmentManager(sp.GetRequiredService<PdfUtilOptions>()));
        services.TryAddSingleton<IPdfPageEditor>(sp => new PdfPageEditor(sp.GetRequiredService<PdfUtilOptions>()));
        services.TryAddSingleton<IPdfTextExtractor>(sp => new PdfTextExtractor(sp.GetRequiredService<PdfUtilOptions>()));
        services.TryAddSingleton<IPdfStamper>(sp => new PdfStamper(sp.GetRequiredService<PdfUtilOptions>()));
        services.TryAddSingleton<IPdfPipelineFactory>(sp => new PdfPipelineFactory(sp.GetRequiredService<PdfUtilOptions>()));
        services.TryAddSingleton<IPdfBatchProcessor>(sp => new PdfBatchProcessor(
            sp.GetRequiredService<IPdfProtector>(),
            sp.GetRequiredService<IPdfEditor>(),
            sp.GetRequiredService<IPdfInspector>(),
            sp.GetRequiredService<IPdfOpener>()));
        return services;
    }

    /// <summary>Creates a PdfUtil log callback that writes to <paramref name="logger"/>.</summary>
    public static Action<PdfLogLevel, string> CreateBridge(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return (level, message) =>
        {
            var mapped = level switch
            {
                PdfLogLevel.Debug => LogLevel.Debug,
                PdfLogLevel.Information => LogLevel.Information,
                PdfLogLevel.Warning => LogLevel.Warning,
                _ => LogLevel.Error,
            };
            if (logger.IsEnabled(mapped))
            {
#pragma warning disable CA2254 // The message is a fixed, password-free text produced by PdfUtil.
                logger.Log(mapped, message);
#pragma warning restore CA2254
            }
        };
    }
}
