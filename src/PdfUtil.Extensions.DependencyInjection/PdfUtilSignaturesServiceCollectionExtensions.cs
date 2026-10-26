using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PdfUtil.Signatures;

namespace PdfUtil.Extensions.DependencyInjection;

/// <summary>Registers the PdfUtil.Signatures services with <see cref="IServiceCollection"/>.</summary>
public static class PdfUtilSignaturesServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IPdfSigner"/> and <see cref="IPdfCertificateSecurity"/> as singletons, using the same
    /// <see cref="PdfUtilOptions"/> as <see cref="PdfUtilServiceCollectionExtensions.AddPdfUtil"/> (which is called if needed).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration of limits and buffering, applied when PdfUtil is not registered yet.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddPdfUtilSignatures(this IServiceCollection services, Action<PdfUtilOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddPdfUtil(configure);
        services.TryAddSingleton<IPdfSigner>(sp => new PdfSigner(sp.GetRequiredService<PdfUtilOptions>()));
        services.TryAddSingleton<IPdfCertificateSecurity>(sp => new PdfCertificateSecurity(sp.GetRequiredService<PdfUtilOptions>()));
        return services;
    }
}
