using System.Buffers;
using PdfUtil.Internal.Parsing;

namespace PdfUtil.Internal.IO;

/// <summary>Turns caller streams into seekable sources, and provides verified-before-commit output buffers.</summary>
internal static class InputBuffer
{
    /// <summary>
    /// Returns a <see cref="PdfSource"/> for the input. Seekable streams are used in place (from their current
    /// position); others are copied to memory or a temporary file, enforcing the maximum file size.
    /// </summary>
    public static async Task<PdfSource> OpenAsync(Stream input, PdfUtilOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("The input stream is not readable.", nameof(input));
        }

        if (input.CanSeek)
        {
            if (input.Length - input.Position > options.MaxFileSizeBytes)
            {
                throw new PdfLimitExceededException($"The input is larger than the configured maximum of {options.MaxFileSizeBytes} bytes.");
            }

            return new PdfSource(input, ownsStream: false);
        }

        var buffer = new MemoryStream();
        Stream target = buffer;
        var rented = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long total = 0;
            while (true)
            {
                var n = await input.ReadAsync(rented.AsMemory(0, rented.Length), ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    break;
                }

                total += n;
                if (total > options.MaxFileSizeBytes)
                {
                    throw new PdfLimitExceededException($"The input is larger than the configured maximum of {options.MaxFileSizeBytes} bytes.");
                }

                if (ReferenceEquals(target, buffer) && total > options.InMemoryBufferThresholdBytes)
                {
                    // Switch to a temporary file for large inputs.
                    var file = CreateTempFile(options);
                    buffer.Position = 0;
                    await buffer.CopyToAsync(file, ct).ConfigureAwait(false);
                    await buffer.DisposeAsync().ConfigureAwait(false);
                    target = file;
                }

                await target.WriteAsync(rented.AsMemory(0, n), ct).ConfigureAwait(false);
            }

            await target.FlushAsync(ct).ConfigureAwait(false);
            target.Position = 0;
            return new PdfSource(target, ownsStream: true);
        }
        catch
        {
            await target.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>An empty, seekable buffer for output that is verified before being committed.</summary>
    public static Stream CreateOutputBuffer(long expectedSize, PdfUtilOptions options) =>
        expectedSize <= options.InMemoryBufferThresholdBytes ? new MemoryStream() : CreateTempFile(options);

    public static FileStream CreateTempFile(PdfUtilOptions options)
    {
        var dir = options.TempDirectory ?? Path.GetTempPath();
        var path = Path.Combine(dir, "pdfutil-" + Guid.NewGuid().ToString("N") + ".tmp");
        return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
    }
}
