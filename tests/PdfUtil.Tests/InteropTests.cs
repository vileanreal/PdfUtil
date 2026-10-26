using System.Text.Json;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>
/// Optional interoperability tests against external tools, run as separate processes. Each test is skipped when the
/// tool is not installed. pdf.js additionally requires the PDFUTIL_PDFJS_DIR environment variable to point at a
/// directory where <c>npm install pdfjs-dist</c> has been run; Apple PDFKit runs through <c>swift</c> on macOS. The corpus covers every input version and structure
/// protected with every algorithm.
/// </summary>
public class InteropTests
{
    internal sealed record Sample(string Name, byte[] Pdf, string User, string Owner, int Pages, string Title, bool Encrypted = true);

    internal static IEnumerable<Sample> Corpus()
    {
        foreach (var version in new[] { "1.0", "1.3", "1.4", "1.5", "1.6", "1.7", "2.0" })
        {
            foreach (var algorithm in TestHelpers.WritableAlgorithms)
            {
                var input = new TestPdfBuilder { Version = version, Xmp = TestPdfBuilder.PlainXmp() }.Build();
                yield return Make($"v{version}-{algorithm}", input, algorithm, "user-pw", "owner-pw");
            }
        }

        var structures = new Dictionary<string, TestPdfBuilder>
        {
            ["xrefstream"] = new() { Mode = XrefMode.XrefStream },
            ["objstm"] = new() { Mode = XrefMode.XrefStreamWithObjectStreams },
            ["hybrid"] = new() { Mode = XrefMode.Hybrid },
            ["linearized"] = new() { Linearized = true },
            ["incremental"] = new() { IncrementalUpdate = true },
            ["brokenxref"] = new() { BreakXrefOffsets = true },
            ["wronglength"] = new() { WrongStreamLength = true },
            ["junk"] = new() { JunkBeforeHeader = 100 },
            ["signed"] = new() { Signed = true },
            ["tricky-id"] = new() { Id0 = new byte[] { 0xFE, 0xFF, 0x00, 0x5C, 0x28, 0x29, 0x0D, 0x0A } },
            ["no-id"] = new() { IdMode = IdMode.Missing, Version = "1.2" },
        };
        foreach (var (name, builder) in structures)
        {
            var input = builder.Build();
            var title = builder.IncrementalUpdate ? "Updated title" : "Test document";
            foreach (var algorithm in TestHelpers.WritableAlgorithms)
            {
                yield return Make($"{name}-{algorithm}", input, algorithm, "user-pw", "owner-pw", title);
            }
        }

        var plain = new TestPdfBuilder().Build();
        yield return Make("aes256-unicode", plain, PdfEncryptionAlgorithm.Aes256, "пароль密码", "owner-pw");
        yield return Make("aes256-mixed-scripts", plain, PdfEncryptionAlgorithm.Aes256, "ΑΘΗΝΑ-Москва-東京", "ВЛАДЕЛЕЦ");
        yield return Make("aes256-long", plain, PdfEncryptionAlgorithm.Aes256, new string('L', 150), "owner-pw");
        yield return Make("aes256-empty-user", plain, PdfEncryptionAlgorithm.Aes256, "", "owner-pw");
        yield return Make("aes128-empty-user", plain, PdfEncryptionAlgorithm.Aes128, "", "owner-pw");
        yield return Make("rc4-ascii-punct", plain, PdfEncryptionAlgorithm.Rc4_128, "P@ss w0rd!(\\)~", "owner-pw");

        var withXmp = new TestPdfBuilder { Xmp = TestPdfBuilder.PlainXmp() }.Build();
        foreach (var algorithm in TestHelpers.WritableAlgorithms)
        {
            yield return Make($"info-changed-{algorithm}", withXmp, algorithm, "user-pw", "owner-pw", "Changed title ñ 中文", new PdfInfoChanges { Title = "Changed title ñ 中文", Author = "Someone" });
        }

        // Phase 1 editing operations: decrypted, re-secured and edited-in-place outputs.
        foreach (var revision in new[] { 3, 4, 6 })
        {
            var encrypted = DecryptTests.Encrypted(revision);
            yield return new Sample($"decrypted-r{revision}", Pdf.Decrypt(encrypted, "o-pass"), "", "", 2, "Test document", Encrypted: false);
            yield return new Sample($"edit-info-preserved-r{revision}", Pdf.EditInfo(encrypted, new PdfInfoChanges { Title = "Edited in place" }, "o-pass", out _), "u-pass", "o-pass", 2, "Edited in place");
        }

        yield return new Sample("change-security-r3-to-aes256", Pdf.ChangeSecurity(DecryptTests.Encrypted(3), new PdfSecurityChanges { CurrentOwnerPassword = "o-pass", Algorithm = PdfEncryptionAlgorithm.Aes256 }, out _), "u-pass", "o-pass", 2, "Test document");
        yield return new Sample("change-security-new-passwords", Pdf.ChangeSecurity(DecryptTests.Encrypted(6), new PdfSecurityChanges { CurrentOwnerPassword = "o-pass", UserPassword = "new-user", OwnerPassword = "new-owner", Permissions = PdfPermissions.Print }, out _), "new-user", "new-owner", 2, "Test document");
        yield return new Sample("edit-info-unencrypted", Pdf.EditInfo(plain, new PdfInfoChanges { Title = "Edited plain" }, null, out _), "", "", 2, "Edited plain", Encrypted: false);

        // Phase 2: repaired, sanitized and attachment outputs, and rich content (fonts, images, forms, outlines, attachments).
        foreach (var (name, builder) in new Dictionary<string, TestPdfBuilder>
        {
            ["brokenxref"] = new() { BreakXrefOffsets = true },
            ["startxref"] = new() { BreakStartXref = true },
            ["wronglength"] = new() { WrongStreamLength = true },
            ["pagetree"] = new() { WrongPageCount = true, DuplicateKid = true, NoMediaBox = true, DanglingReference = true },
            ["incremental"] = new() { IncrementalUpdate = true, Mode = XrefMode.XrefStreamWithObjectStreams },
        })
        {
            yield return new Sample($"repaired-{name}", Pdf.Repair(builder.Build(), null, out _), "", "", 2, builder.IncrementalUpdate ? "Updated title" : "Test document", Encrypted: false);
        }

        yield return new Sample("repaired-protected-r6", Pdf.Repair(new TestPdfBuilder { Encryption = (6, "u-pass", "o-pass", true), BreakXrefOffsets = true }.Build(), new PdfRepairOptions { Password = "o-pass" }, out _), "u-pass", "o-pass", 2, "Test document");

        var rich = new TestPdfBuilder { Rich = true, Xmp = TestPdfBuilder.PlainXmp() }.Build();
        yield return new Sample("rich-plain", rich, "", "", 2, "Test document", Encrypted: false);
        yield return Make("rich-aes256", rich, PdfEncryptionAlgorithm.Aes256, "user-pw", "owner-pw");
        var everythingButInfo = PdfSanitizeOptions.All();
        everythingButInfo.RemoveDocumentInfo = false;
        yield return new Sample("sanitized-all", Pdf.Sanitize(rich, everythingButInfo, out _), "", "", 2, "Test document", Encrypted: false);
        yield return new Sample("sanitized-js-only", Pdf.Sanitize(rich, new PdfSanitizeOptions(), out _), "", "", 2, "Test document", Encrypted: false);
        var richEncrypted = new TestPdfBuilder { Rich = true, Encryption = (4, "u-pass", "o-pass", true), Version = "1.6" }.Build();
        yield return new Sample("sanitized-preserved-r4", Pdf.Sanitize(richEncrypted, new PdfSanitizeOptions { Password = "o-pass", RemoveAttachments = true }, out _), "u-pass", "o-pass", 2, "Test document");

        var attachment = new PdfAddAttachmentOptions { Name = "invoice.xml", Content = System.Text.Encoding.UTF8.GetBytes("<invoice/>"), MimeType = "application/xml", Relationship = PdfAttachmentRelationship.Data };
        yield return new Sample("attachment-added", Pdf.AddAttachment(rich, attachment), "", "", 2, "Test document", Encrypted: false);
        yield return new Sample("attachment-added-v10", Pdf.AddAttachment(new TestPdfBuilder { Version = "1.0" }.Build(), attachment), "", "", 2, "Test document", Encrypted: false);
        attachment.Password = "o-pass";
        yield return new Sample("attachment-added-preserved-r6", Pdf.AddAttachment(DecryptTests.Encrypted(6), attachment), "u-pass", "o-pass", 2, "Test document");
        yield return new Sample("attachment-removed-preserved-r3", Pdf.RemoveAttachment(DecryptTests.Encrypted(3), "data.bin", "o-pass"), "u-pass", "o-pass", 2, "Test document");

        // Phase 3: compressed output (object streams + cross-reference stream) and incremental updates.
        var compressed = new PdfWriteOptions { UseObjectStreams = true };
        foreach (var algorithm in TestHelpers.WritableAlgorithms)
        {
            var options = TestHelpers.Options(algorithm, "user-pw", "owner-pw");
            options.Write = compressed;
            yield return new Sample($"compressed-{algorithm}", Pdf.Protect(rich, options), "user-pw", "owner-pw", 2, "Test document");
        }

        yield return new Sample("compressed-decrypted", Pdf.Decrypt(DecryptTests.Encrypted(4), new PdfDecryptOptions { Password = "o-pass", Write = compressed }, out _), "", "", 2, "Test document", Encrypted: false);
        yield return new Sample("compressed-v10-repaired", Pdf.Repair(new TestPdfBuilder { Version = "1.0", BreakXrefOffsets = true }.Build(), new PdfRepairOptions { Write = compressed }, out _), "", "", 2, "Test document", Encrypted: false);
        yield return new Sample("compressed-preserved-r6", Pdf.EditInfo(DecryptTests.Encrypted(6), new PdfEditInfoOptions { Changes = new PdfInfoChanges { Title = "Compressed R6" }, Password = "o-pass", Write = compressed }, out _), "u-pass", "o-pass", 2, "Compressed R6");
        foreach (var (name, builder) in new Dictionary<string, TestPdfBuilder>
        {
            ["classic"] = new(),
            ["xrefstream"] = new() { Mode = XrefMode.XrefStreamWithObjectStreams },
            ["hybrid"] = new() { Mode = XrefMode.Hybrid },
            ["linearized"] = new() { Linearized = true },
            ["signed"] = new() { Signed = true, RealByteRange = true },
        })
        {
            yield return new Sample($"incremental-{name}", Pdf.EditInfo(builder.Build(), new PdfEditInfoOptions { Changes = new PdfInfoChanges { Title = "Incremental title" }, SaveMode = PdfSaveMode.Incremental }, out _), "", "", 2, "Incremental title", Encrypted: false);
        }

        yield return new Sample("incremental-attachment", Pdf.AddAttachment(rich, new PdfAddAttachmentOptions { Name = "added.txt", Content = new byte[] { 65, 66 }, SaveMode = PdfSaveMode.Incremental }), "", "", 2, "Test document", Encrypted: false);

        // Phase 4: page operations.
        var linked = new TestPdfBuilder { Rich = true, InternalLinks = true }.Build();
        yield return new Sample("rotated", Pdf.RotatePages(linked, new PdfRotatePagesOptions { Angle = 90 }, out _), "", "", 2, "Test document", Encrypted: false);
        yield return new Sample("rotated-preserved-r6", Pdf.RotatePages(DecryptTests.Encrypted(6), new PdfRotatePagesOptions { Angle = 270, Password = "o-pass" }, out _), "u-pass", "o-pass", 2, "Test document");
        yield return new Sample("deleted", Pdf.DeletePages(new TestPdfBuilder { PageCount = 4, Rich = true, InternalLinks = true }.Build(), new PdfDeletePagesOptions { Pages = PdfPageRange.Parse("2-3") }, out _), "", "", 2, "Test document", Encrypted: false);
        yield return new Sample("merged", Pdf.Merge(new[] { PdfMergeInput.FromBytes(linked), PdfMergeInput.FromBytes(new TestPdfBuilder { Mode = XrefMode.XrefStreamWithObjectStreams }.Build()) }, new PdfMergeOptions { DeduplicateResources = true }, out _), "", "", 4, "Test document", Encrypted: false);
        yield return new Sample("merged-protected", Pdf.Merge(new[] { PdfMergeInput.FromBytes(DecryptTests.Encrypted(4), "o-pass"), PdfMergeInput.FromBytes(linked) }, new PdfMergeOptions { Protection = TestHelpers.Options(PdfEncryptionAlgorithm.Aes256, "user-pw", "owner-pw") }, out _), "user-pw", "owner-pw", 4, "Test document");
        yield return new Sample("split-part", Pdf.Split(new TestPdfBuilder { PageCount = 4, Rich = true, InternalLinks = true }.Build(), new PdfSplitOptions { EveryNPages = 2 }, out _)[0], "", "", 2, "Test document", Encrypted: false);

        // Phase 5: stamps.
        yield return new Sample("stamp-text", Pdf.Stamp(linked, new PdfStampOptions { Text = "CONFIDENTIAL\nDo not copy" }, out _), "", "", 2, "Test document", Encrypted: false);
        yield return new Sample("stamp-under-r6", Pdf.Stamp(DecryptTests.Encrypted(6), new PdfStampOptions { Text = "Background", Layer = PdfStampLayer.Under, Password = "o-pass" }, out _), "u-pass", "o-pass", 2, "Test document");
        var stampPng = PdfUtil.Internal.Images.ImageCodecs.EncodePng(2, 2, 4, new byte[] { 255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 64, 0, 0, 0, 0 });
        yield return new Sample("stamp-png", Pdf.Stamp(new TestPdfBuilder().Build(), new PdfStampOptions { Image = stampPng, ImageWidth = 144, Position = PdfStampPosition.TopRight }, out _), "", "", 2, "Test document", Encrypted: false);

#if !NETFRAMEWORK
        // Phase 6: signed documents (certificate-encrypted ones are not supported by pdf.js or PDFKit). The test
        // certificates need .NET 8 APIs.
        var signedOnce = SignatureTests.Sign(new TestPdfBuilder().Build(), new PdfUtil.Signatures.PdfSignOptions { Certificate = TestCertificates.Rsa() }, out _);
        yield return new Sample("signed-invisible", signedOnce, "", "", 2, "Test document", Encrypted: false);
        yield return new Sample("signed-twice", SignatureTests.Sign(signedOnce, new PdfUtil.Signatures.PdfSignOptions { Certificate = TestCertificates.Ecdsa(), Appearance = new PdfUtil.Signatures.PdfSignatureAppearance { Page = 2 } }, out _), "", "", 2, "Test document", Encrypted: false);
        yield return new Sample("signed-visible-objstm", SignatureTests.Sign(new TestPdfBuilder { Mode = XrefMode.XrefStreamWithObjectStreams, Rich = true }.Build(), new PdfUtil.Signatures.PdfSignOptions { Certificate = TestCertificates.Rsa(), FieldName = "approval", Appearance = new PdfUtil.Signatures.PdfSignatureAppearance() }, out _), "", "", 2, "Test document", Encrypted: false);
#endif

        // Phase 7c: HTML converted to PDF (embedded TrueType subsets, tables, images, links, bookmarks, footers).
        foreach (var (name, pdf) in HtmlToPdfTests.InteropSamples())
        {
            yield return new Sample(name, pdf, "", "", 2, "Test document", Encrypted: false);
            yield return Make(name + "-aes256", pdf, PdfEncryptionAlgorithm.Aes256, "user-pw", "owner-pw");
        }

        // Phase 7d: Word converted to PDF (sections, headers and footers, lists, tables, images).
        foreach (var (name, pdf) in DocxToPdfTests.InteropSamples())
        {
            var pages = Pdf.GetInfo(pdf).PageCount ?? 2;
            yield return new Sample(name, pdf, "", "", pages, "Test document", Encrypted: false);
        }

        // Phase 7c follow-up: a Unicode text stamp with an embedded TrueType subset.
        yield return new Sample("stamp-unicode", Pdf.Stamp(new TestPdfBuilder().Build(), new PdfStampOptions { Text = "Привет 日本語 😀", FontData = TestFont.Build() }, out _), "", "", 2, "Test document", Encrypted: false);

        var utf16 = new TestPdfBuilder();
        utf16.Info["Title"] = ("中文 ñ 😀", TextEncodingKind.Utf16Be);
        yield return Make("utf16-title", utf16.Build(), PdfEncryptionAlgorithm.Aes256, "user-pw", "owner-pw", "中文 ñ 😀");
    }

