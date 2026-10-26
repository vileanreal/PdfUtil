using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Text;

namespace PdfUtil.Internal.Document;

/// <summary>
/// A page found in the page tree: its object number (0 for a direct dictionary), dictionary, inherited resources and
/// geometry, and the raw inheritable attributes (<c>/Resources /MediaBox /CropBox /Rotate</c>) as found on the page or
/// its nearest ancestor.
/// </summary>
internal readonly record struct PageNode(int ObjectNumber, PdfDictionary Page, PdfDictionary? Resources, PdfPageInfo Info, PdfDictionary Inherited);

/// <summary>Traverses the page tree (ISO 32000-1 §7.7.3) with inherited attributes and cycle detection.</summary>
internal static class PageTreeReader
{
    private static readonly PdfRectangle DefaultMediaBox = new(0, 0, 612, 792);

    /// <summary>Page attributes inherited from page tree nodes (ISO 32000-1 Table 30).</summary>
    public static readonly string[] InheritableKeys = { "Resources", "MediaBox", "CropBox", "Rotate" };

    public static List<PdfPageInfo> ReadPages(PdfDocument doc, int maxDepth, CancellationToken ct) =>
        ReadPageNodes(doc, maxDepth, ct).Select(p => p.Info).ToList();

    public static List<PageNode> ReadPageNodes(PdfDocument doc, int maxDepth, CancellationToken ct)
    {
        var pages = new List<PageNode>();
        var catalog = doc.Catalog ?? throw new PdfCorruptException("The document catalog cannot be read.");
        var rootRef = catalog["Pages"];
        var visited = new HashSet<int>();
        var stack = new Stack<(PdfObject Node, PdfRectangle? Media, PdfRectangle? Crop, int Rotate, PdfDictionary? Resources, PdfDictionary Inherited, int Depth)>();
        stack.Push((rootRef ?? PdfNull.Instance, null, null, 0, null, new PdfDictionary(), 0));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (nodeObj, media, crop, rotate, resources, inherited, depth) = stack.Pop();
            if (depth > maxDepth)
            {
                throw new PdfLimitExceededException("The page tree is nested too deeply.");
            }

            if (nodeObj is PdfReference r && !visited.Add(r.Id.Number))
            {
                doc.AddWarning("The page tree contains a cycle; repeated nodes were skipped.", PdfValidationCodes.PageTreeCycle, r.Id.Number);
                continue;
            }

            if (doc.Resolve(nodeObj) is not PdfDictionary node)
            {
                continue;
            }

            foreach (var key in InheritableKeys)
            {
                if (node[key] is { } value)
                {
                    inherited = inherited.ShallowClone();
                    inherited[key] = value;
                }
            }

            media = ReadRect(doc, node["MediaBox"]) ?? media;
            crop = ReadRect(doc, node["CropBox"]) ?? crop;
            resources = doc.ResolveDictionary(node["Resources"]) ?? resources;
            if (doc.Resolve(node["Rotate"]) is PdfInteger rot)
            {
                rotate = (int)rot.Value;
            }

            var type = (doc.Resolve(node[PdfName.Type]) as PdfName)?.Value;
            if (doc.Resolve(node["Kids"]) is PdfArray kids && type != "Page")
            {
                for (var i = kids.Count - 1; i >= 0; i--)
                {
                    stack.Push((kids[i], media, crop, rotate, resources, inherited, depth + 1));
                }

                continue;
            }

            if (type is "Pages")
            {
                continue; // an intermediate node without kids
            }

            if (media is null)
            {
                doc.AddWarning("Some pages have no /MediaBox; US Letter was assumed.", PdfValidationCodes.PageMediaBoxMissing, (nodeObj as PdfReference)?.Id.Number);
            }

            var normalizedRotate = ((rotate % 360) + 360) % 360;
            normalizedRotate = normalizedRotate - (normalizedRotate % 90);
            var info = new PdfPageInfo(pages.Count + 1, media ?? DefaultMediaBox, crop, normalizedRotate);
            pages.Add(new PageNode((nodeObj as PdfReference)?.Id.Number ?? 0, node, resources, info, inherited));
        }

        return pages;
    }

    private static PdfRectangle? ReadRect(PdfDocument doc, PdfObject? obj)
    {
        if (doc.Resolve(obj) is not PdfArray a || a.Count < 4)
        {
            return null;
        }

        var v = new double[4];
        for (var i = 0; i < 4; i++)
        {
            switch (doc.Resolve(a[i]))
            {
                case PdfInteger n:
                    v[i] = n.Value;
                    break;
                case PdfReal d:
                    v[i] = d.Value;
                    break;
                default:
                    return null;
            }
        }

        return new PdfRectangle(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3]));
    }
}

/// <summary>Reads the document information dictionary.</summary>
internal static class InfoReader
{
    public static PdfInfoDictionary? Read(PdfDocument doc) =>
        doc.Resolve(doc.Trailer[PdfName.Info]) is PdfDictionary info ? Read(info, doc.Resolve) : null;

    /// <summary>Decodes an Info dictionary whose strings are already decrypted.</summary>
    public static PdfInfoDictionary Read(PdfDictionary info, Func<PdfObject?, PdfObject?> resolve)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in info.Entries)
        {
            switch (resolve(value))
            {
                case PdfString s:
                    entries[key.Value] = TextString.Decode(s.Value);
                    break;
                case PdfName n:
                    entries[key.Value] = n.Value;
                    break;
                case PdfBoolean b:
                    entries[key.Value] = b.Value ? "true" : "false";
                    break;
                case PdfInteger i:
                    entries[key.Value] = i.Value.ToString(CultureInfo.InvariantCulture);
                    break;
                case PdfReal r:
                    entries[key.Value] = r.ToString();
                    break;
            }
        }

        entries.TryGetValue("CreationDate", out var created);
        entries.TryGetValue("ModDate", out var modified);
        return new PdfInfoDictionary(entries, PdfDate.Parse(created), PdfDate.Parse(modified));
    }
}

