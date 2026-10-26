using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Security;

namespace PdfUtil.Tests.Infrastructure;

public enum XrefMode
{
    Classic,
    XrefStream,
    XrefStreamWithObjectStreams,
    Hybrid,
}

public enum IdMode
{
    Normal,
    Missing,
    Malformed,
}

public enum TextEncodingKind
{
    PdfDoc,
    Utf16Be,
    Utf8,
}

/// <summary>
/// Generates test PDFs programmatically (no sample files). Supports every structural
/// variant the library must read: classic tables (with 19/20/21-byte entries and multiple subsections), xref streams
/// with PNG/TIFF predictors, object streams, hybrid files, linearization, incremental updates, damaged xref data,
/// wrong stream lengths, junk before the header, and Standard (R2-R6) or public-key encryption.
/// </summary>
internal sealed partial class TestPdfBuilder
{
    public string Version { get; set; } = "1.7";
    public string? CatalogVersion { get; set; }
    public XrefMode Mode { get; set; } = XrefMode.Classic;
    public int PageCount { get; set; } = 2;
    public IdMode IdMode { get; set; } = IdMode.Normal;
    public byte[] Id0 { get; set; } = Enumerable.Range(1, 16).Select(i => (byte)(i * 7)).ToArray();
    public byte[] Id1 { get; set; } = Enumerable.Range(1, 16).Select(i => (byte)(255 - i)).ToArray();
    public Dictionary<string, (string Text, TextEncodingKind Encoding)> Info { get; } = new()
    {
        ["Title"] = ("Test document", TextEncodingKind.PdfDoc),
        ["Author"] = ("PdfUtil tests", TextEncodingKind.PdfDoc),
        ["Producer"] = ("TestPdfBuilder", TextEncodingKind.PdfDoc),
        ["CreationDate"] = ("D:20240102030405+01'00'", TextEncodingKind.PdfDoc),
    };
    public bool OmitInfo { get; set; }
    public string? Xmp { get; set; }
    public bool CompressXmp { get; set; }
    public bool Signed { get; set; }
    public bool Tagged { get; set; }
    public bool Linearized { get; set; }
    public bool IncrementalUpdate { get; set; }
    public byte[]? IncrementalId0 { get; set; }
    public bool BreakXrefOffsets { get; set; }
    public bool BreakStartXref { get; set; }
    public bool WrongStreamLength { get; set; }
    public int JunkBeforeHeader { get; set; }
    public string XrefEntryEol { get; set; } = "\r\n";
    public bool MultipleSubsections { get; set; }
    public int Predictor { get; set; } = 12;
    public bool IndirectLength { get; set; }
    public (int Revision, string User, string Owner, bool EncryptMetadata)? Encryption { get; set; }
    public bool PubSecEncryption { get; set; }
    public bool LegacyPasswordUtf8 { get; set; }
    public PdfPermissions Permissions { get; set; } = PdfPermissions.Print | PdfPermissions.CopyContent;

    /// <summary>Plain stream contents by object number of the last build (for content-preservation checks).</summary>
    public Dictionary<int, byte[]> PlainStreams { get; } = new();

    public static string PdfAXmp(int part, string conformance) =>
        "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n" +
        $"<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" pdfaid:part=\"{part}\" pdfaid:conformance=\"{conformance}\"/>\n" +
        "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">XMP title</rdf:li></rdf:Alt></dc:title>" +
        "<dc:creator><rdf:Seq><rdf:li>Alice</rdf:li><rdf:li>Bob</rdf:li></rdf:Seq></dc:creator></rdf:Description>\n" +
        "<rdf:Description rdf:about=\"\" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"><pdf:Producer>XMP producer</pdf:Producer><xmp:CreateDate>2024-01-02T03:04:05+01:00</xmp:CreateDate></rdf:Description>\n" +
        "</rdf:RDF></x:xmpmeta>\n<?xpacket end=\"w\"?>";

    public static string PlainXmp() =>
        "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n" +
        "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Plain XMP</rdf:li></rdf:Alt></dc:title></rdf:Description>\n" +
        "</rdf:RDF></x:xmpmeta>\n<?xpacket end=\"w\"?>";

