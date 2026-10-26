using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Text;

namespace PdfUtil.Internal.Document;

/// <summary>Applies <see cref="PdfInfoChanges"/> to an Info dictionary.</summary>
internal static class InfoEditor
{
    /// <summary>Returns a copy of <paramref name="source"/> (or a new dictionary) with the changes applied.</summary>
    public static PdfDictionary Apply(PdfDictionary? source, PdfInfoChanges changes, DateTimeOffset now)
    {
        var info = source?.ShallowClone() ?? new PdfDictionary();
        foreach (var key in changes.Remove)
        {
            info.Remove(PdfName.Get(key));
        }

        void SetText(string key, string? value)
        {
            if (value is not null)
            {
                info[key] = new PdfString(TextString.Encode(value));
            }
        }

        SetText("Title", changes.Title);
        SetText("Author", changes.Author);
        SetText("Subject", changes.Subject);
        SetText("Keywords", changes.Keywords);
        SetText("Creator", changes.Creator);
        SetText("Producer", changes.Producer);
        if (changes.CreationDate is { } created)
        {
            info["CreationDate"] = new PdfString(Encoding.ASCII.GetBytes(PdfDate.Format(created)));
        }

        if (ModificationDate(changes, now) is { } modified)
        {
            info["ModDate"] = new PdfString(Encoding.ASCII.GetBytes(PdfDate.Format(modified)));
        }

        foreach (var (key, value) in changes.CustomProperties)
        {
            info[key] = new PdfString(TextString.Encode(value));
        }

        return info;
    }

    public static DateTimeOffset? ModificationDate(PdfInfoChanges changes, DateTimeOffset now) =>
        changes.ModificationDate ?? (changes.UpdateModificationDate ? now : null);
}

/// <summary>
/// Updates the XMP properties that mirror the Info dictionary (ISO 32000-2 §14.3.3). Returns null when the packet
/// cannot be parsed, so the caller can leave it untouched.
/// </summary>
internal static class XmpEditor
{
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";
    private static readonly XNamespace Pdf = "http://ns.adobe.com/pdf/1.3/";

    public static byte[]? Apply(byte[] raw, PdfInfoChanges changes, DateTimeOffset now)
    {
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 64L * 1024 * 1024,
            };
            using var reader = XmlReader.Create(new StringReader(XmpReader.DecodePacket(raw).TrimStart('﻿').Trim('\0')), settings);
            doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return null;
        }

        var rdf = doc.Descendants(Rdf + "RDF").FirstOrDefault();
        if (rdf is null)
        {
            return null;
        }

        var description = rdf.Elements(Rdf + "Description").FirstOrDefault();
        if (description is null)
        {
            description = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", ""));
            rdf.Add(description);
        }

        void RemoveProperty(XNamespace ns, string local)
        {
            foreach (var d in rdf.Elements(Rdf + "Description"))
            {
                d.Attribute(ns + local)?.Remove();
                d.Elements(ns + local).Remove();
            }
        }

        void Declare(XNamespace ns, string prefix)
        {
            if (description.GetPrefixOfNamespace(ns) is null)
            {
                description.Add(new XAttribute(XNamespace.Xmlns + prefix, ns.NamespaceName));
            }
        }

        void SetSimple(XNamespace ns, string prefix, string local, string value)
        {
            RemoveProperty(ns, local);
            Declare(ns, prefix);
            description.Add(new XElement(ns + local, value));
        }

        void SetAlt(XNamespace ns, string prefix, string local, string value)
        {
            RemoveProperty(ns, local);
            Declare(ns, prefix);
            description.Add(new XElement(ns + local,
                new XElement(Rdf + "Alt", new XElement(Rdf + "li", new XAttribute(XNamespace.Xml + "lang", "x-default"), value))));
        }

        void SetSeq(XNamespace ns, string prefix, string local, string value)
        {
            RemoveProperty(ns, local);
            Declare(ns, prefix);
            description.Add(new XElement(ns + local, new XElement(Rdf + "Seq", new XElement(Rdf + "li", value))));
        }

        // Removals first, so that a key that is both removed and set ends up set (as in the Info dictionary).
        var mapping = new Dictionary<string, (XNamespace Ns, string Local)>(StringComparer.Ordinal)
        {
            ["Title"] = (Dc, "title"),
            ["Author"] = (Dc, "creator"),
            ["Subject"] = (Dc, "description"),
            ["Keywords"] = (Pdf, "Keywords"),
            ["Creator"] = (Xmp, "CreatorTool"),
            ["Producer"] = (Pdf, "Producer"),
            ["CreationDate"] = (Xmp, "CreateDate"),
            ["ModDate"] = (Xmp, "ModifyDate"),
        };
        foreach (var key in changes.Remove)
        {
            if (mapping.TryGetValue(key, out var property))
            {
                RemoveProperty(property.Ns, property.Local);
            }
        }

        if (changes.Title is { } title)
        {
            SetAlt(Dc, "dc", "title", title);
        }

        if (changes.Author is { } author)
        {
            SetSeq(Dc, "dc", "creator", author);
        }

        if (changes.Subject is { } subject)
        {
            SetAlt(Dc, "dc", "description", subject);
        }

        if (changes.Keywords is { } keywords)
        {
            SetSimple(Pdf, "pdf", "Keywords", keywords);
        }

        if (changes.Creator is { } creator)
        {
            SetSimple(Xmp, "xmp", "CreatorTool", creator);
        }

        if (changes.Producer is { } producer)
        {
            SetSimple(Pdf, "pdf", "Producer", producer);
        }

        if (changes.CreationDate is { } created)
        {
            SetSimple(Xmp, "xmp", "CreateDate", FormatXmpDate(created));
        }

        if (InfoEditor.ModificationDate(changes, now) is { } modified)
        {
            SetSimple(Xmp, "xmp", "ModifyDate", FormatXmpDate(modified));
        }

        SetSimple(Xmp, "xmp", "MetadataDate", FormatXmpDate(now));

        var writerSettings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = doc.Declaration is null,
            Indent = false,
        };
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, writerSettings))
        {
            doc.Save(writer);
        }

        return output.ToArray();
    }

    private static string FormatXmpDate(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
}
