using PdfUtil.Internal.Filters;
using PdfUtil.Internal.Fonts;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>F15: text extraction.</summary>
public class TextExtractionTests
{
    public static IEnumerable<object[]> Fixtures() => TextFixtures.All().Select(f => new object[] { f.Name });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryEncoding_DecodesToTheKnownText(string name)
    {
        var (_, pdf, expected) = TextFixtures.All().Single(f => f.Name == name);
        var result = Pdf.ExtractText(pdf);
        Assert.Equal(expected, result.Pages[0].Text);
        Assert.Equal(0, result.UnknownGlyphCount);
    }

    [Fact]
    public void UnknownGlyphs_AreCountedAsReplacementCharacters()
    {
        var pdf = TextFixtures.Page("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> >>] >>", "BT /F1 12 Tf 72 700 Td <00410042> Tj ET");
        var result = Pdf.ExtractText(pdf);
        Assert.Equal("��", result.Text);
        Assert.Equal(2, result.UnknownGlyphCount);
        Assert.Equal(2, result.Pages[0].UnknownGlyphCount);
    }

    [Fact]
    public void Runs_HaveFontSizeColourAndPosition()
    {
        var pdf = TextFixtures.Page("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>", "q 2 0 0 2 0 0 cm 1 0 0 rg BT /F1 12 Tf 36 350 Td (Bold red) Tj ET Q");
        var run = Assert.Single(Pdf.ExtractText(pdf).Pages[0].Runs);
        Assert.Equal("Bold red", run.Text);
        Assert.Equal("Helvetica-Bold", run.FontName);
        Assert.Equal(24, run.FontSize, 3);
        Assert.True(run.IsBold);
        Assert.False(run.IsItalic);
        Assert.Equal("#FF0000", run.Color);
        Assert.Equal(72, run.X, 3);
        Assert.Equal(700, run.Y, 3);
        Assert.Equal(0, run.Angle);

        // "Bold red" in Helvetica-Bold: 3893/1000 em at 24 pt.
        var expectedWidth = "Bold red".Sum(c => StandardFonts.Width("Helvetica-Bold", c)) / 1000.0 * 24;
        Assert.Equal(expectedWidth, run.Width, 2);
        Assert.Equal(700 - (0.207 * 24), run.Bounds.LowerLeftY, 2);
        Assert.Equal(700 + (0.718 * 24), run.Bounds.UpperRightY, 2);
    }

    [Fact]
    public void Glyphs_AreIncludedOnRequest()
    {
        var pdf = TextFixtures.Page("<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>", "BT /F1 10 Tf 100 100 Td (abc) Tj ET");
        var run = Pdf.ExtractText(pdf, new PdfTextExtractionOptions { IncludeGlyphs = true }).Pages[0].Runs[0];
        Assert.Equal(new[] { 100.0, 106, 112 }, run.Glyphs.Select(g => Math.Round(g.X, 3)));
        Assert.All(run.Glyphs, g => Assert.Equal(6, g.Width, 3));
        Assert.Empty(Pdf.ExtractText(pdf).Pages[0].Runs[0].Glyphs);
    }

    [Fact]
    public void LayoutOrder_SortsByPosition()
    {
        var pdf = TextFixtures.Page("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", "BT /F1 12 Tf 72 600 Td (second) Tj ET BT /F1 12 Tf 300 700 Td (right) Tj ET BT /F1 12 Tf 72 700 Td (first) Tj ET");
        Assert.Equal("second\nright\nfirst", Pdf.ExtractText(pdf).Text);
        Assert.Equal("first right\nsecond", Pdf.ExtractText(pdf, new PdfTextExtractionOptions { Order = PdfTextOrder.Layout }).Text);
    }

    [Fact]
    public void RotatedPage_UsesDisplayCoordinates()
    {
        var pdf = TextFixtures.Page("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", "BT /F1 12 Tf 72 700 Td (Sideways) Tj ET", pageExtras: "/MediaBox [0 0 600 800] /Rotate 90");
        var page = Pdf.ExtractText(pdf).Pages[0];
        Assert.Equal(800, page.Width);
        Assert.Equal(600, page.Height);
        var run = page.Runs[0];
        Assert.Equal(700, run.X, 3);
        Assert.Equal(528, run.Y, 3);
        Assert.Equal(-90, run.Angle, 3);
    }

    [Fact]
    public void Images_AreReported()
    {
        var (_, pdf, _) = TextFixtures.All().Single(f => f.Name == "inline-image");
        var image = Assert.Single(Pdf.ExtractText(pdf).Pages[0].Images);
        Assert.True(image.IsInline);
        Assert.Equal(2, image.PixelWidth);
        Assert.Equal(new PdfRectangle(72, 600, 82, 610), image.Bounds);

        // An image XObject drawn through the current transformation matrix.
        var drawn = RawPdf.Minimal(extra: new Dictionary<int, string>
        {
            [4] = RawPdf.Stream("", "q 100 0 0 50 10 20 cm /Im1 Do Q"),
            [5] = RawPdf.Stream("/Type /XObject /Subtype /Image /Width 4 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8", "abcdefgh"),
        }, pageExtras: "/Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R");
        var placed = Assert.Single(Pdf.ExtractText(drawn).Pages[0].Images);
        Assert.Equal(5, placed.ObjectNumber);
        Assert.Equal(4, placed.PixelWidth);
        Assert.Equal(new PdfRectangle(10, 20, 110, 70), placed.Bounds);
    }

