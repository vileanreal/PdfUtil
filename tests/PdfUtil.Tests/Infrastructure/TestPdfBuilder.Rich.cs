using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PdfUtil.Internal.Objects;

namespace PdfUtil.Tests.Infrastructure;

/// <summary>
/// Rich content for inspection, sanitize and attachment tests (fonts of every kind, images in every colour space,
/// nested outlines, every form field type, JavaScript, page labels, a /Kids attachment tree), a realistic signature
/// byte range, and switches for structural damage.
/// </summary>
internal sealed partial class TestPdfBuilder
{
    /// <summary>Adds the rich content described on the class.</summary>
    public bool Rich { get; set; }

    /// <summary>With <see cref="Signed"/>: a byte range that covers the whole file except the signature value.</summary>
    public bool RealByteRange { get; set; }

    /// <summary>The page tree root declares three pages more than it has.</summary>
    public bool WrongPageCount { get; set; }

    /// <summary>Page 1's /Annots refers to an object that does not exist.</summary>
    public bool DanglingReference { get; set; }

    /// <summary>Page 1's content stream is truncated, and an unused stream holds garbage declared as FlateDecode.</summary>
    public bool CorruptFlate { get; set; }

    /// <summary>A page object that is not in the page tree.</summary>
    public bool OrphanPage { get; set; }

    /// <summary>The page tree root has no /MediaBox (page 1 then has none; page 2 has its own).</summary>
    public bool NoMediaBox { get; set; }

    /// <summary>Page 1 appears twice in the root's /Kids.</summary>
    public bool DuplicateKid { get; set; }

    /// <summary>Page 1 links to page 2 (explicit destination); the last page links to the named destination "top" (page 1).</summary>
    public bool InternalLinks { get; set; }

    public static readonly byte[] AttachmentA = Encoding.UTF8.GetBytes("Hello from attachment A. ");

    public static readonly byte[] AttachmentB = Enumerable.Range(0, 1000).Select(i => (byte)(i * 31)).ToArray();

    public static readonly byte[] AttachmentNote = Encoding.UTF8.GetBytes("A note attached to page 2.");

    private static PdfReference R(int number) => new(number, 0);

    private static PdfInteger I(long value) => new(value);

    private static PdfString S(string text) => new(Encoding.Latin1.GetBytes(text));

    private static PdfDictionary Js(string script) => new() { ["S"] = N("JavaScript"), ["JS"] = S(script) };

