using PdfUtil.Cli;

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

try
{
    return await CliApp.RunAsync(args, CliConsole.System(), cancel.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("pdfutil: cancelled.");
    return 130;
}
