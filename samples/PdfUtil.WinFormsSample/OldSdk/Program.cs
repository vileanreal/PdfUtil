using System.Windows.Forms;

namespace PdfUtil.WinFormsSample;

// Built instead of the sample when the SDK is older than .NET 10 (MSBuild 18): the PdfUtil libraries then have no
// .NET Framework build for the sample to use.
internal static class Program
{
    [STAThread]
    private static void Main() =>
        MessageBox.Show(
            "This sample tests the .NET Framework build of PdfUtil, which needs the .NET 10 SDK (Visual Studio 2026 or later). Rebuild with it to get the real app.",
            "PdfUtil sample");
}
