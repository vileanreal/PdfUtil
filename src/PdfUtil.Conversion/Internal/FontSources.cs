namespace PdfUtil.Conversion.Internal;

/// <summary>Font files from folders and the operating system's font folders, for HTML and Word conversion.</summary>
internal static class FontSources
{
    public static IEnumerable<(string?, byte[], string)> FromDirectories(IEnumerable<string> directories, bool system, Action<string> warn) =>
        directories.Concat(system ? SystemFontDirectories() : Enumerable.Empty<string>()).SelectMany(d => FontFiles(d, warn));

    public static IEnumerable<string> SystemFontDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts";
            yield return "/Library/Fonts";
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Fonts");
        }
        else
        {
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fonts");
        }
    }

    public static IEnumerable<(string?, byte[], string)> FontFiles(string directory, Action<string> warn)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is not (".ttf" or ".ttc" or ".otf"))
            {
                continue;
            }

            if (++count > 5000)
            {
                warn($"Font folder {directory} has more than 5000 fonts; the rest were not read.");
                yield break;
            }

            var info = new FileInfo(file);
            if (info.Length > 64L * 1024 * 1024)
            {
                continue;
            }

            byte[]? data = null;
            try
            {
                data = File.ReadAllBytes(file);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            if (data is not null && !(data.Length > 4 && data[0] == 'O' && data[1] == 'T' && data[2] == 'T' && data[3] == 'O'))
            {
                yield return (null, data, Path.GetFileName(file));
            }
        }
    }
}
