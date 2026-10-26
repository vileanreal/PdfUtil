using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Images;
using PdfUtil.Internal.Objects;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>F16: text and image stamps.</summary>
public class StampTests
{
    private static byte[] Doc(int pages = 2) => new TestPdfBuilder { PageCount = pages }.Build();

    private static PdfTextRun StampRun(byte[] pdf, string text, int page = 0) =>
        Pdf.ExtractText(pdf).Pages[page].Runs.Single(r => r.Text == text);

    [Fact]
    public void Text_Over_IsAddedWithoutTouchingTheContent()
    {
        var input = Doc();
        var output = Pdf.Stamp(input, new PdfStampOptions { Text = "CONFIDENTIAL" }, out var result);
        Assert.Equal(new[] { 1, 2 }, result.StampedPages);
        Assert.Equal("1.7", result.OutputVersion);

        // The original content streams are still there, byte for byte, between the "q" and the stamp.
        using var src = TestHelpers.Open(input);
        using var dst = TestHelpers.Open(output, strict: true);
        var srcPage = PageTreeReader.ReadPageNodes(src, 64, CancellationToken.None)[0].Page;
        var dstPage = PageTreeReader.ReadPageNodes(dst, 64, CancellationToken.None)[0].Page;
        var contents = (PdfArray)dstPage["Contents"]!;
        Assert.Equal(3, contents.Count);
        Assert.Equal("q\n", Encoding.ASCII.GetString(dst.GetStreamData((PdfStream)dst.Resolve(contents[0])!, new ObjectId(0, 0))));
        Assert.Equal(((PdfReference)srcPage["Contents"]!).Id.Number, ((PdfReference)contents[1]).Id.Number);
        Assert.StartsWith("Q\nq\n", Encoding.ASCII.GetString(dst.GetStreamData((PdfStream)dst.Resolve(contents[2])!, new ObjectId(0, 0))), StringComparison.Ordinal);
        ContentPreservationTests.AssertStreamsPreserved(input, null, output, null);

        // Our own extraction finds it, rotated 45 degrees around the page centre.
        var run = StampRun(output, "CONFIDENTIAL");
        Assert.Equal(45, run.Angle, 1);
        Assert.Equal("Helvetica", run.FontName);
        Assert.Equal(48, run.FontSize, 1);
        Assert.Equal(306, (run.Bounds.LowerLeftX + run.Bounds.UpperRightX) / 2, 0);
        Assert.Equal(396, (run.Bounds.LowerLeftY + run.Bounds.UpperRightY) / 2, 0);
        Assert.True(Pdf.Validate(output).IsValid);
    }

    [Fact]
    public void Text_Under_AndOpacity()
    {
        var output = Pdf.Stamp(new TestPdfBuilder { Version = "1.3" }.Build(), new PdfStampOptions { Text = "DRAFT", Layer = PdfStampLayer.Under, Opacity = 0.5 }, out var result);
        Assert.Equal("1.4", result.OutputVersion);
        using var dst = TestHelpers.Open(output, strict: true);
        var page = PageTreeReader.ReadPageNodes(dst, 64, CancellationToken.None)[0].Page;
        var contents = (PdfArray)page["Contents"]!;
        Assert.Equal(2, contents.Count);
        Assert.StartsWith("q\n/PdfUtilGS gs", Encoding.ASCII.GetString(dst.GetStreamData((PdfStream)dst.Resolve(contents[0])!, new ObjectId(0, 0))), StringComparison.Ordinal);
        var gs = dst.ResolveDictionary(dst.ResolveDictionary(dst.ResolveDictionary(page["Resources"])!["ExtGState"])!["PdfUtilGS"])!;
        Assert.Equal(0.5, ((PdfReal)gs["ca"]!).Value);
        Assert.Contains("DRAFT", Pdf.ExtractText(output).Pages[0].Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PdfStampPosition.TopLeft, "left", "top")]
    [InlineData(PdfStampPosition.TopRight, "right", "top")]
    [InlineData(PdfStampPosition.BottomCenter, "center", "bottom")]
    [InlineData(PdfStampPosition.MiddleLeft, "left", "middle")]
    public void Positions_RespectTheMargin(PdfStampPosition position, string horizontal, string vertical)
    {
        var output = Pdf.Stamp(Doc(1), new PdfStampOptions { Text = "Stamp", FontSize = 20, Rotation = 0, Position = position, Margin = 30 }, out _);
        var b = StampRun(output, "Stamp").Bounds;
        switch (horizontal)
        {
            case "left":
                Assert.Equal(30, b.LowerLeftX, 0);
                break;
            case "right":
                Assert.Equal(612 - 30, b.UpperRightX, 0);
                break;
            default:
                Assert.Equal(306, (b.LowerLeftX + b.UpperRightX) / 2, 0);
                break;
        }

        switch (vertical)
        {
            case "top":
                Assert.Equal(792 - 30, b.UpperRightY, 0);
                break;
            case "bottom":
                Assert.Equal(30, b.LowerLeftY, 0);
                break;
            default:
                Assert.Equal(396, (b.LowerLeftY + b.UpperRightY) / 2, 0);
                break;
        }
    }

