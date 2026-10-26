using PdfUtil.Internal.Document;
using PdfUtil.Internal.Filters;
using PdfUtil.Internal.Objects;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

public class ContentPreservationTests
{
    /// <summary>
    /// Every stream reachable in the source exists in the output under the same object number, and its decrypted
    /// bytes are identical (no re-compression); FlateDecode streams still decompress.
    /// </summary>
    internal static void AssertStreamsPreserved(byte[] input, string? inputPassword, byte[] output, string? outputPassword)
    {
        using var src = TestHelpers.Open(input);
        if (src.IsEncrypted)
        {
            Assert.NotEqual(PdfPasswordMatch.None, src.Authenticate(inputPassword));
        }

        using var dst = TestHelpers.Open(output, strict: true);
        if (dst.IsEncrypted)
        {
            Assert.NotEqual(PdfPasswordMatch.None, dst.Authenticate(outputPassword));
        }

        var reachable = ObjectGraph.FindReachable(src, CancellationToken.None, out _);
        var streams = 0;
        foreach (var num in reachable)
        {
            if (src.GetObject(num) is not PdfStream s)
            {
                continue;
            }

            streams++;
            var o = Assert.IsType<PdfStream>(dst.GetObject(num));
            var id = new ObjectId(num, src.GetGeneration(num));
            var srcData = src.GetStreamData(s, id);
            var dstData = dst.GetStreamData(o, new ObjectId(num, dst.GetGeneration(num)));
            Assert.Equal(srcData, dstData);
            Assert.Equal(StreamFilters.GetFilterNames(s.Dictionary, src.Resolve).Where(f => f != "Crypt"), StreamFilters.GetFilterNames(o.Dictionary, dst.Resolve));
            if (StreamFilters.GetFilterNames(o.Dictionary, dst.Resolve) is ["FlateDecode"])
            {
                Assert.NotNull(dst.GetDecodedStreamData(o, new ObjectId(num, dst.GetGeneration(num))));
            }
        }

        Assert.True(streams > 0);
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256)]
    [InlineData(PdfEncryptionAlgorithm.Aes128)]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128)]
    public void AllStreamsStringsAndObjects_SurviveByteExact(PdfEncryptionAlgorithm algorithm)
    {
        var builder = new TestPdfBuilder { Xmp = TestPdfBuilder.PlainXmp(), Signed = false };
        var input = builder.Build();
        var (output, _) = TestHelpers.Protect(input, TestHelpers.Options(algorithm));
        AssertStreamsPreserved(input, null, output, "user");

        // Decrypted streams equal the builder's plain bytes (content, image, attachment, XMP).
        using var dst = TestHelpers.Open(output, strict: true);
        dst.Authenticate("user");
        foreach (var (num, plain) in builder.PlainStreams)
        {
            var s = Assert.IsType<PdfStream>(dst.GetObject(num));
            Assert.Equal(plain, dst.GetStreamData(s, new ObjectId(num, 0)));
        }

        // Strings: annotations, outline titles, attachment names survive byte for byte.
        using var src = TestHelpers.Open(input);
        foreach (var num in ObjectGraph.FindReachable(src, CancellationToken.None, out _))
        {
            var a = src.GetObject(num);
            var b = dst.GetObject(num);
            if (a is PdfStream sa)
            {
                Assert.True(ObjectGraph.DeepEquals(sa.Dictionary, ((PdfStream)b!).Dictionary, ignoreKey: "Length"));
            }
            else if (a is PdfDictionary d && d["Type"] is PdfName { Value: "Catalog" })
            {
                continue; // the catalog may gain /Extensions
            }
            else
            {
                Assert.True(ObjectGraph.DeepEquals(a, b), $"object {num} changed");
            }
        }
    }

    [Fact]
    public void StreamLengths_AreUpdatedToEncryptedLengths()
    {
        var input = new TestPdfBuilder().Build();
        var (output, _) = TestHelpers.Protect(input, TestHelpers.Options(PdfEncryptionAlgorithm.Aes128));
        using var dst = TestHelpers.Open(output, strict: true);
        foreach (var (num, entry) in dst.Xref)
        {
            if (entry.Type == XrefEntryType.InFile && dst.GetObject(num) is PdfStream s)
            {
                var length = ((PdfInteger)s.Dictionary["Length"]!).Value;
                Assert.Equal(s.SourceLength, length);
                Assert.Equal(0, (length - 16) % 16); // IV + whole AES blocks
            }
        }
    }

    [Fact]
    public void EncryptMetadataFalse_LeavesXmpReadableWithoutPassword()
    {
        var input = new TestPdfBuilder { Xmp = TestPdfBuilder.PlainXmp() }.Build();
        var options = TestHelpers.Options(PdfEncryptionAlgorithm.Aes256);
        options.EncryptMetadata = false;
        var (output, _) = TestHelpers.Protect(input, options);
        Assert.Contains("Plain XMP", System.Text.Encoding.UTF8.GetString(output));
        var d = TestHelpers.EncryptDictionary(output);
        Assert.Equal(PdfBoolean.False, d["EncryptMetadata"]);
        var info = Pdf.GetInfo(output, "user");
        Assert.False(info.Encryption!.EncryptMetadata);
        Assert.Equal("Plain XMP", info.Xmp!.Title);
    }

    [Fact]
    public void EncryptMetadataFalse_WithRc4_IsRejected()
    {
        var options = TestHelpers.Options(PdfEncryptionAlgorithm.Rc4_128);
        options.EncryptMetadata = false;
        Assert.Throws<ArgumentException>(() => Pdf.Protect(new TestPdfBuilder().Build(), options));
    }
}
