using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using PdfUtil.Conversion.Internal.Model;

namespace PdfUtil.Conversion.Internal.Docx;

/// <summary>OOXML namespaces, relationship types and content types used by the DOCX writer, reader and validator.</summary>
internal static class Ooxml
{
    public const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    public const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public const string Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    public const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    public const string ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
    public const string CoreProperties = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    public const string ExtendedProperties = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";
    public const string DC = "http://purl.org/dc/elements/1.1/";
    public const string DCTerms = "http://purl.org/dc/terms/";
    public const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    public const string RelOfficeDocument = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    public const string RelCore = "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties";
    public const string RelExtended = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties";
    public const string RelStyles = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles";
    public const string RelNumbering = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering";
    public const string RelSettings = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings";
    public const string RelImage = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image";
    public const string RelHyperlink = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink";

    public const string CtDocument = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
    public const string CtStyles = "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml";
    public const string CtNumbering = "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml";
    public const string CtSettings = "application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml";
    public const string CtCore = "application/vnd.openxmlformats-package.core-properties+xml";
    public const string CtExtended = "application/vnd.openxmlformats-officedocument.extended-properties+xml";
    public const string CtRelationships = "application/vnd.openxmlformats-package.relationships+xml";

    /// <summary>Points to twentieths of a point (twips).</summary>
    public static string Twips(double points) => ((long)Math.Round(points * 20)).ToString(CultureInfo.InvariantCulture);

    /// <summary>Points to English Metric Units.</summary>
    public static long Emu(double points) => (long)Math.Round(points * 12700);

    public static string Str(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Writes a <see cref="DocModel"/> as a Word document (OOXML WordprocessingML) by hand: content types, package and
/// document relationships, <c>document.xml</c>, <c>styles.xml</c> (Normal, Heading 1–6, Hyperlink, Table Grid),
/// <c>numbering.xml</c>, <c>settings.xml</c>, core and extended properties, and media. Elements are written in the
/// order the schema requires, so Word opens the file without a repair prompt.
/// </summary>
internal sealed class DocxWriter
{
    private static readonly double[] HeadingSizes = { 20, 16, 14, 13, 12, 11 };

    private readonly DocModel _model;
    private readonly int _maxElements;
    private readonly List<(string Id, string Type, string Target, bool External)> _relationships = new();
    private readonly Dictionary<ImageData, (string RelId, string Name)> _media = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, string> _bookmarkNames = new(StringComparer.Ordinal);
    private readonly List<string> _pendingBookmarks = new();
    private readonly List<(ListKind Kind, int Level, int Start)> _nums = new();
    private readonly HashSet<string> _anchorsInDocument;
    private int _elements;
    private int _bookmarkId;
    private int _drawingId;
    private string _bodyFamily = "Times New Roman";
    private double _bodySize = 11;
    private double _textWidth = 468;

    private DocxWriter(DocModel model, int maxElements)
    {
        _model = model;
        _maxElements = maxElements;
        _anchorsInDocument = model.AllBlocks().SelectMany(b => b.Anchors)
            .Concat(model.AllBlocks().OfType<Paragraph>().SelectMany(p => Inline.Flatten(p.Inlines)).OfType<AnchorInline>().Select(a => a.Name))
            .ToHashSet(StringComparer.Ordinal);
    }

    public int ImageCount => _media.Count;

    public int LinkCount { get; private set; }

    /// <summary>Writes the package; <paramref name="writer"/> exposes counts afterwards.</summary>
    public static byte[] Write(DocModel model, int maxElements, out DocxWriter writer)
    {
        writer = new DocxWriter(model, maxElements);
        return writer.Write();
    }

    private byte[] Write()
    {
        var runs = _model.AllBlocks().OfType<Paragraph>().Where(p => p.HeadingLevel == 0).SelectMany(p => Inline.Flatten(p.Inlines)).OfType<TextInline>().ToList();
        if (runs.Count > 0)
        {
            _bodySize = runs.Where(r => r.Style.Size is not null).GroupBy(r => r.Style.Size!.Value).OrderByDescending(g => g.Sum(r => r.Text.Length)).Select(g => g.Key).FirstOrDefault(11);
            _bodyFamily = runs.GroupBy(r => FontName(r.Style)).OrderByDescending(g => g.Sum(r => r.Text.Length)).First().Key;
        }

        var section = _model.Sections.FirstOrDefault() ?? new Section();
        _textWidth = Math.Max(72, section.PageWidth - section.Margins.Left - section.Margins.Right);

        // The document is written first: it decides which relationships, media and numbering instances exist.
        var document = Part(DocumentXml);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", Part(ContentTypesXml));
            Add(zip, "_rels/.rels", Part(PackageRelationshipsXml));
            Add(zip, "docProps/core.xml", Part(CoreXml));
            Add(zip, "docProps/app.xml", Part(AppXml));
            Add(zip, "word/document.xml", document);
            Add(zip, "word/styles.xml", Part(StylesXml));
            Add(zip, "word/numbering.xml", Part(NumberingXml));
            Add(zip, "word/settings.xml", Part(SettingsXml));
            Add(zip, "word/_rels/document.xml.rels", Part(DocumentRelationshipsXml));
            foreach (var (image, (_, name)) in _media)
            {
                Add(zip, "word/media/" + name, image.Data!);
            }
        }

        return buffer.ToArray();
    }

    private static void Add(ZipArchive zip, string name, byte[] data)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(data);
    }

