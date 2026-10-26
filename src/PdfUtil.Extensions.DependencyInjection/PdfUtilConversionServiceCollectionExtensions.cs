using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PdfUtil.Conversion;

namespace PdfUtil.Extensions.DependencyInjection;

/// <summary>Registers the PdfUtil.Conversion services with <see cref="IServiceCollection"/>.</summary>
public static class PdfUtilConversionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IPdfConverter"/> as a singleton, using the same <see cref="PdfUtilOptions"/> as
    /// <see cref="PdfUtilServiceCollectionExtensions.AddPdfUtil"/> (which is called if needed).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureLimits">Optional configuration of the conversion limits.</param>
    /// <param name="configure">Optional configuration of limits and buffering, applied when PdfUtil is not registered yet.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddPdfUtilConversion(this IServiceCollection services, Action<PdfConversionLimits>? configureLimits = null, Action<PdfUtilOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddPdfUtil(configure);
        var limits = new PdfConversionLimits();
        configureLimits?.Invoke(limits);
        services.TryAddSingleton<IPdfConverter>(sp => new PdfConverter(sp.GetRequiredService<PdfUtilOptions>(), limits));
        return services;
    }
}
