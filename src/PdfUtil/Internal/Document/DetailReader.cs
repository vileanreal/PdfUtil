using System.Globalization;
using System.Text;
using PdfUtil.Internal.Filters;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Text;

namespace PdfUtil.Internal.Document;

/// <summary>A terminal form field with its inherited attributes.</summary>
internal readonly record struct FieldNode(PdfDictionary Field, int? ObjectNumber, string FullName, string? FieldType, int Flags, PdfObject? Value, IReadOnlyList<int> Pages);

/// <summary>
/// Collects the optional details of <see cref="PdfInspectionDetails"/>: fonts, images, attachments, bookmarks, form
/// fields, signatures, page labels and actions. Every traversal has cycle detection and depth and size limits.
/// </summary>
internal sealed class DetailReader
{
    private readonly PdfDocument _doc;
    private readonly List<PageNode> _pages;
    private readonly int _maxDepth;
    private readonly int _maxEntries;
    private readonly CancellationToken _ct;
    private readonly Dictionary<int, int> _pageNumbers = new();
    private Dictionary<int, int>? _annotationPages;
    private Dictionary<string, PdfObject>? _namedDestinations;

    public DetailReader(PdfDocument doc, List<PageNode> pages, PdfUtilOptions options, CancellationToken ct)
    {
        _doc = doc;
        _pages = pages;
        _maxDepth = options.MaxNestingDepth;
        _maxEntries = options.MaxObjectCount;
        _ct = ct;
        foreach (var page in pages)
        {
            if (page.ObjectNumber > 0)
            {
                _pageNumbers.TryAdd(page.ObjectNumber, page.Info.Number);
            }
        }
    }

    private PdfDictionary Catalog => _doc.Catalog ?? throw new PdfCorruptException("The document catalog cannot be read.");

    /// <summary>Fills the requested details into <paramref name="info"/>.</summary>
    public void Fill(PdfDocumentInfo info, PdfInspectionDetails details)
    {
        info.InspectedDetails = details;
        if ((details & (PdfInspectionDetails.Fonts | PdfInspectionDetails.Images)) != 0)
        {
            var (fonts, images) = ReadFontsAndImages();
            if (details.HasFlag(PdfInspectionDetails.Fonts))
            {
                info.Fonts = fonts;
            }

            if (details.HasFlag(PdfInspectionDetails.Images))
            {
                info.Images = images;
            }
        }

        if (details.HasFlag(PdfInspectionDetails.Attachments))
        {
            info.Attachments = ReadAttachments();
        }

        if (details.HasFlag(PdfInspectionDetails.Bookmarks))
        {
            info.Bookmarks = ReadBookmarks();
        }

        if ((details & (PdfInspectionDetails.FormFields | PdfInspectionDetails.Signatures)) != 0)
        {
            var fields = ReadFieldNodes();
            if (details.HasFlag(PdfInspectionDetails.FormFields))
            {
                info.FormFields = fields.Select(ToFormField).ToList();
            }

            if (details.HasFlag(PdfInspectionDetails.Signatures))
            {
                info.Signatures = ReadSignatures(fields);
            }
        }

        if (details.HasFlag(PdfInspectionDetails.PageLabels))
        {
            info.PageLabels = ReadPageLabels();
        }

        if (details.HasFlag(PdfInspectionDetails.Actions))
        {
            info.Actions = ReadActions(out var hasJavaScript, out var names);
            info.HasJavaScript = hasJavaScript;
            info.JavaScriptNames = names;
        }
    }

    // ------------------------------------------------------------------ helpers

    private string? Text(PdfObject? o) => _doc.Resolve(o) switch
    {
        PdfString s => TextString.Decode(s.Value),
        PdfName n => n.Value,
        _ => null,
    };

    private string? Name(PdfObject? o) => (_doc.Resolve(o) as PdfName)?.Value;

    private int? Int(PdfObject? o) => _doc.Resolve(o) switch
    {
        PdfInteger i => (int)Math.Clamp(i.Value, int.MinValue, int.MaxValue),
        PdfReal r when !double.IsNaN(r.Value) => (int)Math.Clamp(r.Value, int.MinValue, int.MaxValue),
        _ => null,
    };

    private DateTimeOffset? Date(PdfObject? o) => _doc.Resolve(o) is PdfString s ? PdfDate.Parse(TextString.Decode(s.Value)) : null;