    private byte[] Part(Action<XmlWriter> write)
    {
        using var ms = new MemoryStream();
        using (var x = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
        {
            x.WriteStartDocument(standalone: true);
            write(x);
            x.WriteEndDocument();
        }

        return ms.ToArray();
    }

    private void Count()
    {
        if (++_elements > _maxElements)
        {
            throw new PdfLimitExceededException($"The output has more elements than the configured maximum of {_maxElements}.");
        }
    }

    private static void W(XmlWriter x, string name) => x.WriteStartElement("w", name, Ooxml.W);

    private static void Val(XmlWriter x, string name, string value)
    {
        W(x, name);
        x.WriteAttributeString("w", "val", Ooxml.W, value);
        x.WriteEndElement();
    }

    private static void Attr(XmlWriter x, string name, string value) => x.WriteAttributeString("w", name, Ooxml.W, value);

    private string Relationship(string type, string target, bool external)
    {
        var id = "rId" + Ooxml.Str(_relationships.Count + 10);
        _relationships.Add((id, type, target, external));
        return id;
    }

    // ------------------------------------------------------------------ package parts

    private void ContentTypesXml(XmlWriter x)
    {
        x.WriteStartElement("Types", Ooxml.ContentTypes);
        void Default(string ext, string type)
        {
            x.WriteStartElement("Default", Ooxml.ContentTypes);
            x.WriteAttributeString("Extension", ext);
            x.WriteAttributeString("ContentType", type);
            x.WriteEndElement();
        }

        void Override(string part, string type)
        {
            x.WriteStartElement("Override", Ooxml.ContentTypes);
            x.WriteAttributeString("PartName", part);
            x.WriteAttributeString("ContentType", type);
            x.WriteEndElement();
        }

        Default("rels", Ooxml.CtRelationships);
        Default("xml", "application/xml");
        Default("png", "image/png");
        Default("jpg", "image/jpeg");
        Override("/word/document.xml", Ooxml.CtDocument);
        Override("/word/styles.xml", Ooxml.CtStyles);
        Override("/word/numbering.xml", Ooxml.CtNumbering);
        Override("/word/settings.xml", Ooxml.CtSettings);
        Override("/docProps/core.xml", Ooxml.CtCore);
        Override("/docProps/app.xml", Ooxml.CtExtended);
        x.WriteEndElement();
    }

    private static void RelationshipElement(XmlWriter x, string id, string type, string target, bool external)
    {
        x.WriteStartElement("Relationship", Ooxml.PackageRelationships);
        x.WriteAttributeString("Id", id);
        x.WriteAttributeString("Type", type);
        x.WriteAttributeString("Target", target);
        if (external)
        {
            x.WriteAttributeString("TargetMode", "External");
        }

        x.WriteEndElement();
    }

    private void PackageRelationshipsXml(XmlWriter x)
    {
        x.WriteStartElement("Relationships", Ooxml.PackageRelationships);
        RelationshipElement(x, "rId1", Ooxml.RelOfficeDocument, "word/document.xml", false);
        RelationshipElement(x, "rId2", Ooxml.RelCore, "docProps/core.xml", false);
        RelationshipElement(x, "rId3", Ooxml.RelExtended, "docProps/app.xml", false);
        x.WriteEndElement();
    }

    private void DocumentRelationshipsXml(XmlWriter x)
    {
        x.WriteStartElement("Relationships", Ooxml.PackageRelationships);
        RelationshipElement(x, "rId1", Ooxml.RelStyles, "styles.xml", false);
        RelationshipElement(x, "rId2", Ooxml.RelNumbering, "numbering.xml", false);
        RelationshipElement(x, "rId3", Ooxml.RelSettings, "settings.xml", false);
        foreach (var (id, type, target, external) in _relationships)
        {
            RelationshipElement(x, id, type, target, external);
        }

        x.WriteEndElement();
    }

    private void CoreXml(XmlWriter x)
    {
        x.WriteStartElement("cp", "coreProperties", Ooxml.CoreProperties);
        x.WriteAttributeString("xmlns", "dc", null, Ooxml.DC);
        x.WriteAttributeString("xmlns", "dcterms", null, Ooxml.DCTerms);
        x.WriteAttributeString("xmlns", "xsi", null, Ooxml.Xsi);
        void Text(string prefix, string ns, string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                x.WriteElementString(prefix, name, ns, XmlChars.Clean(value));
            }
        }

        Text("dc", Ooxml.DC, "title", _model.Title);
        Text("dc", Ooxml.DC, "subject", _model.Subject);
        Text("dc", Ooxml.DC, "creator", _model.Author);
        Text("cp", Ooxml.CoreProperties, "keywords", _model.Keywords);
        Text("dc", Ooxml.DC, "language", _model.Language);
        foreach (var (name, date) in new[] { ("created", _model.Created), ("modified", _model.Modified) })
        {
            if (date is DateTimeOffset d)
            {
                x.WriteStartElement("dcterms", name, Ooxml.DCTerms);
                x.WriteAttributeString("xsi", "type", Ooxml.Xsi, "dcterms:W3CDTF");
                x.WriteString(d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                x.WriteEndElement();
            }
        }

        x.WriteEndElement();
    }

    private static void AppXml(XmlWriter x)
    {
        x.WriteStartElement("Properties", Ooxml.ExtendedProperties);
        x.WriteElementString("Application", Ooxml.ExtendedProperties, "PdfUtil");
        x.WriteEndElement();
    }

    private static void SettingsXml(XmlWriter x)
    {
        W(x, "settings");
        x.WriteAttributeString("xmlns", "w", null, Ooxml.W);
        Val(x, "defaultTabStop", "720");
        Val(x, "characterSpacingControl", "doNotCompress");
        W(x, "compat");
        W(x, "compatSetting");
        Attr(x, "name", "compatibilityMode");
        Attr(x, "uri", "http://schemas.microsoft.com/office/word");
        Attr(x, "val", "15");
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
    }

    private void StylesXml(XmlWriter x)
    {
        W(x, "styles");
        x.WriteAttributeString("xmlns", "w", null, Ooxml.W);
        W(x, "docDefaults");
        W(x, "rPrDefault");
        W(x, "rPr");
        Fonts(x, _bodyFamily);
        Val(x, "sz", HalfPoints(_bodySize));
        Val(x, "szCs", HalfPoints(_bodySize));
        if (!string.IsNullOrWhiteSpace(_model.Language))
        {
            Val(x, "lang", XmlChars.Clean(_model.Language.Trim()));
        }

        x.WriteEndElement();
        x.WriteEndElement();
        W(x, "pPrDefault");
        W(x, "pPr");
        W(x, "spacing");
        Attr(x, "after", "120");
        Attr(x, "line", "264");
        Attr(x, "lineRule", "auto");
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();

        // Some readers ignore the document defaults, so Normal repeats the body font and size.
        Style(x, "paragraph", "Normal", "Normal", null, isDefault: true, null, r =>
        {
            Fonts(r, _bodyFamily);
            Val(r, "sz", HalfPoints(_bodySize));
            Val(r, "szCs", HalfPoints(_bodySize));
        });
        for (var level = 1; level <= 6; level++)
        {
            var size = HeadingSizes[level - 1];
            var outline = level - 1;
            Style(x, "paragraph", "Heading" + Ooxml.Str(level), "heading " + Ooxml.Str(level), "Normal", isDefault: false, p =>
            {
                Val(p, "keepNext", "1");
                W(p, "spacing");
                Attr(p, "before", "240");
                Attr(p, "after", "120");
                p.WriteEndElement();
                Val(p, "outlineLvl", Ooxml.Str(outline));
            }, r =>
            {
                Val(r, "b", "1");
                Val(r, "sz", HalfPoints(size));
                Val(r, "szCs", HalfPoints(size));
            });
        }

        Style(x, "character", "Hyperlink", "Hyperlink", null, isDefault: false, null, r =>
        {
            Val(r, "color", "0563C1");
            Val(r, "u", "single");
        });
        W(x, "style");
        Attr(x, "type", "table");
        Attr(x, "styleId", "TableGrid");
        Val(x, "name", "Table Grid");
        W(x, "tblPr");
        W(x, "tblBorders");
        foreach (var side in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
        {
            W(x, side);
            Attr(x, "val", "single");
            Attr(x, "sz", "4");
            Attr(x, "space", "0");
            Attr(x, "color", "auto");
            x.WriteEndElement();
        }

        x.WriteEndElement();
        W(x, "tblCellMar");
        foreach (var (side, width) in new[] { ("left", "108"), ("right", "108") })
        {
            W(x, side);
            Attr(x, "w", width);
            Attr(x, "type", "dxa");
            x.WriteEndElement();
        }

        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
    }

    private static void Style(XmlWriter x, string type, string id, string name, string? basedOn, bool isDefault, Action<XmlWriter>? pPr, Action<XmlWriter>? rPr)
    {
        W(x, "style");
        Attr(x, "type", type);
        if (isDefault)
        {
            Attr(x, "default", "1");
        }

        Attr(x, "styleId", id);
        Val(x, "name", name);
        if (basedOn is not null)
        {
            Val(x, "basedOn", basedOn);
            Val(x, "next", "Normal");
        }

        W(x, "qFormat");
        x.WriteEndElement();
        if (pPr is not null)
        {
            W(x, "pPr");
            pPr(x);
            x.WriteEndElement();
        }

        if (rPr is not null)
        {
            W(x, "rPr");
            rPr(x);
            x.WriteEndElement();
        }

        x.WriteEndElement();
    }

    /// <summary>
    /// One abstract numbering per list, with the list's start value at its level, so every list restarts its count
    /// in every reader (some ignore <c>startOverride</c>).
    /// </summary>
    private void NumberingXml(XmlWriter x)
    {
        W(x, "numbering");
        x.WriteAttributeString("xmlns", "w", null, Ooxml.W);
        for (var i = 0; i < _nums.Count; i++)
        {
            var (kind, listLevel, start) = _nums[i];
            W(x, "abstractNum");
            Attr(x, "abstractNumId", Ooxml.Str(i));
            Val(x, "multiLevelType", "hybridMultilevel");
            for (var level = 0; level < 9; level++)
            {
                W(x, "lvl");
                Attr(x, "ilvl", Ooxml.Str(level));
                Val(x, "start", Ooxml.Str(level == listLevel ? start : 1));
                Val(x, "numFmt", kind switch
                {
                    ListKind.Bullet => "bullet",
                    ListKind.Decimal => "decimal",
                    ListKind.LowerAlpha => "lowerLetter",
                    ListKind.UpperAlpha => "upperLetter",
                    ListKind.LowerRoman => "lowerRoman",
                    _ => "upperRoman",
                });
                Val(x, "lvlText", kind == ListKind.Bullet ? (level % 3) switch { 0 => "\u2022", 1 => "\u25E6", _ => "\u25AA" } : "%" + Ooxml.Str(level + 1) + ".");
                Val(x, "lvlJc", "left");
                W(x, "pPr");
                W(x, "ind");
                Attr(x, "left", Ooxml.Str(720 * (level + 1)));
                Attr(x, "hanging", "360");
                x.WriteEndElement();
                x.WriteEndElement();
                x.WriteEndElement();
            }

            x.WriteEndElement();
        }

        for (var i = 0; i < _nums.Count; i++)
        {
            W(x, "num");
            Attr(x, "numId", Ooxml.Str(i + 1));
            Val(x, "abstractNumId", Ooxml.Str(i));
            x.WriteEndElement();
        }

        x.WriteEndElement();
    }

    // ------------------------------------------------------------------ document

    private void DocumentXml(XmlWriter x)
    {
        W(x, "document");
        x.WriteAttributeString("xmlns", "w", null, Ooxml.W);
        x.WriteAttributeString("xmlns", "r", null, Ooxml.R);
        x.WriteAttributeString("xmlns", "wp", null, Ooxml.WP);
        x.WriteAttributeString("xmlns", "a", null, Ooxml.A);
        x.WriteAttributeString("xmlns", "pic", null, Ooxml.Pic);
        W(x, "body");
        foreach (var section in _model.Sections)
        {
            foreach (var block in section.Blocks)
            {
                WriteBlock(x, block, null, 0);
            }
        }

        if (_pendingBookmarks.Count > 0)
        {
            W(x, "p");
            FlushBookmarks(x);
            x.WriteEndElement();
        }

        var first = _model.Sections.FirstOrDefault() ?? new Section();
        W(x, "sectPr");
        W(x, "pgSz");
        Attr(x, "w", Ooxml.Twips(first.PageWidth));
        Attr(x, "h", Ooxml.Twips(first.PageHeight));
        if (first.PageWidth > first.PageHeight)
        {
            Attr(x, "orient", "landscape");
        }

        x.WriteEndElement();
        W(x, "pgMar");
        Attr(x, "top", Ooxml.Twips(first.Margins.Top));
        Attr(x, "right", Ooxml.Twips(first.Margins.Right));
        Attr(x, "bottom", Ooxml.Twips(first.Margins.Bottom));
        Attr(x, "left", Ooxml.Twips(first.Margins.Left));
        Attr(x, "header", "720");
        Attr(x, "footer", "720");
        Attr(x, "gutter", "0");
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
    }

    /// <summary>Numbering for a paragraph inside a list: the list instance and its level.</summary>
    private readonly record struct Numbering(int NumId, int Level, bool Continuation);

    private void WriteBlock(XmlWriter x, Block block, Numbering? numbering, int depth)
    {
        Count();
        if (block is not (Paragraph or ListBlock or TableBlock or ImageBlock))
        {
            _pendingBookmarks.AddRange(block.Anchors);
        }

        switch (block)
        {
            case ContainerBlock container:
                foreach (var child in container.Children)
                {
                    WriteBlock(x, child, numbering, depth);
                }

                break;
            case Paragraph p:
                _pendingBookmarks.AddRange(p.Anchors);
                WriteParagraph(x, p, numbering);
                break;
            case ListBlock list:
                _pendingBookmarks.AddRange(list.Anchors);
                var level = Math.Min(depth, 8);
                _nums.Add((list.Kind, level, list.Start));
                var numId = _nums.Count;
                foreach (var item in list.Items)
                {
                    var firstParagraph = true;
                    foreach (var child in item.Blocks)
                    {
                        if (child is ListBlock nested)
                        {
                            WriteBlock(x, nested, null, depth + 1);
                        }
                        else
                        {
                            WriteBlock(x, child, new Numbering(numId, level, !firstParagraph), depth);
                            firstParagraph = false;
                        }
                    }
                }

                break;
            case TableBlock table:
                _pendingBookmarks.AddRange(table.Anchors);
                WriteTable(x, table);
                break;
            case ImageBlock image:
                _pendingBookmarks.AddRange(image.Anchors);
                W(x, "p");
                W(x, "pPr");
                Val(x, "jc", "center");
                x.WriteEndElement();
                FlushBookmarks(x);
                WriteImage(x, image.Image, image.Width, image.Height, image.AltText);
                x.WriteEndElement();
                break;
            case RuleBlock:
                W(x, "p");
                W(x, "pPr");
                W(x, "pBdr");
                W(x, "bottom");
                Attr(x, "val", "single");
                Attr(x, "sz", "6");
                Attr(x, "space", "1");
                Attr(x, "color", "auto");
                x.WriteEndElement();
                x.WriteEndElement();
                x.WriteEndElement();
                FlushBookmarks(x);
                x.WriteEndElement();
                break;
            case PageBreakBlock:
                W(x, "p");
                FlushBookmarks(x);
                W(x, "r");
                W(x, "br");
                Attr(x, "type", "page");
                x.WriteEndElement();
                x.WriteEndElement();
                x.WriteEndElement();
                break;
        }
    }

    private void WriteParagraph(XmlWriter x, Paragraph p, Numbering? numbering)
    {
        W(x, "p");
        W(x, "pPr");
        if (p.HeadingLevel > 0)
        {
            Val(x, "pStyle", "Heading" + Ooxml.Str(Math.Clamp(p.HeadingLevel, 1, 6)));
        }

        if (p.KeepWithNext)
        {
            Val(x, "keepNext", "1");
        }

        if (p.PageBreakBefore)
        {
            Val(x, "pageBreakBefore", "1");
        }

        if (numbering is { Continuation: false } n)
        {
            W(x, "numPr");
            Val(x, "ilvl", Ooxml.Str(n.Level));
            Val(x, "numId", Ooxml.Str(n.NumId));
            x.WriteEndElement();
        }

        if (p.IsPreformatted || p.SpaceBefore is not null || p.SpaceAfter is not null)
        {
            W(x, "spacing");
            if (p.SpaceBefore is double before)
            {
                Attr(x, "before", Ooxml.Twips(before));
            }

            if (p.SpaceAfter is double after || p.IsPreformatted)
            {
                Attr(x, "after", p.SpaceAfter is double a ? Ooxml.Twips(a) : "0");
            }

            x.WriteEndElement();
        }

        if (numbering is { Continuation: true } c)
        {
            W(x, "ind");
            Attr(x, "left", Ooxml.Str(720 * (c.Level + 1)));
            x.WriteEndElement();
        }
        else if (p.IndentLeft > 0 && numbering is null)
        {
            W(x, "ind");
            Attr(x, "left", Ooxml.Twips(p.IndentLeft));
            x.WriteEndElement();
        }

        if (p.Alignment != TextAlignment.Left)
        {
            Val(x, "jc", p.Alignment switch { TextAlignment.Center => "center", TextAlignment.Right => "right", _ => "both" });
        }

        x.WriteEndElement();
        FlushBookmarks(x);
        WriteInlines(x, p.Inlines, p);
        x.WriteEndElement();
    }

    private void WriteInlines(XmlWriter x, IEnumerable<Inline> inlines, Paragraph p, bool inLink = false)
    {
        foreach (var inline in inlines)
        {
            Count();
            switch (inline)
            {
                case TextInline text:
                    WriteRun(x, text.Text, text.Style, p, inLink);
                    break;
                case BreakInline:
                    W(x, "r");
                    W(x, "br");
                    x.WriteEndElement();
                    x.WriteEndElement();
                    break;
                case LinkInline link when !inLink && (link.Uri is not null || (link.Anchor is not null && _anchorsInDocument.Contains(link.Anchor))):
                    W(x, "hyperlink");
                    if (link.Uri is not null)
                    {
                        x.WriteAttributeString("r", "id", Ooxml.R, Relationship(Ooxml.RelHyperlink, link.Uri, external: true));
                    }
                    else
                    {
                        Attr(x, "anchor", BookmarkName(link.Anchor!));
                    }

                    Attr(x, "history", "1");
                    WriteInlines(x, link.Children, p, inLink: true);
                    x.WriteEndElement();
                    LinkCount++;
                    break;
                case LinkInline link:
                    WriteInlines(x, link.Children, p, inLink);
                    break;
                case ImageInline image:
                    WriteImage(x, image.Image, image.Width, image.Height, image.AltText);
                    break;
                case AnchorInline anchor:
                    _pendingBookmarks.Add(anchor.Name);
                    FlushBookmarks(x);
                    break;
            }
        }
    }

    private void WriteRun(XmlWriter x, string text, RunStyle style, Paragraph p, bool inLink)
    {
        W(x, "r");
        W(x, "rPr");
        if (inLink)
        {
            Val(x, "rStyle", "Hyperlink");
        }

        var family = p.IsPreformatted ? "Courier New" : FontName(style);
        if (family != _bodyFamily)
        {
            Fonts(x, family);
        }

        var headingBold = p.HeadingLevel > 0;
        if (style.Bold != headingBold)
        {
            Val(x, "b", style.Bold ? "1" : "0");
        }

        if (style.Italic)
        {
            Val(x, "i", "1");
        }

        if (style.Strike)
        {
            Val(x, "strike", "1");
        }

        if (style.Color is { Length: 7 } color)
        {
            Val(x, "color", color[1..].ToUpperInvariant());
        }

        if (style.Size is double size && (p.HeadingLevel > 0 || Math.Abs(size - _bodySize) > 0.25) && style.Position == VerticalPosition.Baseline)
        {
            Val(x, "sz", HalfPoints(size));
            Val(x, "szCs", HalfPoints(size));
        }

        if (style.Underline && !inLink)
        {
            Val(x, "u", "single");
        }

        if (style.Background is { Length: 7 } background)
        {
            W(x, "shd");
            Attr(x, "val", "clear");
            Attr(x, "color", "auto");
            Attr(x, "fill", background[1..].ToUpperInvariant());
            x.WriteEndElement();
        }

        if (style.Position != VerticalPosition.Baseline)
        {
            Val(x, "vertAlign", style.Position == VerticalPosition.Superscript ? "superscript" : "subscript");
        }

        x.WriteEndElement();

        // Tabs and line feeds become their own elements; everything else is text with spaces preserved.
        var clean = XmlChars.Clean(text);
        var start = 0;
        for (var i = 0; i <= clean.Length; i++)
        {
            if (i < clean.Length && clean[i] is not ('\t' or '\n' or '\r'))
            {
                continue;
            }

            if (i > start)
            {
                W(x, "t");
                x.WriteAttributeString("xml", "space", null, "preserve");
                x.WriteString(clean[start..i]);
                x.WriteEndElement();
            }

            if (i < clean.Length)
            {
                W(x, clean[i] == '\t' ? "tab" : "br");
                x.WriteEndElement();
            }

            start = i + 1;
        }

        x.WriteEndElement();
    }

    private void WriteTable(XmlWriter x, TableBlock table)
    {
        var columns = table.ColumnWidths?.Count ?? table.Rows.Select(r => r.Cells.Sum(c => c.ColumnSpan)).DefaultIfEmpty(1).Max();
        var widths = table.ColumnWidths is { Count: > 0 } known ? known.ToList() : Enumerable.Repeat(_textWidth / columns, columns).ToList();
        var total = widths.Sum();
        if (total > _textWidth)
        {
            widths = widths.Select(w => w * _textWidth / total).ToList();
        }

        W(x, "tbl");
        W(x, "tblPr");
        Val(x, "tblStyle", "TableGrid");
        W(x, "tblW");
        Attr(x, "w", Ooxml.Twips(widths.Sum()));
        Attr(x, "type", "dxa");
        x.WriteEndElement();
        W(x, "tblLayout");
        Attr(x, "type", "fixed");
        x.WriteEndElement();
        x.WriteEndElement();
        W(x, "tblGrid");
        foreach (var w in widths)
        {
            W(x, "gridCol");
            Attr(x, "w", Ooxml.Twips(w));
            x.WriteEndElement();
        }

        x.WriteEndElement();

        // Cells covered by a row span from above are written as vertical-merge continuation cells.
        var spanning = new int[columns];
        var spanWidth = new int[columns];
        for (var r = 0; r < table.Rows.Count; r++)
        {
            Count();
            W(x, "tr");
            if (r < table.HeaderRows)
            {
                W(x, "trPr");
                Val(x, "tblHeader", "1");
                x.WriteEndElement();
            }

            var column = 0;
            var cells = new Queue<TableCell>(table.Rows[r].Cells);
            while (column < columns)
            {
                if (spanning[column] > 0)
                {
                    var width = spanWidth[column];
                    WriteCell(x, null, width, widths.Skip(column).Take(width).Sum(), continuation: true);
                    spanning[column]--;
                    column += width;
                    continue;
                }

                if (!cells.TryDequeue(out var cell))
                {
                    WriteCell(x, null, 1, widths[column], continuation: false);
                    column++;
                    continue;
                }

                var span = Math.Clamp(cell.ColumnSpan, 1, columns - column);
                if (cell.RowSpan > 1)
                {
                    spanning[column] = cell.RowSpan - 1;
                    spanWidth[column] = span;
                }

                WriteCell(x, cell, span, widths.Skip(column).Take(span).Sum(), continuation: false);
                column += span;
            }

            x.WriteEndElement();
        }

        x.WriteEndElement();

        // Word needs a paragraph between a table and whatever follows it.
        W(x, "p");
        x.WriteEndElement();
    }

    private void WriteCell(XmlWriter x, TableCell? cell, int span, double width, bool continuation)
    {
        W(x, "tc");
        W(x, "tcPr");
        W(x, "tcW");
        Attr(x, "w", Ooxml.Twips(width));
        Attr(x, "type", "dxa");
        x.WriteEndElement();
        if (span > 1)
        {
            Val(x, "gridSpan", Ooxml.Str(span));
        }

        if (continuation)
        {
            W(x, "vMerge");
            x.WriteEndElement();
        }
        else if (cell is { RowSpan: > 1 })
        {
            Val(x, "vMerge", "restart");
        }

        if (cell?.Background is { Length: 7 } fill)
        {
            W(x, "shd");
            Attr(x, "val", "clear");
            Attr(x, "color", "auto");
            Attr(x, "fill", fill[1..].ToUpperInvariant());
            x.WriteEndElement();
        }

        x.WriteEndElement();
        var wrote = false;
        foreach (var block in cell?.Blocks ?? new List<Block>())
        {
            WriteBlock(x, block, null, 0);

            wrote |= block is Paragraph or ImageBlock or RuleBlock or PageBreakBlock;
        }

        if (!wrote)
        {
            W(x, "p");
            x.WriteEndElement();
        }

        x.WriteEndElement();
    }

    private void WriteImage(XmlWriter x, ImageData image, double width, double height, string? alt)
    {
        if (image.IsPlaceholder)
        {
            W(x, "r");
            W(x, "rPr");
            Val(x, "i", "1");
            Val(x, "color", "808080");
            x.WriteEndElement();
            W(x, "t");
            x.WriteString("[Image not converted]");
            x.WriteEndElement();
            x.WriteEndElement();
            return;
        }

        if (!_media.TryGetValue(image, out var media))
        {
            var name = "image" + Ooxml.Str(_media.Count + 1) + "." + image.Extension;
            media = (Relationship(Ooxml.RelImage, "media/" + name, external: false), name);
            _media[image] = media;
        }

        // Keep the image inside the text area.
        if (width <= 0 || height <= 0)
        {
            (width, height) = (image.PixelWidth * 0.75, image.PixelHeight * 0.75);
        }

        if (width > _textWidth)
        {
            height *= _textWidth / width;
            width = _textWidth;
        }

        var cx = Ooxml.Str(Math.Max(1, Ooxml.Emu(width)));
        var cy = Ooxml.Str(Math.Max(1, Ooxml.Emu(height)));
        var id = Ooxml.Str(++_drawingId);
        W(x, "r");
        W(x, "drawing");
        x.WriteStartElement("wp", "inline", Ooxml.WP);
        foreach (var d in new[] { "distT", "distB", "distL", "distR" })
        {
            x.WriteAttributeString(d, "0");
        }

        x.WriteStartElement("wp", "extent", Ooxml.WP);
        x.WriteAttributeString("cx", cx);
        x.WriteAttributeString("cy", cy);
        x.WriteEndElement();
        x.WriteStartElement("wp", "docPr", Ooxml.WP);
        x.WriteAttributeString("id", id);
        x.WriteAttributeString("name", "Picture " + id);
        x.WriteAttributeString("descr", XmlChars.Clean(alt ?? ""));
        x.WriteEndElement();
        x.WriteStartElement("wp", "cNvGraphicFramePr", Ooxml.WP);
        x.WriteStartElement("a", "graphicFrameLocks", Ooxml.A);
        x.WriteAttributeString("noChangeAspect", "1");
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteStartElement("a", "graphic", Ooxml.A);
        x.WriteStartElement("a", "graphicData", Ooxml.A);
        x.WriteAttributeString("uri", Ooxml.Pic);
        x.WriteStartElement("pic", "pic", Ooxml.Pic);
        x.WriteStartElement("pic", "nvPicPr", Ooxml.Pic);
        x.WriteStartElement("pic", "cNvPr", Ooxml.Pic);
        x.WriteAttributeString("id", id);
        x.WriteAttributeString("name", media.Name);
        x.WriteEndElement();
        x.WriteStartElement("pic", "cNvPicPr", Ooxml.Pic);
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteStartElement("pic", "blipFill", Ooxml.Pic);
        x.WriteStartElement("a", "blip", Ooxml.A);
        x.WriteAttributeString("r", "embed", Ooxml.R, media.RelId);
        x.WriteEndElement();
        x.WriteStartElement("a", "stretch", Ooxml.A);
        x.WriteStartElement("a", "fillRect", Ooxml.A);
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteStartElement("pic", "spPr", Ooxml.Pic);
        x.WriteStartElement("a", "xfrm", Ooxml.A);
        x.WriteStartElement("a", "off", Ooxml.A);
        x.WriteAttributeString("x", "0");
        x.WriteAttributeString("y", "0");
        x.WriteEndElement();
        x.WriteStartElement("a", "ext", Ooxml.A);
        x.WriteAttributeString("cx", cx);
        x.WriteAttributeString("cy", cy);
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteStartElement("a", "prstGeom", Ooxml.A);
        x.WriteAttributeString("prst", "rect");
        x.WriteStartElement("a", "avLst", Ooxml.A);
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
        x.WriteEndElement();
    }

    private void FlushBookmarks(XmlWriter x)
    {
        foreach (var anchor in _pendingBookmarks)
        {
            var id = Ooxml.Str(_bookmarkId++);
            W(x, "bookmarkStart");
            Attr(x, "id", id);
            Attr(x, "name", BookmarkName(anchor));
            x.WriteEndElement();
            W(x, "bookmarkEnd");
            Attr(x, "id", id);
            x.WriteEndElement();
        }

        _pendingBookmarks.Clear();
    }

    /// <summary>A Word bookmark name for an anchor: letters, digits and underscores, at most 40 characters, unique.</summary>
    private string BookmarkName(string anchor)
    {
        if (_bookmarkNames.TryGetValue(anchor, out var existing))
        {
            return existing;
        }

        var sb = new StringBuilder();
        foreach (var c in anchor)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }

        if (sb.Length == 0 || !char.IsAsciiLetter(sb[0]))
        {
            sb.Insert(0, 'a');
        }

        var name = sb.ToString();
        name = name[..Math.Min(name.Length, 36)];
        var unique = name;
        for (var n = 2; _bookmarkNames.ContainsValue(unique); n++)
        {
            unique = name + "_" + Ooxml.Str(n);
        }

        _bookmarkNames[anchor] = unique;
        return unique;
    }

    private static void Fonts(XmlWriter x, string family)
    {
        W(x, "rFonts");
        var clean = XmlChars.Clean(family);
        foreach (var slot in new[] { "ascii", "hAnsi", "eastAsia", "cs" })
        {
            Attr(x, slot, clean);
        }

        x.WriteEndElement();
    }

    /// <summary>The font to name in Word: the PDF family, or a common font for the generic family.</summary>
    private static string FontName(RunStyle style) => style.FontFamily is { Length: > 0 } family && family is not ("serif" or "sans-serif" or "monospace")
        ? family
        : style.GenericFamily switch { "monospace" => "Courier New", "sans-serif" => "Arial", _ => "Times New Roman" };

    private static string HalfPoints(double points) => Ooxml.Str(Math.Max(2, (long)Math.Round(points * 2)));
}
