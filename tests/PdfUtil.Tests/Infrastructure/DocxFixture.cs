using System.IO.Compression;
using System.Text;

namespace PdfUtil.Tests.Infrastructure;

/// <summary>Builds DOCX packages from hand-written WordprocessingML for Word to PDF tests.</summary>
internal static class DocxFixture
{
    public const string Ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" xmlns:m=\"http://schemas.openxmlformats.org/officeDocument/2006/math\"";

    /// <summary>
    /// A package with the given body (the inside of <c>w:body</c>), optional styles and numbering (the inside of their
    /// root elements), extra document relationships (id, type suffix, target, external) and extra parts.
    /// </summary>
    public static byte[] Build(string body, string? styles = null, string? numbering = null, IEnumerable<(string Id, string Type, string Target, bool External)>? relationships = null, IDictionary<string, byte[]>? parts = null, string? settings = null, string? core = null)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        void Add(string name, string text) => files[name] = Encoding.UTF8.GetBytes(text);
        Add("[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
            <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
            <Default Extension="xml" ContentType="application/xml"/>
            <Default Extension="png" ContentType="image/png"/>
            <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
            </Types>
            """);
        Add("_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
            <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
            </Relationships>
            """);
        Add("docProps/core.xml", $"""<?xml version="1.0" encoding="UTF-8"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/">{core ?? "<dc:title>Fixture</dc:title><dc:creator>Test Author</dc:creator>"}</cp:coreProperties>""");
        Add("word/document.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document {Ns}><w:body>{body}</w:body></w:document>""");
        var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        if (styles is not null)
        {
            Add("word/styles.xml", $"""<?xml version="1.0" encoding="UTF-8"?><w:styles {Ns}>{styles}</w:styles>""");
            rels.Append("<Relationship Id=\"rIdS\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        }

        if (numbering is not null)
        {
            Add("word/numbering.xml", $"""<?xml version="1.0" encoding="UTF-8"?><w:numbering {Ns}>{numbering}</w:numbering>""");
            rels.Append("<Relationship Id=\"rIdN\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/>");
        }

        if (settings is not null)
        {
            Add("word/settings.xml", $"""<?xml version="1.0" encoding="UTF-8"?><w:settings {Ns}>{settings}</w:settings>""");
            rels.Append("<Relationship Id=\"rIdT\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings\" Target=\"settings.xml\"/>");
        }

        foreach (var (id, type, target, external) in relationships ?? Enumerable.Empty<(string, string, string, bool)>())
        {
            rels.Append($"<Relationship Id=\"{id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/{type}\" Target=\"{System.Security.SecurityElement.Escape(target)}\"{(external ? " TargetMode=\"External\"" : "")}/>");
        }

        Add("word/_rels/document.xml.rels", rels.Append("</Relationships>").ToString());
        foreach (var (name, data) in parts ?? new Dictionary<string, byte[]>())
        {
            files[name] = data;
        }

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in files)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(data);
            }
        }

        return output.ToArray();
    }

    /// <summary>A paragraph with optional paragraph properties and one run.</summary>
    public static string P(string text, string pPr = "", string rPr = "") =>
        $"<w:p>{(pPr.Length > 0 ? $"<w:pPr>{pPr}</w:pPr>" : "")}<w:r>{(rPr.Length > 0 ? $"<w:rPr>{rPr}</w:rPr>" : "")}<w:t xml:space=\"preserve\">{System.Security.SecurityElement.Escape(text)}</w:t></w:r></w:p>";

    /// <summary>A header or footer part.</summary>
    public static byte[] Part(string root, string content) => Encoding.UTF8.GetBytes($"""<?xml version="1.0" encoding="UTF-8"?><w:{root} {Ns}>{content}</w:{root}>""");
}