    private static int? NumberOf(PdfObject? o) => (o as PdfReference)?.Id.Number;

    private PdfDictionary? NamesDictionary => _doc.ResolveDictionary(Catalog["Names"]);

    /// <summary>Annotation object number → page number, from the pages' <c>/Annots</c> arrays.</summary>
    private Dictionary<int, int> AnnotationPages
    {
        get
        {
            if (_annotationPages is null)
            {
                _annotationPages = new Dictionary<int, int>();
                foreach (var page in _pages)
                {
                    foreach (var annot in Annotations(page))
                    {
                        if (annot.Number is int n)
                        {
                            _annotationPages.TryAdd(n, page.Info.Number);
                        }
                    }
                }
            }

            return _annotationPages;
        }
    }

    private IEnumerable<(PdfDictionary Annot, int? Number)> Annotations(PageNode page)
    {
        if (_doc.Resolve(page.Page["Annots"]) is not PdfArray annots)
        {
            yield break;
        }

        foreach (var item in annots.Items)
        {
            if (_doc.ResolveDictionary(item) is { } annot)
            {
                yield return (annot, NumberOf(item));
            }
        }
    }

    private int? PageOfWidget(PdfObject? widgetRef, PdfDictionary widget)
    {
        if (NumberOf(widgetRef) is int n && AnnotationPages.TryGetValue(n, out var page))
        {
            return page;
        }

        return NumberOf(widget["P"]) is int p && _pageNumbers.TryGetValue(p, out var byParent) ? byParent : null;
    }

    // ------------------------------------------------------------------ fonts and images

    private (List<PdfFontInfo> Fonts, List<PdfImageInfo> Images) ReadFontsAndImages()
    {
        var fonts = new Dictionary<object, (PdfDictionary Font, string ResourceName, int? Number, SortedSet<int> Pages)>();
        var images = new Dictionary<int, (PdfStream Image, SortedSet<int> Pages)>();
        foreach (var page in _pages)
        {
            _ct.ThrowIfCancellationRequested();
            var pageNumber = page.Info.Number;
            var visited = new HashSet<int>();
            var stack = new Stack<(PdfDictionary Resources, int Depth)>();
            if (page.Resources is { } resources)
            {
                stack.Push((resources, 0));
            }

            // Annotation appearances (form fields, stamps) use fonts and images too.
            foreach (var (annot, _) in Annotations(page))
            {
                if (_doc.ResolveDictionary(annot["AP"]) is not { } ap)
                {
                    continue;
                }

                foreach (var key in new[] { "N", "R", "D" })
                {
                    var appearance = ap[key];
                    var states = _doc.Resolve(appearance) is PdfDictionary stateDict ? stateDict.Entries.Select(e => e.Value) : new[] { appearance! };
                    foreach (var state in states.Where(s => s is not null))
                    {
                        if ((state is not PdfReference sr || visited.Add(sr.Id.Number)) && _doc.Resolve(state) is PdfStream form && _doc.ResolveDictionary(form.Dictionary["Resources"]) is { } formResources)
                        {
                            stack.Push((formResources, 1));
                        }
                    }
                }
            }

            while (stack.Count > 0)
            {
                var (res, depth) = stack.Pop();
                if (depth > _maxDepth)
                {
                    continue;
                }

                if (_doc.ResolveDictionary(res["Font"]) is { } fontDict)
                {
                    foreach (var (name, value) in fontDict.Entries)
                    {
                        if (_doc.ResolveDictionary(value) is not { } font)
                        {
                            continue;
                        }

                        object key = NumberOf(value) is int n ? n : font;
                        if (!fonts.TryGetValue(key, out var entry))
                        {
                            if (fonts.Count >= _maxEntries)
                            {
                                throw new PdfLimitExceededException("The document uses more fonts than the configured maximum object count.");
                            }

                            entry = (font, name.Value, NumberOf(value), new SortedSet<int>());
                            fonts[key] = entry;

                            // Type 3 glyph procedures have their own resources.
                            if (Name(font["Subtype"]) == "Type3" && _doc.ResolveDictionary(font["Resources"]) is { } type3Resources)
                            {
                                stack.Push((type3Resources, depth + 1));
                            }
                        }

                        entry.Pages.Add(pageNumber);
                    }
                }

                if (_doc.ResolveDictionary(res["XObject"]) is not { } xobjects)
                {
                    continue;
                }

                foreach (var (_, value) in xobjects.Entries)
                {
                    if (value is not PdfReference xr || _doc.Resolve(value) is not PdfStream xobject)
                    {
                        continue;
                    }

                    switch (Name(xobject.Dictionary["Subtype"]))
                    {
                        case "Image":
                            if (!images.TryGetValue(xr.Id.Number, out var image))
                            {
                                image = (xobject, new SortedSet<int>());
                                images[xr.Id.Number] = image;
                            }

                            image.Pages.Add(pageNumber);
                            break;
                        case "Form" when visited.Add(xr.Id.Number):
                            if (_doc.ResolveDictionary(xobject.Dictionary["Resources"]) is { } formResources)
                            {
                                stack.Push((formResources, depth + 1));
                            }

                            break;
                    }
                }
            }
        }

        var fontList = fonts.Values.Select(f => DescribeFont(f.Font, f.ResourceName, f.Number, f.Pages.ToArray())).ToList();
        var imageList = images.OrderBy(i => i.Key).Select(i => DescribeImage(i.Value.Image, i.Key, i.Value.Pages.ToArray())).ToList();
        return (fontList, imageList);
    }

