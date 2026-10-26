namespace PdfUtil.Tests.Infrastructure;

/// <summary>Single-page documents with known text in each font encoding, for text extraction tests.</summary>
internal static class TextFixtures
{
    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    /// <summary>A one-page document: font 4 (and extra objects from 6), content stream 5.</summary>
    public static byte[] Page(string font, string content, IDictionary<int, string>? extra = null, string pageExtras = "", string contentFilter = "")
    {
        var objects = new Dictionary<int, string>
        {
            [4] = font,
            [5] = RawPdf.Stream(contentFilter, content),
        };
        foreach (var (k, v) in extra ?? new Dictionary<int, string>())
        {
            objects[k] = v;
        }

        return RawPdf.Minimal(extra: objects, pageExtras: "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R " + pageExtras);
    }

    public static string ToUnicodeCMap(string body) =>
        RawPdf.Stream("", "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n/CMapName /Test def\n1 begincodespacerange <0000> <FFFF> endcodespacerange\n" + body + "\nendcmap CMapName currentdict /CMap defineresource pop end end");

    private static string Type0(string encoding, string toUnicode = "", string ordering = "Identity") =>
        $"<< /Type /Font /Subtype /Type0 /BaseFont /ABCDEF+NotoSans /Encoding /{encoding} /DescendantFonts [<< /Type /Font /Subtype /CIDFontType2 /BaseFont /NotoSans /CIDSystemInfo << /Registry (Adobe) /Ordering ({ordering}) /Supplement 0 >> /FontDescriptor << /Type /FontDescriptor /FontName /NotoSans /Flags 4 /FontBBox [0 -120 1000 880] /ItalicAngle 0 /Ascent 880 /Descent -120 /CapHeight 700 /StemV 80 >> /DW 1000 /W [1 [500 600]] >>] {toUnicode} >>";

    /// <summary>Name → (document, expected text in content order).</summary>
    public static IEnumerable<(string Name, byte[] Pdf, string Expected)> All()
    {
        yield return ("winansi", Page(Helvetica, "BT /F1 12 Tf 72 700 Td (Grüße, café \u0097 \u0093quotes\u0094 \u0080100) Tj ET"), "Grüße, café — “quotes” €100");
        yield return ("standard", Page("<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>", "BT /F1 12 Tf 72 700 Td (It's) Tj ET"), "It’s");
        yield return ("macroman", Page("<< /Type /Font /Subtype /TrueType /BaseFont /Arial /Encoding /MacRomanEncoding >>", "BT /F1 12 Tf 72 700 Td (\u0085lb\u008Aume) Tj ET"), "Ölbäume");
        yield return ("differences", Page("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /BaseEncoding /WinAnsiEncoding /Differences [65 /Alpha /Beta /uni0416 /f_i /afii10071] >> >>", "BT /F1 12 Tf 72 700 Td (ABCDEFG) Tj ET"), "ΑΒЖfiёFG");
        yield return ("tounicode-identity", Page(Type0("Identity-H", "/ToUnicode 6 0 R"), "BT /F1 12 Tf 72 700 Td <00010002001000200021> Tj ET",
            new Dictionary<int, string> { [6] = ToUnicodeCMap("2 beginbfchar <0001> <65E5> <0002> <672C> endbfchar\n2 beginbfrange <0010> <0012> <8A9E> <0020> <0021> [<30C6> <30AD>] endbfrange") }), "日本語テキ");
        yield return ("tounicode-ligature", Page(Type0("Identity-H", "/ToUnicode 6 0 R"), "BT /F1 12 Tf 72 700 Td <00050006> Tj ET",
            new Dictionary<int, string> { [6] = ToUnicodeCMap("2 beginbfchar <0005> <006600660069> <0006> <D83DDE00> endbfchar") }), "ffi😀");
        yield return ("ucs2-cmap", Page(Type0("UniGB-UCS2-H", ordering: "GB1"), "BT /F1 12 Tf 72 700 Td <4E2D6587> Tj ET"), "中文");
        yield return ("tj-spacing", Page(Helvetica, "BT /F1 12 Tf 72 700 Td [(Hel) -20 (lo) -800 (World)] TJ ET"), "Hello World");
        yield return ("lines", Page(Helvetica, "BT /F1 12 Tf 14 TL 72 700 Td (Line one) Tj T* (Line two) Tj (Line three) ' 2 1 (Line four) \" ET"), "Line one\nLine two\nLine three\nLine four");
        yield return ("type3", Page("<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /a 6 0 R >> /Encoding << /Type /Encoding /Differences [65 /a /b /c] >> /FirstChar 65 /LastChar 67 /Widths [500 500 500] /Resources << >> >>",
            "BT /F1 12 Tf 72 700 Td (ABC) Tj ET", new Dictionary<int, string> { [6] = RawPdf.Stream("", "500 0 d0 0 0 500 500 re f") }), "abc");
        yield return ("symbol", Page("<< /Type /Font /Subtype /Type1 /BaseFont /Symbol >>", "BT /F1 12 Tf 72 700 Td (abg) Tj ET"), "αβγ");
        yield return ("hex-string", Page(Helvetica, "BT /F1 12 Tf 72 700 Td <48656C6C6F> Tj ET"), "Hello");
        yield return ("form-xobject", RawPdf.Minimal(extra: new Dictionary<int, string>
        {
            [4] = Helvetica,
            [5] = RawPdf.Stream("", "q 1 0 0 1 72 600 cm /X1 Do Q BT /F1 12 Tf 72 700 Td (Page text) Tj ET"),
            [6] = RawPdf.Stream("/Type /XObject /Subtype /Form /BBox [0 0 500 100] /Matrix [2 0 0 2 0 0] /Resources << /Font << /F2 4 0 R >> >>", "BT /F2 12 Tf 0 0 Td (Form text) Tj ET"),
        }, pageExtras: "/Resources << /Font << /F1 4 0 R >> /XObject << /X1 6 0 R >> >> /Contents 5 0 R"), "Form text\nPage text");
        yield return ("ascii-hex-content", Page(Helvetica, Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("BT /F1 12 Tf 72 700 Td (Hex content) Tj ET")) + ">", contentFilter: "/Filter /ASCIIHexDecode"), "Hex content");
        yield return ("inline-image", Page(Helvetica, "BT /F1 12 Tf 72 700 Td (Before) Tj ET q 10 0 0 10 72 600 cm BI /W 2 /H 2 /CS /G /BPC 8 ID EI x\nEI Q BT /F1 12 Tf 72 500 Td (After) Tj ET"), "Before\nAfter");
    }
}
