using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>F6: richer inspection.</summary>
public class InspectionTests
{
    internal static byte[] Rich(XrefMode mode = XrefMode.Classic, (int, string, string, bool)? encryption = null, bool signed = false) =>
        new TestPdfBuilder { Rich = true, Mode = mode, Encryption = encryption, Signed = signed, Version = encryption is { Item1: < 5 } ? "1.6" : "1.7" }.Build();

    [Fact]
    public void BasicInspection_CollectsNoDetails()
    {
        var info = Pdf.GetInfo(Rich());
        Assert.Equal(PdfInspectionDetails.None, info.InspectedDetails);
        Assert.Empty(info.Fonts);
        Assert.Empty(info.Attachments);
        Assert.Empty(info.Bookmarks);

        // Layout, mode and language are cheap and always reported.
        Assert.Equal("TwoColumnLeft", info.PageLayout);
        Assert.Equal("UseOutlines", info.PageMode);
        Assert.Equal("en-US", info.Language);
    }

    [Fact]
    public void Details_AreOnlyCollectedWhenRequested()
    {
        var info = Pdf.Inspect(Rich(), new PdfInspectionOptions { Details = PdfInspectionDetails.Fonts | PdfInspectionDetails.PageLabels });
        Assert.NotEmpty(info.Fonts);
        Assert.NotEmpty(info.PageLabels);
        Assert.Empty(info.Images);
        Assert.Empty(info.FormFields);
        Assert.Empty(info.Actions);
        Assert.False(info.HasJavaScript);
    }

    [Fact]
    public void Fonts_EveryKind()
    {
        var fonts = Pdf.Inspect(Rich()).Fonts.ToDictionary(f => f.Name);

        Assert.Equal("Type1", fonts["Helvetica"].Subtype);
        Assert.False(fonts["Helvetica"].IsEmbedded);
        Assert.Equal(new[] { 1, 2 }, fonts["Helvetica"].Pages);

        var arial = fonts["Arial"];
        Assert.Equal("TrueType", arial.Subtype);
        Assert.True(arial.IsEmbedded);
        Assert.False(arial.IsSubset);
        Assert.Equal("FontFile2", arial.FontFileType);
        Assert.Equal("WinAnsiEncoding", arial.Encoding);

        var noto = fonts["NotoSans"];
        Assert.Equal("Type0", noto.Subtype);
        Assert.Equal("CIDFontType2", noto.DescendantSubtype);
        Assert.True(noto.IsEmbedded);
        Assert.True(noto.IsSubset);
        Assert.Equal("ABCDEF+NotoSans", noto.BaseFont);
        Assert.Equal("Identity-H", noto.Encoding);
        Assert.True(noto.HasToUnicode);

        var type3 = fonts["F4"]; // no /BaseFont: named after its resource
        Assert.Equal("Type3", type3.Subtype);
        Assert.True(type3.IsEmbedded);
        Assert.Equal("Custom", type3.Encoding);

        var minion = fonts["Minion"];
        Assert.Equal("FontFile3", minion.FontFileType);
        Assert.Equal("Custom (based on WinAnsiEncoding)", minion.Encoding);

        // Fonts reached through a Type 3 font's resources and through a form XObject.
        Assert.Equal(new[] { 1 }, fonts["Times-Roman"].Pages);
        Assert.Equal(new[] { 2 }, fonts["Courier"].Pages);
        Assert.All(fonts.Values, f => Assert.NotNull(f.ObjectNumber));
    }

    [Fact]
    public void Images_EveryColourSpace()
    {
        var images = Pdf.Inspect(Rich()).Images;
        string? Cs(string prefix) => images.FirstOrDefault(i => i.ColorSpace?.StartsWith(prefix, StringComparison.Ordinal) == true)?.ColorSpace;

        Assert.Equal("ICCBased (3 components)", Cs("ICCBased"));
        Assert.Equal("Indexed (DeviceRGB, 4 colours)", Cs("Indexed"));
        Assert.Equal("Separation (Spot)", Cs("Separation"));
        Assert.Equal("ImageMask", Cs("ImageMask"));

        var gray = images.Single(i => i.ColorSpace == "DeviceGray");
        Assert.Equal(new[] { 1, 2 }, gray.Pages); // directly on page 1, through a form XObject on page 2
        Assert.Equal(4, gray.Width);
        Assert.Equal(8, gray.BitsPerComponent);
        Assert.Equal(new[] { "FlateDecode" }, gray.Filters);

        var jpeg = images.Single(i => i.ColorSpace == "DeviceCMYK");
        Assert.Equal(new[] { "DCTDecode" }, jpeg.Filters);
        Assert.Equal(4, jpeg.StoredLength);

        Assert.True(images.Single(i => i.ColorSpace == "Separation (Spot)").HasSoftMask);
        Assert.Equal(1, images.Single(i => i.ColorSpace == "ImageMask").BitsPerComponent);

        // The builder's original RGB image is there too; the soft mask is not listed as an image of its own.
        Assert.Equal(7, images.Count);
    }