    private PdfFontInfo DescribeFont(PdfDictionary font, string resourceName, int? number, int[] pages)
    {
        var subtype = Name(font["Subtype"]) ?? "Unknown";
        var baseFont = Name(font["BaseFont"]);
        var descriptor = _doc.ResolveDictionary(font["FontDescriptor"]);
        string? descendantSubtype = null;
        if (subtype == "Type0" && _doc.Resolve(font["DescendantFonts"]) is PdfArray { Count: > 0 } descendants && _doc.ResolveDictionary(descendants[0]) is { } cidFont)
        {
            descendantSubtype = Name(cidFont["Subtype"]);
            descriptor ??= _doc.ResolveDictionary(cidFont["FontDescriptor"]);
        }

        var fileType = descriptor is null ? null : new[] { "FontFile", "FontFile2", "FontFile3" }.FirstOrDefault(k => _doc.Resolve(descriptor[k]) is PdfStream);
        var isSubset = baseFont is { Length: > 7 } && baseFont[6] == '+' && baseFont[..6].All(c => c is >= 'A' and <= 'Z');
        var encoding = _doc.Resolve(font["Encoding"]) switch
        {
            PdfName n => n.Value,
            PdfDictionary e => Name(e["BaseEncoding"]) is { } b ? $"Custom (based on {b})" : "Custom",
            PdfStream cmap => Name(cmap.Dictionary["CMapName"]) ?? "Embedded CMap",
            _ => null,
        };
        return new PdfFontInfo
        {
            Name = isSubset ? baseFont![7..] : baseFont ?? resourceName,
            BaseFont = baseFont,
            Subtype = subtype,
            DescendantSubtype = descendantSubtype,
            IsEmbedded = subtype == "Type3" || fileType is not null,
            IsSubset = isSubset,
            FontFileType = fileType,
            Encoding = encoding,
            HasToUnicode = font.ContainsKey("ToUnicode"),
            Pages = pages,
            ObjectNumber = number,
        };
    }

    private PdfImageInfo DescribeImage(PdfStream image, int number, int[] pages)
    {
        var d = image.Dictionary;
        var isMask = _doc.Resolve(d["ImageMask"]) is PdfBoolean { Value: true };
        return new PdfImageInfo
        {
            Pages = pages,
            Width = Int(d["Width"]) ?? 0,
            Height = Int(d["Height"]) ?? 0,
            BitsPerComponent = isMask ? 1 : Int(d["BitsPerComponent"]),
            ColorSpace = isMask ? "ImageMask" : DescribeColorSpace(d["ColorSpace"], 0),
            Filters = StreamFilters.GetFilterNames(d, _doc.Resolve),
            HasSoftMask = d.ContainsKey("SMask"),
            StoredLength = image.SourceLength,
            ObjectNumber = number,
        };
    }

