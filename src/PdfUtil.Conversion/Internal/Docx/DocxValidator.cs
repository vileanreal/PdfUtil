using System.Xml.Linq;

namespace PdfUtil.Conversion.Internal.Docx;

/// <summary>
/// Structural validation of a written DOCX before it is returned: every part is well-formed XML and has a content type;
/// the package and document relationships point at parts that exist, with unique ids; every relationship the document
/// uses exists and has the right type; styles, numbering instances and bookmark targets resolve; bookmark and drawing
/// ids are unique; and every table row covers the table grid, with a paragraph in every cell.
/// </summary>
internal static class DocxValidator
{
    private static readonly XNamespace W = Ooxml.W;
    private static readonly XNamespace R = Ooxml.R;
    private static readonly XNamespace WP = Ooxml.WP;
    private static readonly XNamespace A = Ooxml.A;

    public static void Validate(byte[] docx)
    {
        var problems = new List<string>();
        DocxPackage package;
        try
        {
            package = DocxPackage.Read(docx, 100_000, long.MaxValue);
        }
        catch (PdfUtilException ex)
        {
            throw new PdfVerificationException("The Word output is not a readable package: " + ex.Message, ex);
        }

        try
        {
            Check(package, problems);
        }
        catch (PdfCorruptException ex)
        {
            problems.Add(ex.Message);
        }

        if (problems.Count > 0)
        {
            throw new PdfVerificationException("The Word output failed validation: " + string.Join("; ", problems.Distinct().Take(10)) + ".");
        }
    }