    private void AddRichContent(Func<PdfObject, byte[]?, int> add, Func<int> reserve, List<Obj> list, PdfArray kids, PdfDictionary catalog)
    {
        PdfDictionary Page(int i) => (PdfDictionary)list.First(o => o.Number == ((PdfReference)kids[i]).Id.Number).Value;
        var page1 = Page(0);
        var page2 = Page(Math.Min(1, kids.Count - 1));
        var page1Ref = kids[0];
        var page2Ref = kids[Math.Min(1, kids.Count - 1)];
        var res1 = (PdfDictionary)page1["Resources"]!;
        var res2 = (PdfDictionary)page2["Resources"]!;
        var fonts = (PdfDictionary)res1["Font"]!;
        var xobjects = (PdfDictionary)res1["XObject"]!;

        // ---- Fonts: embedded TrueType, Type 0 subset with a CIDFontType2 descendant, Type 3, embedded CFF Type 1 with a differences encoding.
        var ttFile = add(new PdfDictionary { ["Length1"] = I(4) }, new byte[] { 0, 1, 0, 0 });
        var ttDescriptor = add(new PdfDictionary { ["Type"] = N("FontDescriptor"), ["FontName"] = N("Arial"), ["Flags"] = I(32), ["FontFile2"] = R(ttFile) }, null);
        fonts["F2"] = R(add(new PdfDictionary { ["Type"] = N("Font"), ["Subtype"] = N("TrueType"), ["BaseFont"] = N("Arial"), ["Encoding"] = N("WinAnsiEncoding"), ["FontDescriptor"] = R(ttDescriptor) }, null));

        var cidFile = add(new PdfDictionary(), new byte[] { 0, 1, 0, 0, 9 });
        var cidDescriptor = add(new PdfDictionary { ["Type"] = N("FontDescriptor"), ["FontName"] = N("ABCDEF+NotoSans"), ["Flags"] = I(4), ["FontFile2"] = R(cidFile) }, null);
        var cidFont = add(new PdfDictionary
        {
            ["Type"] = N("Font"),
            ["Subtype"] = N("CIDFontType2"),
            ["BaseFont"] = N("ABCDEF+NotoSans"),
            ["CIDSystemInfo"] = new PdfDictionary { ["Registry"] = S("Adobe"), ["Ordering"] = S("Identity"), ["Supplement"] = I(0) },
            ["FontDescriptor"] = R(cidDescriptor),
        }, null);
        var toUnicode = add(new PdfDictionary(), Encoding.ASCII.GetBytes("/CIDInit /ProcSet findresource begin 12 dict begin begincmap endcmap end end"));
        fonts["F3"] = R(add(new PdfDictionary
        {
            ["Type"] = N("Font"),
            ["Subtype"] = N("Type0"),
            ["BaseFont"] = N("ABCDEF+NotoSans"),
            ["Encoding"] = N("Identity-H"),
            ["DescendantFonts"] = new PdfArray(new PdfObject[] { R(cidFont) }),
            ["ToUnicode"] = R(toUnicode),
        }, null));

        var times = add(new PdfDictionary { ["Type"] = N("Font"), ["Subtype"] = N("Type1"), ["BaseFont"] = N("Times-Roman") }, null);
        var glyph = add(new PdfDictionary(), Encoding.ASCII.GetBytes("1 0 0 0 1 1 d1 0 0 1 1 re f"));
        fonts["F4"] = R(add(new PdfDictionary
        {
            ["Type"] = N("Font"),
            ["Subtype"] = N("Type3"),
            ["FontBBox"] = Arr(0, 0, 1, 1),
            ["FontMatrix"] = new PdfArray(new PdfObject[] { new PdfReal(0.001, "0.001"), I(0), I(0), new PdfReal(0.001, "0.001"), I(0), I(0) }),
            ["CharProcs"] = new PdfDictionary { ["a"] = R(glyph) },
            ["Encoding"] = new PdfDictionary { ["Type"] = N("Encoding"), ["Differences"] = new PdfArray(new PdfObject[] { I(65), N("a") }) },
            ["FirstChar"] = I(65),
            ["LastChar"] = I(65),
            ["Widths"] = Arr(1000),
            ["Resources"] = new PdfDictionary { ["Font"] = new PdfDictionary { ["T1"] = R(times) } },
        }, null));

        var cffFile = add(new PdfDictionary { ["Subtype"] = N("Type1C") }, new byte[] { 1, 0, 4, 2 });
        var cffDescriptor = add(new PdfDictionary { ["Type"] = N("FontDescriptor"), ["FontName"] = N("QRSTUV+Minion"), ["Flags"] = I(34), ["FontFile3"] = R(cffFile) }, null);
        fonts["F5"] = R(add(new PdfDictionary
        {
            ["Type"] = N("Font"),
            ["Subtype"] = N("Type1"),
            ["BaseFont"] = N("QRSTUV+Minion"),
            ["Encoding"] = new PdfDictionary { ["BaseEncoding"] = N("WinAnsiEncoding"), ["Differences"] = new PdfArray(new PdfObject[] { I(128), N("Euro") }) },
            ["FontDescriptor"] = R(cffDescriptor),
        }, null));

        // ---- Images: gray (Flate), ICC-based RGB, indexed, CMYK JPEG, a stencil mask, a Separation image with a soft mask.
        PdfDictionary Image(int w, int h, PdfObject? colorSpace, int? bpc)
        {
            var d = new PdfDictionary { ["Type"] = N("XObject"), ["Subtype"] = N("Image"), ["Width"] = I(w), ["Height"] = I(h) };
            if (colorSpace is not null)
            {
                d["ColorSpace"] = colorSpace;
            }

            if (bpc is int b)
            {
                d["BitsPerComponent"] = I(b);
            }

            return d;
        }

        var gray = Image(4, 4, N("DeviceGray"), 8);
        gray["Filter"] = N("FlateDecode");
        var grayNum = add(gray, Deflate(Enumerable.Range(0, 16).Select(i => (byte)(i * 16)).ToArray()));
        xobjects["Im2"] = R(grayNum);

        var icc = add(new PdfDictionary { ["N"] = I(3), ["Alternate"] = N("DeviceRGB") }, new byte[] { 0, 0, 2, 12 });
        var iccImage = Image(4, 4, new PdfArray(new PdfObject[] { N("ICCBased"), R(icc) }), 8);
        iccImage["Filter"] = N("FlateDecode");
        xobjects["Im3"] = R(add(iccImage, Deflate(new byte[48])));

        var indexed = Image(4, 4, new PdfArray(new PdfObject[] { N("Indexed"), N("DeviceRGB"), I(3), new PdfString(new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0 }, isHex: true) }), 2);
        xobjects["Im4"] = R(add(indexed, new byte[4]));