    private string? DescribeColorSpace(PdfObject? colorSpace, int depth)
    {
        if (depth > 8)
        {
            return null;
        }

        switch (_doc.Resolve(colorSpace))
        {
            case PdfName n:
                return n.Value;
            case PdfArray { Count: > 0 } a:
                var family = Name(a[0]);
                switch (family)
                {
                    case "ICCBased":
                        return a.Count > 1 && _doc.Resolve(a[1]) is PdfStream icc && Int(icc.Dictionary["N"]) is int components
                            ? $"ICCBased ({components} components)"
                            : "ICCBased";
                    case "Indexed":
                        var baseSpace = a.Count > 1 ? DescribeColorSpace(a[1], depth + 1) : null;
                        var hival = a.Count > 2 ? Int(a[2]) : null;
                        return hival is int h ? $"Indexed ({baseSpace}, {h + 1} colours)" : $"Indexed ({baseSpace})";
                    case "Separation":
                        return a.Count > 1 ? $"Separation ({Name(a[1])})" : "Separation";
                    case "DeviceN":
                        return a.Count > 1 && _doc.Resolve(a[1]) is PdfArray colorants ? $"DeviceN ({colorants.Count} colorants)" : "DeviceN";
                    default:
                        return family;
                }

            default:
                return null;
        }
    }

    // ------------------------------------------------------------------ attachments

    public List<(byte[] Key, PdfObject Value)> EmbeddedFileEntries() =>
        NameTree.Read(_doc, NamesDictionary?["EmbeddedFiles"], _maxDepth, _maxEntries);

    public List<PdfAttachmentInfo> ReadAttachments()
    {
        var result = EmbeddedFileEntries().Select(e => DescribeFileSpec(_doc, TextString.Decode(e.Key), e.Value, null)).ToList();
        foreach (var page in _pages)
        {
            foreach (var (annot, _) in Annotations(page))
            {
                if (Name(annot["Subtype"]) == "FileAttachment" && annot["FS"] is { } fs)
                {
                    var name = FileNameOf(_doc, fs) ?? $"Attachment on page {page.Info.Number}";
                    result.Add(DescribeFileSpec(_doc, name, fs, page.Info.Number));
                }
            }
        }

        return result;
    }

    /// <summary>The file name of a file specification (<c>/UF</c>, else <c>/F</c>, else a string file specification).</summary>
    public static string? FileNameOf(PdfDocument doc, PdfObject? fileSpec) => doc.Resolve(fileSpec) switch
    {
        PdfString s => TextString.Decode(s.Value),
        PdfDictionary d => (doc.Resolve(d["UF"]) ?? doc.Resolve(d["F"])) is PdfString n ? TextString.Decode(n.Value) : null,
        _ => null,
    };

    /// <summary>The embedded file stream of a file specification (<c>/EF /UF</c>, else <c>/EF /F</c>), with its object number.</summary>
    public static (PdfStream Stream, int Number)? EmbeddedStreamOf(PdfDocument doc, PdfObject? fileSpec)
    {
        if (doc.ResolveDictionary(fileSpec) is not { } fs || doc.ResolveDictionary(fs["EF"]) is not { } ef)
        {
            return null;
        }

        foreach (var key in new[] { "UF", "F" })
        {
            if (ef[key] is PdfReference r && doc.GetObject(r.Id.Number) is PdfStream s)
            {
                return (s, r.Id.Number);
            }
        }

        return null;
    }

    public static PdfAttachmentInfo DescribeFileSpec(PdfDocument doc, string name, PdfObject fileSpec, int? page)
    {
        var fs = doc.ResolveDictionary(fileSpec);
        var embedded = EmbeddedStreamOf(doc, fileSpec);
        var streamDict = embedded?.Stream.Dictionary;
        var parameters = doc.ResolveDictionary(streamDict?["Params"]);
        DateTimeOffset? DateOf(string key) => doc.Resolve(parameters?[key]) is PdfString s ? PdfDate.Parse(TextString.Decode(s.Value)) : null;
        return new PdfAttachmentInfo
        {
            Name = name,
            FileName = FileNameOf(doc, fileSpec),
            Description = doc.Resolve(fs?["Desc"]) is PdfString desc ? TextString.Decode(desc.Value) : null,
            Size = doc.Resolve(parameters?["Size"]) is PdfInteger size ? size.Value : null,
            MimeType = (doc.Resolve(streamDict?["Subtype"]) as PdfName)?.Value,
            CreationDate = DateOf("CreationDate"),
            ModificationDate = DateOf("ModDate"),
            CheckSum = doc.Resolve(parameters?["CheckSum"]) is PdfString sum ? Convert.ToHexString(sum.Value) : null,
            Relationship = (doc.Resolve(fs?["AFRelationship"]) as PdfName)?.Value,
            Page = page,
            IsEmbedded = embedded is not null,
            ObjectNumber = NumberOf(fileSpec),
        };
    }