    public static byte[] EncodeText(string text, TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf16Be => new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray(),
        TextEncodingKind.Utf8 => new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(text)).ToArray(),
        _ => Encoding.Latin1.GetBytes(text),
    };

    private sealed class Obj
    {
        public required int Number;
        public required PdfObject Value; // dictionary for streams
        public byte[]? Data;
        public bool IsStream => Data is not null;
        public bool NeverCompress;
    }

    public byte[] Build()
    {
        PlainStreams.Clear();
        var objects = BuildObjects(out var catalogNum, out var infoNum);
        return Serialize(objects, catalogNum, infoNum);
    }

    private List<Obj> BuildObjects(out int catalogNum, out int infoNum)
    {
        var list = new List<Obj>();
        var next = 1;
        int Add(PdfObject value, byte[]? data = null, bool neverCompress = false)
        {
            var num = next++;
            list.Add(new Obj { Number = num, Value = value, Data = data, NeverCompress = neverCompress });
            return num;
        }

        catalogNum = next++;
        var pagesNum = next++;
        var fontNum = Add(new PdfDictionary { ["Type"] = N("Font"), ["Subtype"] = N("Type1"), ["BaseFont"] = N("Helvetica") });

        // An image with a pass-through filter and binary data, used by page 1.
        var imageData = new byte[300];
        new Random(42).NextBytes(imageData);
        var imageNum = Add(new PdfDictionary { ["Type"] = N("XObject"), ["Subtype"] = N("Image"), ["Width"] = new PdfInteger(10), ["Height"] = new PdfInteger(10), ["ColorSpace"] = N("DeviceRGB"), ["BitsPerComponent"] = new PdfInteger(8), ["Filter"] = N("DCTDecode") }, imageData);

        var kids = new PdfArray();
        var annotRefs = new List<int>();
        for (var i = 0; i < PageCount; i++)
        {
            var text = $"BT /F1 24 Tf 72 720 Td (Hello page {i + 1} \\(paren\\)) Tj ET\n";
            var plain = Encoding.ASCII.GetBytes(text);
            var compressed = Deflate(plain);
            var contentDict = new PdfDictionary { ["Filter"] = N("FlateDecode") };
            var contentNum = Add(contentDict, compressed);
            var annotNum = Add(new PdfDictionary
            {
                ["Type"] = N("Annot"),
                ["Subtype"] = N("Text"),
                ["Rect"] = Arr(100, 100, 200, 200),
                ["Contents"] = new PdfString(Encoding.Latin1.GetBytes($"Note ({i}) with \\ backslash\r\nand newline")),
            });
            annotRefs.Add(annotNum);
            var page = new PdfDictionary
            {
                ["Type"] = N("Page"),
                ["Parent"] = new PdfReference(pagesNum, 0),
                ["Resources"] = new PdfDictionary
                {
                    ["Font"] = new PdfDictionary { ["F1"] = new PdfReference(fontNum, 0) },
                    ["XObject"] = new PdfDictionary { ["Im1"] = new PdfReference(imageNum, 0) },
                },
                ["Contents"] = new PdfReference(contentNum, 0),
                ["Annots"] = new PdfArray(new PdfObject[] { new PdfReference(annotNum, 0) }),
            };
            if (i == 1)
            {
                page["Rotate"] = new PdfInteger(90);
                page["MediaBox"] = Arr(0, 0, 595, 842);
            }

            kids.Add(new PdfReference(Add(page), 0));
        }

        // An embedded file (attachment), uncompressed binary data.
        var fileData = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var efNum = Add(new PdfDictionary { ["Type"] = N("EmbeddedFile") }, fileData);
        var filespecNum = Add(new PdfDictionary
        {
            ["Type"] = N("Filespec"),
            ["F"] = new PdfString(Encoding.ASCII.GetBytes("data.bin")),
            ["UF"] = new PdfString(EncodeText("dätä.bin", TextEncodingKind.Utf16Be)),
            ["EF"] = new PdfDictionary { ["F"] = new PdfReference(efNum, 0) },
        });

        // An outline (bookmark).
        var outlinesNum = next++;
        var outlineItemNum = Add(new PdfDictionary
        {
            ["Title"] = new PdfString(EncodeText("Chapter ñ 第一章", TextEncodingKind.Utf16Be)),
            ["Parent"] = new PdfReference(outlinesNum, 0),
            ["Dest"] = new PdfArray(new PdfObject[] { kids[0], N("Fit") }),
        });
        list.Add(new Obj { Number = outlinesNum, Value = new PdfDictionary { ["Type"] = N("Outlines"), ["First"] = new PdfReference(outlineItemNum, 0), ["Last"] = new PdfReference(outlineItemNum, 0), ["Count"] = new PdfInteger(1) } });

        var catalog = new PdfDictionary
        {
            ["Type"] = N("Catalog"),
            ["Pages"] = new PdfReference(pagesNum, 0),
            ["Outlines"] = new PdfReference(outlinesNum, 0),
            ["Names"] = new PdfDictionary
            {
                ["EmbeddedFiles"] = new PdfDictionary { ["Names"] = new PdfArray(new PdfObject[] { new PdfString(Encoding.ASCII.GetBytes("data.bin")), new PdfReference(filespecNum, 0) }) },
            },
        };
        if (CatalogVersion is not null)
        {
            catalog["Version"] = N(CatalogVersion);
        }

        if (Tagged)
        {
            catalog["MarkInfo"] = new PdfDictionary { ["Marked"] = PdfBoolean.True };
        }

        if (Xmp is not null)
        {
            var xmpBytes = Encoding.UTF8.GetBytes(Xmp);
            var md = new PdfDictionary { ["Type"] = N("Metadata"), ["Subtype"] = N("XML") };
            if (CompressXmp)
            {
                md["Filter"] = N("FlateDecode");
                xmpBytes = Deflate(xmpBytes);
            }

            catalog["Metadata"] = new PdfReference(Add(md, xmpBytes), 0);
        }

        if (Signed)
        {
            var sigNum = Add(new PdfDictionary
            {
                ["Type"] = N("Sig"),
                ["Filter"] = N("Adobe.PPKLite"),
                ["SubFilter"] = N("adbe.pkcs7.detached"),
                ["ByteRange"] = RealByteRange ? ByteRangePlaceholder() : Arr(0, 100, 200, 300),
                ["Contents"] = new PdfString(Enumerable.Range(0, 64).Select(i => (byte)(i * 3)).ToArray(), isHex: true),
                ["M"] = new PdfString(Encoding.ASCII.GetBytes("D:20240101000000Z")),
            });
            var widgetNum = Add(new PdfDictionary
            {
                ["Type"] = N("Annot"),
                ["Subtype"] = N("Widget"),
                ["FT"] = N("Sig"),
                ["T"] = new PdfString(Encoding.ASCII.GetBytes("Signature1")),
                ["V"] = new PdfReference(sigNum, 0),
                ["Rect"] = Arr(0, 0, 0, 0),
                ["P"] = kids[0],
            });
            catalog["AcroForm"] = new PdfDictionary { ["Fields"] = new PdfArray(new PdfObject[] { new PdfReference(widgetNum, 0) }), ["SigFlags"] = new PdfInteger(3) };
            ((PdfArray)((PdfDictionary)list.First(o => o.Number == ((PdfReference)kids[0]).Id.Number).Value)["Annots"]!).Add(new PdfReference(widgetNum, 0));
        }

        if (Rich)
        {
            AddRichContent((value, data) => Add(value, data), () => next++, list, kids, catalog);
        }

        AddDamage((value, data) => Add(value, data), list, kids, pagesNum, catalog);

        list.Add(new Obj { Number = catalogNum, Value = catalog });
        list.Add(new Obj
        {
            Number = pagesNum,
            Value = NoMediaBox
                ? new PdfDictionary { ["Type"] = N("Pages"), ["Kids"] = kids, ["Count"] = new PdfInteger(WrongPageCount ? PageCount + 3 : PageCount) }
                : new PdfDictionary { ["Type"] = N("Pages"), ["Kids"] = kids, ["Count"] = new PdfInteger(WrongPageCount ? PageCount + 3 : PageCount), ["MediaBox"] = Arr(0, 0, 612, 792) },
        });

        infoNum = 0;
        if (!OmitInfo)
        {
            var info = new PdfDictionary();
            foreach (var (key, (text, enc)) in Info)
            {
                info[key] = new PdfString(EncodeText(text, enc));
            }

            info["Trapped"] = N("False");
            infoNum = Add(info);
        }

        if (IndirectLength)
        {
            // Give the first content stream an indirect /Length, written after the stream.
            var stream = list.First(o => o.IsStream);
            var lenNum = Add(new PdfInteger(stream.Data!.Length));
            ((PdfDictionary)stream.Value)["Length"] = new PdfReference(lenNum, 0);
        }

        list.Sort((a, b) => a.Number.CompareTo(b.Number));
        foreach (var o in list.Where(o => o.IsStream))
        {
            PlainStreams[o.Number] = o.Data!;
        }

        return list;
    }

    // ------------------------------------------------------------------ serialisation

    private byte[] Serialize(List<Obj> objects, int catalogNum, int infoNum)
    {
        var maxNum = objects.Max(o => o.Number);
        StandardSecurityHandler? handler = null;
        Obj? encryptObj = null;
        var trailerExtras = new PdfDictionary();
        if (Encryption is { } enc)
        {
            var p = SecurityAlgorithms.ComputeP(Permissions);
            var legacy = LegacyPasswordUtf8 ? Encoding.UTF8 : Encoding.Latin1;
            var user = enc.Revision >= 5 ? Encoding.UTF8.GetBytes(enc.User) : legacy.GetBytes(enc.User);
            var owner = enc.Revision >= 5 ? Encoding.UTF8.GetBytes(enc.Owner) : legacy.GetBytes(enc.Owner);
            var (h, dict) = StandardSecurityHandler.Create(enc.Revision, user, owner, p, IdMode == IdMode.Normal ? Id0 : Array.Empty<byte>(), enc.EncryptMetadata);
            handler = h;
            encryptObj = new Obj { Number = ++maxNum, Value = dict, NeverCompress = true };
        }
        else if (PubSecEncryption)
        {
            var dict = new PdfDictionary
            {
                ["Filter"] = N("Adobe.PubSec"),
                ["SubFilter"] = N("adbe.pkcs7.s5"),
                ["V"] = new PdfInteger(4),
                ["R"] = new PdfInteger(4),
                ["Length"] = new PdfInteger(128),
                ["Recipients"] = new PdfArray(new PdfObject[] { new PdfString(new byte[] { 1, 2, 3 }, true) }),
            };
            encryptObj = new Obj { Number = ++maxNum, Value = dict, NeverCompress = true };
        }

        if (encryptObj is not null)
        {
            objects.Add(encryptObj);
            trailerExtras["Encrypt"] = new PdfReference(encryptObj.Number, 0);
        }

        // Apply encryption to strings and streams (never to the Encrypt dictionary or signature /Contents).
        var serialized = new Dictionary<int, (PdfObject Value, byte[]? Data)>();
        var compressible = Mode is XrefMode.XrefStreamWithObjectStreams or XrefMode.Hybrid;
        foreach (var o in objects)
        {
            var inObjStm = compressible && !o.IsStream && !o.NeverCompress && o.Number != infoNum;
            if (handler is null || o == encryptObj || inObjStm)
            {
                serialized[o.Number] = (o.Value, o.Data);
                continue;
            }

            var id = new ObjectId(o.Number, 0);
            var value = EncryptStrings(o.Value, b => new PdfString(handler.EncryptString(b, id), true));
            byte[]? data = null;
            if (o.IsStream)
            {
                var isMeta = ((PdfDictionary)o.Value)["Type"] is PdfName { Value: "Metadata" };
                data = isMeta && !handler.EncryptMetadata ? o.Data : handler.EncryptStream(o.Data!, id);
            }

            serialized[o.Number] = (value, data);
        }

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        void WB(byte[] b) => ms.Write(b);

        if (JunkBeforeHeader > 0)
        {
            WB(Enumerable.Repeat((byte)'J', JunkBeforeHeader - 1).Append((byte)'\n').ToArray());
        }

        var baseOffset = JunkBeforeHeader; // offsets are written relative to the header, as damaged real-world files do
        W($"%PDF-{Version}\n");
        WB(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

        var offsets = new Dictionary<int, long>();
        var compressed = new Dictionary<int, (int Stream, int Index)>();
        int? linNum = null;
        long linDictPos = 0;
        long firstXrefPos = 0;
        int? hintNum = null;
        var prevPatchPos = -1L;

        if (Linearized)
        {
            linNum = ++maxNum;
            hintNum = ++maxNum;
            linDictPos = ms.Position;
            offsets[linNum.Value] = ms.Position - baseOffset;
            // Fixed-width placeholders are patched once the layout is known.
            W($"{linNum} 0 obj\n<< /Linearized 1 /L 0000000000 /H [ 0000000000 0000000000 ] /O {objects.First(o => o.Value is PdfDictionary d && d["Type"] is PdfName { Value: "Page" }).Number} /E 0 /N {PageCount} /T 0 >>\nendobj\n");
            firstXrefPos = ms.Position;
            W($"xref\n{linNum} 2\n");
            // Entries patched below.
            W("0000000000 00000 n\r\n0000000000 00000 n\r\n");
            W("trailer\n<< /Size " + (maxNum + 1) + " /Root " + catalogNum + " 0 R" + (infoNum > 0 ? $" /Info {infoNum} 0 R" : "") + IdString() + TrailerExtrasString(trailerExtras) + " /Prev ");
            prevPatchPos = ms.Position;
            W("0000000000 >>\nstartxref\n0\n%%EOF\n");
            offsets[hintNum.Value] = ms.Position - baseOffset;
            var hint = new byte[] { 0, 0, 0, 1, 0, 0, 0, 2 };
            W($"{hintNum} 0 obj\n<< /Length {hint.Length} /S 0 >>\nstream\n");
            WB(hint);
            W("\nendstream\nendobj\n");
        }

        var mainObjects = objects.Where(o => !(compressible && !o.IsStream && !o.NeverCompress && o.Number != infoNum)).ToList();
        var toCompress = objects.Where(o => compressible && !o.IsStream && !o.NeverCompress && o.Number != infoNum).ToList();

        foreach (var o in mainObjects)
        {
            offsets[o.Number] = ms.Position - baseOffset;
            WriteIndirect(ms, o.Number, serialized[o.Number].Value, serialized[o.Number].Data, wrongLength: WrongStreamLength && o.IsStream && o == mainObjects.First(x => x.IsStream));
        }

        int? objStmNum = null;
        if (toCompress.Count > 0)
        {
            objStmNum = ++maxNum;
            var header = new StringBuilder();
            var body = new MemoryStream();
            for (var i = 0; i < toCompress.Count; i++)
            {
                header.Append(toCompress[i].Number).Append(' ').Append(body.Length).Append(' ');
                var bytes = Ser(toCompress[i].Value);
                body.Write(bytes);
                body.WriteByte((byte)'\n');
                compressed[toCompress[i].Number] = (objStmNum.Value, i);
            }

            var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
            var content = headerBytes.Concat(body.ToArray()).ToArray();
            var objStmDict = new PdfDictionary { ["Type"] = N("ObjStm"), ["N"] = new PdfInteger(toCompress.Count), ["First"] = new PdfInteger(headerBytes.Length), ["Filter"] = N("FlateDecode") };
            var data = Deflate(content);
            if (handler is not null)
            {
                data = handler.EncryptStream(data, new ObjectId(objStmNum.Value, 0));
            }

            offsets[objStmNum.Value] = ms.Position - baseOffset;
            WriteIndirect(ms, objStmNum.Value, objStmDict, data, false);
        }

        var size = maxNum + 1;
        long startxref;
        switch (Mode)
        {
            case XrefMode.Classic:
                startxref = ms.Position - baseOffset;
                WriteClassicXref(ms, offsets, size, null, linNum);
                W("trailer\n<< /Size " + size + " /Root " + catalogNum + " 0 R" + (infoNum > 0 ? $" /Info {infoNum} 0 R" : "") + IdString() + TrailerExtrasString(trailerExtras) + " >>\n");
                break;
            case XrefMode.XrefStream:
            case XrefMode.XrefStreamWithObjectStreams:
            {
                var xrefNum = size;
                size++;
                startxref = ms.Position - baseOffset;
                offsets[xrefNum] = startxref;
                WriteXrefStream(ms, xrefNum, offsets, compressed, size, catalogNum, infoNum, trailerExtras, linNum, prevOffset: null, onlyCompressed: false);
                break;
            }

            case XrefMode.Hybrid:
            {
                var xrefNum = size;
                size++;
                var xrefStmPos = ms.Position - baseOffset;
                offsets[xrefNum] = xrefStmPos;
                WriteXrefStream(ms, xrefNum, offsets, compressed, size, catalogNum, infoNum, trailerExtras, linNum, prevOffset: null, onlyCompressed: true);
                startxref = ms.Position - baseOffset;
                WriteClassicXref(ms, offsets, size, compressed.Keys.ToHashSet(), linNum);
                W("trailer\n<< /Size " + size + " /Root " + catalogNum + " 0 R" + (infoNum > 0 ? $" /Info {infoNum} 0 R" : "") + IdString() + TrailerExtrasString(trailerExtras) + $" /XRefStm {xrefStmPos} >>\n");
                break;
            }

            default:
                throw new InvalidOperationException();
        }

        var finalStartXref = Linearized ? firstXrefPos - baseOffset : startxref;
        if (BreakStartXref)
        {
            finalStartXref += 3;
        }

        W($"startxref\n{finalStartXref}\n%%EOF\n");

        if (IncrementalUpdate)
        {
            AppendIncrementalUpdate(ms, baseOffset, objects, startxref, size, catalogNum, infoNum, trailerExtras, handler);
        }

        var bytesOut = ms.ToArray();
        if (Linearized)
        {
            Patch(bytesOut, linDictPos, "/L 0000000000", "/L " + bytesOut.Length.ToString("D10", CultureInfo.InvariantCulture));
            Patch(bytesOut, linDictPos, "/H [ 0000000000 0000000000 ]", "/H [ " + offsets[hintNum!.Value].ToString("D10", CultureInfo.InvariantCulture) + " 0000000008 ]");
            Patch(bytesOut, prevPatchPos, "0000000000", startxref.ToString("D10", CultureInfo.InvariantCulture));
            var entries = offsets[linNum!.Value].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n\r\n" + offsets[hintNum.Value].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n\r\n";
            Patch(bytesOut, firstXrefPos, "0000000000 00000 n\r\n0000000000 00000 n\r\n", entries);
        }

        if (RealByteRange)
        {
            PatchByteRange(bytesOut);
        }

        if (BreakXrefOffsets)
        {
            // Shift every in-use classic xref offset by 7 bytes.
            var text = Encoding.Latin1.GetString(bytesOut);
            var xrefPos = text.LastIndexOf("\nxref\n", StringComparison.Ordinal);
            var trailerPos = text.IndexOf("trailer", xrefPos, StringComparison.Ordinal);
            var section = text.Substring(xrefPos, trailerPos - xrefPos);
            var fixedSection = System.Text.RegularExpressions.Regex.Replace(section, @"(\d{10}) (\d{5}) n", m =>
                (long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 7).ToString("D10", CultureInfo.InvariantCulture) + " " + m.Groups[2].Value + " n");
            bytesOut = Encoding.Latin1.GetBytes(text[..xrefPos] + fixedSection + text[trailerPos..]);
        }

        handler?.Dispose();
        return bytesOut;
    }

    private void AppendIncrementalUpdate(MemoryStream ms, long baseOffset, List<Obj> objects, long prevXref, int size, int catalogNum, int infoNum, PdfDictionary trailerExtras, StandardSecurityHandler? handler)
    {
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        var offsets = new Dictionary<int, long>();

        // New version of the Info dictionary and of the first content stream.
        if (infoNum > 0)
        {
            var info = new PdfDictionary
            {
                ["Title"] = new PdfString(EncodeText("Updated title", TextEncodingKind.PdfDoc)),
                ["Producer"] = new PdfString(EncodeText("TestPdfBuilder (incremental)", TextEncodingKind.PdfDoc)),
            };
            offsets[infoNum] = ms.Position - baseOffset;
            var id = new ObjectId(infoNum, 0);
            WriteIndirect(ms, infoNum, handler is null ? info : EncryptStrings(info, b => new PdfString(handler.EncryptString(b, id), true)), null, false);
        }

        var firstContent = objects.First(o => o.IsStream && ((PdfDictionary)o.Value)["Filter"] is PdfName { Value: "FlateDecode" });
        var newPlain = Deflate(Encoding.ASCII.GetBytes("BT /F1 30 Tf 72 700 Td (Updated page 1) Tj ET\n"));
        PlainStreams[firstContent.Number] = newPlain;
        offsets[firstContent.Number] = ms.Position - baseOffset;
        var data = handler is null ? newPlain : handler.EncryptStream(newPlain, new ObjectId(firstContent.Number, 0));
        WriteIndirect(ms, firstContent.Number, new PdfDictionary { ["Filter"] = N("FlateDecode") }, data, false);

        var xrefPos = ms.Position - baseOffset;
        W("xref\n");
        foreach (var (num, off) in offsets.OrderBy(e => e.Key))
        {
            W($"{num} 1\n{off:D10} 00000 n\r\n");
        }

        var id0 = IncrementalId0 ?? Id0;
        var idPart = IdMode == IdMode.Normal ? $" /ID [<{Convert.ToHexString(id0)}> <{Convert.ToHexString(new byte[] { 9, 9, 9, 9 })}>]" : "";
        W("trailer\n<< /Size " + size + " /Root " + catalogNum + " 0 R" + (infoNum > 0 ? $" /Info {infoNum} 0 R" : "") + idPart + TrailerExtrasString(trailerExtras) + $" /Prev {prevXref} >>\nstartxref\n{xrefPos}\n%%EOF\n");
    }

    private void WriteClassicXref(MemoryStream ms, Dictionary<int, long> offsets, int size, HashSet<int>? markFree, int? skipFrom)
    {
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        string Entry(int i)
        {
            if (i != 0 && offsets.TryGetValue(i, out var off) && (markFree is null || !markFree.Contains(i)) && (skipFrom is null || i < skipFrom))
            {
                return off.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n" + XrefEntryEol;
            }

            return "0000000000 " + (i == 0 ? "65535" : "00001") + " f" + XrefEntryEol;
        }

        W("xref\n");
        var count = skipFrom ?? size;
        if (MultipleSubsections && count > 3)
        {
            W("0 1\n" + Entry(0));
            var mid = count / 2;
            W($"1 {mid - 1}\n");
            for (var i = 1; i < mid; i++)
            {
                W(Entry(i));
            }

            W($"{mid} {count - mid}\n");
            for (var i = mid; i < count; i++)
            {
                W(Entry(i));
            }
        }
        else
        {
            W($"0 {count}\n");
            for (var i = 0; i < count; i++)
            {
                W(Entry(i));
            }
        }
    }

    private void WriteXrefStream(MemoryStream ms, int xrefNum, Dictionary<int, long> offsets, Dictionary<int, (int Stream, int Index)> compressed, int size, int catalogNum, int infoNum, PdfDictionary trailerExtras, int? skipFrom, long? prevOffset, bool onlyCompressed)
    {
        var rows = new List<byte[]>();
        var count = skipFrom ?? size;
        var index = new PdfArray();
        if (onlyCompressed)
        {
            foreach (var (num, (stream, idx)) in compressed.OrderBy(c => c.Key))
            {
                index.Add(new PdfInteger(num));
                index.Add(new PdfInteger(1));
                rows.Add(Row(2, stream, idx));
            }
        }
        else
        {
            index.Add(new PdfInteger(0));
            index.Add(new PdfInteger(count));
            for (var i = 0; i < count; i++)
            {
                if (compressed.TryGetValue(i, out var c))
                {
                    rows.Add(Row(2, c.Stream, c.Index));
                }
                else if (i != 0 && offsets.TryGetValue(i, out var off))
                {
                    rows.Add(Row(1, off, 0));
                }
                else
                {
                    rows.Add(Row(0, 0, i == 0 ? 65535 : 1));
                }
            }
        }

        var raw = rows.SelectMany(r => r).ToArray();
        const int columns = 7;
        byte[] encoded;
        PdfDictionary? parms = null;
        if (Predictor == 2)
        {
            encoded = (byte[])raw.Clone();
            for (var r = 0; r < raw.Length / columns; r++)
            {
                for (var c = columns - 1; c >= 1; c--)
                {
                    encoded[(r * columns) + c] = (byte)(raw[(r * columns) + c] - raw[(r * columns) + c - 1]);
                }
            }

            parms = new PdfDictionary { ["Predictor"] = new PdfInteger(2), ["Columns"] = new PdfInteger(columns) };
        }
        else if (Predictor >= 10)
        {
            var output = new List<byte>();
            var prior = new byte[columns];
            for (var r = 0; r < raw.Length / columns; r++)
            {
                output.Add(2); // PNG Up
                for (var c = 0; c < columns; c++)
                {
                    output.Add((byte)(raw[(r * columns) + c] - prior[c]));
                }

                Array.Copy(raw, r * columns, prior, 0, columns);
            }

            encoded = output.ToArray();
            parms = new PdfDictionary { ["Predictor"] = new PdfInteger(Predictor), ["Columns"] = new PdfInteger(columns) };
        }
        else
        {
            encoded = raw;
        }

        var dict = new PdfDictionary
        {
            ["Type"] = N("XRef"),
            ["Size"] = new PdfInteger(size),
            ["W"] = Arr(1, 4, 2),
            ["Index"] = index,
            ["Root"] = new PdfReference(catalogNum, 0),
            ["Filter"] = N("FlateDecode"),
        };
        if (parms is not null)
        {
            dict["DecodeParms"] = parms;
        }

        if (infoNum > 0)
        {
            dict["Info"] = new PdfReference(infoNum, 0);
        }

        if (IdArray() is { } id)
        {
            dict["ID"] = id;
        }

        foreach (var e in trailerExtras.Entries)
        {
            dict[e.Key] = e.Value;
        }

        if (prevOffset is long p)
        {
            dict["Prev"] = new PdfInteger(p);
        }

        WriteIndirect(ms, xrefNum, dict, Deflate(encoded), false);
    }

    private static byte[] Row(int type, long f2, int f3) => new[]
    {
        (byte)type, (byte)(f2 >> 24), (byte)(f2 >> 16), (byte)(f2 >> 8), (byte)f2, (byte)(f3 >> 8), (byte)f3,
    };

    private PdfObject? IdArray() => IdMode switch
    {
        IdMode.Normal => new PdfArray(new PdfObject[] { new PdfString(Id0, true), new PdfString(Id1, true) }),
        IdMode.Malformed => new PdfArray(new PdfObject[] { new PdfInteger(5) }),
        _ => null,
    };

    private string IdString() => IdArray() is { } a ? " /ID " + Encoding.Latin1.GetString(Ser(a)) : "";

    private static string TrailerExtrasString(PdfDictionary extras) =>
        string.Concat(extras.Entries.Select(e => " /" + e.Key.Value + " " + Encoding.Latin1.GetString(Ser(e.Value))));

    private static void WriteIndirect(MemoryStream ms, int num, PdfObject value, byte[]? data, bool wrongLength)
    {
        ms.Write(Encoding.ASCII.GetBytes($"{num} 0 obj\n"));
        if (data is not null)
        {
            var dict = ((PdfDictionary)value).ShallowClone();
            if (dict["Length"] is not PdfReference)
            {
                dict["Length"] = new PdfInteger(wrongLength ? Math.Max(0, data.Length - 5) : data.Length);
            }

            ms.Write(Ser(dict));
            ms.Write("\nstream\r\n"u8);
            ms.Write(data);
            ms.Write("\nendstream\nendobj\n"u8);
        }
        else
        {
            ms.Write(Ser(value));
            ms.Write("\nendobj\n"u8);
        }
    }

    private static void Patch(byte[] data, long from, string placeholder, string value)
    {
        if (placeholder.Length != value.Length)
        {
            throw new InvalidOperationException("Patch width mismatch.");
        }

        var text = Encoding.Latin1.GetString(data);
        var idx = text.IndexOf(placeholder, (int)from, StringComparison.Ordinal);
        Encoding.Latin1.GetBytes(value).CopyTo(data, idx);
    }

    public static PdfObject EncryptStrings(PdfObject obj, Func<byte[], PdfString> f) => obj switch
    {
        PdfString s => f(s.Value),
        PdfArray a => new PdfArray(a.Items.Select(i => EncryptStrings(i, f))),
        PdfDictionary d => CopyDict(d, f),
        _ => obj,
    };

    private static PdfDictionary CopyDict(PdfDictionary d, Func<byte[], PdfString> f)
    {
        var isSig = d["Type"] is PdfName { Value: "Sig" };
        var c = new PdfDictionary();
        foreach (var e in d.Entries)
        {
            c[e.Key] = isSig && e.Key.Value == "Contents" ? e.Value : EncryptStrings(e.Value, f);
        }

        return c;
    }

    public static byte[] Ser(PdfObject o)
    {
        var sb = new MemoryStream();
        void W(string s) => sb.Write(Encoding.Latin1.GetBytes(s));
        void Go(PdfObject x)
        {
            switch (x)
            {
                case PdfNull: W("null"); break;
                case PdfBoolean b: W(b.Value ? "true" : "false"); break;
                case PdfInteger i: W(i.Value.ToString(CultureInfo.InvariantCulture)); break;
                case PdfReal r: W(r.ToString()); break;
                case PdfName n: W("/" + EscapeName(n.Value)); break;
                case PdfReference r: W($"{r.Id.Number} {r.Id.Generation} R"); break;
                case PdfString s when s.IsHex: W("<" + Convert.ToHexString(s.Value) + ">"); break;
                case PdfString s:
                    sb.WriteByte((byte)'(');
                    foreach (var b in s.Value)
                    {
                        if (b is (byte)'(' or (byte)')' or (byte)'\\')
                        {
                            sb.WriteByte((byte)'\\');
                            sb.WriteByte(b);
                        }
                        else if (b == '\r')
                        {
                            W("\\r");
                        }
                        else
                        {
                            sb.WriteByte(b);
                        }
                    }

                    sb.WriteByte((byte)')');
                    break;
                case PdfArray a:
                    W("[");
                    for (var k = 0; k < a.Count; k++)
                    {
                        if (k > 0) W(" ");
                        Go(a[k]);
                    }

                    W("]");
                    break;
                case PdfDictionary d:
                    W("<<");
                    foreach (var e in d.Entries)
                    {
                        W("/" + EscapeName(e.Key.Value) + " ");
                        Go(e.Value);
                        W(" ");
                    }

                    W(">>");
                    break;
            }
        }

        Go(o);
        return sb.ToArray();
    }

    private static string EscapeName(string name) =>
        string.Concat(name.Select(c => c < '!' || c > '~' || "#()<>[]{}/%".Contains(c) ? "#" + ((int)c).ToString("X2", CultureInfo.InvariantCulture) : c.ToString()));

    public static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }

    private static PdfName N(string s) => PdfName.Get(s);

    private static PdfArray Arr(params double[] values) => new(values.Select(v => (PdfObject)new PdfInteger((long)v)));
}
