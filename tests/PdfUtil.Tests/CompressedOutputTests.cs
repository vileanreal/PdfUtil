using PdfUtil.Internal.Document;
using PdfUtil.Internal.Objects;
using PdfUtil.Tests.Infrastructure;
using static PdfUtil.Tests.InspectionTests;

namespace PdfUtil.Tests;

/// <summary>F11: compressed output (object streams and a cross-reference stream).</summary>
public class CompressedOutputTests
{
    private static readonly PdfWriteOptions Compressed = new() { UseObjectStreams = true };

    /// <summary>The output has one cross-reference stream, object streams, and the expected objects inside them.</summary>
    internal static void AssertCompressedLayout(byte[] output, string? password = null)
    {
        using var doc = TestHelpers.Open(output, strict: true);
        Assert.Equal(1, doc.XrefSectionCount);
        Assert.True(doc.LatestSectionIsStream);
        Assert.True(doc.HasObjectStreams);
        if (doc.IsEncrypted)
        {
            Assert.NotEqual(PdfPasswordMatch.None, doc.Authenticate(password));
        }

        PdfVersion.TryParse(doc.HeaderVersion, out var version);
        Assert.True(version >= new PdfVersion(1, 5));

        foreach (var (number, entry) in doc.Xref)
        {
            var obj = doc.GetObject(number);
            switch (entry.Type)
            {
                case XrefEntryType.Compressed:
                    Assert.IsNotType<PdfStream>(obj);
                    Assert.False(obj is PdfDictionary d && SignatureHelper.IsSignatureDictionary(d), "signature dictionaries must not be packed");
                    Assert.NotEqual(doc.EncryptObjectNumber, number);
                    break;
                case XrefEntryType.InFile when obj is PdfDictionary plain:
                    Assert.True(SignatureHelper.IsSignatureDictionary(plain) || number == doc.EncryptObjectNumber || doc.GetGeneration(number) != 0, $"object {number} should have been packed");
                    break;
            }
        }
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256)]
    [InlineData(PdfEncryptionAlgorithm.Aes128)]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128)]
    public void Protect_EveryAlgorithm(PdfEncryptionAlgorithm algorithm)
    {
        var input = new TestPdfBuilder { Rich = true, Xmp = TestPdfBuilder.PlainXmp(), Signed = true }.Build();
        var options = TestHelpers.Options(algorithm);
        options.PdfAPolicy = PdfAPolicy.Proceed;
        var plain = Pdf.Protect(input, options);
        options.Write = Compressed;
        var output = Pdf.Protect(input, options, out var result);

        Assert.True(output.Length < plain.Length, $"{output.Length} >= {plain.Length}");
        Assert.Equal("1.7", result.OutputVersion); // the builder writes 1.7; RC4 alone would only need 1.5 for object streams
        AssertCompressedLayout(output, "user");
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, "user").MatchedAs);
        Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(output, "owner").MatchedAs);
        Assert.False(Pdf.VerifyPassword(output, "wrong").IsValid);

        // Same content as the classic layout.
        var a = Pdf.Inspect(plain, new PdfInspectionOptions { Password = "user" });
        var b = Pdf.Inspect(output, new PdfInspectionOptions { Password = "user" });
        Assert.Equal(a.Info!.Entries, b.Info!.Entries);
        Assert.Equal(a.FormFields.Select(f => f.ToString()), b.FormFields.Select(f => f.ToString()));
        Assert.Equal(a.Actions, b.Actions);
        Assert.Equal(a.Attachments.Select(x => x.ToString()), b.Attachments.Select(x => x.ToString()));
        Assert.Equal(TestPdfBuilder.AttachmentB, Pdf.ExtractAttachment(output, "b.txt", "user"));
        ContentPreservationTests.AssertStreamsPreserved(input, null, output, "user");
    }

    [Fact]
    public void EncryptionRules_ObjectStreamEncryptedAsAWhole_XrefStreamNever()
    {
        var input = new TestPdfBuilder { Info = { ["Title"] = ("A secret title", TextEncodingKind.PdfDoc) } }.Build();
        var options = TestHelpers.Options(PdfEncryptionAlgorithm.Aes256);
        options.Write = Compressed;
        var output = Pdf.Protect(input, options);
        var text = System.Text.Encoding.Latin1.GetString(output);
        Assert.DoesNotContain("A secret title", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello page", text, StringComparison.Ordinal);

        using var doc = TestHelpers.Open(output, strict: true);
        var xrefNumber = doc.Xref.Where(e => e.Value.Type == XrefEntryType.InFile).Select(e => e.Key)
            .Single(n => doc.GetObject(n) is PdfStream { Dictionary: var d } && d["Type"] is PdfName { Value: "XRef" });

        // Readable without the password: the cross-reference stream is not encrypted and its /ID is plain.
        var xref = (PdfStream)doc.GetObject(xrefNumber)!;
        Assert.NotNull(doc.GetDecodedStreamData(xref, new ObjectId(xrefNumber, 0)));
        Assert.IsType<PdfString>(((PdfArray)xref.Dictionary["ID"]!)[0]);
        Assert.Equal(Pdf.GetInfo(input).PermanentId, Pdf.GetInfo(output, "user").PermanentId);
    }

    [Fact]
    public void Unprotected_Operations()
    {
        var rich = new TestPdfBuilder { Rich = true, Version = "1.4" }.Build();
        var encrypted = DecryptTests.Encrypted(4);

        var decrypted = Pdf.Decrypt(encrypted, new PdfDecryptOptions { Password = "o-pass", Write = Compressed }, out var decryptResult);
        AssertCompressedLayout(decrypted);
        Assert.Equal("1.5", decryptResult.OutputVersion);

        var edited = Pdf.EditInfo(rich, new PdfEditInfoOptions { Changes = new PdfInfoChanges { Title = "Compressed" }, Write = Compressed }, out _);
        AssertCompressedLayout(edited);
        Assert.Equal("Compressed", Pdf.GetInfo(edited).Info!.Title);

        var repaired = Pdf.Repair(new TestPdfBuilder { BreakXrefOffsets = true }.Build(), new PdfRepairOptions { Write = Compressed }, out var repair);
        AssertCompressedLayout(repaired);
        Assert.DoesNotContain(repair.Repairs, r => r.StartsWith("Rewrote", StringComparison.Ordinal));
        Assert.True(Pdf.Validate(repaired).IsValid);

        var sanitized = Pdf.Sanitize(rich, new PdfSanitizeOptions { Write = Compressed }, out _);
        AssertCompressedLayout(sanitized);
        Assert.False(Pdf.Inspect(sanitized).HasJavaScript);

        var attached = Pdf.AddAttachment(rich, new PdfAddAttachmentOptions { Name = "x.txt", Content = new byte[] { 1, 2, 3 }, Write = Compressed });
        AssertCompressedLayout(attached);
        Assert.Equal(new byte[] { 1, 2, 3 }, Pdf.ExtractAttachment(attached, "x.txt"));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public void PreservedEncryption(int revision)
    {
        var output = Pdf.EditInfo(Rich(encryption: (revision, "u", "o", true)), new PdfEditInfoOptions { Changes = new PdfInfoChanges { Title = "T" }, Password = "o", Write = Compressed }, out var result);
        Assert.Equal(revision, result.Revision);
        AssertCompressedLayout(output, "u");
        Assert.Equal("T", Pdf.GetInfo(output, "u").Info!.Title);

        var changed = Pdf.ChangeSecurity(output, new PdfSecurityChanges { CurrentOwnerPassword = "o", UserPassword = "new", Write = Compressed }, out _);
        AssertCompressedLayout(changed, "new");
    }

    [Fact]
    public void ManyObjects_SpanSeveralObjectStreams()
    {
        var input = new TestPdfBuilder { PageCount = 150 }.Build();
        var output = Pdf.Protect(input, new PdfProtectionOptions { UserPassword = "u", Write = Compressed });
        using var doc = TestHelpers.Open(output, strict: true);
        doc.Authenticate("u");
        Assert.True(doc.Xref.Values.Where(e => e.Type == XrefEntryType.Compressed).Select(e => e.StreamNumber).Distinct().Count() >= 3);
        Assert.Equal(150, Pdf.GetInfo(output, "u").PageCount);
    }

    [Fact]
    public void EveryInputStructure()
    {
        foreach (var mode in Enum.GetValues<XrefMode>())
        {
            var input = new TestPdfBuilder { Mode = mode, IncrementalUpdate = mode == XrefMode.Classic }.Build();
            var output = Pdf.Protect(input, new PdfProtectionOptions { UserPassword = "u", Write = Compressed });
            AssertCompressedLayout(output, "u");
        }
    }
}