    // ------------------------------------------------------------------ bookmarks

    public List<PdfBookmark> ReadBookmarks()
    {
        var outlines = _doc.ResolveDictionary(Catalog["Outlines"]);
        var visited = new HashSet<int>();
        var count = 0;
        return ReadOutlineLevel(outlines?["First"], 0, visited, ref count);
    }

    private List<PdfBookmark> ReadOutlineLevel(PdfObject? first, int depth, HashSet<int> visited, ref int count)
    {
        var list = new List<PdfBookmark>();
        if (depth > _maxDepth)
        {
            return list;
        }

        var current = first;
        while (current is not null)
        {
            _ct.ThrowIfCancellationRequested();
            if ((current is PdfReference r && !visited.Add(r.Id.Number)) || _doc.ResolveDictionary(current) is not { } item)
            {
                break;
            }

            if (++count > _maxEntries)
            {
                throw new PdfLimitExceededException("The outline has more items than the configured maximum object count.");
            }

            var (page, action, uri) = ResolveTarget(item["Dest"], item["A"]);
            var bookmark = new PdfBookmark
            {
                Title = Text(item["Title"]) ?? "",
                Page = page,
                Action = action,
                Uri = uri,
                IsOpen = Int(item["Count"]) > 0,
            };
            bookmark.Children = ReadOutlineLevel(item["First"], depth + 1, visited, ref count);
            list.Add(bookmark);
            current = item["Next"];
        }

        return list;
    }

    private (int? Page, string? Action, string? Uri) ResolveTarget(PdfObject? dest, PdfObject? action)
    {
        if (dest is not null)
        {
            return (DestinationPage(dest, 0), null, null);
        }

        if (_doc.ResolveDictionary(action) is not { } a)
        {
            return (null, null, null);
        }

        return Name(a["S"]) switch
        {
            "GoTo" => (DestinationPage(a["D"], 0), null, null),
            "URI" => (null, "URI", Text(a["URI"])),
            var other => (null, other, null),
        };
    }

    /// <summary>The page number of a destination: an explicit array, a named destination, or a <c>/D</c> dictionary.</summary>
    public int? DestinationPage(PdfObject? dest, int depth)
    {
        if (depth > 4)
        {
            return null;
        }

        switch (_doc.Resolve(dest))
        {
            case PdfArray { Count: > 0 } explicitDest:
                if (explicitDest[0] is PdfReference r)
                {
                    return _pageNumbers.TryGetValue(r.Id.Number, out var p) ? p : null;
                }

                return _doc.Resolve(explicitDest[0]) is PdfInteger index && index.Value >= 0 && index.Value < _pages.Count ? (int)index.Value + 1 : null;
            case PdfDictionary d:
                return DestinationPage(d["D"], depth + 1);
            case PdfName n:
                return NamedDestinations.TryGetValue(n.Value, out var byName) ? DestinationPage(byName, depth + 1) : null;
            case PdfString s:
                return NamedDestinations.TryGetValue(Encoding.Latin1.GetString(s.Value), out var byString) ? DestinationPage(byString, depth + 1) : null;
            default:
                return null;
        }
    }

    /// <summary>Named destinations from the catalog <c>/Dests</c> dictionary (PDF 1.1) and the <c>/Names /Dests</c> tree, keyed by their bytes as Latin-1.</summary>
    private Dictionary<string, PdfObject> NamedDestinations
    {
        get
        {
            if (_namedDestinations is null)
            {
                _namedDestinations = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
                if (_doc.ResolveDictionary(Catalog["Dests"]) is { } dests)
                {
                    foreach (var (key, value) in dests.Entries)
                    {
                        _namedDestinations.TryAdd(key.Value, value);
                    }
                }

                foreach (var (key, value) in NameTree.Read(_doc, NamesDictionary?["Dests"], _maxDepth, _maxEntries))
                {
                    _namedDestinations.TryAdd(Encoding.Latin1.GetString(key), value);
                }
            }

            return _namedDestinations;
        }
    }

