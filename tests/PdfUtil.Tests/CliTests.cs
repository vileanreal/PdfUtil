using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PdfUtil.Cli;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>F23: the pdfutil command-line tool, in-process and as a process.</summary>
public sealed class CliTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("pdfutil-cli-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string File_(string name, byte[] data)
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private string Out(string name) => Path.Combine(_dir.FullName, name);

    private sealed record Run(int Code, string Output, string Error)
    {
        public JsonElement Json => JsonDocument.Parse(Output).RootElement;
    }

    private static Run Cli(string[] args, string stdin = "", Dictionary<string, string>? env = null, Func<string, string?>? prompt = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var console = new CliConsole { In = new StringReader(stdin), Out = output, Error = error, Environment = name => env is not null && env.TryGetValue(name, out var v) ? v : null, Prompt = prompt };
        var code = CliApp.RunAsync(args, console).GetAwaiter().GetResult();
        return new Run(code, output.ToString(), error.ToString());
    }

    private static Run Cli(params string[] args) => Cli(args, "");

    private static readonly Dictionary<string, string> Env = new() { ["USER_PW"] = "user-pw", ["OWNER_PW"] = "owner-pw", ["WRONG"] = "nope", ["CERT_PW"] = "cert-pw" };

    [Fact]
    public void Usage_HelpAndErrors()
    {
        var none = Cli();
        Assert.Equal(1, none.Code);
        Assert.Contains("Commands:", none.Output, StringComparison.Ordinal);
        Assert.Equal(0, Cli("--help").Code);
        Assert.Contains("--owner-password-env", Cli("protect", "--help").Output, StringComparison.Ordinal);
        Assert.Contains("pdfutil 1.0.0", Cli("--version").Output, StringComparison.Ordinal);
        Assert.Equal(1, Cli("frobnicate").Code);
        var unknown = Cli("info", "x.pdf", "--colour");
        Assert.Equal(1, unknown.Code);
        Assert.Contains("Unknown option --colour", unknown.Error, StringComparison.Ordinal);
        Assert.Equal(1, Cli("rotate", "only-one.pdf").Code);
        Assert.Equal(1, Cli("rotate", "a.pdf", "b.pdf", "--angle", "ninety").Code);
        Assert.Equal(1, Cli("rotate", "a.pdf", "b.pdf", "--angle").Code);

        // There is no plain password option at all.
        Assert.Equal(1, Cli("protect", "a.pdf", "b.pdf", "--password", "secret").Code);
        var json = Cli("info", "missing.pdf", "--json");
        Assert.Equal(3, json.Code);
        Assert.False(json.Json.GetProperty("ok").GetBoolean());
        Assert.Equal(3, json.Json.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void Protect_Decrypt_PasswordSources_AndExitCodes()
    {
        var input = File_("in.pdf", new TestPdfBuilder().Build());
        var protectedPath = Out("protected.pdf");

        // Environment variables.
        var protect = Cli(new[] { "protect", input, protectedPath, "--user-password-env", "USER_PW", "--owner-password-env", "OWNER_PW", "--permissions", "print,copy-content", "--algorithm", "aes128", "--json" }, env: Env);
        Assert.Equal(0, protect.Code);
        Assert.True(protect.Json.GetProperty("ok").GetBoolean());
        Assert.Equal("Aes128", protect.Json.GetProperty("result").GetProperty("Algorithm").GetString());
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(protectedPath, "user-pw").MatchedAs);
        Assert.Equal(PdfPermissions.Print | PdfPermissions.CopyContent, Pdf.GetInfo(protectedPath, "owner-pw").Encryption!.Permissions);

        // Standard input: user password first, then owner.
        var fromStdin = Out("stdin.pdf");
        Assert.Equal(0, Cli(new[] { "protect", input, fromStdin, "--password-stdin" }, "u2\no2\n").Code);
        Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(fromStdin, "o2").MatchedAs);

        // A prompt, when a terminal is attached.
        var prompts = new List<string>();
        var prompted = Out("prompted.pdf");
        Assert.Equal(0, Cli(new[] { "protect", input, prompted, "--no-user-password" }, prompt: p => { prompts.Add(p); return "o3"; }).Code);
        Assert.Equal(new[] { "Owner password: " }, prompts);
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(prompted, "").MatchedAs);

        // Without any source and without a terminal: a usage error, not a guess.
        var missing = Cli("protect", input, Out("x.pdf"));
        Assert.Equal(1, missing.Code);
        Assert.Contains("-password-env", missing.Error, StringComparison.Ordinal);

        // Wrong password: 2; already encrypted: 4 (policy); right password: 0.
        Assert.Equal(2, Cli(new[] { "decrypt", protectedPath, Out("d.pdf"), "--password-env", "WRONG" }, env: Env).Code);
        Assert.Equal(4, Cli(new[] { "protect", protectedPath, Out("again.pdf"), "--user-password-env", "USER_PW", "--owner-password-env", "OWNER_PW" }, env: Env).Code);
        Assert.Equal(0, Cli(new[] { "decrypt", protectedPath, Out("d.pdf"), "--password-env", "OWNER_PW" }, env: Env).Code);
        Assert.False(Pdf.IsEncrypted(Out("d.pdf")));

        // Opening an encrypted input asks for its password; changing security.
        var info = Cli(new[] { "info", protectedPath, "--json" }, prompt: _ => "user-pw");
        Assert.Equal(2, info.Json.GetProperty("result").GetProperty("Value").GetProperty("PageCount").GetInt32());
        Assert.Equal(0, Cli(new[] { "security", protectedPath, Out("sec.pdf"), "--password-env", "OWNER_PW", "--algorithm", "aes256", "--permissions", "none" }, env: Env).Code);
        Assert.Equal(PdfPermissions.None, Pdf.GetInfo(Out("sec.pdf"), "owner-pw").Encryption!.Permissions);
        Assert.Equal(0, Cli(new[] { "security", protectedPath, Out("sec2.pdf"), "--password-stdin", "--change-passwords" }, "owner-pw\nnew-user\nnew-owner\n").Code);
        Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(Out("sec2.pdf"), "new-owner").MatchedAs);
    }

    [Fact]
    public void Inspect_Validate_Repair_Sanitize_EditInfo()
    {
        var rich = File_("rich.pdf", new TestPdfBuilder { Rich = true }.Build());
        var inspect = Cli("inspect", rich, "--details", "fonts,attachments");
        Assert.Equal(0, inspect.Code);
        Assert.Contains("Pages:      2", inspect.Output, StringComparison.Ordinal);
        Assert.Contains("font ", inspect.Output, StringComparison.Ordinal);
        Assert.Contains("attachment ", inspect.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("bookmark ", inspect.Output, StringComparison.Ordinal);

        Assert.Equal(0, Cli("validate", rich).Code);
        var damaged = File_("damaged.pdf", new TestPdfBuilder { CorruptFlate = true }.Build());
        var invalid = Cli("validate", damaged, "--json");
        Assert.Equal(3, invalid.Code);
        Assert.True(invalid.Json.GetProperty("result").GetProperty("Value").GetProperty("Issues").GetArrayLength() > 0);

        var broken = File_("broken.pdf", new TestPdfBuilder { BreakXrefOffsets = true }.Build());
        Assert.Equal(0, Cli("repair", broken, Out("repaired.pdf")).Code);
        Assert.Equal(0, Cli("sanitize", rich, Out("clean.pdf"), "--all").Code);
        Assert.Empty(Pdf.ListAttachments(Out("clean.pdf")));
        Assert.Equal(0, Cli("edit-info", rich, Out("edited.pdf"), "--title", "New title", "--set", "Project=Apollo", "--remove", "Subject").Code);
        var edited = Pdf.GetInfo(Out("edited.pdf"));
        Assert.Equal("New title", edited.Info!.Title);
        Assert.Equal("Apollo", edited.Info.Entries["Project"]);
        Assert.Equal(1, Cli("edit-info", rich, Out("e2.pdf"), "--set", "novalue").Code);
    }

    [Fact]
    public void Pages_Attachments_Text_Watermark()
    {
        var input = File_("doc.pdf", new TestPdfBuilder { PageCount = 4 }.Build());
        Assert.Equal(0, Cli("rotate", input, Out("rot.pdf"), "--angle", "180", "--pages", "even").Code);
        var before = Pdf.GetInfo(input).Pages.Select(p => p.Rotate).ToList();
        Assert.Equal(before.Select((r, i) => i % 2 == 1 ? (r + 180) % 360 : r), Pdf.GetInfo(Out("rot.pdf")).Pages.Select(p => p.Rotate));
        Assert.Equal(0, Cli("delete-pages", input, Out("del.pdf"), "--pages", "1,last").Code);
        Assert.Equal(2, Pdf.GetInfo(Out("del.pdf")).PageCount);
        Assert.Equal(1, Cli("delete-pages", input, Out("del2.pdf")).Code);
        Assert.Equal(1, Cli("delete-pages", input, Out("del2.pdf"), "--pages", "1-x").Code);

        Assert.Equal(0, Cli("merge", Out("merged.pdf"), input, input + ":2-3").Code);
        Assert.Equal(6, Pdf.GetInfo(Out("merged.pdf")).PageCount);
        var split = Cli("split", input, Out("part.pdf"), "--every", "3", "--json");
        Assert.Equal(0, split.Code);
        Assert.True(File.Exists(Out("part-1.pdf")) && File.Exists(Out("part-2.pdf")));

        var text = Cli("text", input, "--pages", "2");
        Assert.Contains("Hello page 2", text.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello page 1", text.Output, StringComparison.Ordinal);
        Assert.Equal(0, Cli("text", input, "--output", Out("t.txt")).Code);
        Assert.Contains("Hello page 4", File.ReadAllText(Out("t.txt")), StringComparison.Ordinal);

        var note = File_("note.txt", Encoding.UTF8.GetBytes("attached note"));
        Assert.Equal(0, Cli("attach", input, Out("att.pdf"), note, "--description", "A note").Code);
        Assert.Contains("note.txt", Cli("extract-attachment", Out("att.pdf")).Output, StringComparison.Ordinal);
        Assert.Equal(0, Cli("extract-attachment", Out("att.pdf"), "note.txt", Out("back.txt")).Code);
        Assert.Equal("attached note", File.ReadAllText(Out("back.txt")));
        Assert.Equal(0, Cli("detach", Out("att.pdf"), Out("det.pdf"), "note.txt").Code);
        Assert.Equal(3, Cli("detach", Out("det.pdf"), Out("det2.pdf"), "note.txt").Code);

        Assert.Equal(0, Cli("watermark", input, Out("wm.pdf"), "--text", "DRAFT\\nCopy", "--color", "#FF0000", "--opacity", "0.5", "--position", "top-right", "--pages", "1").Code);
        Assert.Contains("DRAFT", Pdf.ExtractText(Out("wm.pdf")).Pages[0].Text, StringComparison.Ordinal);
        Assert.Equal(0, Cli("stamp", input, Out("wm2.pdf"), "--text", "Черновик", "--font", File_("font.ttf", TestFont.Build())).Code);
        Assert.Contains("Черновик", Pdf.ExtractText(Out("wm2.pdf")).Text, StringComparison.Ordinal);
        Assert.Equal(1, Cli("watermark", input, Out("wm3.pdf"), "--text", "x", "--position", "upside-down").Code);
    }

    [Fact]
    public void Sign_AndVerifySignatures()
    {
        var certificate = TestCertificates.Ecdsa();
        byte[] pfx;
        try
        {
            pfx = certificate.Export(X509ContentType.Pfx, "cert-pw");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return; // The platform cannot export this key; signing is covered by SignatureTests.
        }

        var pfxPath = File_("signer.pfx", pfx);
        var input = File_("unsigned.pdf", new TestPdfBuilder().Build());
        var signed = Out("signed.pdf");
        var sign = Cli(new[] { "sign", input, signed, "--certificate", pfxPath, "--certificate-password-env", "CERT_PW", "--reason", "Approved", "--visible", "1:36,36,236,96" }, env: Env);
        Assert.True(sign.Code == 0, sign.Error);
        var verify = Cli("verify-signatures", signed);
        Assert.Equal(0, verify.Code);
        Assert.Contains("valid", verify.Output, StringComparison.Ordinal);

        // Tampering with the signed bytes is a verification failure (5).
        var bytes = File.ReadAllBytes(signed);
        var at = Encoding.Latin1.GetString(bytes).IndexOf("Hello page 1", StringComparison.Ordinal);
        if (at > 0)
        {
            bytes[at] = (byte)'J';
            File.WriteAllBytes(Out("tampered.pdf"), bytes);
            Assert.Equal(5, Cli("verify-signatures", Out("tampered.pdf")).Code);
        }

        Assert.Equal(0, Cli("verify-signatures", input).Code);
        Assert.Equal(1, Cli("sign", input, Out("x.pdf")).Code);
        Assert.Equal(1, Cli(new[] { "sign", input, Out("x.pdf"), "--certificate", pfxPath, "--certificate-password-env", "CERT_PW", "--visible", "1:1,2" }, env: Env).Code);
    }

    [Fact]
    public void Convert_EveryDirection()
    {
        var pdf = File_("layout.pdf", HtmlConversionTests.LayoutDocument());
        Assert.Equal(0, Cli("convert", pdf, Out("layout.html"), "--layout", "fixed").Code);
        Assert.Contains("pdf-page", File.ReadAllText(Out("layout.html")), StringComparison.Ordinal);
        Assert.Equal(0, Cli("convert", pdf, Out("layout.docx")).Code);
        Assert.Equal(0, Cli("convert", Out("layout.docx"), Out("back.pdf")).Code);
        Assert.Contains("Annual Report", Pdf.ExtractText(Out("back.pdf")).Text, StringComparison.Ordinal);

        var html = File_("page.html", Encoding.UTF8.GetBytes("<link rel=stylesheet href=style.css><h1>From HTML</h1><img src=dot.png>"));
        File_("style.css", Encoding.UTF8.GetBytes("h1 { color: red }"));
        File_("dot.png", PdfUtil.Internal.Images.ImageCodecs.EncodePng(1, 1, 3, new byte[3]));
        var toPdf = Cli("convert", html, Out("page.pdf"), "--page-size", "letter", "--landscape", "--footer", "{page}/{pages}", "--json");
        Assert.Equal(0, toPdf.Code);
        Assert.Equal(1, toPdf.Json.GetProperty("result").GetProperty("ImageCount").GetInt32());
        var pageInfo = Pdf.GetInfo(Out("page.pdf")).Pages[0];
        Assert.Equal(792, pageInfo.DisplayWidth, 1);
        Assert.Contains("1/1", Pdf.ExtractText(Out("page.pdf")).Text, StringComparison.Ordinal);
        Assert.Equal(0, Cli("convert", html, Out("noresources.pdf"), "--no-resources").Code);

        // Direction errors are usage errors; a PDF without text converts to nothing usable (6).
        Assert.Equal(1, Cli("convert", pdf, Out("x.txt")).Code);
        Assert.Equal(1, Cli("convert", html, Out("x.docx")).Code);
        var scanned = new LayoutPdf();
        scanned.Page();
        var empty = File_("empty.pdf", scanned.Build());
        Assert.Equal(6, Cli("convert", empty, Out("empty.docx")).Code);
        Assert.Equal(6, Cli("convert", empty, Out("empty.html")).Code);
    }

    [Fact]
    public void Batch_ManyFiles()
    {
        var inputDir = Directory.CreateDirectory(Path.Combine(_dir.FullName, "batch-in"));
        for (var i = 1; i <= 3; i++)
        {
            File.WriteAllBytes(Path.Combine(inputDir.FullName, $"doc{i}.pdf"), new TestPdfBuilder().Build());
        }

        File.WriteAllBytes(Path.Combine(inputDir.FullName, "bad.pdf"), Encoding.ASCII.GetBytes("not a pdf"));
        var outDir = Path.Combine(_dir.FullName, "batch-out");
        var run = Cli(new[] { "batch", "protect", "--out-dir", outDir, "--owner-password-env", "OWNER_PW", "--no-user-password", "--parallel", "2", Path.Combine(inputDir.FullName, "doc*.pdf"), Path.Combine(inputDir.FullName, "bad.pdf") }, env: Env);
        Assert.Equal(3, run.Code);
        Assert.Contains("3 of 4 succeeded", run.Output, StringComparison.Ordinal);
        for (var i = 1; i <= 3; i++)
        {
            Assert.True(Pdf.IsEncrypted(Path.Combine(outDir, $"doc{i}.pdf")));
        }

        var json = Cli(new[] { "batch", "info", "--json", Path.Combine(inputDir.FullName, "doc*.pdf") });
        Assert.Equal(0, json.Code);
        Assert.Equal(3, json.Json.GetProperty("result").GetProperty("Items").GetArrayLength());
        var converted = Cli(new[] { "batch", "convert", "--to", "html", "--out-dir", outDir, Path.Combine(inputDir.FullName, "doc1.pdf") });
        Assert.Equal(0, converted.Code);
        Assert.True(File.Exists(Path.Combine(outDir, "doc1.html")));
        Assert.Equal(1, Cli("batch", "merge", "--out-dir", outDir, "a.pdf").Code);
        Assert.Equal(1, Cli("batch", "protect", "a.pdf").Code);
        Assert.Equal(1, Cli("batch", "protect", "--out-dir", outDir, "--colour", "a.pdf").Code);
    }

    /// <summary>The tool as a separate process: exit codes, JSON on standard output, a password on standard input.</summary>
    [Fact]
    public void Process_RunsTheTool()
    {
        var tool = Path.Combine(AppContext.BaseDirectory, "PdfUtil.Cli.dll");
        Assert.True(File.Exists(tool), "the tool is built next to the tests");
        var input = File_("p.pdf", new TestPdfBuilder().Build());
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";

        (int Code, string Output) Exec(string stdin, params string[] args)
        {
            var info = new ProcessStartInfo(dotnet) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add(tool);
            foreach (var a in args)
            {
                info.ArgumentList.Add(a);
            }

            using var process = Process.Start(info)!;
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(60_000);
            return (process.ExitCode, output);
        }

        var info = Exec("", "info", input, "--json");
        Assert.Equal(0, info.Code);
        Assert.Equal(2, JsonDocument.Parse(info.Output).RootElement.GetProperty("result").GetProperty("Value").GetProperty("PageCount").GetInt32());
        Assert.Equal(0, Exec("u\no\n", "protect", input, Out("p-protected.pdf"), "--password-stdin").Code);
        Assert.Equal(2, Exec("bad\n", "decrypt", Out("p-protected.pdf"), Out("p-plain.pdf"), "--password-stdin").Code);
        Assert.Equal(0, Exec("o\n", "decrypt", Out("p-protected.pdf"), Out("p-plain.pdf"), "--password-stdin").Code);
        Assert.Equal(1, Exec("", "nonsense").Code);
        Assert.Equal(3, Exec("", "info", Out("does-not-exist.pdf")).Code);
    }
}