    private static Sample Make(string name, byte[] input, PdfEncryptionAlgorithm algorithm, string user, string owner, string title = "Test document", PdfInfoChanges? changes = null)
    {
        var options = TestHelpers.Options(algorithm, user, owner);
        options.InfoChanges = changes;
        var (output, _) = TestHelpers.Protect(input, options);
        return new Sample(name, output, user, owner, 2, title);
    }

    private static string WriteCorpus(out List<Sample> samples)
    {
        var dir = Directory.CreateTempSubdirectory("pdfutil-interop-").FullName;
        samples = Corpus().ToList();
        foreach (var s in samples)
        {
            File.WriteAllBytes(Path.Combine(dir, s.Name + ".pdf"), s.Pdf);
        }

        return dir;
    }

    private static string WriteManifest(string dir, List<Sample> samples)
    {
        var manifest = Path.Combine(dir, "manifest.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(samples.Select(s => new
        {
            file = Path.Combine(dir, s.Name + ".pdf"),
            name = s.Name,
            user = s.User,
            owner = s.Owner,
            pages = s.Pages,
            title = s.Title,
            encrypted = s.Encrypted,
        })));
        return manifest;
    }

    [RequiresToolFact("qpdf")]
    public void Qpdf_CheckSucceeds()
    {
        var qpdf = TestHelpers.FindTool("qpdf")!;
        var dir = WriteCorpus(out var samples);
        try
        {
            foreach (var s in samples)
            {
                var path = Path.Combine(dir, s.Name + ".pdf");
                foreach (var password in new[] { s.User, s.Owner })
                {
                    var (code, output) = TestHelpers.Run(qpdf, "--check", "--password=" + password, path);
                    Assert.True(code == 0, $"qpdf --check failed for {s.Name}: {output}");
                    Assert.Contains("No syntax or stream encoding errors", output, StringComparison.Ordinal);
                }

                if (s.Encrypted && s.User.Length > 0)
                {
                    var (badCode, _) = TestHelpers.Run(qpdf, "--check", "--password=wrong", path);
                    Assert.NotEqual(0, badCode);
                }
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [RequiresToolFact("mutool")]
    public void Mutool_InfoSucceeds()
    {
        var mutool = TestHelpers.FindTool("mutool")!;
        var dir = WriteCorpus(out var samples);
        try
        {
            foreach (var s in samples)
            {
                var (code, output) = TestHelpers.Run(mutool, "info", "-p", s.User, Path.Combine(dir, s.Name + ".pdf"));
                Assert.True(code == 0, $"mutool info failed for {s.Name}: {output}");
                Assert.Contains($"Pages: {s.Pages}", output, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [RequiresToolFact("node")]
    public void PdfJs_OpensDocuments()
    {
        var pdfjsDir = Environment.GetEnvironmentVariable("PDFUTIL_PDFJS_DIR");
        if (string.IsNullOrEmpty(pdfjsDir) || !Directory.Exists(Path.Combine(pdfjsDir, "node_modules", "pdfjs-dist")))
        {
            return; // pdf.js not installed: nothing to check (documented in the class summary)
        }

        var dir = WriteCorpus(out var samples);
        try
        {
            var manifest = WriteManifest(dir, samples);
            var script = Path.Combine(pdfjsDir, "pdfutil-check.mjs");
            File.WriteAllText(script, """
                import * as pdfjs from 'pdfjs-dist/legacy/build/pdf.mjs';
                import { readFileSync } from 'node:fs';
                const items = JSON.parse(readFileSync(process.argv[2], 'utf8'));
                const failures = [];
                for (const it of items) {
                  for (const password of [it.user, it.owner]) {
                    try {
                      const doc = await pdfjs.getDocument({ data: new Uint8Array(readFileSync(it.file)), password, verbosity: 0 }).promise;
                      const page = await doc.getPage(1);
                      const text = (await page.getTextContent()).items.map(i => i.str).join(' ');
                      const meta = await doc.getMetadata();
                      if (doc.numPages !== it.pages) failures.push(`${it.name}: pages ${doc.numPages}`);
                      if (meta.info.Title !== it.title) failures.push(`${it.name}: title '${meta.info.Title}'`);
                      if (!/(Hello|Updated) page 1/.test(text)) failures.push(`${it.name}: text '${text}'`);
                      await doc.destroy();
                    } catch (e) { failures.push(`${it.name} (${password === it.user ? 'user' : 'owner'}): ${e.name} ${e.message}`); }
                  }
                  if (it.encrypted && it.user.length > 0) {
                    try {
                      await pdfjs.getDocument({ data: new Uint8Array(readFileSync(it.file)), password: 'definitely-wrong', verbosity: 0 }).promise;
                      failures.push(`${it.name}: wrong password accepted`);
                    } catch (e) { if (e.name !== 'PasswordException') failures.push(`${it.name}: ${e.name}`); }
                  }
                }
                console.log(failures.length === 0 ? `OK ${items.length}` : failures.join('\n'));
                process.exit(failures.length === 0 ? 0 : 1);
                """);
            var (code, output) = TestHelpers.Run(TestHelpers.FindTool("node")!, script, manifest);
            Assert.True(code == 0, "pdf.js failures:\n" + output);
            Assert.Contains($"OK {samples.Count}", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
    /// <summary>F15: our text equals pdf.js's getTextContent, with whitespace removed (the two place spaces differently).</summary>
    [RequiresToolFact("node")]
    public void PdfJs_TextMatchesExtraction()
    {
        var pdfjsDir = Environment.GetEnvironmentVariable("PDFUTIL_PDFJS_DIR");
        if (string.IsNullOrEmpty(pdfjsDir) || !Directory.Exists(Path.Combine(pdfjsDir, "node_modules", "pdfjs-dist")))
        {
            return;
        }

        var dir = Directory.CreateTempSubdirectory("pdfutil-text-").FullName;
        try
        {
            var docs = TextFixtures.All().Select(f => (f.Name, f.Pdf)).ToList();
            docs.Add(("builder", new TestPdfBuilder { PageCount = 3 }.Build()));
            docs.Add(("builder-objstm", new TestPdfBuilder { Mode = XrefMode.XrefStreamWithObjectStreams }.Build()));
            var expected = new Dictionary<string, string>();
            foreach (var (name, pdf) in docs)
            {
                File.WriteAllBytes(Path.Combine(dir, name + ".pdf"), pdf);
                expected[name] = Strip(Pdf.ExtractText(pdf).Text);
            }

            var script = Path.Combine(pdfjsDir, "pdfutil-text.mjs");
            File.WriteAllText(script, """
                import * as pdfjs from 'pdfjs-dist/legacy/build/pdf.mjs';
                import { readFileSync, readdirSync } from 'node:fs';
                import { join } from 'node:path';
                const dir = process.argv[2];
                const cMapUrl = join(process.argv[3], 'node_modules/pdfjs-dist/cmaps/');
                const out = {};
                for (const f of readdirSync(dir).filter(f => f.endsWith('.pdf'))) {
                  const doc = await pdfjs.getDocument({ data: new Uint8Array(readFileSync(join(dir, f))), cMapUrl, cMapPacked: true, verbosity: 0 }).promise;
                  let text = '';
                  for (let i = 1; i <= doc.numPages; i++) {
                    const page = await doc.getPage(i);
                    text += (await page.getTextContent()).items.map(x => x.str).join('');
                  }
                  out[f.replace('.pdf', '')] = text;
                  await doc.destroy();
                }
                console.log(JSON.stringify(out));
                """);
            var (code, output) = TestHelpers.Run(TestHelpers.FindTool("node")!, script, dir, pdfjsDir);
            Assert.True(code == 0, output);
            var json = output.Trim().Split('\n').Last();
            var actual = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
            var failures = expected.Where(e => Strip(actual[e.Key]) != e.Value).Select(e => $"{e.Key}: pdf.js '{actual[e.Key]}' vs PdfUtil '{e.Value}'").ToList();
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        static string Strip(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    /// <summary>Apple PDFKit (Safari, Preview, iOS): runs a Swift script on macOS.</summary>
    [RequiresToolFact("swift")]
    public void PdfKit_OpensDocuments()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var dir = WriteCorpus(out var samples);
        try
        {
            var manifest = WriteManifest(dir, samples);
            var script = Path.Combine(dir, "check.swift");
            File.WriteAllText(script, """
                import Foundation
                import PDFKit
                struct Item: Decodable { let file, name, user, owner, title: String; let pages: Int; let encrypted: Bool }
                let items = try! JSONDecoder().decode([Item].self, from: Data(contentsOf: URL(fileURLWithPath: CommandLine.arguments[1])))
                var failures: [String] = []
                for it in items {
                    for (role, password) in [("user", it.user), ("owner", it.owner)] {
                        guard let doc = PDFDocument(url: URL(fileURLWithPath: it.file)) else { failures.append("\(it.name): cannot open"); continue }
                        if doc.isEncrypted != it.encrypted { failures.append("\(it.name): isEncrypted \(doc.isEncrypted)") }
                        if doc.isLocked && !doc.unlock(withPassword: password) { failures.append("\(it.name) (\(role)): unlock failed"); continue }
                        if doc.pageCount != it.pages { failures.append("\(it.name): pages \(doc.pageCount)") }
                        let title = doc.documentAttributes?[PDFDocumentAttribute.titleAttribute] as? String
                        if title != it.title { failures.append("\(it.name): title \(title ?? "nil")") }
                        let text = doc.page(at: 0)?.string ?? ""
                        if !(text.contains("Hello page 1") || text.contains("Updated page 1")) { failures.append("\(it.name): text '\(text)'") }
                    }
                    if it.encrypted, !it.user.isEmpty, let doc = PDFDocument(url: URL(fileURLWithPath: it.file)), doc.unlock(withPassword: "definitely-wrong") {
                        failures.append("\(it.name): wrong password accepted")
                    }
                }
                print(failures.isEmpty ? "OK \(items.count)" : failures.joined(separator: "\n"))
                exit(failures.isEmpty ? 0 : 1)
                """);
            var (code, output) = TestHelpers.Run(TestHelpers.FindTool("swift")!, script, manifest);
            Assert.True(code == 0, "PDFKit failures:\n" + output);
            Assert.Contains($"OK {samples.Count}", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