        var cmyk = Image(8, 8, N("DeviceCMYK"), 8);
        cmyk["Filter"] = N("DCTDecode");
        xobjects["Im5"] = R(add(cmyk, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }));

        var mask = Image(8, 8, null, 1);
        mask["ImageMask"] = PdfBoolean.True;
        xobjects["Im6"] = R(add(mask, new byte[8]));

        var softMask = add(Image(2, 2, N("DeviceGray"), 8), new byte[] { 0, 64, 128, 255 });
        var tint = new PdfDictionary { ["FunctionType"] = I(2), ["Domain"] = Arr(0, 1), ["C0"] = Arr(0, 0, 0, 0), ["C1"] = Arr(1, 0, 0, 0), ["N"] = I(1) };
        var separation = Image(2, 2, new PdfArray(new PdfObject[] { N("Separation"), N("Spot"), N("DeviceCMYK"), tint }), 8);
        separation["SMask"] = R(softMask);
        xobjects["Im7"] = R(add(separation, new byte[] { 10, 20, 30, 40 }));

        // A form XObject on page 2 with its own font and the gray image (nested resources).
        var courier = add(new PdfDictionary { ["Type"] = N("Font"), ["Subtype"] = N("Type1"), ["BaseFont"] = N("Courier") }, null);
        var form = add(new PdfDictionary
        {
            ["Type"] = N("XObject"),
            ["Subtype"] = N("Form"),
            ["BBox"] = Arr(0, 0, 10, 10),
            ["Resources"] = new PdfDictionary
            {
                ["Font"] = new PdfDictionary { ["C1"] = R(courier) },
                ["XObject"] = new PdfDictionary { ["ImG"] = R(grayNum) },
            },
        }, Encoding.ASCII.GetBytes("q /ImG Do Q"));
        ((PdfDictionary)res2["XObject"]!)["Fm1"] = R(form);

        // ---- Navigation.
        catalog["PageLabels"] = new PdfDictionary
        {
            ["Nums"] = new PdfArray(new PdfObject[]
            {
                I(0), new PdfDictionary { ["S"] = N("r") },
                I(1), new PdfDictionary { ["S"] = N("D"), ["P"] = S("A-"), ["St"] = I(5) },
            }),
        };
        catalog["PageLayout"] = N("TwoColumnLeft");
        catalog["PageMode"] = N("UseOutlines");
        catalog["Lang"] = S("en-US");

        // ---- Outlines: two chapters, nested sections, a named destination, a GoTo action, a URI action, a script.
        var names = (PdfDictionary)catalog["Names"]!;
        names["Dests"] = new PdfDictionary { ["Names"] = new PdfArray(new PdfObject[] { S("sec11"), new PdfArray(new PdfObject[] { page2Ref, N("Fit") }) }) };
        int root = reserve(), ch1 = reserve(), s11 = reserve(), s12 = reserve(), ch2 = reserve(), script = reserve();
        list.Add(new Obj { Number = root, Value = new PdfDictionary { ["Type"] = N("Outlines"), ["First"] = R(ch1), ["Last"] = R(ch2), ["Count"] = I(4) } });
        list.Add(new Obj { Number = ch1, Value = new PdfDictionary { ["Title"] = S("Chapter 1"), ["Parent"] = R(root), ["Next"] = R(ch2), ["First"] = R(s11), ["Last"] = R(s12), ["Count"] = I(2), ["Dest"] = new PdfArray(new PdfObject[] { page1Ref, N("XYZ"), I(0), I(792), I(0) }) } });
        list.Add(new Obj { Number = s11, Value = new PdfDictionary { ["Title"] = S("Section 1.1"), ["Parent"] = R(ch1), ["Next"] = R(s12), ["Dest"] = S("sec11") } });
        list.Add(new Obj { Number = s12, Value = new PdfDictionary { ["Title"] = new PdfString(EncodeText("Section 1.2 é", TextEncodingKind.Utf16Be)), ["Parent"] = R(ch1), ["Prev"] = R(s11), ["A"] = new PdfDictionary { ["S"] = N("GoTo"), ["D"] = new PdfArray(new PdfObject[] { page2Ref, N("Fit") }) } } });
        list.Add(new Obj { Number = ch2, Value = new PdfDictionary { ["Title"] = S("Website"), ["Parent"] = R(root), ["Prev"] = R(ch1), ["First"] = R(script), ["Last"] = R(script), ["Count"] = I(-1), ["A"] = new PdfDictionary { ["S"] = N("URI"), ["URI"] = S("https://example.com/") } } });
        list.Add(new Obj { Number = script, Value = new PdfDictionary { ["Title"] = S("Run script"), ["Parent"] = R(ch2), ["A"] = Js("app.alert('bookmark');") } });
        catalog["Outlines"] = R(root);

        // ---- JavaScript and other actions.
        names["JavaScript"] = new PdfDictionary { ["Names"] = new PdfArray(new PdfObject[] { S("init"), R(add(Js("console.println('init');"), null)) }) };
        catalog["OpenAction"] = Js("this.print();");
        catalog["AA"] = new PdfDictionary { ["WC"] = Js("1;") };
        page1["AA"] = new PdfDictionary { ["O"] = Js("2;"), ["C"] = new PdfDictionary { ["S"] = N("URI"), ["URI"] = S("https://example.com/close") } };
        var annots1 = (PdfArray)page1["Annots"]!;
        var annots2 = (PdfArray)page2["Annots"]!;
        annots1.Add(R(add(new PdfDictionary
        {
            ["Type"] = N("Annot"),
            ["Subtype"] = N("Link"),
            ["Rect"] = Arr(10, 10, 50, 30),
            ["A"] = new PdfDictionary { ["S"] = N("URI"), ["URI"] = S("https://example.com/"), ["Next"] = Js("3;") },
        }, null)));

        // ---- Form fields of every type.
        PdfDictionary Widget(PdfObject pageRef) => new() { ["Type"] = N("Annot"), ["Subtype"] = N("Widget"), ["Rect"] = Arr(100, 600, 200, 620), ["P"] = pageRef };
        var text = Widget(page1Ref);
        text["FT"] = N("Tx");
        text["T"] = S("name");
        text["TU"] = S("Your name");
        text["V"] = S("Jane Doe");
        text["Ff"] = I(2);
        text["AA"] = new PdfDictionary { ["K"] = Js("AFNumber_Keystroke();") };
        var textNum = add(text, null);
        annots1.Add(R(textNum));

        var check = Widget(page1Ref);
        check["FT"] = N("Btn");
        check["T"] = S("agree");
        check["V"] = N("Yes");
        check["AS"] = N("Yes");
        var checkNum = add(check, null);
        annots1.Add(R(checkNum));

        var radioNum = reserve();
        var radio1 = Widget(page1Ref);
        radio1["Parent"] = R(radioNum);
        var radio2 = Widget(page2Ref);
        radio2["Parent"] = R(radioNum);
        var radio1Num = add(radio1, null);
        var radio2Num = add(radio2, null);
        annots1.Add(R(radio1Num));
        annots2.Add(R(radio2Num));
        list.Add(new Obj { Number = radioNum, Value = new PdfDictionary { ["FT"] = N("Btn"), ["Ff"] = I(49152), ["T"] = S("choice"), ["V"] = N("B"), ["Kids"] = new PdfArray(new PdfObject[] { R(radio1Num), R(radio2Num) }) } });

        var push = Widget(page2Ref);
        push["FT"] = N("Btn");
        push["Ff"] = I(65536);
        push["T"] = S("submit");
        push["A"] = new PdfDictionary { ["S"] = N("SubmitForm"), ["F"] = S("https://example.com/submit") };
        var pushNum = add(push, null);
        annots2.Add(R(pushNum));

        var combo = Widget(page2Ref);
        combo["FT"] = N("Ch");
        combo["Ff"] = I(131072);
        combo["T"] = S("country");
        combo["V"] = S("Norway");
        combo["Opt"] = new PdfArray(new PdfObject[] { S("Norway"), S("Sweden") });
        var comboNum = add(combo, null);
        annots2.Add(R(comboNum));

        var listBox = Widget(page2Ref);
        listBox["FT"] = N("Ch");
        listBox["T"] = S("colors");
        listBox["V"] = new PdfArray(new PdfObject[] { S("Red"), S("Blue") });
        listBox["Opt"] = new PdfArray(new PdfObject[] { S("Red"), S("Green"), S("Blue") });
        var listNum = add(listBox, null);
        annots2.Add(R(listNum));

        var addressNum = reserve();
        var city = Widget(page2Ref);
        city["T"] = S("city");
        city["V"] = S("Oslo");
        city["Parent"] = R(addressNum);
        var cityNum = add(city, null);
        annots2.Add(R(cityNum));
        list.Add(new Obj { Number = addressNum, Value = new PdfDictionary { ["T"] = S("address"), ["FT"] = N("Tx"), ["Kids"] = new PdfArray(new PdfObject[] { R(cityNum) }) } });

        var approval = Widget(page2Ref);
        approval["FT"] = N("Sig");
        approval["T"] = S("approval");
        approval["Rect"] = Arr(0, 0, 0, 0);
        var approvalNum = add(approval, null);
        annots2.Add(R(approvalNum));

        var fields = new PdfObject[] { R(textNum), R(checkNum), R(radioNum), R(pushNum), R(comboNum), R(listNum), R(addressNum), R(approvalNum) };
        if (catalog["AcroForm"] is PdfDictionary existingForm)
        {
            foreach (var f in fields)
            {
                ((PdfArray)existingForm["Fields"]!).Add(f);
            }
        }
        else
        {
            catalog["AcroForm"] = new PdfDictionary { ["Fields"] = new PdfArray(fields), ["DA"] = S("/Helv 0 Tf 0 g") };
        }

        // ---- Attachments: a /Kids name tree (a.txt and b.txt in one leaf, the existing data.bin in another) and a file attachment annotation.
        var existingData = ((PdfArray)((PdfDictionary)names["EmbeddedFiles"]!)["Names"]!)[1];
        int FileSpec(string fileName, byte[] content, string mime, bool compress, string? description, string? relationship)
        {
            var parameters = new PdfDictionary
            {
                ["Size"] = I(content.Length),
                ["CreationDate"] = S("D:20240305060708Z"),
                ["ModDate"] = S("D:20240405060708+02'00'"),
                ["CheckSum"] = new PdfString(MD5.HashData(content), isHex: true),
            };
            var ef = new PdfDictionary { ["Type"] = N("EmbeddedFile"), ["Subtype"] = N(mime), ["Params"] = parameters };
            if (compress)
            {
                ef["Filter"] = N("FlateDecode");
            }

            var efNum = add(ef, compress ? Deflate(content) : content);
            var fs = new PdfDictionary
            {
                ["Type"] = N("Filespec"),
                ["F"] = S(fileName),
                ["UF"] = new PdfString(EncodeText(fileName, TextEncodingKind.Utf16Be)),
                ["EF"] = new PdfDictionary { ["F"] = R(efNum) },
            };
            if (description is not null)
            {
                fs["Desc"] = S(description);
            }

            if (relationship is not null)
            {
                fs["AFRelationship"] = N(relationship);
            }

            return add(fs, null);
        }

        var fsA = FileSpec("a.txt", AttachmentA, "text/plain", compress: true, "First file", "Data");
        var fsB = FileSpec("b.txt", AttachmentB, "application/octet-stream", compress: false, null, null);
        var leaf1 = add(new PdfDictionary { ["Limits"] = new PdfArray(new PdfObject[] { S("a.txt"), S("b.txt") }), ["Names"] = new PdfArray(new PdfObject[] { S("a.txt"), R(fsA), S("b.txt"), R(fsB) }) }, null);
        var leaf2 = add(new PdfDictionary { ["Limits"] = new PdfArray(new PdfObject[] { S("data.bin"), S("data.bin") }), ["Names"] = new PdfArray(new PdfObject[] { S("data.bin"), existingData }) }, null);
        names["EmbeddedFiles"] = new PdfDictionary { ["Kids"] = new PdfArray(new PdfObject[] { R(leaf1), R(leaf2) }) };
        catalog["AF"] = new PdfArray(new PdfObject[] { R(fsA) });

        var fsNote = FileSpec("note.txt", AttachmentNote, "text/plain", compress: true, "Page note", null);
        annots2.Add(R(add(new PdfDictionary { ["Type"] = N("Annot"), ["Subtype"] = N("FileAttachment"), ["Rect"] = Arr(300, 300, 320, 320), ["FS"] = R(fsNote), ["Contents"] = S("Note") }, null)));
    }

    private void AddDamage(Func<PdfObject, byte[]?, int> add, List<Obj> list, PdfArray kids, int pagesNum, PdfDictionary catalog)
    {
        var page1 = (PdfDictionary)list.First(o => o.Number == ((PdfReference)kids[0]).Id.Number).Value;
        if (InternalLinks && kids.Count > 1)
        {
            var last = (PdfDictionary)list.First(o => o.Number == ((PdfReference)kids[^1]).Id.Number).Value;
            ((PdfArray)page1["Annots"]!).Add(R(add(new PdfDictionary { ["Type"] = N("Annot"), ["Subtype"] = N("Link"), ["Rect"] = Arr(10, 50, 60, 70), ["Dest"] = new PdfArray(new PdfObject[] { kids[1], N("Fit") }) }, null)));
            ((PdfArray)last["Annots"]!).Add(R(add(new PdfDictionary { ["Type"] = N("Annot"), ["Subtype"] = N("Link"), ["Rect"] = Arr(10, 50, 60, 70), ["Dest"] = S("top") }, null)));
            var names = (PdfDictionary)catalog["Names"]!;
            var top = new PdfObject[] { S("top"), new PdfArray(new PdfObject[] { kids[0], N("Fit") }) };
            if (names["Dests"] is PdfDictionary dests)
            {
                ((PdfArray)dests["Names"]!).Items.AddRange(top);
            }
            else
            {
                names["Dests"] = new PdfDictionary { ["Names"] = new PdfArray(top) };
            }
        }

        if (DanglingReference)
        {
            ((PdfArray)page1["Annots"]!).Add(new PdfReference(900, 0));
        }

        if (CorruptFlate)
        {
            var content = list.First(o => o.Number == ((PdfReference)page1["Contents"]!).Id.Number);
            content.Data = content.Data![..(content.Data.Length / 2)];
            add(new PdfDictionary { ["Filter"] = N("FlateDecode") }, Enumerable.Range(0, 64).Select(i => (byte)(i * 37 + 5)).ToArray());
        }

        if (OrphanPage)
        {
            add(new PdfDictionary { ["Type"] = N("Page"), ["Parent"] = new PdfReference(pagesNum, 0), ["MediaBox"] = Arr(0, 0, 300, 300) }, null);
        }

        if (DuplicateKid)
        {
            kids.Add(kids[0]);
        }
    }

    private static PdfArray ByteRangePlaceholder() =>
        new(new PdfObject[] { new PdfReal(0, "0"), new PdfReal(0, "0000000000"), new PdfReal(0, "0000000000"), new PdfReal(0, "0000000000") });

    /// <summary>Fills the byte range placeholder so it covers everything except the signature's hex /Contents string.</summary>
    private static void PatchByteRange(byte[] data)
    {
        var text = Encoding.Latin1.GetString(data);
        const string Placeholder = "[0 0000000000 0000000000 0000000000]";
        var at = text.IndexOf(Placeholder, StringComparison.Ordinal);
        var contents = "<" + Convert.ToHexString(Enumerable.Range(0, 64).Select(i => (byte)(i * 3)).ToArray()) + ">";
        var start = text.IndexOf(contents, StringComparison.Ordinal);
        if (at < 0 || start < 0)
        {
            throw new InvalidOperationException("Byte range placeholder or signature contents not found.");
        }

        var end = start + contents.Length;
        string D10(long v) => v.ToString("D10", CultureInfo.InvariantCulture);
        var value = $"[0 {D10(start)} {D10(end)} {D10(data.Length - end)}]";
        Encoding.Latin1.GetBytes(value).CopyTo(data, at);
    }
}
