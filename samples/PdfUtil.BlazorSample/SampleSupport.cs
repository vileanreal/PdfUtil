namespace PdfUtil.BlazorSample;

/// <summary>Limits used by the sample.</summary>
public static class SampleLimits
{
    /// <summary>Largest PDF the sample accepts for upload (50 MB).</summary>
    public const long MaxUploadBytes = 50L * 1024 * 1024;
}

/// <summary>A file (usually a PDF) uploaded by the user, held in memory for the current session only.</summary>
/// <param name="Name">Original file name.</param>
/// <param name="Bytes">File contents.</param>
public sealed record UploadedPdf(string Name, byte[] Bytes)
{
    /// <summary>A fresh read-only stream over the contents.</summary>
    public MemoryStream OpenRead() => new(Bytes, writable: false);
}

/// <summary>Turns library exceptions into messages for end users.</summary>
public static class ErrorMessages
{
    /// <summary>Describes a failure. Library messages never contain passwords.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        PdfComplianceException => ex.Message + " Tick “Encrypt PDF/A files anyway” to continue.",
        PdfAlreadyEncryptedException => "This PDF is already password protected. Tick “Re-encrypt” and enter its current owner password, or use Change security.",
        PdfNotEncryptedException => "This PDF is not password protected. Use the Protect page to protect it.",
        PdfInvalidPasswordException => ex.Message,
        PdfVerificationException => "The result failed its safety check, so it was not produced. " + ex.Message,
        PdfCorruptException => "The file is not a PDF or is too damaged to read. " + ex.Message,
        PdfUtilException or ArgumentException => ex.Message,
        IOException => "The file could not be read: " + ex.Message,
        _ => "Something went wrong: " + ex.Message,
    };

    /// <summary>Whether <see cref="Describe"/> should be shown instead of letting the exception surface.</summary>
    public static bool IsExpected(Exception ex) => ex is PdfUtilException or ArgumentException or IOException;
}

/// <summary>
/// Base for pages that run one library operation at a time: busy state, cancellation, progress and friendly errors.
/// </summary>
public abstract class OperationPage : Microsoft.AspNetCore.Components.ComponentBase, IDisposable
{
    private CancellationTokenSource? _cts;

    /// <summary>Whether an operation is running.</summary>
    protected bool Busy { get; private set; }

    /// <summary>The last error message, if any.</summary>
    protected string? Error { get; set; }

    /// <summary>The latest progress report of the running operation.</summary>
    protected PdfProgress? CurrentProgress { get; private set; }

    /// <summary>
    /// Runs <paramref name="work"/> with a cancellation token and a progress sink. The library does its parsing and
    /// encryption on the calling thread, so the work should use Task.Run to keep the UI responsive.
    /// </summary>
    protected async Task RunAsync(Func<CancellationToken, IProgress<PdfProgress>, Task> work)
    {
        Busy = true;
        Error = null;
        CurrentProgress = null;
        _cts = new CancellationTokenSource();

        // Progress<T> posts reports back to the page's synchronization context.
        var progress = new Progress<PdfProgress>(p =>
        {
            if (Busy)
            {
                CurrentProgress = p;
                StateHasChanged();
            }
        });
        try
        {
            await work(_cts.Token, progress);
        }
        catch (OperationCanceledException)
        {
            Error = "Cancelled.";
        }
        catch (Exception ex) when (ErrorMessages.IsExpected(ex))
        {
            Error = ErrorMessages.Describe(ex);
        }
        finally
        {
            Busy = false;
            CurrentProgress = null;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Runs a stream-to-stream operation on a background thread and returns its result and the bytes it wrote.
    /// </summary>
    protected static Task<(T Result, byte[] Output)> Produce<T>(UploadedPdf input, Func<Stream, Stream, Task<T>> operation) => Task.Run(async () =>
    {
        using var source = input.OpenRead();
        using var buffer = new MemoryStream();
        var result = await operation(source, buffer);
        return (result, buffer.ToArray());
    });

    /// <summary>A password from an input box: null when empty.</summary>
    protected static string? Optional(string? password) => string.IsNullOrEmpty(password) ? null : password;

    /// <summary>A file name based on the input's, with a suffix and extension.</summary>
    protected static string OutputName(UploadedPdf input, string suffix, string extension = ".pdf") => Path.GetFileNameWithoutExtension(input.Name) + suffix + extension;

    /// <summary>Requests cancellation of the running operation.</summary>
    protected void Cancel() => _cts?.Cancel();

    /// <inheritdoc />
    public void Dispose()
    {
        _cts?.Cancel();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Content the sample pre-fills.</summary>
public static class SampleContent
{
    /// <summary>HTML for the HTML to PDF converter.</summary>
    public const string Html = """
        <html lang="en">
        <head><title>Sample</title>
        <style>
          body { font-family: Helvetica, sans-serif }
          h1 { color: #1a4d8f; border-bottom: 2px solid #1a4d8f }
          table { border-collapse: collapse; width: 100% }
          td, th { border: 1px solid #999; padding: 4pt }
        </style></head>
        <body>
          <h1>Quarterly report</h1>
          <p>Sales grew in <b>every</b> region. See <a href="https://example.com/">the website</a>.</p>
          <ul><li>North: +12%</li><li>South: +8%</li></ul>
          <table><tr><th>Region</th><th>Sales</th></tr><tr><td>North</td><td>1,200</td></tr></table>
        </body></html>
        """;
}
