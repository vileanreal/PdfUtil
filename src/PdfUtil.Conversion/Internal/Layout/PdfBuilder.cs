using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PdfUtil.Conversion.Internal.Model;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Images;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Text;
using PdfUtil.Internal.Writing;

namespace PdfUtil.Conversion.Internal.Layout;

/// <summary>Document information for a new PDF.</summary>
internal sealed record PdfMetadata(string? Title, string? Author, string? Subject, string? Keywords, string? Language, string Creator = "PdfUtil HTML to PDF");

/// <summary>A bookmark for a new PDF: title, nesting level (1 = top) and the anchor it goes to.</summary>
internal sealed record PdfBookmarkEntry(string Title, int Level, string Anchor);

/// <summary>
/// Writes laid-out pages as a new PDF: one content stream per page (Flate), the standard fonts by reference and
/// TrueType fonts embedded as subsets, images (PNG decoded to Flate samples with a soft mask for transparency, JPEG
/// passed through), link annotations (web addresses and named destinations), the named destinations, bookmarks,
/// document information and language, and a classic cross-reference table.
/// </summary>
internal sealed class PdfBuilder
{
    private readonly List<(PdfObject Object, byte[]? Stream)> _objects = new() { (PdfNull.Instance, null) };
    private readonly Dictionary<FontHandle, (string Name, PdfReference? Ref)> _fonts = new();
    private readonly Dictionary<ImageData, (string Name, PdfReference Ref)> _images = new(ReferenceEqualityComparer.Instance);
    private readonly long _maxPixels;
    private readonly Action<string> _warn;

    public PdfBuilder(long maxPixels, Action<string> warn)
    {
        _maxPixels = maxPixels;
        _warn = warn;
    }

    public int ImageCount => _images.Count;

    public int LinkCount { get; private set; }

    public IEnumerable<string> EmbeddedFonts => _fonts.Keys.OfType<TrueTypeFontHandle>().Where(f => f.Embedded.IsUsed).Select(f => f.Family).Distinct();

    private PdfReference Add(PdfObject obj, byte[]? stream = null)
    {
        _objects.Add((obj, stream));
        return new PdfReference(_objects.Count - 1, 0);
    }

    private PdfReference Reserve() => Add(PdfNull.Instance);

    private void Set(PdfReference reference, PdfObject obj, byte[]? stream = null) => _objects[reference.Id.Number] = (obj, stream);

    private static PdfObject N(double v) => Math.Abs(v - Math.Round(v)) < 1e-6 ? new PdfInteger((long)Math.Round(v)) : new PdfReal(Math.Round(v, 3));

    private static PdfString Text(string text) => new(TextString.Encode(text));

    public byte[] Build(List<LaidOutPage> pages, PdfMetadata metadata, List<PdfBookmarkEntry> bookmarks)
    {
        var pagesRef = Reserve();
        var pageRefs = new List<PdfReference>();
        var destinations = new SortedDictionary<string, PdfArray>(StringComparer.Ordinal);
        var pending = new List<(PdfDictionary Annotation, string Anchor)>();
        foreach (var page in pages)
        {
            var pageRef = Reserve();
            pageRefs.Add(pageRef);
            foreach (var (name, y) in page.Anchors)
            {
                destinations.TryAdd(name, new PdfArray(new PdfObject[] { pageRef, PdfName.Get("XYZ"), new PdfInteger(0), N(page.Height - y), PdfNull.Instance }));
            }
        }

        for (var p = 0; p < pages.Count; p++)
        {
            var page = pages[p];
            var resources = new PdfDictionary();
            var content = Content(page, resources);
            var annots = new PdfArray();
            foreach (var link in page.Links)
            {
                var annotation = new PdfDictionary
                {
                    ["Type"] = PdfName.Get("Annot"),
                    ["Subtype"] = PdfName.Get("Link"),
                    ["Rect"] = new PdfArray(new[] { N(link.X), N(page.Height - link.Y - link.Height), N(link.X + link.Width), N(page.Height - link.Y) }),
                    ["Border"] = new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfInteger(0) }),
                    ["F"] = new PdfInteger(4),
                };
                if (link.Uri is not null)
                {
                    annotation["A"] = new PdfDictionary { ["S"] = PdfName.Get("URI"), ["URI"] = new PdfString(Encoding.UTF8.GetBytes(link.Uri)) };
                }
                else if (link.Anchor is not null && destinations.ContainsKey(link.Anchor))
                {
                    annotation["Dest"] = new PdfString(Encoding.UTF8.GetBytes(link.Anchor));
                }
                else
                {
                    continue;
                }

                annotation["P"] = pageRefs[p];
                annots.Add(Add(annotation));
                LinkCount++;
            }

