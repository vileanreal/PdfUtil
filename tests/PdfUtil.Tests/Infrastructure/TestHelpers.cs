using System.Diagnostics;
using PdfUtil.Internal;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;

namespace PdfUtil.Tests.Infrastructure;

internal static class TestHelpers
{
    public static (byte[] Output, PdfProtectionResult Result) Protect(byte[] input, PdfProtectionOptions options)
    {
        var output = Pdf.Protect(input, options, out var result);
        return (output, result);
    }

    public static PdfProtectionOptions Options(PdfEncryptionAlgorithm algorithm, string? user = "user", string? owner = "owner") => new()
    {
        Algorithm = algorithm,
        UserPassword = user,
        OwnerPassword = owner,
        Permissions = PdfPermissions.Print | PdfPermissions.CopyContent,
    };

    /// <summary>Opens bytes with the internal reader (strict or lenient) for structural assertions.</summary>
    public static PdfDocument Open(byte[] pdf, bool strict = false) =>
        PdfDocument.Open(PdfSource.FromBytes(pdf), new PdfUtilOptions(), PdfLog.None, strict, CancellationToken.None);

    public static PdfDictionary EncryptDictionary(byte[] pdf)
    {
        using var doc = Open(pdf, strict: true);
        return doc.GetEncryptDictionary()!;
    }

    public static string HeaderOf(byte[] pdf) => System.Text.Encoding.ASCII.GetString(pdf, 0, 8);

    public static readonly PdfEncryptionAlgorithm[] WritableAlgorithms =
    {
        PdfEncryptionAlgorithm.Aes256, PdfEncryptionAlgorithm.Aes128, PdfEncryptionAlgorithm.Rc4_128,
    };

    public static string? FindTool(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator).Concat(new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" }))
        {
            foreach (var candidate in new[] { name, name + ".exe" })
            {
                var full = Path.Combine(dir, candidate);
                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }

    public static (int ExitCode, string Output) Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
#if NETFRAMEWORK
        psi.Arguments = string.Join(" ", args.Select(a => "\"" + a.Replace("\"", "\\\"") + "\""));
#else
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
#endif

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000))
        {
#if NETFRAMEWORK
            p.Kill();
#else
            p.Kill(entireProcessTree: true);
#endif
            return (-1, "timeout");
        }

        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}

/// <summary>A fact that is skipped unless an external command-line tool is installed.</summary>
public sealed class RequiresToolFactAttribute : FactAttribute
{
    public RequiresToolFactAttribute(string tool)
    {
        if (TestHelpers.FindTool(tool) is null)
        {
            Skip = $"'{tool}' is not installed; interop test skipped.";
        }
    }
}
