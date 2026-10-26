namespace PdfUtil.Internal;

/// <summary>Internal logging facade over the optional <see cref="PdfUtilOptions.Logger"/> callback. Never logs secrets.</summary>
internal sealed class PdfLog
{
    private readonly Action<PdfLogLevel, string>? _sink;

    public PdfLog(Action<PdfLogLevel, string>? sink) => _sink = sink;

    public static PdfLog None { get; } = new(null);

    public void Debug(string message) => Write(PdfLogLevel.Debug, message);

    public void Info(string message) => Write(PdfLogLevel.Information, message);

    public void Warn(string message) => Write(PdfLogLevel.Warning, message);

    public void Error(string message) => Write(PdfLogLevel.Error, message);

    private void Write(PdfLogLevel level, string message)
    {
        if (_sink is null)
        {
            return;
        }

        try
        {
            _sink(level, message);
        }
        catch (Exception)
        {
            // A failing log sink must never break document processing.
        }
    }
}