            var pageDict = new PdfDictionary
            {
                ["Type"] = PdfName.Get("Page"),
                ["Parent"] = pagesRef,
                ["MediaBox"] = new PdfArray(new[] { N(0), N(0), N(page.Width), N(page.Height) }),
                ["Resources"] = resources,
                ["Contents"] = Add(new PdfDictionary { ["Filter"] = PdfName.Get("FlateDecode") }, PdfWriter.Deflate(content)),
            };
            if (annots.Count > 0)
            {
                pageDict["Annots"] = annots;
            }

            Set(pageRefs[p], pageDict);
        }

        // Fonts are written last: the subsets hold exactly the glyphs the pages used.
        var subset = 0;
        foreach (var (handle, (name, reference)) in _fonts.ToList())
        {
            PdfDictionary font;
            if (handle is TrueTypeFontHandle tt)
            {
                font = tt.Embedded.Build(SubsetTag(subset++), (obj, data) => data is null ? Add(obj) : Add(StreamDictionary(obj, data), PdfWriter.Deflate(data)));
            }
            else
            {
                font = new PdfDictionary
                {
                    ["Type"] = PdfName.Get("Font"),
                    ["Subtype"] = PdfName.Get("Type1"),
                    ["BaseFont"] = PdfName.Get(((StandardFontHandle)handle).Name),
                    ["Encoding"] = PdfName.Get("WinAnsiEncoding"),
                };
            }

            Set(reference!, font);
        }

        Set(pagesRef, new PdfDictionary
        {
            ["Type"] = PdfName.Get("Pages"),
            ["Kids"] = new PdfArray(pageRefs),
            ["Count"] = new PdfInteger(pageRefs.Count),
        });

        var catalog = new PdfDictionary { ["Type"] = PdfName.Get("Catalog"), ["Pages"] = pagesRef };
        if (destinations.Count > 0)
        {
            var names = new PdfArray();
            foreach (var (name, dest) in destinations.OrderBy(d => Encoding.UTF8.GetBytes(d.Key), ByteComparer.Instance))
            {
                names.Add(new PdfString(Encoding.UTF8.GetBytes(name)));
                names.Add(dest);
            }

            catalog["Names"] = new PdfDictionary { ["Dests"] = Add(new PdfDictionary { ["Names"] = names }) };
        }

        if (Outline(bookmarks.Where(b => destinations.ContainsKey(b.Anchor)).ToList(), destinations) is { } outlines)
        {
            catalog["Outlines"] = outlines;
            catalog["PageMode"] = PdfName.Get("UseOutlines");
        }

        if (!string.IsNullOrWhiteSpace(metadata.Language))
        {
            catalog["Lang"] = Text(metadata.Language.Trim());
        }

        // XMP metadata mirrors the document information (same values and dates).
        var now = DateTimeOffset.Now;
        var changes = new PdfInfoChanges { Title = metadata.Title, Author = metadata.Author, Subject = metadata.Subject, Keywords = metadata.Keywords, Creator = metadata.Creator, Producer = "PdfUtil", CreationDate = now, ModificationDate = now };
        var packet = Encoding.UTF8.GetBytes("<?xpacket begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>");
        if (XmpEditor.Apply(packet, changes, now) is { } xmp)
        {
            catalog["Metadata"] = Add(new PdfDictionary { ["Type"] = PdfName.Get("Metadata"), ["Subtype"] = PdfName.Get("XML") }, xmp);
        }

        var catalogRef = Add(catalog);
        var info = new PdfDictionary { ["Producer"] = Text("PdfUtil"), ["Creator"] = Text(metadata.Creator), ["CreationDate"] = Text(PdfDate.Format(now)), ["ModDate"] = Text(PdfDate.Format(now)) };
        foreach (var (key, value) in new[] { ("Title", metadata.Title), ("Author", metadata.Author), ("Subject", metadata.Subject), ("Keywords", metadata.Keywords) })
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                info[key] = Text(value.Trim());
            }
        }

        var infoRef = Add(info);
        return Serialize(catalogRef, infoRef);
    }

    private static PdfDictionary StreamDictionary(PdfObject obj, byte[] data)
    {
        var dict = (PdfDictionary)obj;
        dict["Filter"] = PdfName.Get("FlateDecode");
        return dict;
    }

    private static string SubsetTag(int n)
    {
        var chars = new char[6];
        var value = (n * 7919) + 104729;
        for (var i = 0; i < 6; i++)
        {
            chars[i] = (char)('A' + (value % 26));
            value /= 26;
        }

        return new string(chars);
    }

    private PdfReference? Outline(List<PdfBookmarkEntry> entries, SortedDictionary<string, PdfArray> destinations)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        var root = Reserve();
        var refs = entries.Select(_ => Reserve()).ToList();
        var dicts = entries.Select(e => new PdfDictionary { ["Title"] = Text(e.Title), ["Dest"] = destinations[e.Anchor] }).ToList();

        // Parent: the nearest earlier entry with a smaller level.
        var parents = new int[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            parents[i] = -1;
            for (var j = i - 1; j >= 0; j--)
            {
                if (entries[j].Level < entries[i].Level)
                {
                    parents[i] = j;
                    break;
                }
            }
        }

        var children = Enumerable.Range(-1, entries.Count + 1).ToDictionary(p => p, p => Enumerable.Range(0, entries.Count).Where(i => parents[i] == p).ToList());
        foreach (var (parent, kids) in children)
        {
            for (var k = 0; k < kids.Count; k++)
            {
                var d = dicts[kids[k]];
                d["Parent"] = parent < 0 ? root : refs[parent];
                if (k > 0)
                {
                    d["Prev"] = refs[kids[k - 1]];
                }

                if (k + 1 < kids.Count)
                {
                    d["Next"] = refs[kids[k + 1]];
                }
            }

            if (kids.Count > 0 && parent >= 0)
            {
                dicts[parent]["First"] = refs[kids[0]];
                dicts[parent]["Last"] = refs[kids[^1]];
                dicts[parent]["Count"] = new PdfInteger(kids.Count);
            }
        }

        for (var i = 0; i < entries.Count; i++)
        {
            Set(refs[i], dicts[i]);
        }

        var top = children[-1];
        Set(root, new PdfDictionary { ["Type"] = PdfName.Get("Outlines"), ["First"] = refs[top[0]], ["Last"] = refs[top[^1]], ["Count"] = new PdfInteger(entries.Count) });
        return root;
    }

    // ------------------------------------------------------------------ content

    private byte[] Content(LaidOutPage page, PdfDictionary resources)
    {
        var sb = new StringBuilder();
        var fonts = new PdfDictionary();
        var xobjects = new PdfDictionary();
        string? fill = null;
        string F(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
        string Color(string hex) => hex.Length == 7
            ? $"{F(Convert.ToInt32(hex[1..3], 16) / 255.0)} {F(Convert.ToInt32(hex[3..5], 16) / 255.0)} {F(Convert.ToInt32(hex[5..7], 16) / 255.0)}"
            : "0 0 0";
        void Fill(string color)
        {
            if (fill != color)
            {
                sb.Append(Color(color)).Append(" rg\n");
                fill = color;
            }
        }

        foreach (var op in page.Ops)
        {
            switch (op)
            {
                case RectOp r when r.Width > 0 && r.Height > 0:
                    Fill(r.Color);
                    sb.Append(F(r.X)).Append(' ').Append(F(page.Height - r.Y - r.Height)).Append(' ').Append(F(r.Width)).Append(' ').Append(F(r.Height)).Append(" re f\n");
                    break;
                case ImageOp i when i.Width > 0 && i.Height > 0:
                    if (Image(i.Image) is { } image)
                    {
                        xobjects[PdfName.Get(image.Name)] = image.Ref;
                        sb.Append("q ").Append(F(i.Width)).Append(" 0 0 ").Append(F(i.Height)).Append(' ').Append(F(i.X)).Append(' ').Append(F(page.Height - i.Y - i.Height)).Append(" cm /").Append(image.Name).Append(" Do Q\n");
                    }
                    else
                    {
                        // Placeholder: a light frame where the image would be.
                        sb.Append("q 0.6 0.6 0.6 RG 0.5 w ").Append(F(i.X)).Append(' ').Append(F(page.Height - i.Y - i.Height)).Append(' ').Append(F(i.Width)).Append(' ').Append(F(i.Height)).Append(" re S Q\n");
                    }

                    break;
                case TextOp t when t.Text.Length > 0:
                    var font = Font(t.Font);
                    fonts[PdfName.Get(font)] = _fonts[t.Font].Ref!;
                    Fill(t.Color);
                    sb.Append("BT /").Append(font).Append(' ').Append(F(t.Size)).Append(" Tf ");
                    if (t.FakeBold)
                    {
                        // Synthetic bold: fill and stroke the outlines in the text colour.
                        sb.Append(Color(t.Color)).Append(" RG ").Append(F(t.Size * 0.035)).Append(" w 2 Tr ");
                    }

                    sb.Append("1 0 ").Append(t.FakeItalic ? "0.2" : "0").Append(" 1 ").Append(F(t.X)).Append(' ').Append(F(page.Height - t.Y)).Append(" Tm <").Append(Convert.ToHexString(t.Font.Encode(t.Text))).Append("> Tj").Append(t.FakeBold ? " 0 Tr" : "").Append(" ET\n");
                    break;
            }
        }

        if (fonts.Count > 0)
        {
            resources["Font"] = fonts;
        }

        if (xobjects.Count > 0)
        {
            resources["XObject"] = xobjects;
        }

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private string Font(FontHandle handle)
    {
        if (!_fonts.TryGetValue(handle, out var entry))
        {
            entry = ("F" + (_fonts.Count + 1).ToString(CultureInfo.InvariantCulture), Reserve());
            _fonts[handle] = entry;
        }

        return entry.Name;
    }

    private (string Name, PdfReference Ref)? Image(ImageData image)
    {
        if (image.IsPlaceholder)
        {
            return null;
        }

        if (_images.TryGetValue(image, out var existing))
        {
            return existing;
        }

        PdfReference reference;
        try
        {
            if (image.MimeType == "image/jpeg")
            {
                var info = ImageCodecs.ReadJpeg(image.Data!);
                var dict = new PdfDictionary
                {
                    ["Type"] = PdfName.Get("XObject"),
                    ["Subtype"] = PdfName.Get("Image"),
                    ["Width"] = new PdfInteger(info.Width),
                    ["Height"] = new PdfInteger(info.Height),
                    ["ColorSpace"] = PdfName.Get(info.Components switch { 1 => "DeviceGray", 4 => "DeviceCMYK", _ => "DeviceRGB" }),
                    ["BitsPerComponent"] = new PdfInteger(8),
                    ["Filter"] = PdfName.Get("DCTDecode"),
                };
                if (info.Components == 4 && info.AdobeInverted)
                {
                    dict["Decode"] = new PdfArray(Enumerable.Range(0, 4).SelectMany(_ => new PdfObject[] { new PdfInteger(1), new PdfInteger(0) }));
                }

                reference = Add(dict, image.Data);
            }
            else
            {
                var raster = ImageCodecs.DecodePng(image.Data!, _maxPixels);
                var dict = new PdfDictionary
                {
                    ["Type"] = PdfName.Get("XObject"),
                    ["Subtype"] = PdfName.Get("Image"),
                    ["Width"] = new PdfInteger(raster.Width),
                    ["Height"] = new PdfInteger(raster.Height),
                    ["BitsPerComponent"] = new PdfInteger(8),
                    ["Filter"] = PdfName.Get("FlateDecode"),
                };
                dict["ColorSpace"] = raster.Palette is { } palette
                    ? new PdfArray(new PdfObject[] { PdfName.Get("Indexed"), PdfName.Get("DeviceRGB"), new PdfInteger((palette.Length / 3) - 1), new PdfString(palette, isHex: true) })
                    : PdfName.Get(raster.Channels == 1 ? "DeviceGray" : "DeviceRGB");
                if (raster.Alpha is { } alpha && alpha.Any(a => a != 255))
                {
                    dict["SMask"] = Add(new PdfDictionary
                    {
                        ["Type"] = PdfName.Get("XObject"),
                        ["Subtype"] = PdfName.Get("Image"),
                        ["Width"] = new PdfInteger(raster.Width),
                        ["Height"] = new PdfInteger(raster.Height),
                        ["ColorSpace"] = PdfName.Get("DeviceGray"),
                        ["BitsPerComponent"] = new PdfInteger(8),
                        ["Filter"] = PdfName.Get("FlateDecode"),
                    }, PdfWriter.Deflate(alpha));
                }

                reference = Add(dict, PdfWriter.Deflate(raster.Samples));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException or PdfLimitExceededException)
        {
            _warn("An image could not be decoded and was left out.");
            return null;
        }

        var entry = ("Im" + (_images.Count + 1).ToString(CultureInfo.InvariantCulture), reference);
        _images[image] = entry;
        return entry;
    }

    // ------------------------------------------------------------------ file

    private byte[] Serialize(PdfReference catalog, PdfReference info)
    {
        using var output = new MemoryStream();
        void Write(string text) => output.Write(Encoding.Latin1.GetBytes(text));
        Write("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new long[_objects.Count];
        for (var i = 1; i < _objects.Count; i++)
        {
            offsets[i] = output.Length;
            var (obj, stream) = _objects[i];
            Write($"{i.ToString(CultureInfo.InvariantCulture)} 0 obj\n");
            if (stream is not null)
            {
                var dict = (PdfDictionary)obj;
                dict["Length"] = new PdfInteger(stream.Length);
                output.Write(PdfWriter.Serialize(dict, 64));
                Write("\nstream\n");
                output.Write(stream);
                Write("\nendstream");
            }
            else
            {
                output.Write(PdfWriter.Serialize(obj, 64));
            }

            Write("\nendobj\n");
        }

        var xref = output.Length;
        Write($"xref\n0 {_objects.Count.ToString(CultureInfo.InvariantCulture)}\n0000000000 65535 f\r\n");
        for (var i = 1; i < _objects.Count; i++)
        {
            Write(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n\r\n");
        }

        var id = RandomNumberGenerator.GetBytes(16);
        var trailer = new PdfDictionary
        {
            ["Size"] = new PdfInteger(_objects.Count),
            ["Root"] = catalog,
            ["Info"] = info,
            ["ID"] = new PdfArray(new PdfObject[] { new PdfString(id, isHex: true), new PdfString(id, isHex: true) }),
        };
        Write("trailer\n");
        output.Write(PdfWriter.Serialize(trailer, 8));
        Write($"\nstartxref\n{xref.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n");
        return output.ToArray();
    }

    private sealed class ByteComparer : IComparer<byte[]>
    {
        public static readonly ByteComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x!.AsSpan().SequenceCompareTo(y);
    }
}
