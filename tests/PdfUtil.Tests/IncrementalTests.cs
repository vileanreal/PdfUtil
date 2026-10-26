using System.Text;
using PdfUtil.Internal;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;
using PdfUtil.Internal.Protection;
using PdfUtil.Internal.Writing;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>F12: incremental save mode.</summary>
public class IncrementalTests
{
    private static readonly PdfInfoChanges Title = new() { Title = "Appended title" };

    private static PdfEditInfoOptions Incremental(PdfInfoChanges? changes = null) => new() { Changes = changes ?? Title, SaveMode = PdfSaveMode.Incremental };

    /// <summary>The output starts with the input byte for byte, has one more revision, and keeps /ID[0].</summary>
    private static void AssertAppended(byte[] input, byte[] output)
    {
        Assert.True(output.Length > input.Length);
        Assert.True(output.AsSpan(0, input.Length).SequenceEqual(input), "the original bytes changed");
        var before = Pdf.GetInfo(input);
        var after = Pdf.GetInfo(output);
        Assert.Equal(before.IncrementalUpdateCount + 1, after.IncrementalUpdateCount);
        Assert.False(after.WasRepaired);
        Assert.Equal(before.PermanentIdHex ?? after.PermanentIdHex, after.PermanentIdHex);
        Assert.NotEqual(before.ChangingIdHex, after.ChangingIdHex);
        Assert.Equal(before.PageCount, after.PageCount);
        var report = Pdf.Validate(output);
        Assert.True(report.IsValid, string.Join("\n", report.Issues));
    }

    public static IEnumerable<object[]> Layouts() => Enum.GetValues<XrefMode>().Select(m => new object[] { m });

    [Theory]
    [MemberData(nameof(Layouts))]
    public void EditInfo_AppendsAnUpdate_InTheSourceStyle(XrefMode mode)
    {
        var input = new TestPdfBuilder { Mode = mode, Xmp = TestPdfBuilder.PlainXmp() }.Build();
        var output = Pdf.EditInfo(input, Incremental(), out var result);
        Assert.Equal(PdfSaveMode.Incremental, result.SaveMode);
        Assert.True(result.DocumentInfoChanged);
        Assert.True(result.XmpUpdated);
        AssertAppended(input, output);

        var info = Pdf.GetInfo(output);
        Assert.Equal("Appended title", info.Info!.Title);
        Assert.Equal("Appended title", info.Xmp!.Title);
        Assert.Equal("PdfUtil tests", info.Info.Author);

        using var source = TestHelpers.Open(input);
        using var doc = TestHelpers.Open(output);
        Assert.Equal(source.LatestSectionIsStream, doc.LatestSectionIsStream);
        Assert.Equal(mode is XrefMode.XrefStream or XrefMode.XrefStreamWithObjectStreams, doc.LatestSectionIsStream);

        // The previous revision is still there: the update's trailer points back at it.
        var tail = Encoding.Latin1.GetString(output, input.Length, output.Length - input.Length);
        Assert.Contains($"/Prev {source.StartXref}", tail, StringComparison.Ordinal);
    }

    [Fact]
    public void EditInfo_IsMuchSmallerThanARewrite_AndTwoUpdatesChain()
    {
        var input = new TestPdfBuilder { Rich = true }.Build();
        var once = Pdf.EditInfo(input, Incremental(), out _);
        Assert.True(once.Length - input.Length < 1000);

        var twice = Pdf.EditInfo(once, Incremental(new PdfInfoChanges { Author = "Second" }), out _);
        AssertAppended(once, twice);
        Assert.Equal(2, Pdf.GetInfo(twice).IncrementalUpdateCount);
        Assert.Equal("Appended title", Pdf.GetInfo(twice).Info!.Title);
        Assert.Equal("Second", Pdf.GetInfo(twice).Info!.Author);
    }

    [Fact]
    public void EditInfo_NoInfoDictionary_AndNoId()
    {
        var input = new TestPdfBuilder { OmitInfo = true, IdMode = IdMode.Missing }.Build();
        var output = Pdf.EditInfo(input, Incremental(), out _);
        Assert.True(output.AsSpan(0, input.Length).SequenceEqual(input));
        var info = Pdf.GetInfo(output);
        Assert.Equal("Appended title", info.Info!.Title);
        Assert.NotNull(info.PermanentId);
    }

    [Fact]
    public void EditInfo_FileWithoutTrailingNewline()
    {
        var input = new TestPdfBuilder().Build();
        var trimmed = input[..^1]; // drop the final '\n' after %%EOF
        var output = Pdf.EditInfo(trimmed, Incremental(), out _);
        Assert.Equal("Appended title", Pdf.GetInfo(output).Info!.Title);
        Assert.Equal((byte)'\n', output[trimmed.Length]);
    }