    // ------------------------------------------------------------------ form fields and signatures

    /// <summary>The terminal fields of the AcroForm field tree, with inherited type, flags and value.</summary>
    public List<FieldNode> ReadFieldNodes()
    {
        var result = new List<FieldNode>();
        var acroForm = _doc.ResolveDictionary(Catalog["AcroForm"]);
        if (_doc.Resolve(acroForm?["Fields"]) is not PdfArray fields)
        {
            return result;
        }

        var visited = new HashSet<int>();
        var stack = new Stack<(PdfObject Node, string Parent, string? Type, int Flags, PdfObject? Value, int Depth)>();
        for (var i = fields.Count - 1; i >= 0; i--)
        {
            stack.Push((fields[i], "", null, 0, null, 0));
        }

        while (stack.Count > 0)
        {
            _ct.ThrowIfCancellationRequested();
            var (node, parent, inheritedType, inheritedFlags, inheritedValue, depth) = stack.Pop();
            if (depth > _maxDepth || (node is PdfReference r && !visited.Add(r.Id.Number)) || _doc.ResolveDictionary(node) is not { } field)
            {
                continue;
            }

            if (result.Count >= _maxEntries)
            {
                throw new PdfLimitExceededException("The form has more fields than the configured maximum object count.");
            }

            var partial = Text(field["T"]);
            var name = partial is null ? parent : parent.Length == 0 ? partial : parent + "." + partial;
            var type = Name(field["FT"]) ?? inheritedType;
            var flags = Int(field["Ff"]) ?? inheritedFlags;
            var value = field["V"] ?? inheritedValue;

            // Kids with a partial name are child fields; kids without one are this field's widgets.
            var kids = _doc.Resolve(field["Kids"]) as PdfArray;
            var childFields = kids?.Items.Where(k => _doc.ResolveDictionary(k)?.ContainsKey("T") == true).ToList();
            if (childFields is { Count: > 0 })
            {
                for (var i = childFields.Count - 1; i >= 0; i--)
                {
                    stack.Push((childFields[i], name, type, flags, value, depth + 1));
                }

                continue;
            }

            var pages = new SortedSet<int>();
            if (Name(field["Subtype"]) == "Widget" && PageOfWidget(node, field) is int own)
            {
                pages.Add(own);
            }

            foreach (var kid in kids?.Items ?? Enumerable.Empty<PdfObject>())
            {
                if (_doc.ResolveDictionary(kid) is { } widget && PageOfWidget(kid, widget) is int p)
                {
                    pages.Add(p);
                }
            }

            result.Add(new FieldNode(field, NumberOf(node), name, type, flags, value, pages.ToArray()));
        }

        return result;
    }

    private PdfFormField ToFormField(FieldNode node) => new()
    {
        FullName = node.FullName,
        Type = FieldTypeOf(node.FieldType, node.Flags),
        Value = node.FieldType == "Sig" ? null : FieldValue(node.Value),
        Flags = node.Flags,
        AlternateName = Text(node.Field["TU"]),
        Pages = node.Pages,
        ObjectNumber = node.ObjectNumber,
    };

    private static PdfFormFieldType FieldTypeOf(string? type, int flags) => type switch
    {
        "Tx" => PdfFormFieldType.Text,
        "Btn" when (flags & (1 << 16)) != 0 => PdfFormFieldType.PushButton,
        "Btn" when (flags & (1 << 15)) != 0 => PdfFormFieldType.RadioButton,
        "Btn" => PdfFormFieldType.CheckBox,
        "Ch" when (flags & (1 << 17)) != 0 => PdfFormFieldType.ComboBox,
        "Ch" => PdfFormFieldType.ListBox,
        "Sig" => PdfFormFieldType.Signature,
        _ => PdfFormFieldType.Unknown,
    };

    private string? FieldValue(PdfObject? value) => _doc.Resolve(value) switch
    {
        PdfString s => TextString.Decode(s.Value),
        PdfName n => n.Value,
        PdfArray a => string.Join("; ", a.Items.Select(Text).Where(t => t is not null)),
        _ => null,
    };