/// <summary>Reads and parses the catalog XMP metadata stream.</summary>
internal static class XmpReader
{
    private const string NsRdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string NsDc = "http://purl.org/dc/elements/1.1/";
    private const string NsXmp = "http://ns.adobe.com/xap/1.0/";
    private const string NsPdf = "http://ns.adobe.com/pdf/1.3/";
    private const string NsPdfAId = "http://www.aiim.org/pdfa/ns/id/";
    private const string NsPdfUAId = "http://www.aiim.org/pdfua/ns/id/";
    private const string NsPdfXId = "http://www.npes.org/pdfx/ns/id/";
    private const string NsPdfX = "http://ns.adobe.com/pdfx/1.3/";

    /// <summary>Returns the raw catalog metadata bytes (decrypted, Flate-decoded), or null.</summary>
    public static byte[]? ReadRaw(PdfDocument doc)
    {
        var catalog = doc.Catalog;
        if (catalog?["Metadata"] is not PdfReference r || doc.GetObject(r.Id.Number) is not PdfStream stream)
        {
            return null;
        }

        return doc.GetDecodedStreamData(stream, new Objects.ObjectId(r.Id.Number, doc.GetGeneration(r.Id.Number)));
    }

    public static PdfXmpMetadata? Read(PdfDocument doc)
    {
        var raw = ReadRaw(doc);
        return raw is null ? null : Parse(raw);
    }

    public static PdfXmpMetadata Parse(byte[] raw)
    {
        var text = DecodePacket(raw);
        var result = new PdfXmpMetadata(text);
        XDocument xdoc;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreProcessingInstructions = true,
                MaxCharactersInDocument = 64L * 1024 * 1024,
            };
            using var reader = XmlReader.Create(new StringReader(text.TrimStart('﻿').Trim('\0')), settings);
            xdoc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            result.IsWellFormed = false;
            return result;
        }

        result.IsWellFormed = true;
        result.Title = GetLangAlt(xdoc, NsDc, "title");
        result.Description = GetLangAlt(xdoc, NsDc, "description");
        result.Creators = GetList(xdoc, NsDc, "creator");
        result.Subjects = GetList(xdoc, NsDc, "subject");
        result.CreatorTool = GetSimple(xdoc, NsXmp, "CreatorTool");
        result.CreateDate = ParseXmpDate(GetSimple(xdoc, NsXmp, "CreateDate"));
        result.ModifyDate = ParseXmpDate(GetSimple(xdoc, NsXmp, "ModifyDate"));
        result.MetadataDate = ParseXmpDate(GetSimple(xdoc, NsXmp, "MetadataDate"));
        result.Producer = GetSimple(xdoc, NsPdf, "Producer");
        result.Keywords = GetSimple(xdoc, NsPdf, "Keywords");
        result.PdfAPart = ParseInt(GetSimple(xdoc, NsPdfAId, "part"));
        result.PdfAConformance = GetSimple(xdoc, NsPdfAId, "conformance");
        result.PdfUAPart = ParseInt(GetSimple(xdoc, NsPdfUAId, "part"));
        result.PdfXVersion = GetSimple(xdoc, NsPdfXId, "GTS_PDFXVersion") ?? GetSimple(xdoc, NsPdfX, "GTS_PDFXVersion");
        return result;
    }

    internal static string DecodePacket(byte[] raw)
    {
        if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2);
        }

        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
        }

        return new UTF8Encoding(false, false).GetString(raw);
    }

    private static string? GetSimple(XDocument doc, string ns, string local)
    {
        XNamespace x = ns;
        foreach (var description in doc.Descendants(XName.Get("Description", NsRdf)))
        {
            var attr = description.Attribute(x + local);
            if (attr is not null)
            {
                return attr.Value.Trim();
            }
        }

        var element = doc.Descendants(x + local).FirstOrDefault();
        if (element is null)
        {
            return null;
        }

        // The value may be plain text or wrapped in rdf containers.
        var li = element.Descendants(XName.Get("li", NsRdf)).FirstOrDefault();
        return (li?.Value ?? element.Value).Trim();
    }

    private static string? GetLangAlt(XDocument doc, string ns, string local)
    {
        XNamespace x = ns;
        var element = doc.Descendants(x + local).FirstOrDefault();
        if (element is null)
        {
            return GetSimple(doc, ns, local);
        }

        var items = element.Descendants(XName.Get("li", NsRdf)).ToList();
        if (items.Count == 0)
        {
            return element.Value.Trim();
        }

        var xmlLang = XNamespace.Xml + "lang";
        var preferred = items.FirstOrDefault(i => string.Equals((string?)i.Attribute(xmlLang), "x-default", StringComparison.OrdinalIgnoreCase)) ?? items[0];
        return preferred.Value;
    }

    private static IReadOnlyList<string> GetList(XDocument doc, string ns, string local)
    {
        XNamespace x = ns;
        var element = doc.Descendants(x + local).FirstOrDefault();
        if (element is null)
        {
            var simple = GetSimple(doc, ns, local);
            return simple is null ? Array.Empty<string>() : new[] { simple };
        }

        var items = element.Descendants(XName.Get("li", NsRdf)).Select(i => i.Value).ToList();
        return items.Count > 0 ? items : new[] { element.Value.Trim() };
    }

    private static int? ParseInt(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static DateTimeOffset? ParseXmpDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        string[] formats =
        {
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
            "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd", "yyyy-MM", "yyyy",
        };
        return DateTimeOffset.TryParseExact(s.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }
}
