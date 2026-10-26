using PdfUtil.Internal.Parsing;

namespace PdfUtil.Internal.IO;

/// <summary>
/// Runs a PDF-writing operation with the library's output guarantees: the work writes to a private buffer, verifies it,
/// and only then is the result copied to the caller's stream, or moved atomically over the destination file.
/// </summary>
internal static class OutputRunner
{
    /// <summary>Stream to stream. <paramref name="description"/> is logged, with <c>{size}</c> replaced by the input size. The output stream receives nothing unless <paramref name="work"/> succeeds.</summary>
    public static async Task<T> ToStreamAsync<T>(Stream input, Stream output, PdfUtilOptions options, PdfLog log, string description, Func<PdfSource, Stream, T> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream is not writable.", nameof(output));
        }

        using var source = await InputBuffer.OpenAsync(input, options, ct).ConfigureAwait(false);
#if NETFRAMEWORK
        using var buffer = InputBuffer.CreateOutputBuffer(source.Length, options);
#else
        await using var buffer = InputBuffer.CreateOutputBuffer(source.Length, options);
#endif
        log.Info(description.Replace("{size}", source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        var result = Guard.Run(() => work(source, buffer));

        // Only a verified result reaches the caller's stream.
        buffer.Position = 0;
        await buffer.CopyToAsync(output, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// File to file through a temporary file next to the destination and an atomic move, so a failure never leaves a
    /// partial file. Input and output may be the same file.
    /// </summary>
    public static async Task<T> ToFileAsync<T>(string inputPath, string outputPath, PdfUtilOptions options, PdfLog log, string description, Func<PdfSource, Stream, T> work, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        var fullInput = Path.GetFullPath(inputPath);
        var fullOutput = Path.GetFullPath(outputPath);
        if (!File.Exists(fullInput))
        {
            throw new FileNotFoundException("The input file does not exist.", fullInput);
        }

        var directory = Path.GetDirectoryName(fullOutput) ?? throw new ArgumentException("The output path has no directory.", nameof(outputPath));
        var tempPath = Path.Combine(directory, "." + Path.GetFileName(fullOutput) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            T result;
#if NETFRAMEWORK
            using (var input = new FileStream(fullInput, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess))
            using (var source = new PdfSource(input, ownsStream: false))
            using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
#else
            await using (var input = new FileStream(fullInput, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess))
            using (var source = new PdfSource(input, ownsStream: false))
            await using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
#endif
            {
                if (source.Length > options.MaxFileSizeBytes)
                {
                    throw new PdfLimitExceededException($"The input is larger than the configured maximum of {options.MaxFileSizeBytes} bytes.");
                }

                log.Info(description.Replace("{size}", source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
                result = Guard.Run(() => work(source, temp));
                await temp.FlushAsync(ct).ConfigureAwait(false);
                temp.Flush(flushToDisk: true);
            }

            // Atomic replace: the destination is either untouched or the complete, verified file.
            File.Move(tempPath, fullOutput, overwrite: true);
            return result;
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {
                // Best effort.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort.
            }

            throw;
        }
    }
    /// <summary>Writes the result of <paramref name="work"/> (which needs no single input, such as a merge) to <paramref name="output"/> once it has succeeded.</summary>
    public static async Task<T> WriteAsync<T>(Stream output, PdfUtilOptions options, Func<Stream, T> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream is not writable.", nameof(output));
        }

#if NETFRAMEWORK
        using var buffer = InputBuffer.CreateOutputBuffer(0, options);
#else
        await using var buffer = InputBuffer.CreateOutputBuffer(0, options);
#endif
        var result = Guard.Run(() => work(buffer));
        buffer.Position = 0;
        await buffer.CopyToAsync(output, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Writes the result of <paramref name="work"/> to a temporary file next to <paramref name="outputPath"/>, then moves it into place atomically.</summary>
    public static Task<T> WriteFileAsync<T>(string outputPath, Func<Stream, T> work, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        var fullOutput = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullOutput) ?? throw new ArgumentException("The output path has no directory.", nameof(outputPath));
        var tempPath = Path.Combine(directory, "." + Path.GetFileName(fullOutput) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            T result;
            using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920))
            {
                result = Guard.Run(() => work(temp));
                ct.ThrowIfCancellationRequested();
                temp.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullOutput, overwrite: true);
            return Task.FromResult(result);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>Copies a finished buffer to <paramref name="outputPath"/> through a temporary file and an atomic move.</summary>
    public static void CommitFile(Stream buffer, string outputPath)
    {
        var fullOutput = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullOutput) ?? throw new ArgumentException("The output path has no directory.", nameof(outputPath));
        var tempPath = Path.Combine(directory, "." + Path.GetFileName(fullOutput) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
            {
                buffer.Position = 0;
                buffer.CopyTo(temp);
                temp.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullOutput, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}

/// <summary>Wraps a caller's stream so that disposing the wrapper leaves the caller's stream open.</summary>
internal sealed class NonDisposingStream : Stream
{
    private readonly Stream _inner;

    public NonDisposingStream(Stream inner) => _inner = inner;

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);

#if !NETFRAMEWORK
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
#endif

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
