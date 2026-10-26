namespace PdfUtil.Signatures;

/// <summary>Signature steps for <see cref="PdfPipeline"/>.</summary>
public static class PdfPipelineSignatures
{
    /// <summary>
    /// Adds a signing step. Make it the last step: any later change rewrites the document and invalidates the
    /// signature. Only unencrypted documents can be signed, so it cannot follow <c>Protect</c>.
    /// </summary>
    public static PdfPipeline Sign(this PdfPipeline pipeline, PdfSignOptions options)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(options);
        var signer = new PdfSigner(pipeline.Options);
        return pipeline.Then("Sign", async (input, output, _, ct) => await signer.SignAsync(input, output, options, ct).ConfigureAwait(false));
    }
}
