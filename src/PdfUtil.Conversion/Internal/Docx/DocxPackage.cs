using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace PdfUtil.Conversion.Internal.Docx;

/// <summary>
/// An OOXML package read into memory with hostile-input limits: entry count, total and per-entry uncompressed size,
/// compression ratio (zip bombs), and part names that are absolute, contain <c>..</c> or backslashes, or repeat
/// (zip-slip and ambiguity). Part names are kept without a leading slash.
/// </summary>
internal sealed class DocxPackage
{
    private const int MaxRatio = 250;

    private readonly Dictionary<string, byte[]> _parts;

    private DocxPackage(Dictionary<string, byte[]> parts) => _parts = parts;

    public IReadOnlyDictionary<string, byte[]> Parts => _parts;

    public static DocxPackage Read(byte[] data, int maxEntries, long maxBytes)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new PdfCorruptException("The package is not a valid ZIP file.", ex);
        }

        using (zip)
        {
            if (zip.Entries.Count > maxEntries)
            {
                throw new PdfLimitExceededException($"The package has more than the configured maximum of {maxEntries} entries.");
            }

            var parts = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                if (name.EndsWith('/'))
                {
                    continue;
                }

                if (name.Length == 0 || name.StartsWith('/') || name.Contains('\\', StringComparison.Ordinal) || name.Split('/').Any(s => s is "" or "." or "..") || name.Contains(':', StringComparison.Ordinal))
                {
                    throw new PdfCorruptException($"The package contains an unsafe part name.");
                }

                if (entry.Length > maxBytes - total || (entry.CompressedLength > 0 && entry.Length / Math.Max(1, entry.CompressedLength) > MaxRatio && entry.Length > 1_000_000))
                {
                    throw new PdfLimitExceededException("The package expands to more than the configured maximum size (or is compressed suspiciously well).");
                }

                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        throw new PdfLimitExceededException("The package expands to more than the configured maximum size.");
                    }

                    buffer.Write(chunk, 0, read);
                }

                if (!parts.TryAdd(name, buffer.ToArray()))
                {
                    throw new PdfCorruptException("The package contains the same part twice.");
                }
            }

            return new DocxPackage(parts);
        }
    }

    public bool Contains(string name) => _parts.ContainsKey(name.TrimStart('/'));

    public byte[]? Get(string name) => _parts.TryGetValue(name.TrimStart('/'), out var data) ? data : null;

    /// <summary>Parses a part as XML with DTDs prohibited and no resolver.</summary>
    public XDocument? Xml(string name)
    {
        var data = Get(name);
        if (data is null)
        {
            return null;
        }

        try
        {
            using var reader = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 512L * 1024 * 1024 });
            return XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new PdfCorruptException($"The package part {name} is not well-formed XML.", ex);
        }
    }

    /// <summary>The relationships part for a part (<c>word/document.xml</c> → <c>word/_rels/document.xml.rels</c>).</summary>
    public static string RelationshipsPartFor(string part)
    {
        var slash = part.LastIndexOf('/');
        return slash < 0 ? "_rels/" + part + ".rels" : part[..(slash + 1)] + "_rels/" + part[(slash + 1)..] + ".rels";
    }

    /// <summary>Resolves a relationship target relative to the part that owns it.</summary>
    public static string ResolveTarget(string sourcePart, string target)
    {
        if (target.StartsWith('/'))
        {
            return target.TrimStart('/');
        }

        var slash = sourcePart.LastIndexOf('/');
        var segments = (slash < 0 ? "" : sourcePart[..slash]).Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var segment in target.Split('/'))
        {
            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
            }
            else if (segment is not ("" or "."))
            {
                segments.Add(segment);
            }
        }

        return string.Join('/', segments);
    }

    /// <summary>The relationships of a part: id → (type, target, external).</summary>
    public Dictionary<string, (string Type, string Target, bool External)> Relationships(string part)
    {
        var result = new Dictionary<string, (string, string, bool)>(StringComparer.Ordinal);
        var rels = Xml(RelationshipsPartFor(part));
        if (rels is null)
        {
            return result;
        }

        XNamespace ns = Ooxml.PackageRelationships;
        foreach (var r in rels.Root!.Elements(ns + "Relationship"))
        {
            var id = (string?)r.Attribute("Id");
            if (id is null || !result.TryAdd(id, ((string?)r.Attribute("Type") ?? "", (string?)r.Attribute("Target") ?? "", (string?)r.Attribute("TargetMode") == "External")))
            {
                throw new PdfCorruptException($"The relationships of {part} have a missing or repeated id.");
            }
        }

        return result;
    }
}