    [Fact]
    public void RotatedAndCroppedPage_StampIsUprightAsDisplayed()
    {
        // Page 2 of the builder is rotated 90 degrees and has a 595 x 842 media box.
        var output = Pdf.Stamp(Doc(), new PdfStampOptions { Text = "Upright", Rotation = 0, Position = PdfStampPosition.TopCenter, Pages = PdfPageRange.Parse("2") }, out var result);
        Assert.Equal(new[] { 2 }, result.StampedPages);
        var page = Pdf.ExtractText(output).Pages[1];
        var run = page.Runs.Single(r => r.Text == "Upright");
        Assert.Equal(0, run.Angle, 3);
        Assert.Equal(842, page.Width);
        Assert.Equal(421, (run.Bounds.LowerLeftX + run.Bounds.UpperRightX) / 2, 0);
        Assert.Equal(595 - 36, run.Bounds.UpperRightY, 0);
        Assert.DoesNotContain(Pdf.ExtractText(output).Pages[0].Runs, r => r.Text == "Upright");

        var cropped = RawPdf.Minimal(pageExtras: "/CropBox [100 100 300 200]");
        var croppedOutput = Pdf.Stamp(cropped, new PdfStampOptions { Text = "C", FontSize = 10, Rotation = 0 }, out _);
        var c = StampRun(croppedOutput, "C");
        Assert.Equal(100, (c.Bounds.LowerLeftX + c.Bounds.UpperRightX) / 2, 0);
        Assert.Equal(50, (c.Bounds.LowerLeftY + c.Bounds.UpperRightY) / 2, 0);
    }

    [Fact]
    public void MultilineText_AndCustomPositionAndColour()
    {
        var output = Pdf.Stamp(Doc(1), new PdfStampOptions { Text = "Line A\nLonger line B", Rotation = 0, Position = PdfStampPosition.Custom, X = 200, Y = 300, FontSize = 12, Color = PdfColor.FromHex("#0000FF"), Font = PdfStandardFont.TimesBold }, out _);
        var a = StampRun(output, "Line A");
        var b = StampRun(output, "Longer line B");
        Assert.Equal("#0000FF", a.Color);
        Assert.Equal("Times-Bold", a.FontName);
        Assert.Equal(14.4, a.Y - b.Y, 3);
        Assert.Equal(200, (b.Bounds.LowerLeftX + b.Bounds.UpperRightX) / 2, 0);
        Assert.Equal(300, (a.Bounds.UpperRightY + b.Bounds.LowerLeftY) / 2, 0);
    }

    [Fact]
    public void Text_OutsideWinAnsi_IsRejected()
    {
        var ex = Assert.Throws<ArgumentException>(() => Pdf.Stamp(Doc(1), new PdfStampOptions { Text = "Grüße 日本" }, out _));
        Assert.Contains("U+65E5", ex.Message, StringComparison.Ordinal);
        Pdf.Stamp(Doc(1), new PdfStampOptions { Text = "Grüße — “€”" }, out _); // WinAnsi is fine
    }

