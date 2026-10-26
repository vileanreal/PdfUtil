using PdfUtil.Internal;
using PdfUtil.Internal.IO;
using PdfUtil.Internal.Parsing;

namespace PdfUtil.Conversion.Internal;

/// <summary>
/// File output for conversions that produce a main file and optionally a folder of assets. Nothing is written until the
/// conversion (and its validation) has succeeded; the main file is replaced atomically, and an existing assets folder
/// is swapped out and restored if anything fails.
/// </summary>
internal static class ConversionOutput
{
    public static async Task<T> ToFilesAsync<T>(string inputPath, string outputPath, string? assetsFolder, PdfUtilOptions options, PdfLog log, string description, Func<PdfSource, Dictionary<string, byte[]>?, (byte[] Main, T Result)> work, CancellationToken ct)
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
        byte[] main;
        T result;
        var assets = assetsFolder is null ? null : new Dictionary<string, byte[]>(StringComparer.Ordinal);
#if NETFRAMEWORK
        using (var input = new FileStream(fullInput, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess))
        using (var source = new PdfSource(input, ownsStream: false))
#else
        await using (var input = new FileStream(fullInput, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess))
        using (var source = new PdfSource(input, ownsStream: false))
#endif
        {
            if (source.Length > options.MaxFileSizeBytes)
            {
                throw new PdfLimitExceededException($"The input is larger than the configured maximum of {options.MaxFileSizeBytes} bytes.");
            }

            log.Info(description.Replace("{size}", source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
            (main, result) = Guard.Run(() => work(source, assets));
        }

        ct.ThrowIfCancellationRequested();
        if (assetsFolder is null || assets is null)
        {
            Commit(main, fullOutput);
            return result;
        }

        var finalFolder = Path.Combine(directory, assetsFolder);
        var tempFolder = Path.Combine(directory, "." + assetsFolder + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var oldFolder = Path.Combine(directory, "." + assetsFolder + "." + Guid.NewGuid().ToString("N") + ".old");
        var movedOld = false;
        var movedNew = false;
        try
        {
            Directory.CreateDirectory(tempFolder);
            foreach (var (name, data) in assets)
            {
                File.WriteAllBytes(Path.Combine(tempFolder, name), data);
            }

            if (Directory.Exists(finalFolder))
            {
                Directory.Move(finalFolder, oldFolder);
                movedOld = true;
            }

            Directory.Move(tempFolder, finalFolder);
            movedNew = true;
            Commit(main, fullOutput);
        }
        catch
        {
            TryDeleteFolder(movedNew ? finalFolder : tempFolder);
            if (movedOld)
            {
                try
                {
                    Directory.Move(oldFolder, finalFolder);
                }
                catch (IOException)
                {
                    // Best effort: the previous folder stays under its temporary name.
                }
            }

            throw;
        }

        if (movedOld)
        {
            TryDeleteFolder(oldFolder);
        }

        return result;
    }

    private static void Commit(byte[] data, string fullOutput)
    {
        using var buffer = new MemoryStream(data, writable: false);
        OutputRunner.CommitFile(buffer, fullOutput);
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
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