    [Fact]
    public void Signed_SignatureBytesStayIntact()
    {
        var input = new TestPdfBuilder { Signed = true, RealByteRange = true }.Build();
        Assert.True(Assert.Single(Pdf.Inspect(input).Signatures).CoversWholeFile);
        var output = Pdf.EditInfo(input, Incremental(), out var result);
        Assert.Contains(result.Warnings, w => w.Contains("signed bytes intact", StringComparison.Ordinal));

        var signature = Assert.Single(Pdf.Inspect(output).Signatures);
        Assert.False(signature.CoversWholeFile); // changed after signing, as a viewer would report
        Assert.Equal(input.Length, signature.ByteRange[2] + signature.ByteRange[3]); // but the signed bytes are unchanged
        Assert.True(output.AsSpan(0, input.Length).SequenceEqual(input));
    }

    [Fact]
    public void Attachments_AddAndRemove()
    {
        var input = new TestPdfBuilder { Version = "1.2" }.Build();
        var added = Pdf.AddAttachment(input, new PdfAddAttachmentOptions { Name = "a.txt", Content = Encoding.ASCII.GetBytes("abc"), SaveMode = PdfSaveMode.Incremental }, out var addResult);
        Assert.Equal(PdfSaveMode.Incremental, addResult.SaveMode);
        Assert.Equal("1.3", addResult.OutputVersion);
        AssertAppended(input, added);
        Assert.Equal("1.3", Pdf.GetInfo(added).EffectiveVersion); // through the catalog /Version; the header is unchanged
        Assert.Equal("%PDF-1.2", TestHelpers.HeaderOf(added));
        Assert.Equal(Encoding.ASCII.GetBytes("abc"), Pdf.ExtractAttachment(added, "a.txt"));

        var removed = Pdf.RemoveAttachment(added, new PdfRemoveAttachmentOptions { Name = "data.bin", SaveMode = PdfSaveMode.Incremental }, out var removeResult);
        AssertAppended(added, removed);
        Assert.Equal(new[] { "a.txt" }, Pdf.ListAttachments(removed).Select(a => a.Name));
        Assert.Contains(removeResult.Warnings, w => w.Contains("still in the file's earlier revision", StringComparison.Ordinal));
    }

    [Fact]
    public void Attachments_KidsTreeAndXrefStreams()
    {
        var input = new TestPdfBuilder { Rich = true, Mode = XrefMode.XrefStreamWithObjectStreams }.Build();
        var output = Pdf.AddAttachment(input, new PdfAddAttachmentOptions { Name = "c.txt", Content = new byte[] { 7 }, SaveMode = PdfSaveMode.Incremental, Relationship = PdfAttachmentRelationship.Source });
        AssertAppended(input, output);
        Assert.Equal(new[] { "a.txt", "b.txt", "c.txt", "data.bin", "note.txt" }, Pdf.ListAttachments(output).Select(a => a.Name));
        Assert.Equal(TestPdfBuilder.AttachmentA, Pdf.ExtractAttachment(output, "a.txt"));
    }

    [Fact]
    public void Unsupported_EncryptedOrDamaged()
    {
        Assert.Throws<NotSupportedException>(() => Pdf.EditInfo(DecryptTests.Encrypted(6), new PdfEditInfoOptions { Changes = Title, Password = "o-pass", SaveMode = PdfSaveMode.Incremental }, out _));
        Assert.Throws<NotSupportedException>(() => Pdf.AddAttachment(DecryptTests.Encrypted(4), new PdfAddAttachmentOptions { Name = "x", Content = new byte[1], Password = "o-pass", SaveMode = PdfSaveMode.Incremental }));
        Assert.Throws<NotSupportedException>(() => Pdf.EditInfo(new TestPdfBuilder { BreakXrefOffsets = true }.Build(), Incremental(), out _));
        Assert.Throws<NotSupportedException>(() => Pdf.EditInfo(new TestPdfBuilder { BreakStartXref = true }.Build(), Incremental(), out _));
        Assert.Throws<ArgumentException>(() => Pdf.EditInfo(new TestPdfBuilder().Build(), new PdfEditInfoOptions { Changes = Title, SaveMode = PdfSaveMode.Incremental, Write = new PdfWriteOptions { UseObjectStreams = true } }, out _));
        Assert.Throws<ArgumentException>(() => Pdf.EditInfo(new TestPdfBuilder().Build(), new PdfEditInfoOptions { Changes = Title, SaveMode = (PdfSaveMode)7 }, out _));
    }

    [Fact]
    public void Linearized_IsAppendedWithAWarning()
    {
        var input = new TestPdfBuilder { Linearized = true }.Build();
        var output = Pdf.EditInfo(input, Incremental(), out var result);
        Assert.True(output.AsSpan(0, input.Length).SequenceEqual(input));
        Assert.Contains(result.Warnings, w => w.Contains("linearized", StringComparison.Ordinal));
        Assert.Equal("Appended title", Pdf.GetInfo(output).Info!.Title);
    }

