using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using PdfUtil.Conversion;
using PdfUtil.Extensions.DependencyInjection;
using PdfUtil.Signatures;

namespace PdfUtil.WinFormsSample;

/// <summary>The outcome of one self-test check.</summary>
internal sealed class CheckResult
{
    public CheckResult(string name, bool passed, string details, TimeSpan elapsed)
    {
        Name = name;
        Passed = passed;
        Details = details;
        Elapsed = elapsed;
    }

    public string Name { get; }

    public bool Passed { get; }

    public string Details { get; }

    public TimeSpan Elapsed { get; }
}

/// <summary>
/// Runs every PdfUtil feature on .NET Framework, in memory, and checks the results. Each check is independent:
/// one failing does not stop the others.
/// </summary>
internal static class SelfTest
{
    private const string Marker = "PdfUtil self-test";

    public static string RuntimeSummary()
    {
        var target = typeof(Pdf).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "unknown";
        return $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.OSDescription.Trim()}, {(Environment.Is64BitProcess ? "64" : "32")}-bit); "
            + $"PdfUtil.dll {typeof(Pdf).Assembly.GetName().Version} built for {target}";
    }

    public static IReadOnlyList<(string Name, Func<string> Run)> Checks()
    {
        // Shared inputs, created lazily so that a failure shows up in the check that needs them.
        var source = new Lazy<byte[]>(() => HtmlToPdf($"<h1>{Marker}</h1><p>First page.</p><p style=\"page-break-before: always\">Second page.</p>"));

        return new (string, Func<string>)[]
        {
            ("Loads the .NET Framework build of PdfUtil", () =>
            {
                var target = typeof(Pdf).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
                Require(target == ".NETFramework,Version=v4.6.2", $"PdfUtil.dll is built for {target ?? "an unknown framework"}.");
                return target!;
            }),
            ("HTML to PDF", () =>
            {
                var pdf = source.Value;
                return $"{pdf.Length:N0} bytes, {Pdf.GetInfo(pdf).PageCount} pages";
            }),
            ("Inspect", () =>
            {
                var info = Pdf.Inspect(source.Value);
                Require(info.PageCount == 2, $"expected 2 pages, got {info.PageCount}.");
                return $"PDF {info.EffectiveVersion}, {info.PageCount} pages, {info.Fonts.Count} fonts";
            }),
            ("Extract text", () =>
            {
                var text = Pdf.ExtractText(source.Value).Text;
                Require(text.Contains(Marker), "the heading text was not found.");
                return $"{text.Length} characters";
            }),
            ("Protect and verify passwords (AES-256, AES-128, RC4-128)", () =>
            {
                var report = new List<string>();
                foreach (var algorithm in new[] { PdfEncryptionAlgorithm.Aes256, PdfEncryptionAlgorithm.Aes128, PdfEncryptionAlgorithm.Rc4_128 })
                {
                    var pdf = Pdf.Protect(source.Value, new PdfProtectionOptions { UserPassword = "user-pw", OwnerPassword = "owner-pw", Algorithm = algorithm }, out var result);
                    Require(Pdf.IsEncrypted(pdf), $"{algorithm}: the output is not encrypted.");
                    Require(Pdf.VerifyPassword(pdf, "user-pw").MatchedAs == PdfPasswordMatch.User, $"{algorithm}: the user password did not match.");
                    Require(Pdf.VerifyPassword(pdf, "owner-pw").MatchedAs == PdfPasswordMatch.Owner, $"{algorithm}: the owner password did not match.");
                    Require(!Pdf.VerifyPassword(pdf, "wrong").IsValid, $"{algorithm}: a wrong password was accepted.");
                    Require(Pdf.ExtractText(pdf, new PdfTextExtractionOptions { Password = "user-pw" }).Text.Contains(Marker), $"{algorithm}: the text cannot be read back.");
                    report.Add($"{algorithm} R{result.Revision} PDF {result.OutputVersion}");
                }

                return string.Join("; ", report);
            }),
            ("Non-ASCII password (SASLprep)", () =>
            {
                // Accented letters are refused by default (readers encode them differently), so ask for the ISO 32000-2 rules.
                var pdf = Pdf.Protect(source.Value, new PdfProtectionOptions { UserPassword = "Grüße-密码", OwnerPassword = "owner", PasswordPolicy = PdfPasswordPolicy.Specification });
                Require(Pdf.VerifyPassword(pdf, "Grüße-密码").IsValid, "the password did not match.");
                Require(Pdf.VerifyPassword(pdf, "Gru\u0308ße-密码").IsValid, "the decomposed form of 'ü' did not match after normalization.");
                return "AES-256 with a German and Chinese password, composed and decomposed 'ü'";
            }),
            ("Decrypt", () =>
            {
                var pdf = Pdf.Decrypt(Pdf.Protect(source.Value, new PdfProtectionOptions { UserPassword = "u", OwnerPassword = "o" }), "o");
                Require(!Pdf.IsEncrypted(pdf), "the output is still encrypted.");
                return $"{pdf.Length:N0} bytes";
            }),
            ("Edit document information", () =>
            {
                var pdf = Pdf.EditInfo(source.Value, new PdfInfoChanges { Title = "Café report", Author = "Self-test" }, null, out _);
                var title = Pdf.GetInfo(pdf).Info?.Title;
                Require(title == "Café report", $"the title reads back as '{title}'.");
                return $"Title = {title}";
            }),
            ("Watermark", () =>
            {
                var pdf = Pdf.Stamp(source.Value, new PdfStampOptions { Text = "CONFIDENTIAL" }, out var result);
                Require(result.StampedPages.Count == 2, $"stamped {result.StampedPages.Count} pages.");
                Require(Pdf.ExtractText(pdf).Text.Contains("CONFIDENTIAL"), "the watermark text was not found.");
                return $"stamped pages {string.Join(", ", result.StampedPages)}";
            }),
            ("Rotate, delete, merge and split pages", () =>
            {
                var rotated = Pdf.RotatePages(source.Value, new PdfRotatePagesOptions { Angle = 90, Pages = PdfPageRange.Parse("1") }, out _);
                Require(Pdf.GetInfo(rotated).PageCount == 2, "rotating changed the page count.");
                var deleted = Pdf.DeletePages(source.Value, new PdfDeletePagesOptions { Pages = PdfPageRange.Parse("2") }, out _);
                Require(Pdf.GetInfo(deleted).PageCount == 1, "deleting did not leave 1 page.");
                var merged = Pdf.Merge(new[] { PdfMergeInput.FromBytes(source.Value), PdfMergeInput.FromBytes(deleted) }, null, out var merge);
                Require(merge.PageCount == 3 && Pdf.GetInfo(merged).PageCount == 3, "merging did not give 3 pages.");
                var parts = Pdf.Split(merged, new PdfSplitOptions { EveryNPages = 1 }, out _);
                Require(parts.Count == 3, $"splitting gave {parts.Count} parts.");
                return "rotate 1, delete 1, merge 2+1, split into 3";
            }),
            ("Attachments", () =>
            {
                var content = Encoding.UTF8.GetBytes("hello from .NET Framework");
                var pdf = Pdf.AddAttachment(source.Value, new PdfAddAttachmentOptions { Name = "note.txt", Content = content, MimeType = "text/plain" });
                var list = Pdf.ListAttachments(pdf);
                Require(list.Count == 1 && list[0].Name == "note.txt", "the attachment is not listed.");
                Require(Pdf.ExtractAttachment(pdf, "note.txt").SequenceEqual(content), "the extracted content differs.");
                var removed = Pdf.RemoveAttachment(pdf, "note.txt");
                Require(Pdf.ListAttachments(removed).Count == 0, "the attachment was not removed.");
                return "add, list, extract, remove";
            }),
            ("Validate, repair and sanitize", () =>
            {
                var report = Pdf.Validate(source.Value);
                Require(report.IsValid, "the document does not validate: " + string.Join("; ", report.Errors.Select(e => e.ToString())));
                var damaged = Damage(source.Value);
                var repaired = Pdf.Repair(damaged, null, out var repair);
                Require(Pdf.Validate(repaired).IsValid && repair.PageCount == 2, "the damaged copy was not repaired.");
                Pdf.Sanitize(source.Value, PdfSanitizeOptions.All(), out var sanitize);
                return $"valid; repaired ({repair.Repairs.Count} repairs); sanitized ({sanitize.Removed.Count} items removed)";
            }),
            ("PDF to HTML and Word", () =>
            {
                var converter = new PdfConverter();
                using var html = new MemoryStream();
                Run(converter.ConvertToHtmlAsync(new MemoryStream(source.Value), html));
                Require(Encoding.UTF8.GetString(html.ToArray()).Contains(Marker), "the HTML has no heading text.");
                using var docx = new MemoryStream();
                Run(converter.ConvertToDocxAsync(new MemoryStream(source.Value), docx));
                Require(docx.Length > 4 && docx.ToArray()[0] == (byte)'P' && docx.ToArray()[1] == (byte)'K', "the Word file is not a ZIP package.");
                using var back = new MemoryStream();
                docx.Position = 0;
                Run(converter.ConvertFromDocxAsync(docx, back));
                Require(Pdf.ExtractText(back.ToArray()).Text.Contains(Marker), "Word to PDF lost the text.");
                return $"HTML {html.Length:N0} bytes, DOCX {docx.Length:N0} bytes, back to PDF {back.Length:N0} bytes";
            }),
            ("Pipeline", () =>
            {
                var pdf = Run(PdfPipeline.FromBytes(source.Value).Watermark("DRAFT").EditInfo(i => i.Title = "Pipeline").Protect("u", "o").ToBytesAsync());
                Require(Pdf.VerifyPassword(pdf, "u").IsValid, "the pipeline output does not open with its password.");
                return $"{pdf.Length:N0} bytes";
            }),
            ("Dependency injection", () =>
            {
                var services = new ServiceCollection().AddPdfUtil().AddPdfUtilSignatures().AddPdfUtilConversion();
                using var provider = services.BuildServiceProvider();
                Require(provider.GetService<IPdfProtector>() is not null, "IPdfProtector is not registered.");
                Require(provider.GetService<IPdfSigner>() is not null, "IPdfSigner is not registered.");
                Require(provider.GetService<IPdfConverter>() is not null, "IPdfConverter is not registered.");
                return "IPdfProtector, IPdfSigner, IPdfConverter";
            }),
            ("Sign (RSA, PAdES) and verify", () =>
            {
                var signed = Sign(source.Value, new PdfSignOptions { Certificate = TestCertificates.Signer, Reason = "Self-test" });
                var signature = VerifySingle(signed);
                return $"valid, signed by {signature.SignerName}, {signature.DigestAlgorithm}";
            }),
            ("Sign with an RFC 3161 timestamp and verify", () =>
            {
                var tsa = new TestCertificates.LocalTsa();
                using var client = TestCertificates.TsaClient(tsa);
                var signed = Sign(source.Value, new PdfSignOptions { Certificate = TestCertificates.Signer, TimestampServer = new Uri("http://tsa.invalid/"), TimestampHttpClient = client });
                Require(tsa.Requests == 1, $"the TSA got {tsa.Requests} requests.");
                var signature = VerifySingle(signed);
                Require(signature.TimestampIsValid == true && signature.TimestampTime is not null, "the timestamp does not verify.");
                return $"timestamp {signature.TimestampTime:u}";
            }),
            ("A separate private key is refused on .NET Framework", () =>
            {
                using var key = RSA.Create();
                try
                {
                    Sign(source.Value, new PdfSignOptions { Certificate = TestCertificates.Signer, PrivateKey = key });
                }
                catch (PlatformNotSupportedException ex)
                {
                    return "PlatformNotSupportedException: " + ex.Message;
                }

                throw new InvalidOperationException("signing with PrivateKey did not throw PlatformNotSupportedException.");
            }),
            ("Certificate encryption", () =>
            {
                var security = new PdfCertificateSecurity();
                using var encrypted = new MemoryStream();
                Run(security.ProtectAsync(new MemoryStream(source.Value), encrypted, new PdfCertificateProtectionOptions { Recipients = new[] { new PdfCertificateRecipient(TestCertificates.Signer) } }));
                Require(Pdf.GetInfo(encrypted.ToArray()).RequiresCertificate, "the output does not require a certificate.");
                using var decrypted = new MemoryStream();
                encrypted.Position = 0;
                Run(security.DecryptAsync(encrypted, decrypted, new PdfCertificateDecryptOptions { Certificate = TestCertificates.Signer }));
                Require(Pdf.ExtractText(decrypted.ToArray()).Text.Contains(Marker), "the decrypted text differs.");
                return "encrypt for a recipient, decrypt with its certificate";
            }),
        };
    }

    public static IEnumerable<CheckResult> RunAll(IEnumerable<(string Name, Func<string> Run)>? checks = null)
    {
        foreach (var (name, run) in checks ?? Checks())
        {
            var watch = Stopwatch.StartNew();
            CheckResult result;
            try
            {
                result = new CheckResult(name, true, run(), watch.Elapsed);
            }
            catch (Exception ex)
            {
                var inner = ex is AggregateException { InnerException: { } first } ? first : ex;
                result = new CheckResult(name, false, $"{inner.GetType().Name}: {inner.Message}", watch.Elapsed);
            }

            yield return result;
        }
    }

    public static string Report(IEnumerable<CheckResult> results)
    {
        var list = results.ToList();
        var sb = new StringBuilder();
        sb.AppendLine("PdfUtil self-test, " + DateTime.Now.ToString("u"));
        sb.AppendLine(RuntimeSummary());
        sb.AppendLine();
        foreach (var r in list)
        {
            sb.AppendLine($"[{(r.Passed ? "PASS" : "FAIL")}] {r.Name} ({r.Elapsed.TotalMilliseconds:N0} ms)");
            sb.AppendLine("       " + r.Details);
        }

        sb.AppendLine();
        sb.AppendLine($"{list.Count(r => r.Passed)} of {list.Count} checks passed.");
        return sb.ToString();
    }

    private static byte[] HtmlToPdf(string html)
    {
        using var output = new MemoryStream();
        Run(new PdfConverter().ConvertFromHtmlStringAsync($"<html><body>{html}</body></html>", output, new HtmlToPdfOptions { Title = Marker }));
        return output.ToArray();
    }

    private static byte[] Sign(byte[] pdf, PdfSignOptions options)
    {
        using var output = new MemoryStream();
        Run(new PdfSigner().SignAsync(new MemoryStream(pdf), output, options));
        return output.ToArray();
    }

    private static PdfSignatureValidation VerifySingle(byte[] pdf)
    {
        var result = Run(new PdfSigner().VerifyAsync(new MemoryStream(pdf)));
        Require(result.Signatures.Count == 1, $"found {result.Signatures.Count} signatures.");
        var signature = result.Signatures[0];
        Require(signature.IsValid && signature.CoversWholeFile, "the signature is not valid: " + string.Join("; ", signature.Problems));
        return signature;
    }

    /// <summary>Breaks the cross-reference offsets, the most common damage, so that Repair has work to do.</summary>
    private static byte[] Damage(byte[] pdf)
    {
        var text = Encoding.GetEncoding(28591).GetString(pdf);
        var start = text.LastIndexOf("startxref", StringComparison.Ordinal);
        Require(start > 0, "the test document has no startxref.");
        return Encoding.GetEncoding(28591).GetBytes(text.Substring(0, start) + "startxref\n12\n%%EOF\n");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Run(Task task) => task.GetAwaiter().GetResult();

    private static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();
}