    [Fact]
    public void Text_AnyScript_WithAnEmbeddedFont()
    {
        var font = TestFont.Build("Stamp Font");
        var output = Pdf.Stamp(Doc(2), new PdfStampOptions { Text = "Привет 日本語\nΕλλάδα 😀", FontData = font, FontSize = 30 }, out var result);
        Assert.Equal(new[] { 1, 2 }, result.StampedPages);
        var text = Pdf.ExtractText(output);
        Assert.All(text.Pages, p => Assert.Contains("Привет 日本語", p.Text, StringComparison.Ordinal));
        Assert.Contains("Ελλάδα 😀", text.Pages[0].Text, StringComparison.Ordinal);
        Assert.Contains(Pdf.Inspect(output, new PdfInspectionOptions { Details = PdfInspectionDetails.Fonts }).Fonts, f => f.IsEmbedded && f.IsSubset && f.Subtype == "Type0");
        Assert.Contains("Hello page 1", text.Pages[0].Text, StringComparison.Ordinal);

        var missing = Assert.Throws<ArgumentException>(() => Pdf.Stamp(Doc(1), new PdfStampOptions { Text = "Arabic عربي", FontData = font }, out _));
        Assert.Contains("U+0639", missing.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Pdf.Stamp(Doc(1), new PdfStampOptions { Text = "x", FontData = TestFont.Build(restricted: true) }, out _));
        Assert.Throws<ArgumentException>(() => Pdf.Stamp(Doc(1), new PdfStampOptions { Text = "x", FontData = new byte[] { 1, 2, 3 } }, out _));
        var ex = Assert.Throws<ArgumentException>(() => Pdf.Stamp(Doc(1), new PdfStampOptions { Text = "日本" }, out _));
        Assert.Contains("FontData", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("x", new byte[] { 1 })]
    public void Options_TextXorImage(string? text, byte[]? image) =>
        Assert.Throws<ArgumentException>(() => Pdf.Stamp(Doc(1), new PdfStampOptions { Text = text, Image = image }, out _));

    [Fact]
    public void Options_Ranges()
    {
        Assert.Throws<ArgumentException>(() => Pdf.Stamp(Doc(1), new PdfStampOptions { Text = "x", Opacity = 2 }, out _));
        Assert.Throws<ArgumentException>(() => Pdf.Stamp(Doc(1), new PdfStampOptions { Text = "x", FontSize = 0 }, out _));
        Assert.Throws<ArgumentException>(() => Pdf.Stamp(Doc(1), new PdfStampOptions { Text = "x", Color = new PdfColor(2, 0, 0) }, out _));
        Assert.Throws<ArgumentException>(() => Pdf.Stamp(Doc(1), new PdfStampOptions { Image = new byte[] { 1, 2, 3, 4 } }, out _));
        Assert.Throws<FormatException>(() => PdfColor.FromHex("#12"));
    }

    // ------------------------------------------------------------------ images

    /// <summary>A PNG written chunk by chunk, for bit depths and chunks the encoder does not produce.</summary>
    private static byte[] Png(int width, int height, int depth, int colorType, byte[] scanlines, byte[]? palette = null, byte[]? trns = null, int interlace = 0)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string type, byte[] body)
        {
            var len = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, body.Length);
            ms.Write(len);
            var t = Encoding.ASCII.GetBytes(type);
            ms.Write(t);
            ms.Write(body);
            var crc = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Compute(body, Crc32.Compute(t)) ^ 0xFFFFFFFFu);
            ms.Write(crc);
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = (byte)depth;
        header[9] = (byte)colorType;
        header[12] = (byte)interlace;
        Chunk("IHDR", header);
        if (palette is not null)
        {
            Chunk("PLTE", palette);
        }

        if (trns is not null)
        {
            Chunk("tRNS", trns);
        }

        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true))
        {
            zs.Write(scanlines);
        }

        Chunk("IDAT", z.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    [Fact]
    public void Png_RoundTripsEveryChannelLayout()
    {
        var rgba = Enumerable.Range(0, 3 * 2 * 4).Select(i => (byte)(i * 11)).ToArray();
        var decoded = ImageCodecs.DecodePng(ImageCodecs.EncodePng(3, 2, 4, rgba), 1000);
        Assert.Equal(3, decoded.Channels);
        Assert.Equal(rgba.Where((_, i) => i % 4 != 3), decoded.Samples);
        Assert.Equal(rgba.Where((_, i) => i % 4 == 3), decoded.Alpha!);

        var gray = new byte[] { 0, 128, 255, 7 };
        Assert.Equal(gray, ImageCodecs.DecodePng(ImageCodecs.EncodePng(2, 2, 1, gray), 1000).Samples);
        var palette = new byte[] { 255, 0, 0, 0, 255, 0 };
        var indexed = ImageCodecs.DecodePng(ImageCodecs.EncodePng(2, 1, 1, new byte[] { 1, 0 }, palette), 1000);
        Assert.Equal(palette, indexed.Palette);
        Assert.Equal(new byte[] { 1, 0 }, indexed.Samples);
    }

    [Fact]
    public void Png_LowBitDepthsTransparencyAndFilters()
    {
        // 2-bit gray, 4 pixels per byte: values 0,1,2,3 → 0, 85, 170, 255; filter 1 (Sub) on the second row.
        var twoBit = ImageCodecs.DecodePng(Png(4, 2, 2, 0, new byte[] { 0, 0b00011011, 1, 0b00011011 }), 1000);
        Assert.Equal(new byte[] { 0, 85, 170, 255 }, twoBit.Samples.AsSpan(0, 4).ToArray());

        // 1-bit palette with a tRNS alpha table.
        var paletted = ImageCodecs.DecodePng(Png(2, 1, 1, 3, new byte[] { 0, 0b01000000 }, new byte[] { 0, 0, 0, 255, 255, 255 }, new byte[] { 0 }), 1000);
        Assert.Equal(new byte[] { 0, 1 }, paletted.Samples);
        Assert.Equal(new byte[] { 0, 255 }, paletted.Alpha!);

        // 16-bit RGB keeps the high byte.
        var deep = ImageCodecs.DecodePng(Png(1, 1, 16, 2, new byte[] { 0, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC }), 1000);
        Assert.Equal(new byte[] { 0x12, 0x56, 0x9A }, deep.Samples);

        Assert.Throws<InvalidDataException>(() => ImageCodecs.DecodePng(Png(1, 1, 8, 0, new byte[] { 0, 0 }, interlace: 1), 1000));
        Assert.Throws<InvalidDataException>(() => ImageCodecs.DecodePng(Png(100, 100, 8, 0, new byte[] { 0 }), 1000));
    }

    [Fact]
    public void Image_Png_WithSoftMask()
    {
        var rgba = Enumerable.Range(0, 4 * 2).SelectMany(i => new byte[] { (byte)(i * 30), 0, 255, (byte)(i * 32) }).ToArray();
        var png = ImageCodecs.EncodePng(4, 2, 4, rgba);
        var output = Pdf.Stamp(Doc(1), new PdfStampOptions { Image = png, ImageWidth = 100, Rotation = 0, Position = PdfStampPosition.BottomLeft, Margin = 10, Opacity = 1 }, out _);
        var placement = Assert.Single(Pdf.ExtractText(output).Pages[0].Images);
        Assert.Equal(new PdfRectangle(10, 10, 110, 60), placement.Bounds);
        Assert.Equal(4, placement.PixelWidth);

        using var doc = TestHelpers.Open(output, strict: true);
        var image = (PdfStream)doc.GetObject(placement.ObjectNumber!.Value)!;
        Assert.Equal(rgba.Where((_, i) => i % 4 != 3), doc.GetDecodedStreamData(image, new ObjectId(placement.ObjectNumber.Value, 0)));
        var mask = (PdfStream)doc.Resolve(image.Dictionary["SMask"])!;
        Assert.Equal(rgba.Where((_, i) => i % 4 == 3), doc.GetDecodedStreamData(mask, new ObjectId(((PdfReference)image.Dictionary["SMask"]!).Id.Number, 0)));
        Assert.True(Pdf.Validate(output).IsValid);
    }

    [Fact]
    public void Image_Jpeg_IsPassedThrough()
    {
        // A JPEG header (SOI, APP14 Adobe, SOF0 16x8 CMYK) and some scan bytes.
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xEE, 0x00, 0x0E, (byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e', 0, 100, 0, 0, 0, 0, 2, 0xFF, 0xC0, 0x00, 0x14, 8, 0, 8, 0, 16, 4, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0, 4, 0x11, 0, 0xFF, 0xD9 };
        var output = Pdf.Stamp(Doc(1), new PdfStampOptions { Image = jpeg, Rotation = 0 }, out _);
        using var doc = TestHelpers.Open(output, strict: true);
        var placement = Pdf.ExtractText(output).Pages[0].Images.Single();
        var image = (PdfStream)doc.GetObject(placement.ObjectNumber!.Value)!;
        Assert.Equal("DCTDecode", ((PdfName)image.Dictionary["Filter"]!).Value);
        Assert.Equal("DeviceCMYK", ((PdfName)image.Dictionary["ColorSpace"]!).Value);
        Assert.Equal(8, ((PdfArray)image.Dictionary["Decode"]!).Count);
        Assert.Equal(jpeg, doc.GetStreamData(image, new ObjectId(placement.ObjectNumber.Value, 0)));
        Assert.Equal(16, placement.Bounds.Width, 3);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    public void Protected_KeepsSecurity(int revision)
    {
        var output = Pdf.Stamp(DecryptTests.Encrypted(revision), new PdfStampOptions { Text = "Copy", Password = "o-pass" }, out var result);
        Assert.True(result.IsEncrypted);
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, "u-pass").MatchedAs);
        Assert.Contains("Copy", Pdf.ExtractText(output, new PdfTextExtractionOptions { Password = "u-pass" }).Text, StringComparison.Ordinal);
    }
}
