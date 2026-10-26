using System.Xml;
using System.Xml.Linq;

namespace PdfUtil.Conversion.Internal.Html;

/// <summary>
/// Structural validation of written HTML before it is returned: well-formed XML in the XHTML namespace, one
/// <c>head</c> with a <c>title</c> and one <c>body</c>, unique ids, every <c>#fragment</c> link resolving to an id,
/// images pointing at data URIs or written assets, and no scripts, event handlers or unsafe links.
/// </summary>
internal static class HtmlValidator
{
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase) { "script", "iframe", "object", "embed", "form", "base" };

    public static void Validate(string html, IReadOnlyCollection<string> assets)
    {
        var problems = new List<string>();
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(html), settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new PdfVerificationException("The HTML output is not well-formed: " + ex.Message, ex);
        }

        var root = doc.Root!;
        if (root.Name != Xhtml + "html")
        {
            problems.Add("the root element is not <html> in the XHTML namespace");
        }

        var heads = root.Elements(Xhtml + "head").ToList();
        var bodies = root.Elements(Xhtml + "body").ToList();
        if (heads.Count != 1 || heads[0].Elements(Xhtml + "title").Count() != 1)
        {
            problems.Add("there must be one <head> with one <title>");
        }

        if (bodies.Count != 1)
        {
            problems.Add("there must be one <body>");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var assetSet = assets.ToHashSet(StringComparer.Ordinal);
        foreach (var element in root.DescendantsAndSelf())
        {
            if (Forbidden.Contains(element.Name.LocalName))
            {
                problems.Add($"<{element.Name.LocalName}> is not allowed");
            }

            foreach (var attribute in element.Attributes())
            {
                if (attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"event handler attribute {attribute.Name.LocalName}");
                }
            }

            if (element.Attribute("id")?.Value is { } id && !ids.Add(id))
            {
                problems.Add($"duplicate id \"{id}\"");
            }
        }

        foreach (var a in root.Descendants(Xhtml + "a"))
        {
            var href = a.Attribute("href")?.Value;
            if (href is null)
            {
                continue;
            }

            if (href.StartsWith('#'))
            {
                if (!ids.Contains(href[1..]))
                {
                    problems.Add($"link to missing anchor {href}");
                }
            }
            else if (UrlSafety.Check(href) is null)
            {
                problems.Add("unsafe link address");
            }
        }

        foreach (var img in root.Descendants(Xhtml + "img"))
        {
            var src = img.Attribute("src")?.Value ?? "";
            if (!(src.StartsWith("data:image/png;base64,", StringComparison.Ordinal) || src.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal) || assetSet.Contains(src)))
            {
                problems.Add("an image points at something that was not written");
            }
        }

        if (problems.Count > 0)
        {
            throw new PdfVerificationException("The HTML output failed validation: " + string.Join("; ", problems.Distinct().Take(10)) + ".");
        }
    }
}