    private List<PdfSignatureInfo> ReadSignatures(List<FieldNode> fields)
    {
        var result = new List<PdfSignatureInfo>();
        foreach (var field in fields)
        {
            if (field.FieldType != "Sig" || _doc.ResolveDictionary(field.Value) is not { } sig)
            {
                continue;
            }

            var byteRange = (_doc.Resolve(sig["ByteRange"]) as PdfArray)?.Items
                .Select(i => _doc.Resolve(i) is PdfInteger n ? n.Value : -1)
                .ToArray() ?? Array.Empty<long>();
            var contents = _doc.Resolve(sig["Contents"]) as PdfString;
            result.Add(new PdfSignatureInfo
            {
                FieldName = field.FullName,
                SignerName = Text(sig["Name"]),
                SigningTime = Date(sig["M"]),
                Reason = Text(sig["Reason"]),
                Location = Text(sig["Location"]),
                ContactInfo = Text(sig["ContactInfo"]),
                Filter = Name(sig["Filter"]),
                SubFilter = Name(sig["SubFilter"]),
                IsDocumentTimestamp = Name(sig["Type"]) == "DocTimeStamp",
                ByteRange = byteRange,
                CoversWholeFile = CoversWholeFile(byteRange),
                ContentsLength = contents?.Value.Length ?? 0,
                Page = field.Pages.Count > 0 ? field.Pages[0] : null,
            });
        }

        return result;
    }

    /// <summary>
    /// Whether a byte range is <c>[0 a b c]</c> with <c>b + c</c> = file length, and bytes <c>a</c> to <c>b - 1</c> are
    /// exactly one hexadecimal string (the signature value).
    /// </summary>
    private bool CoversWholeFile(long[] range)
    {
        const long MaxGap = 16L * 1024 * 1024;
        var source = _doc.Source;
        if (range.Length != 4 || range[0] != 0 || range[1] <= 0 || range[2] <= range[1] + 1 || range[3] < 0 || range[2] + range[3] != source.Length || range[2] - range[1] > MaxGap)
        {
            return false;
        }

        if (source.PeekAt(range[1]) != '<' || source.PeekAt(range[2] - 1) != '>')
        {
            return false;
        }

        var gap = source.ReadBytes(range[1] + 1, range[2] - range[1] - 2);
        return gap.All(b => char.IsAsciiHexDigit((char)b) || b is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or 0 or (byte)'\f');
    }

    // ------------------------------------------------------------------ page labels

    public List<string> ReadPageLabels()
    {
        var ranges = NumberTree.Read(_doc, Catalog["PageLabels"], _maxDepth, _maxEntries)
            .Where(r => r.Key >= 0)
            .OrderBy(r => r.Key)
            .ToList();
        var labels = new List<string>(_pages.Count);
        if (ranges.Count == 0)
        {
            return labels;
        }

        var current = -1;
        for (var i = 0; i < _pages.Count; i++)
        {
            while (current + 1 < ranges.Count && ranges[current + 1].Key <= i)
            {
                current++;
            }

            if (current < 0)
            {
                labels.Add((i + 1).ToString(CultureInfo.InvariantCulture));
                continue;
            }

            var (start, value) = ranges[current];
            var d = _doc.ResolveDictionary(value);
            var number = (long)(Int(d?["St"]) ?? 1) + (i - start);
            labels.Add((Text(d?["P"]) ?? "") + FormatLabel(Name(d?["S"]), number));
        }

        return labels;
    }