    [Fact]
    public async Task FileApi_SamePath()
    {
        var path = Path.Combine(Path.GetTempPath(), "pdfutil-incremental-" + Guid.NewGuid().ToString("N") + ".pdf");
        var input = new TestPdfBuilder().Build();
        await File.WriteAllBytesAsync(path, input);
        try
        {
            var stages = new HashSet<PdfProgressStage>();
            var options = Incremental();
            options.Progress = new RepairTests.SyncProgress(p => stages.Add(p.Stage));
            await new PdfEditor().EditInfoFileAsync(path, path, options);
            var output = await File.ReadAllBytesAsync(path);
            Assert.True(output.AsSpan(0, input.Length).SequenceEqual(input));
            Assert.Contains(PdfProgressStage.Writing, stages);
            Assert.Contains(PdfProgressStage.Verifying, stages);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The Phase 6 signing hook: reserve space for a byte range and a signature value, write the update, then fill
    /// both in before verification. The result is a signature whose byte range covers the whole file except its value.
    /// </summary>
    [Fact]
    public void ReservedSpace_IsFilledAfterWriting()
    {
        var input = new TestPdfBuilder().Build();
        using var doc = PdfDocument.Open(PdfSource.FromBytes(input), new PdfUtilOptions(), PdfLog.None, strict: false, CancellationToken.None);
        var edits = new ObjectEdits(doc);
        var contents = ReservedSpace.HexString(256);
        var byteRange = ReservedSpace.Text("[0 0 0 0]", 60);
        var sigNumber = edits.Add(new PdfDictionary
        {
            ["Type"] = PdfName.Get("Sig"),
            ["Filter"] = PdfName.Get("Adobe.PPKLite"),
            ["SubFilter"] = PdfName.Get("adbe.pkcs7.detached"),
            ["ByteRange"] = byteRange,
            ["Contents"] = contents,
        });
        var catalogNumber = ((PdfReference)doc.Trailer[PdfName.Root]!).Id.Number;
        var pageNumber = PageTreeReader.ReadPageNodes(doc, 64, CancellationToken.None)[0].ObjectNumber;
        var widgetNumber = edits.Add(new PdfDictionary
        {
            ["Type"] = PdfName.Get("Annot"),
            ["Subtype"] = PdfName.Get("Widget"),
            ["FT"] = PdfName.Get("Sig"),
            ["T"] = new PdfString(Encoding.ASCII.GetBytes("Signature1")),
            ["V"] = new PdfReference(sigNumber, 0),
            ["Rect"] = new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0) }),
            ["P"] = new PdfReference(pageNumber, 0),
        });
        var catalog = ((PdfDictionary)doc.GetObject(catalogNumber)!).ShallowClone();
        catalog["AcroForm"] = new PdfDictionary { ["Fields"] = new PdfArray(new PdfObject[] { new PdfReference(widgetNumber, 0) }), ["SigFlags"] = new PdfInteger(3) };
        edits.Replace(catalogNumber, catalog);

        byte[]? hashedRanges = null;
        using var buffer = new MemoryStream();
        new IncrementalEngine(new PdfUtilOptions(), PdfLog.None).Append(doc, new IncrementalSettings
        {
            Progress = ProgressReporter.None,
            Edits = edits,
            Fill = output =>
            {
                var start = contents.Offset;
                var end = start + contents.Width;
                output.Fill(byteRange, Encoding.ASCII.GetBytes($"[0 {start} {end} {output.Length - end}]"));
                hashedRanges = output.Read(0, (int)start).Concat(output.Read(end, (int)(output.Length - end))).ToArray();
                output.Fill(contents, System.Security.Cryptography.SHA256.HashData(hashedRanges));
                Assert.Throws<InvalidOperationException>(() => output.Fill(ReservedSpace.HexString(1), new byte[1]));
            },
        }, buffer, CancellationToken.None);

        var result = buffer.ToArray();
        Assert.True(result.AsSpan(0, input.Length).SequenceEqual(input));
        var signature = Assert.Single(Pdf.Inspect(result).Signatures);
        Assert.True(signature.CoversWholeFile);
        Assert.Equal(256, signature.ContentsLength);

        // The value written into the reserved space is the hash of exactly the signed byte ranges.
        using var outDoc = TestHelpers.Open(result);
        var value = ((PdfString)((PdfDictionary)outDoc.GetObject(sigNumber)!)["Contents"]!).Value;
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(hashedRanges!), value[..32]);
        Assert.Throws<ArgumentException>(() => contents.Encode(new byte[257]));
    }
}