    private static void Check(DocxPackage package, List<string> problems)
    {
        // Content types.
        var types = package.Xml("[Content_Types].xml");
        if (types is null)
        {
            problems.Add("[Content_Types].xml is missing");
            return;
        }

        XNamespace ct = Ooxml.ContentTypes;
        var defaults = types.Root!.Elements(ct + "Default").Select(d => ((string?)d.Attribute("Extension") ?? "").ToLowerInvariant()).ToHashSet();
        var overrides = types.Root.Elements(ct + "Override").ToDictionary(o => ((string?)o.Attribute("PartName") ?? "").TrimStart('/'), o => (string?)o.Attribute("ContentType") ?? "", StringComparer.OrdinalIgnoreCase);
        foreach (var part in package.Parts.Keys.Where(p => p != "[Content_Types].xml"))
        {
            var extension = Path.GetExtension(part).TrimStart('.').ToLowerInvariant();
            if (!overrides.ContainsKey(part) && !defaults.Contains(extension))
            {
                problems.Add($"{part} has no content type");
            }

            if (extension is "xml" or "rels")
            {
                package.Xml(part);
            }
        }

        // Package relationships.
        var root = package.Relationships("");
        var main = root.Values.Where(r => r.Type == Ooxml.RelOfficeDocument).Select(r => r.Target.TrimStart('/')).FirstOrDefault();
        if (main is null || !package.Contains(main))
        {
            problems.Add("the package does not point at its main document");
            return;
        }

        if (!overrides.TryGetValue(main, out var mainType) || mainType != Ooxml.CtDocument)
        {
            problems.Add("the main document has the wrong content type");
        }

        foreach (var (_, (_, target, external)) in root)
        {
            if (!external && !package.Contains(target))
            {
                problems.Add($"package relationship to missing {target}");
            }
        }

        // Document relationships.
        var relationships = package.Relationships(main);
        foreach (var (_, (type, target, external)) in relationships)
        {
            if (!external && !package.Contains(DocxPackage.ResolveTarget(main, target)))
            {
                problems.Add($"document relationship to missing {target}");
            }

            if (external && type != Ooxml.RelHyperlink)
            {
                problems.Add("an external relationship that is not a hyperlink");
            }
        }

        var document = package.Xml(main)!;
        var body = document.Root?.Element(W + "body");
        if (document.Root?.Name != W + "document" || body is null)
        {
            problems.Add("the main document has no body");
            return;
        }

        if (body.Elements().LastOrDefault()?.Name != W + "sectPr")
        {
            problems.Add("the body does not end with section properties");
        }

        foreach (var attribute in document.Descendants().Attributes().Where(a => a.Name.Namespace == R))
        {
            if (!relationships.TryGetValue(attribute.Value, out var rel))
            {
                problems.Add($"relationship {attribute.Value} is used but not defined");
            }
            else if (attribute.Name.LocalName == "embed" && rel.Type != Ooxml.RelImage)
            {
                problems.Add($"image {attribute.Value} is not an image relationship");
            }
            else if (attribute.Parent?.Name == W + "hyperlink" && (rel.Type != Ooxml.RelHyperlink || !rel.External))
            {
                problems.Add($"hyperlink {attribute.Value} is not an external hyperlink relationship");
            }
        }

        // Styles and numbering.
        var stylesPart = relationships.Values.Where(r => r.Type == Ooxml.RelStyles).Select(r => DocxPackage.ResolveTarget(main, r.Target)).FirstOrDefault();
        var styleIds = (stylesPart is null ? null : package.Xml(stylesPart))?.Descendants(W + "style").Select(s => (string?)s.Attribute(W + "styleId") ?? "").ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
        foreach (var use in document.Descendants().Where(e => e.Name == W + "pStyle" || e.Name == W + "rStyle" || e.Name == W + "tblStyle"))
        {
            if (!styleIds.Contains((string?)use.Attribute(W + "val") ?? ""))
            {
                problems.Add($"style {(string?)use.Attribute(W + "val")} is not defined");
            }
        }

        var numberingPart = relationships.Values.Where(r => r.Type == Ooxml.RelNumbering).Select(r => DocxPackage.ResolveTarget(main, r.Target)).FirstOrDefault();
        var numbering = numberingPart is null ? null : package.Xml(numberingPart);
        var abstracts = numbering?.Root?.Elements(W + "abstractNum").Select(a => (string?)a.Attribute(W + "abstractNumId")).ToHashSet() ?? new HashSet<string?>();
        var nums = new HashSet<string>(StringComparer.Ordinal);
        foreach (var num in numbering?.Root?.Elements(W + "num") ?? Enumerable.Empty<XElement>())
        {
            nums.Add((string?)num.Attribute(W + "numId") ?? "");
            if (!abstracts.Contains((string?)num.Element(W + "abstractNumId")?.Attribute(W + "val")))
            {
                problems.Add("a numbering instance refers to a missing abstract numbering");
            }
        }

        foreach (var numId in document.Descendants(W + "numId"))
        {
            if (!nums.Contains((string?)numId.Attribute(W + "val") ?? ""))
            {
                problems.Add($"numbering {(string?)numId.Attribute(W + "val")} is not defined");
            }
        }

        // Bookmarks and drawings.
        var starts = document.Descendants(W + "bookmarkStart").ToList();
        var ends = document.Descendants(W + "bookmarkEnd").Select(e => (string?)e.Attribute(W + "id")).ToHashSet();
        var bookmarkIds = new HashSet<string?>();
        var bookmarkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in starts)
        {
            var id = (string?)start.Attribute(W + "id");
            var name = (string?)start.Attribute(W + "name") ?? "";
            if (!bookmarkIds.Add(id) || !ends.Contains(id))
            {
                problems.Add($"bookmark id {id} is repeated or not closed");
            }

            if (!bookmarkNames.Add(name) || name.Length is 0 or > 40)
            {
                problems.Add($"bookmark name \"{name}\" is repeated or invalid");
            }
        }

        foreach (var anchor in document.Descendants(W + "hyperlink").Select(h => (string?)h.Attribute(W + "anchor")).OfType<string>())
        {
            if (!bookmarkNames.Contains(anchor))
            {
                problems.Add($"link to missing bookmark {anchor}");
            }
        }

        var drawingIds = document.Descendants(WP + "docPr").Select(d => (string?)d.Attribute("id")).ToList();
        if (drawingIds.Count != drawingIds.Distinct().Count())
        {
            problems.Add("drawing ids are repeated");
        }

        foreach (var blip in document.Descendants(A + "blip"))
        {
            if ((string?)blip.Attribute(R + "embed") is null)
            {
                problems.Add("an image has no data");
            }
        }

        // Tables.
        foreach (var table in document.Descendants(W + "tbl"))
        {
            var grid = table.Element(W + "tblGrid")?.Elements(W + "gridCol").Count() ?? 0;
            foreach (var row in table.Elements(W + "tr"))
            {
                var covered = row.Elements(W + "tc").Sum(tc => (int?)tc.Element(W + "tcPr")?.Element(W + "gridSpan")?.Attribute(W + "val") ?? 1);
                if (covered != grid)
                {
                    problems.Add($"a table row covers {covered} of {grid} grid columns");
                }
            }

            if (table.Descendants(W + "tc").Any(tc => !tc.Elements(W + "p").Any() && !tc.Elements(W + "tbl").Any()))
            {
                problems.Add("a table cell has no paragraph");
            }
        }
    }
}
