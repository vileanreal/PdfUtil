using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PdfUtil.WinFormsSample;

// PdfUtil Windows Forms sample (.NET Framework 4.8).
//
//   PdfUtil.WinFormsSample.exe                         opens the window
//   PdfUtil.WinFormsSample.exe --selftest [report.txt] runs every check without a window, writes the report
//                                                      (default: selftest-report.txt) and exits with 0 if all passed
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        {
            return RunHeadless(args.Length > 1 ? args[1] : "selftest-report.txt");
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
    }

    private static int RunHeadless(string reportPath)
    {
        // A WinExe has no console; borrow the one it was started from, if any, so the report is also printed.
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            AttachConsole(-1);
        }

        var results = SelfTest.RunAll().ToList();
        var report = SelfTest.Report(results);
        File.WriteAllText(reportPath, report);
        Console.WriteLine();
        Console.WriteLine(report);
        Console.WriteLine("Report written to " + Path.GetFullPath(reportPath));
        return results.All(r => r.Passed) ? 0 : 1;
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
