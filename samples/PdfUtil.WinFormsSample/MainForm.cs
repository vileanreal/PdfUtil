using System.Drawing;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows.Forms;
using PdfUtil.Conversion;
using PdfUtil.Signatures;

namespace PdfUtil.WinFormsSample;

/// <summary>Two tabs: a self-test of every feature, and operations on a PDF the user picks.</summary>
internal sealed class MainForm : Form
{
    private readonly ListView _results = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
    private readonly Button _runAll = new() { Text = "Run all checks", AutoSize = true };
    private readonly Button _saveReport = new() { Text = "Save report…", AutoSize = true, Enabled = false };
    private readonly Label _summary = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly List<CheckResult> _lastRun = new();

    private readonly TextBox _file = new() { Width = 420 };
    private readonly TextBox _password = new() { Width = 160, UseSystemPasswordChar = true };
    private readonly TextBox _pfxPassword = new() { Width = 160, UseSystemPasswordChar = true };
    private readonly TextBox _output = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, ReadOnly = true, WordWrap = false, Font = new Font(FontFamily.GenericMonospace, 9) };

    public MainForm()
    {
        Text = "PdfUtil on .NET Framework";
        Width = 1000;
        Height = 680;
        StartPosition = FormStartPosition.CenterScreen;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(SelfTestPage());
        tabs.TabPages.Add(FilePage());
        Controls.Add(tabs);
    }

    private TabPage SelfTestPage()
    {
        _results.Columns.Add("Result", 70);
        _results.Columns.Add("Check", 330);
        _results.Columns.Add("Details", 470);
        _results.Columns.Add("ms", 60, HorizontalAlignment.Right);
        _runAll.Click += async (_, _) => await RunAllAsync();
        _saveReport.Click += (_, _) => SaveReport();

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
        bar.Controls.AddRange(new Control[] { _runAll, _saveReport, _summary });
        var runtime = new Label { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), Text = SelfTest.RuntimeSummary() };

        var page = new TabPage("Self-test");
        page.Controls.Add(_results);
        page.Controls.Add(bar);
        page.Controls.Add(runtime);
        return page;
    }

    private TabPage FilePage()
    {
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _file.Text = dialog.FileName;
            }
        };

        var inputs = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
        inputs.Controls.AddRange(new Control[] { Caption("PDF:"), _file, browse, Caption("Password:"), _password });

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
        actions.Controls.AddRange(new Control[]
        {
            Action("Info", Info),
            Action("Check password", CheckPassword),
            Action("Extract text", ExtractText),
            Action("Validate", ValidateFile),
            Action("Verify signatures", VerifySignatures),
            Action("Protect (AES-256)…", Protect),
            Action("Decrypt…", Decrypt),
            Action("Watermark…", Watermark),
            Action("To HTML…", ToHtml),
            Action("To Word…", ToWord),
            Action("Sign with PFX…", SignWithPfx),
            Caption("PFX password:"),
            _pfxPassword,
        });

        var page = new TabPage("Your PDF");
        page.Controls.Add(_output);
        page.Controls.Add(actions);
        page.Controls.Add(inputs);
        return page;
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };

    private Button Action(string text, Func<string> run)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_file.Text) || !File.Exists(_file.Text))
            {
                MessageBox.Show(this, "Choose a PDF file first.", Text);
                return;
            }

            UseWaitCursor = true;
            _output.Text = text + "…";
            try
            {
                _output.Text = await Task.Run(run);
            }
            catch (Exception ex)
            {
                _output.Text = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                UseWaitCursor = false;
            }
        };
        return button;
    }

    private async Task RunAllAsync()
    {
        _runAll.Enabled = false;
        _saveReport.Enabled = false;
        _results.Items.Clear();
        _lastRun.Clear();
        UseWaitCursor = true;
        try
        {
            foreach (var (name, run) in SelfTest.Checks())
            {
                _summary.Text = "Running: " + name;
                var result = await Task.Run(() => SelfTest.RunAll(new[] { (name, run) }).Single());
                _lastRun.Add(result);
                var item = new ListViewItem(new[] { result.Passed ? "PASS" : "FAIL", result.Name, result.Details, result.Elapsed.TotalMilliseconds.ToString("N0") })
                {
                    ForeColor = result.Passed ? Color.DarkGreen : Color.Firebrick,
                };
                _results.Items.Add(item);
            }

            _summary.Text = $"{_lastRun.Count(r => r.Passed)} of {_lastRun.Count} checks passed.";
        }
        finally
        {
            UseWaitCursor = false;
            _runAll.Enabled = true;
            _saveReport.Enabled = _lastRun.Count > 0;
        }
    }

    private void SaveReport()
    {
        using var dialog = new SaveFileDialog { FileName = "selftest-report.txt", Filter = "Text files (*.txt)|*.txt" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            File.WriteAllText(dialog.FileName, SelfTest.Report(_lastRun));
        }
    }

    private string? PasswordOrNull => _password.Text.Length == 0 ? null : _password.Text;

    private string Info()
    {
        var info = Pdf.Inspect(_file.Text, new PdfInspectionOptions { Password = PasswordOrNull });
        var sb = new StringBuilder();
        sb.AppendLine($"Version:      PDF {info.EffectiveVersion}");
        sb.AppendLine($"Pages:        {info.PageCount}");
        sb.AppendLine($"Encrypted:    {(info.Encryption is { } e ? $"{e.Algorithm}" : "no")}");
        sb.AppendLine($"Signed:       {info.IsSigned}");
        sb.AppendLine($"PDF/A:        {info.IsPdfA}");
        sb.AppendLine($"Title:        {info.Info?.Title}");
        sb.AppendLine($"Author:       {info.Info?.Author}");
        sb.AppendLine($"Producer:     {info.Info?.Producer}");
        sb.AppendLine($"Fonts:        {string.Join(", ", info.Fonts.Select(f => f.BaseFont ?? f.Name))}");
        sb.AppendLine($"Images:       {info.Images.Count}");
        foreach (var warning in info.Warnings)
        {
            sb.AppendLine("Warning:      " + warning);
        }

        return sb.ToString();
    }

    private string CheckPassword()
    {
        var check = Pdf.VerifyPassword(_file.Text, _password.Text);
        return check.IsEncrypted
            ? $"Encrypted ({check.Algorithm}, R{check.Revision}). Password valid: {check.IsValid}, matched as: {check.MatchedAs}."
            : "The document is not encrypted.";
    }

    private string ExtractText() => Pdf.ExtractText(_file.Text, new PdfTextExtractionOptions { Password = PasswordOrNull }).Text;

    private string ValidateFile()
    {
        var report = Pdf.Validate(_file.Text, new PdfValidationOptions { Password = PasswordOrNull });
        var lines = report.Issues.Select(i => $"{i.Severity}: {i}");
        return $"Valid: {report.IsValid}, objects: {report.ObjectCount}{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
    }

    private string VerifySignatures()
    {
        using var input = File.OpenRead(_file.Text);
        var result = new PdfSigner().VerifyAsync(input, new PdfSignatureVerificationOptions { Password = PasswordOrNull }).GetAwaiter().GetResult();
        if (result.Signatures.Count == 0)
        {
            return "The document has no signatures.";
        }

        var sb = new StringBuilder();
        foreach (var s in result.Signatures)
        {
            sb.AppendLine($"{s.FieldName}: {(s.IsValid ? "VALID" : "INVALID")}, signer {s.SignerName}, {s.DigestAlgorithm}, covers whole file: {s.CoversWholeFile}");
            if (s.TimestampTime is { } time)
            {
                sb.AppendLine($"    timestamp {time:u}, valid: {s.TimestampIsValid}");
            }

            foreach (var problem in s.Problems)
            {
                sb.AppendLine("    " + problem);
            }
        }

        return sb.ToString();
    }

    private string Protect()
    {
        var output = AskOutput("-protected.pdf");
        if (output is null)
        {
            return "Cancelled.";
        }

        var password = _password.Text.Length == 0 ? "password" : _password.Text;
        var result = Pdf.Protect(_file.Text, output, new PdfProtectionOptions { UserPassword = password, OwnerPassword = password + "-owner" });
        return $"Wrote {output}{Environment.NewLine}{result.Algorithm} R{result.Revision}, PDF {result.SourceVersion} -> {result.OutputVersion}{Environment.NewLine}User password: {password}{Environment.NewLine}Owner password: {password}-owner";
    }

    private string Decrypt()
    {
        var output = AskOutput("-decrypted.pdf");
        return output is null ? "Cancelled." : $"Wrote {output}: {Pdf.Decrypt(_file.Text, output, _password.Text)}";
    }

    private string Watermark()
    {
        var output = AskOutput("-watermarked.pdf");
        return output is null ? "Cancelled." : $"Wrote {output}: {Pdf.Stamp(_file.Text, output, new PdfStampOptions { Text = "CONFIDENTIAL", Password = PasswordOrNull })}";
    }

    private string ToHtml()
    {
        var output = AskOutput(".html", "HTML files (*.html)|*.html");
        return output is null ? "Cancelled." : $"Wrote {output}: {new PdfConverter().ConvertToHtmlFileAsync(_file.Text, output, new PdfToHtmlOptions { Password = PasswordOrNull }).GetAwaiter().GetResult()}";
    }

    private string ToWord()
    {
        var output = AskOutput(".docx", "Word documents (*.docx)|*.docx");
        return output is null ? "Cancelled." : $"Wrote {output}: {new PdfConverter().ConvertToDocxFileAsync(_file.Text, output, new PdfToDocxOptions { Password = PasswordOrNull }).GetAwaiter().GetResult()}";
    }

    private string SignWithPfx()
    {
        string? pfx = null;
        Invoke(new Action(() =>
        {
            using var dialog = new OpenFileDialog { Filter = "Certificates with a private key (*.pfx;*.p12)|*.pfx;*.p12" };
            pfx = dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
        }));
        if (pfx is null)
        {
            return "Cancelled.";
        }

        var output = AskOutput("-signed.pdf");
        if (output is null)
        {
            return "Cancelled.";
        }

        using var certificate = new X509Certificate2(pfx, _pfxPassword.Text, X509KeyStorageFlags.Exportable);
        var result = new PdfSigner().SignFileAsync(_file.Text, output, new PdfSignOptions { Certificate = certificate, Reason = "Signed with the PdfUtil sample" }).GetAwaiter().GetResult();
        return $"Wrote {output}: {result}";
    }

    /// <summary>Asks for an output path on the UI thread (the operations run on a worker thread).</summary>
    private string? AskOutput(string suffix, string filter = "PDF files (*.pdf)|*.pdf")
    {
        string? path = null;
        Invoke(new Action(() =>
        {
            using var dialog = new SaveFileDialog
            {
                FileName = Path.GetFileNameWithoutExtension(_file.Text) + suffix,
                InitialDirectory = Path.GetDirectoryName(_file.Text),
                Filter = filter,
            };
            path = dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
        }));
        return path;
    }
}