    public static IEnumerable<object[]> Structures() =>
        from mode in Enum.GetValues<XrefMode>()
        from revision in new[] { 0, 2, 4, 6 }
        select new object[] { mode, revision };

    [Theory]
    [MemberData(nameof(Structures))]
    public void BuilderDocuments_EveryStructureAndRevision(XrefMode mode, int revision)
    {
        var builder = new TestPdfBuilder { PageCount = 3, Mode = mode, Encryption = revision == 0 ? null : (revision, "u", "o", true), Version = revision is > 0 and < 5 ? "1.6" : "1.7" };
        var result = Pdf.ExtractText(builder.Build(), new PdfTextExtractionOptions { Password = revision == 0 ? null : "u" });
        Assert.Equal(new[] { "Hello page 1 (paren)", "Hello page 2 (paren)", "Hello page 3 (paren)" }, result.Pages.Select(p => p.Text));
        Assert.Equal("Helvetica", result.Pages[0].Runs[0].FontName);
        Assert.Equal(24, result.Pages[0].Runs[0].FontSize, 3);
    }

    [Fact]
    public void PageSelection_PasswordsAndProgress()
    {
        var input = new TestPdfBuilder { PageCount = 4, Encryption = (6, "u", "o", true) }.Build();
        var reports = new List<PdfProgress>();
        var result = Pdf.ExtractText(input, new PdfTextExtractionOptions { Password = "o", Pages = PdfPageRange.Parse("2,4"), Progress = new RepairTests.SyncProgress(reports.Add) });
        Assert.Equal(new[] { 2, 4 }, result.Pages.Select(p => p.Number));
        Assert.Equal("Hello page 2 (paren)\n\nHello page 4 (paren)", result.Text);
        Assert.Equal(1.0, reports[^1].Fraction);
        Assert.Throws<PdfInvalidPasswordException>(() => Pdf.ExtractText(input));
        Assert.Throws<PdfInvalidPasswordException>(() => Pdf.ExtractText(input, new PdfTextExtractionOptions { Password = "wrong" }));
    }

    [Fact]
    public void CopyRestriction_IsAWarning()
    {
        var input = new TestPdfBuilder { Encryption = (4, "u", "o", true), Permissions = PdfPermissions.Print }.Build();
        Assert.Contains(Pdf.ExtractText(input, new PdfTextExtractionOptions { Password = "u" }).Warnings, w => w.Contains("do not allow copying", StringComparison.Ordinal));
        Assert.Empty(Pdf.ExtractText(input, new PdfTextExtractionOptions { Password = "o" }).Warnings);
    }

    [Fact]
    public void Hostile_SelfReferencingFormAndGarbage()
    {
        var pdf = RawPdf.Minimal(extra: new Dictionary<int, string>
        {
            [4] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            [5] = RawPdf.Stream("", "/X1 Do BT /F1 12 Tf (ok) Tj ET ) ] >> (unterminated"),
            [6] = RawPdf.Stream("/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /X1 6 0 R >> >>", "/X1 Do"),
        }, pageExtras: "/Resources << /Font << /F1 4 0 R >> /XObject << /X1 6 0 R >> >> /Contents 5 0 R");
        var result = Pdf.ExtractText(pdf);
        Assert.Equal("ok", result.Text);
    }

    [Fact]
    public void Filters_DecodeKnownVectors()
    {
        Assert.Equal("Hello", System.Text.Encoding.ASCII.GetString(StreamFilters.Ascii85Decode(System.Text.Encoding.ASCII.GetBytes("87cURDZ~>"))));
        Assert.Equal(new byte[] { 1, 1, 1, 2, 3 }, StreamFilters.RunLengthDecode(new byte[] { 254, 1, 1, 2, 3, 128 }, 1000));
        // "-----A---B" LZW-encoded (ISO 32000-1 §7.4.4.2 example): 80 0B 60 50 22 0C 0C 85 01.
        Assert.Equal("-----A---B", System.Text.Encoding.ASCII.GetString(StreamFilters.LzwDecode(new byte[] { 0x80, 0x0B, 0x60, 0x50, 0x22, 0x0C, 0x0C, 0x85, 0x01 }, 1, 1000)));
    }

    [Fact]
    public void EncodingTables_AreComplete()
    {
        Assert.Equal('Ä', FontEncodings.MacRoman[0x80]);
        Assert.Equal('ˇ', FontEncodings.MacRoman[0xFF]);
        Assert.Equal('Ÿ', FontEncodings.WinAnsi[0x9F]);
        Assert.Equal(1015, StandardFonts.Width("Helvetica", '@'));
        Assert.Equal(584, StandardFonts.Width("Helvetica", '~'));
        Assert.Equal(541, StandardFonts.Width("Times-Roman", '~'));
        Assert.Equal(StandardFonts.Width("Times-Bold", 'e'), StandardFonts.Width("Times-Bold", 'é'));
        Assert.Equal("Helvetica-BoldOblique", StandardFonts.Resolve("Arial,BoldItalic"));
        Assert.Equal("Times-Roman", StandardFonts.Resolve("TimesNewRomanPSMT"));

        // Glyph names: the AGL rules, including multi-character uni names (which pdf.js does not support).
        Assert.Equal("AB", GlyphNames.ToUnicode("uni00410042"));
        Assert.Equal("é", GlyphNames.ToUnicode("eacute.sc"));
        Assert.Equal("😀", GlyphNames.ToUnicode("u1F600"));
        Assert.Null(GlyphNames.ToUnicode("g123"));
    }
}
