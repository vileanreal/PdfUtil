using System.Text;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Text;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>Section 7.5: text strings in PDFDocEncoding, UTF-16BE (FE FF) and UTF-8 (EF BB BF).</summary>
public class MetadataEncodingTests
{
    public static IEnumerable<object[]> Texts()
    {
        var texts = new[]
        {
            "Español: ñandú",
            "中文标题 日本語のタイトル 한국어",
            "Emoji 😀🎉 and 𝄞 (surrogate pairs)",
            "العربية: عنوان المستند",
            "Mixed ñ 中 😀 ع",
        };
        return from t in texts from a in TestHelpers.WritableAlgorithms select new object[] { t, a };
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void Utf16BeInfoStrings_DecodeBeforeAndAfterProtection(string text, PdfEncryptionAlgorithm algorithm)
    {
        var builder = new TestPdfBuilder();
        builder.Info["Title"] = (text, TextEncodingKind.Utf16Be);
        builder.Info["Subject"] = (text + " (subject)", TextEncodingKind.Utf16Be);
        builder.Info["CustomKey"] = (text, TextEncodingKind.Utf16Be);
        var input = builder.Build();
        Assert.Equal(text, Pdf.GetInfo(input).Info!.Title);

        var (output, _) = TestHelpers.Protect(input, TestHelpers.Options(algorithm));
        var info = Pdf.GetInfo(output, "user").Info!;
        Assert.Equal(text, info.Title);
        Assert.Equal(text + " (subject)", info.Subject);
        Assert.Equal(text, info.CustomEntries["CustomKey"]);

        // The encrypted bytes decrypt to BOM + UTF-16BE, i.e. the BOM was encrypted with the text (no double encoding).
        using var doc = TestHelpers.Open(output, strict: true);
        doc.Authenticate("user");
        var raw = ((PdfString)((PdfDictionary)doc.Resolve(doc.Trailer["Info"])!)["Title"]!).Value;
        Assert.Equal(TestPdfBuilder.EncodeText(text, TextEncodingKind.Utf16Be), raw);
    }

    [Fact]
    public void Utf8InfoStrings_AreConvertedToUtf16_BelowPdf20()
    {
        var builder = new TestPdfBuilder { Version = "1.7" };
        builder.Info["Title"] = ("UTF-8 ñ 中 😀", TextEncodingKind.Utf8);
        var input = builder.Build();
        Assert.Equal("UTF-8 ñ 中 😀", Pdf.GetInfo(input).Info!.Title);
        foreach (var algorithm in TestHelpers.WritableAlgorithms)
        {
            var (output, result) = TestHelpers.Protect(input, TestHelpers.Options(algorithm));
            Assert.NotEqual("2.0", result.OutputVersion);
            using var doc = TestHelpers.Open(output, strict: true);
            doc.Authenticate("owner");
            var info = (PdfDictionary)doc.Resolve(doc.Trailer["Info"])!;
            foreach (var (_, value) in info.Entries)
            {
                if (value is PdfString s)
                {
                    Assert.False(TextString.HasUtf8Bom(s.Value), "UTF-8 text strings must not be emitted below PDF 2.0");
                }
            }

            var title = ((PdfString)info["Title"]!).Value;
            Assert.True(TextString.HasUtf16Bom(title));
            Assert.Equal("UTF-8 ñ 中 😀", Pdf.GetInfo(output, "user").Info!.Title);
        }
    }

    [Fact]
    public void Utf8InfoStrings_ArePreservedInPdf20()
    {
        var builder = new TestPdfBuilder { Version = "2.0" };
        builder.Info["Title"] = ("UTF-8 ñ 😀", TextEncodingKind.Utf8);
        var (output, _) = TestHelpers.Protect(builder.Build(), TestHelpers.Options(PdfEncryptionAlgorithm.Aes256));
        using var doc = TestHelpers.Open(output, strict: true);
        doc.Authenticate("user");
        var title = ((PdfString)((PdfDictionary)doc.Resolve(doc.Trailer["Info"])!)["Title"]!).Value;
        Assert.Equal(TestPdfBuilder.EncodeText("UTF-8 ñ 😀", TextEncodingKind.Utf8), title);
    }

    [Theory]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x41, 0xD8, 0x3D, 0xDE, 0x00 }, "A😀")]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x1B, 0x00, 0x65, 0x00, 0x6E, 0x00, 0x1B, 0x00, 0x48, 0x00, 0x69 }, "Hi")]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x1B, 0x00, 0x65, 0x00, 0x6E, 0x00, 0x55, 0x00, 0x53, 0x00, 0x1B, 0x00, 0x48 }, "H")]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x1B, 0x64, 0x65, 0x1B, 0xC3, 0xBC }, "ü")]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0xF0, 0x9F, 0x98, 0x80 }, "😀")]
    [InlineData(new byte[] { 0x80, 0x84, 0x92, 0xA0, 0xE9, 0x18 }, "•—™€é˘")]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x41, 0x42 }, "A")]
    [InlineData(new byte[] { }, "")]
    public void TextString_DecodesAllForms(byte[] bytes, string expected)
    {
        Assert.Equal(expected, TextString.Decode(bytes));
    }

    [Fact]
    public void TextString_EncodePrefersPdfDocEncodingAndNeverEmitsUtf8()
    {
        Assert.Equal(Encoding.Latin1.GetBytes("Plain é"), TextString.Encode("Plain é"));
        Assert.Equal(new byte[] { 0x80, 0xA0 }, TextString.Encode("•€"));
        var cjk = TextString.Encode("中");
        Assert.Equal(new byte[] { 0xFE, 0xFF, 0x4E, 0x2D }, cjk);
        Assert.Equal("中", TextString.Decode(cjk));
    }

    [Fact]
    public void PdfDates_AreParsed()
    {
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(1)), PdfDate.Parse("D:20240102030405+01'00'"));
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromMinutes(-330)), PdfDate.Parse("D:20240102030405-05'30"));
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero), PdfDate.Parse("D:20240102030405Z"));
        Assert.Equal(new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero), PdfDate.Parse("D:1999"));
        Assert.Equal(new DateTimeOffset(2023, 7, 15, 0, 0, 0, TimeSpan.Zero), PdfDate.Parse("20230715"));
        Assert.Null(PdfDate.Parse("garbage"));
        Assert.Null(PdfDate.Parse(null));
    }

    [Fact]
    public void Inspector_ReportsInfoAndXmpFields()
    {
        var builder = new TestPdfBuilder { Xmp = TestPdfBuilder.PdfAXmp(2, "B"), Tagged = true };
        builder.Info["Keywords"] = ("a, b", TextEncodingKind.PdfDoc);
        var info = Pdf.GetInfo(builder.Build());
        Assert.Equal("Test document", info.Info!.Title);
        Assert.Equal("PdfUtil tests", info.Info.Author);
        Assert.Equal("a, b", info.Info.Keywords);
        Assert.Equal("False", info.Info.Trapped);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(1)), info.Info.CreationDate);
        Assert.Equal("XMP title", info.Xmp!.Title);
        Assert.Equal(new[] { "Alice", "Bob" }, info.Xmp.Creators);
        Assert.Equal("XMP producer", info.Xmp.Producer);
        Assert.Equal(2, info.Xmp.PdfAPart);
        Assert.Equal("B", info.Xmp.PdfAConformance);
        Assert.Equal("PDF/A-2B", info.Xmp.PdfALevel);
        Assert.True(info.IsPdfA);
        Assert.True(info.IsTagged);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(1)), info.Xmp.CreateDate);
        Assert.Equal(2, info.Pages.Count);
        Assert.Equal(612, info.Pages[0].MediaBox.Width);
        Assert.Equal(90, info.Pages[1].Rotate);
        Assert.Equal(842, info.Pages[1].DisplayWidth);
        Assert.True(info.FileSize > 0);
        Assert.False(info.IsEncrypted);
    }
}