    internal static string FormatLabel(string? style, long n) => style switch
    {
        null => "",
        "R" => Roman(n),
        "r" => Roman(n).ToLowerInvariant(),
        "A" => Letters(n),
        "a" => Letters(n).ToLowerInvariant(),
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    private static string Roman(long n)
    {
        if (n <= 0 || n > 100_000)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }

        var sb = new StringBuilder();
        foreach (var (value, symbol) in new[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") })
        {
            while (n >= value)
            {
                sb.Append(symbol);
                n -= value;
            }
        }

        return sb.ToString();
    }

    /// <summary>A to Z, then AA to ZZ, then AAA to ZZZ, and so on (ISO 32000-1 Table 159).</summary>
    private static string Letters(long n)
    {
        if (n <= 0 || n > 26_000)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }

        return new string((char)('A' + ((n - 1) % 26)), (int)((n - 1) / 26) + 1);
    }

    // ------------------------------------------------------------------ actions

    public List<PdfActionInfo> ReadActions(out bool hasJavaScript, out List<string> javaScriptNames)
    {
        var actions = new List<PdfActionInfo>();
        var foundJavaScript = false;

        void Add(string location, PdfObject? action)
        {
            var visited = new HashSet<int>();
            var queue = new Queue<(PdfObject? Action, string Location, int Depth)>();
            queue.Enqueue((action, location, 0));
            while (queue.Count > 0)
            {
                var (current, loc, depth) = queue.Dequeue();
                if (current is null || depth > _maxDepth || (current is PdfReference r && !visited.Add(r.Id.Number)))
                {
                    continue;
                }

                if (actions.Count >= _maxEntries)
                {
                    throw new PdfLimitExceededException("The document has more actions than the configured maximum object count.");
                }

                switch (_doc.Resolve(current))
                {
                    case PdfArray:
                        actions.Add(new PdfActionInfo(loc, "GoTo")); // an /OpenAction destination
                        break;
                    case PdfDictionary d:
                        var type = Name(d["S"]) ?? "Unknown";
                        actions.Add(new PdfActionInfo(loc, type));
                        foundJavaScript |= type == "JavaScript";
                        if (type == "Rendition" && d.ContainsKey("JS"))
                        {
                            actions.Add(new PdfActionInfo(loc + " (rendition script)", "JavaScript"));
                            foundJavaScript = true;
                        }

                        switch (_doc.Resolve(d["Next"]))
                        {
                            case PdfDictionary:
                                queue.Enqueue((d["Next"], loc + " /Next", depth + 1));
                                break;
                            case PdfArray next:
                                foreach (var item in next.Items)
                                {
                                    queue.Enqueue((item, loc + " /Next", depth + 1));
                                }

                                break;
                        }

                        break;
                }
            }
        }

        void AddAdditional(string location, PdfObject? additionalActions)
        {
            if (_doc.ResolveDictionary(additionalActions) is { } aa)
            {
                foreach (var (trigger, action) in aa.Entries)
                {
                    Add($"{location} /AA /{trigger.Value}", action);
                }
            }
        }

        var catalog = Catalog;
        Add("OpenAction", catalog["OpenAction"]);
        AddAdditional("Document", catalog["AA"]);

        javaScriptNames = new List<string>();
        foreach (var (key, value) in NameTree.Read(_doc, NamesDictionary?["JavaScript"], _maxDepth, _maxEntries))
        {
            var name = TextString.Decode(key);
            javaScriptNames.Add(name);
            Add($"Named JavaScript \"{name}\"", value);
        }

        foreach (var page in _pages)
        {
            _ct.ThrowIfCancellationRequested();
            AddAdditional($"Page {page.Info.Number}", page.Page["AA"]);
            foreach (var (annot, _) in Annotations(page))
            {
                var where = $"Annotation /{Name(annot["Subtype"]) ?? "Unknown"} on page {page.Info.Number}";
                Add(where, annot["A"]);
                AddAdditional(where, annot["AA"]);
            }
        }

        // Fields that are not also widget annotations (those were covered with the pages).
        foreach (var field in ReadFieldNodes())
        {
            if (field.ObjectNumber is not int n || !AnnotationPages.ContainsKey(n))
            {
                AddAdditional($"Field \"{field.FullName}\"", field.Field["AA"]);
            }
        }

        foreach (var (item, title) in OutlineItems())
        {
            Add($"Bookmark \"{title}\"", item["A"]);
        }

        hasJavaScript = foundJavaScript || javaScriptNames.Count > 0;
        return actions;
    }

    private IEnumerable<(PdfDictionary Item, string Title)> OutlineItems()
    {
        var visited = new HashSet<int>();
        var stack = new Stack<(PdfObject? Node, int Depth)>();
        stack.Push((_doc.ResolveDictionary(Catalog["Outlines"])?["First"], 0));
        var count = 0;
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            while (node is not null && depth <= _maxDepth)
            {
                if ((node is PdfReference r && !visited.Add(r.Id.Number)) || _doc.ResolveDictionary(node) is not { } item || ++count > _maxEntries)
                {
                    break;
                }

                yield return (item, Text(item["Title"]) ?? "");
                if (item["First"] is { } child)
                {
                    stack.Push((child, depth + 1));
                }

                node = item["Next"];
            }
        }
    }
}