    [Fact]
    public void Attachments_KidsTreeAndAnnotation()
    {
        var attachments = Pdf.Inspect(Rich()).Attachments;
        Assert.Equal(new[] { "a.txt", "b.txt", "data.bin", "note.txt" }, attachments.Select(a => a.Name));

        var a = attachments[0];
        Assert.Equal("a.txt", a.FileName);
        Assert.Equal("First file", a.Description);
        Assert.Equal(TestPdfBuilder.AttachmentA.Length, a.Size);
        Assert.Equal("text/plain", a.MimeType);
        Assert.Equal("Data", a.Relationship);
        Assert.Equal(new DateTimeOffset(2024, 3, 5, 6, 7, 8, TimeSpan.Zero), a.CreationDate);
        Assert.Equal(new DateTimeOffset(2024, 4, 5, 6, 7, 8, TimeSpan.FromHours(2)), a.ModificationDate);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.MD5.HashData(TestPdfBuilder.AttachmentA)), a.CheckSum);
        Assert.True(a.IsEmbedded);
        Assert.Null(a.Page);

        Assert.Equal("dätä.bin", attachments[2].FileName);
        Assert.Null(attachments[2].Size);
        Assert.Equal(2, attachments[3].Page);
        Assert.Equal("Page note", attachments[3].Description);
    }

    [Fact]
    public void Bookmarks_NestedWithResolvedDestinations()
    {
        var bookmarks = Pdf.Inspect(Rich()).Bookmarks;
        Assert.Equal(2, bookmarks.Count);

        var chapter1 = bookmarks[0];
        Assert.Equal("Chapter 1", chapter1.Title);
        Assert.Equal(1, chapter1.Page);
        Assert.True(chapter1.IsOpen);
        Assert.Equal(2, chapter1.Children.Count);
        Assert.Equal(2, chapter1.Children[0].Page); // named destination
        Assert.Equal("Section 1.2 é", chapter1.Children[1].Title);
        Assert.Equal(2, chapter1.Children[1].Page); // GoTo action

        var website = bookmarks[1];
        Assert.Null(website.Page);
        Assert.Equal("URI", website.Action);
        Assert.Equal("https://example.com/", website.Uri);
        Assert.False(website.IsOpen);
        Assert.Equal("JavaScript", Assert.Single(website.Children).Action);
    }

    [Fact]
    public void FormFields_EveryType()
    {
        var fields = Pdf.Inspect(Rich()).FormFields.ToDictionary(f => f.FullName);
        Assert.Equal(8, fields.Count);

        var name = fields["name"];
        Assert.Equal(PdfFormFieldType.Text, name.Type);
        Assert.Equal("Jane Doe", name.Value);
        Assert.True(name.IsRequired);
        Assert.False(name.IsReadOnly);
        Assert.Equal("Your name", name.AlternateName);
        Assert.Equal(new[] { 1 }, name.Pages);

        Assert.Equal(PdfFormFieldType.CheckBox, fields["agree"].Type);
        Assert.Equal("Yes", fields["agree"].Value);
        Assert.Equal(PdfFormFieldType.RadioButton, fields["choice"].Type);
        Assert.Equal("B", fields["choice"].Value);
        Assert.Equal(new[] { 1, 2 }, fields["choice"].Pages);
        Assert.Equal(PdfFormFieldType.PushButton, fields["submit"].Type);
        Assert.Equal(PdfFormFieldType.ComboBox, fields["country"].Type);
        Assert.Equal("Norway", fields["country"].Value);
        Assert.Equal(PdfFormFieldType.ListBox, fields["colors"].Type);
        Assert.Equal("Red; Blue", fields["colors"].Value);

        // A child field inherits /FT from its parent and gets a qualified name.
        Assert.Equal(PdfFormFieldType.Text, fields["address.city"].Type);
        Assert.Equal("Oslo", fields["address.city"].Value);

        Assert.Equal(PdfFormFieldType.Signature, fields["approval"].Type);
        Assert.Null(fields["approval"].Value);
    }

    [Fact]
    public void Signatures_ByteRangeCoverage()
    {
        var real = new TestPdfBuilder { Signed = true, RealByteRange = true }.Build();
        var signature = Assert.Single(Pdf.Inspect(real).Signatures);
        Assert.Equal("Signature1", signature.FieldName);
        Assert.Equal("adbe.pkcs7.detached", signature.SubFilter);
        Assert.Equal("Adobe.PPKLite", signature.Filter);
        Assert.Equal(64, signature.ContentsLength);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), signature.SigningTime);
        Assert.Equal(4, signature.ByteRange.Count);
        Assert.Equal(real.Length, signature.ByteRange[2] + signature.ByteRange[3]);
        Assert.True(signature.CoversWholeFile);
        Assert.Equal(1, signature.Page);

        // A byte range that does not reach the end of the file (for example after an incremental update).
        var fake = new TestPdfBuilder { Signed = true }.Build();
        Assert.False(Assert.Single(Pdf.Inspect(fake).Signatures).CoversWholeFile);

        var extended = real.Concat(System.Text.Encoding.ASCII.GetBytes("\n% appended\n")).ToArray();
        Assert.False(Assert.Single(Pdf.Inspect(extended).Signatures).CoversWholeFile);

        // An unsigned signature field is a form field, not a signature.
        Assert.Empty(Pdf.Inspect(Rich()).Signatures);
    }

    [Fact]
    public void PageLabels_RomanThenPrefixedDecimal()
    {
        Assert.Equal(new[] { "i", "A-5" }, Pdf.Inspect(Rich()).PageLabels);
        Assert.Empty(Pdf.Inspect(new TestPdfBuilder().Build()).PageLabels);
    }

    [Theory]
    [InlineData("D", 7, "7")]
    [InlineData("R", 1994, "MCMXCIV")]
    [InlineData("r", 4, "iv")]
    [InlineData("A", 1, "A")]
    [InlineData("A", 28, "BB")]
    [InlineData("a", 53, "aaa")]
    [InlineData(null, 3, "")]
    [InlineData("R", 0, "0")]
    public void PageLabels_Formats(string? style, long number, string expected) =>
        Assert.Equal(expected, Internal.Document.DetailReader.FormatLabel(style, number));

    [Fact]
    public void Actions_JavaScriptNamesAndLocations()
    {
        var info = Pdf.Inspect(Rich());
        Assert.True(info.HasJavaScript);
        Assert.Equal(new[] { "init" }, info.JavaScriptNames);
        var actions = info.Actions.Select(a => (a.Location, a.Type)).ToList();
        Assert.Contains(("OpenAction", "JavaScript"), actions);
        Assert.Contains(("Document /AA /WC", "JavaScript"), actions);
        Assert.Contains(("Named JavaScript \"init\"", "JavaScript"), actions);
        Assert.Contains(("Page 1 /AA /O", "JavaScript"), actions);
        Assert.Contains(("Page 1 /AA /C", "URI"), actions);
        Assert.Contains(("Annotation /Link on page 1", "URI"), actions);
        Assert.Contains(("Annotation /Link on page 1 /Next", "JavaScript"), actions);
        Assert.Contains(("Annotation /Widget on page 1 /AA /K", "JavaScript"), actions);
        Assert.Contains(("Annotation /Widget on page 2", "SubmitForm"), actions);
        Assert.Contains(("Bookmark \"Website\"", "URI"), actions);
        Assert.Contains(("Bookmark \"Run script\"", "JavaScript"), actions);

        var plain = Pdf.Inspect(new TestPdfBuilder().Build());
        Assert.False(plain.HasJavaScript);
        Assert.Empty(plain.Actions);
    }

    public static IEnumerable<object[]> Structures() =>
        from mode in Enum.GetValues<XrefMode>()
        from revision in new[] { 0, 2, 3, 4, 5, 6 }
        select new object[] { mode, revision };

    [Theory]
    [MemberData(nameof(Structures))]
    public void EveryStructureAndRevision_GivesTheSameDetails(XrefMode mode, int revision)
    {
        var expected = Pdf.Inspect(Rich());
        var input = Rich(mode, revision == 0 ? null : (revision, "u", "o", true));
        var info = Pdf.Inspect(input, new PdfInspectionOptions { Password = revision == 0 ? null : "u" });
        Assert.Equal(expected.Fonts.Select(f => f.ToString()).Order(), info.Fonts.Select(f => f.ToString()).Order());
        Assert.Equal(expected.Images.Count, info.Images.Count);
        Assert.Equal(expected.Attachments.Select(a => a.ToString()), info.Attachments.Select(a => a.ToString()));
        Assert.Equal(expected.Bookmarks.Select(b => b.ToString()), info.Bookmarks.Select(b => b.ToString()));
        Assert.Equal(expected.FormFields.Select(f => f.ToString()), info.FormFields.Select(f => f.ToString()));
        Assert.Equal(expected.PageLabels, info.PageLabels);
        Assert.Equal(expected.Actions, info.Actions);
        Assert.Equal(expected.JavaScriptNames, info.JavaScriptNames);
    }

    [Fact]
    public void EncryptedWithoutPassword_ReturnsPartialResult()
    {
        var input = Rich(encryption: (6, "u", "o", true));
        var info = Pdf.Inspect(input);
        Assert.True(info.RequiresPassword);
        Assert.Empty(info.Fonts);
        Assert.Throws<PdfInvalidPasswordException>(() => Pdf.Inspect(input, new PdfInspectionOptions { Password = "wrong" }));
    }

    [Fact]
    public async Task Analyzer_RejectsUndefinedDetails()
    {
        using var input = new MemoryStream(Rich());
        await Assert.ThrowsAsync<ArgumentException>(() => new PdfAnalyzer().InspectAsync(input, new PdfInspectionOptions { Details = (PdfInspectionDetails)(1 << 20) }));
    }

    [Fact]
    public void Hostile_CyclesAndHugeValuesTerminate()
    {
        var pdf = RawPdf.Minimal(
            "/Outlines 4 0 R /PageLabels << /Nums [0 << /S /R /St 999999999 >>] >> /Names << /EmbeddedFiles 7 0 R /Dests 9 0 R >> /AcroForm << /Fields [10 0 R] >> /OpenAction 12 0 R",
            new Dictionary<int, string>
            {
                [4] = "<< /First 5 0 R >>",
                [5] = "<< /Title (a) /Next 6 0 R /First 5 0 R /Dest (loop) >>",
                [6] = "<< /Title (b) /Next 5 0 R >>",
                [7] = "<< /Kids [8 0 R] >>",
                [8] = "<< /Kids [7 0 R] /Names [(x) 7 0 R] >>",
                [9] = "<< /Names [(loop) (loop)] >>",
                [10] = "<< /T (f) /Kids [11 0 R] >>",
                [11] = "<< /T (g) /Kids [10 0 R] >>",
                [12] = "<< /S /JavaScript /JS (x) /Next 12 0 R >>",
            },
            "/Resources << /XObject << /X 13 0 R >> /Font << /F 14 0 R >> >>");
        var withForm = RawPdf.Build(new Dictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R >>",
            [2] = "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            [3] = "<< /Type /Page /Parent 2 0 R /Resources << /XObject << /X 4 0 R >> >> >>",
            [4] = RawPdf.Stream("/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /Y 4 0 R >> >>", "q Q"),
        });

        var info = Pdf.Inspect(pdf);
        Assert.Equal(2, info.Bookmarks.Count);
        Assert.Equal("a", info.Bookmarks[0].Title);
        Assert.Null(info.Bookmarks[0].Page);
        Assert.Equal(new[] { "999999999" }, info.PageLabels); // too large for roman numerals
        Assert.Single(info.Attachments);
        Assert.Empty(info.FormFields); // in a cycle of two parents, neither is a terminal field
        Assert.True(info.HasJavaScript);
        Assert.Single(info.Actions);
        Assert.Empty(Pdf.Inspect(withForm).Images);
    }
}
