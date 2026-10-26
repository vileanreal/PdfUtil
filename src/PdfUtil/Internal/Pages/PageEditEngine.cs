using System.Text;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;
using PdfUtil.Internal.Protection;
using PdfUtil.Internal.Text;

namespace PdfUtil.Internal.Pages;

/// <summary>Rotate and delete pages (F13), on top of the shared <see cref="RewriteEngine"/>.</summary>
internal sealed class PageEditEngine
{
    private readonly PdfUtilOptions _lib;
    private readonly PdfLog _log;

    public PageEditEngine(PdfUtilOptions lib, PdfLog log)
    {
        _lib = lib;
        _log = log;
    }

    public static void Validate(PdfRotatePagesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Angle % 90 != 0)
        {
            throw new ArgumentException("Angle must be a multiple of 90 degrees.", nameof(options));
        }
    }

    public static void Validate(PdfDeletePagesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Pages is null)
        {
            throw new ArgumentException("Pages is required.", nameof(options));
        }
    }

    public PdfPageEditResult Rotate(PdfSource input, Stream buffer, PdfRotatePagesOptions options, CancellationToken ct)
    {
        Validate(options);
        using var doc = PdfDocument.Open(input, _lib, _log, strict: false, ct);
        using var security = EditEngine.KeepSecurity(doc, options.Password);
        var pages = PageTreeReader.ReadPageNodes(doc, _lib.MaxNestingDepth, ct);
        var selected = (options.Pages ?? PdfPageRange.All).Resolve(pages.Count).ToHashSet();
        var edits = new ObjectEdits(doc);
        var warnings = new List<string>();
        var expected = new List<PdfPageInfo>();
        foreach (var page in pages)
        {
            var info = page.Info;
            if (selected.Contains(info.Number))
            {
                var rotate = (((info.Rotate + options.Angle) % 360) + 360) % 360;
                if (page.ObjectNumber == 0)
                {
                    warnings.Add($"Page {info.Number} is a direct object inside the page tree and could not be rotated.");
                }
                else
                {
                    var copy = page.Page.ShallowClone();
                    copy["Rotate"] = new PdfInteger(rotate);
                    edits.Replace(page.ObjectNumber, copy);
                    info = new PdfPageInfo(info.Number, info.MediaBox, info.CropBox, rotate);
                }
            }

            expected.Add(info);
        }

        var outcome = Rewrite(doc, security, edits, expected, options.Progress, options.Write, warnings, buffer, ct);
        return Result(doc, outcome, pages.Count, expected.Count, Array.Empty<string>());
    }

    public PdfPageEditResult Delete(PdfSource input, Stream buffer, PdfDeletePagesOptions options, CancellationToken ct)
    {
        Validate(options);
        using var doc = PdfDocument.Open(input, _lib, _log, strict: false, ct);
        using var security = EditEngine.KeepSecurity(doc, options.Password);
        var pages = PageTreeReader.ReadPageNodes(doc, _lib.MaxNestingDepth, ct);
        var deleted = options.Pages!.Resolve(pages.Count).ToHashSet();
        if (deleted.Count == pages.Count)
        {
            throw new ArgumentException("A document must keep at least one page; the range selects every page.", nameof(options));
        }

        var remover = new PageRemover(doc, pages, deleted, _lib, ct);
        var edits = remover.Run();
        var expected = pages.Where(p => !deleted.Contains(p.Info.Number))
            .Select((p, i) => new PdfPageInfo(i + 1, p.Info.MediaBox, p.Info.CropBox, p.Info.Rotate))
            .ToList();
        var outcome = Rewrite(doc, security, edits, expected, options.Progress, options.Write, remover.Warnings, buffer, ct);
        return Result(doc, outcome, pages.Count, expected.Count, remover.Removed);
    }

    private RewriteOutcome Rewrite(PdfDocument doc, OutputSecurity security, ObjectEdits edits, List<PdfPageInfo> expected, IProgress<PdfProgress>? progress, PdfWriteOptions? write, List<string> warnings, Stream buffer, CancellationToken ct) =>
        new RewriteEngine(_lib, _log).Rewrite(doc, new RewriteSettings
        {
            Security = security,
            Progress = new ProgressReporter(progress),
            Edits = edits.IsEmpty ? null : edits,
            ExpectedPages = expected,
            UseObjectStreams = write?.UseObjectStreams == true,
            InitialWarnings = warnings,
        }, buffer, ct);

    private static PdfPageEditResult Result(PdfDocument doc, RewriteOutcome outcome, int sourcePages, int pages, IReadOnlyList<string> removed) => new()
    {
        SourceVersion = outcome.SourceVersion.ToString(),
        OutputVersion = outcome.OutputVersion.ToString(),
        Algorithm = doc.Security?.Algorithm ?? PdfEncryptionAlgorithm.None,
        Revision = doc.Security?.R ?? 0,
        SourcePageCount = sourcePages,
        PageCount = pages,
        Removed = removed,
        Warnings = outcome.Warnings,
    };
}

/// <summary>
/// Builds the edits for deleting pages: a flat page tree with inherited attributes resolved onto each remaining page,
/// and removal of everything that pointed at a deleted page (links, bookmark destinations, named destinations, the
/// open action, form fields and widgets, page labels shifted, and any other reference).
/// </summary>
internal sealed class PageRemover
{
    private readonly PdfDocument _doc;
    private readonly List<PageNode> _pages;
    private readonly HashSet<int> _deleted;
    private readonly int _maxDepth;
    private readonly int _maxEntries;
    private readonly CancellationToken _ct;
    private readonly ObjectEdits _edits;
    private readonly HashSet<int> _deletedObjects = new();
    private readonly Dictionary<int, int> _pageNumbers = new();
    private Dictionary<string, PdfObject>? _named;

    public PageRemover(PdfDocument doc, List<PageNode> pages, HashSet<int> deleted, PdfUtilOptions lib, CancellationToken ct)
    {
        _doc = doc;
        _pages = pages;
        _deleted = deleted;
        _maxDepth = lib.MaxNestingDepth;
        _maxEntries = lib.MaxObjectCount;
        _ct = ct;
        _edits = new ObjectEdits(doc);
    }

    public List<string> Removed { get; } = new();

    public List<string> Warnings { get; } = new();

    private PdfObject? Lookup(int n) => _edits.Lookup(_doc, n);

    private string? Name(PdfObject? o) => (_doc.Resolve(o) as PdfName)?.Value;

    public ObjectEdits Run()
    {
        foreach (var page in _pages.Where(p => p.ObjectNumber > 0))
        {
            _pageNumbers.TryAdd(page.ObjectNumber, page.Info.Number);
            if (_deleted.Contains(page.Info.Number))
            {
                _deletedObjects.Add(page.ObjectNumber);
                if (_doc.Resolve(page.Page["Annots"]) is PdfArray annots)
                {
                    foreach (var a in annots.Items.OfType<PdfReference>())
                    {
                        _deletedObjects.Add(a.Id.Number);
                    }
                }
            }
        }

        var catalogNumber = ((PdfReference)_doc.Trailer[PdfName.Root]!).Id.Number;
        var catalog = ((PdfDictionary)_doc.GetObject(catalogNumber)!).ShallowClone();
        RebuildPageTree(catalog);
        RemoveLinks();
        RemoveOutlineDestinations(catalog);
        RemoveNamedDestinations(catalog);
        if (catalog["OpenAction"] is { } openAction && PointsToDeletedPage(_doc.Resolve(openAction) is PdfArray ? openAction : _doc.ResolveDictionary(openAction)?["D"]))
        {
            catalog.Remove(PdfName.Get("OpenAction"));
            Removed.Add("The open action went to a deleted page and was removed.");
        }

        RemoveFormFields(catalog);
        RebuildPageLabels(catalog);
        _edits.Replace(catalogNumber, catalog);
        SweepReferences();
        return _edits;
    }

    // ------------------------------------------------------------------ page tree

    private void RebuildPageTree(PdfDictionary catalog)
    {
        var rootRef = catalog["Pages"] as PdfReference ?? throw new PdfCorruptException("The page tree root must be an indirect object.");
        var root = ((PdfDictionary)_doc.GetObject(rootRef.Id.Number)!).ShallowClone();
        var kids = new PdfArray();
        foreach (var page in _pages.Where(p => !_deleted.Contains(p.Info.Number)))
        {
            var copy = page.Page.ShallowClone();
            foreach (var (key, value) in page.Inherited.Entries)
            {
                if (!copy.ContainsKey(key))
                {
                    copy[key] = value;
                }
            }

            copy["Parent"] = rootRef;
            if (page.ObjectNumber > 0)
            {
                _edits.Replace(page.ObjectNumber, copy);
                kids.Add(new PdfReference(page.ObjectNumber, _doc.GetGeneration(page.ObjectNumber)));
            }
            else
            {
                kids.Add(copy);
            }
        }

        root["Kids"] = kids;
        root["Count"] = new PdfInteger(kids.Count);
        _edits.Replace(rootRef.Id.Number, root);
        foreach (var number in _deleted.OrderBy(n => n))
        {
            Removed.Add($"Page {number} was deleted.");
        }
    }

    // ------------------------------------------------------------------ destinations

    private Dictionary<string, PdfObject> NamedDestinations
    {
        get
        {
            if (_named is null)
            {
                _named = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
                var catalog = _doc.Catalog!;
                if (_doc.ResolveDictionary(catalog["Dests"]) is { } dests)
                {
                    foreach (var (key, value) in dests.Entries)
                    {
                        _named.TryAdd(key.Value, value);
                    }
                }

                foreach (var (key, value) in NameTree.Read(_doc, _doc.ResolveDictionary(catalog["Names"])?["Dests"], _maxDepth, _maxEntries))
                {
                    _named.TryAdd(Encoding.Latin1.GetString(key), value);
                }
            }

            return _named;
        }
    }

    /// <summary>The source page number a destination goes to, or null.</summary>
    private int? TargetPage(PdfObject? dest, int depth = 0)
    {
        if (depth > 4)
        {
            return null;
        }

        return _doc.Resolve(dest) switch
        {
            PdfArray { Count: > 0 } a when a[0] is PdfReference r => _pageNumbers.TryGetValue(r.Id.Number, out var p) ? p : null,
            PdfDictionary d => TargetPage(d["D"], depth + 1),
            PdfName n => NamedDestinations.TryGetValue(n.Value, out var v) ? TargetPage(v, depth + 1) : null,
            PdfString s => NamedDestinations.TryGetValue(Encoding.Latin1.GetString(s.Value), out var v) ? TargetPage(v, depth + 1) : null,
            _ => null,
        };
    }

    private bool PointsToDeletedPage(PdfObject? dest) => TargetPage(dest) is int p && _deleted.Contains(p);

    /// <summary>The destination of an annotation or outline item: /Dest, or the /D of a GoTo action.</summary>
    private PdfObject? DestinationOf(PdfDictionary d) =>
        d["Dest"] ?? (_doc.ResolveDictionary(d["A"]) is { } a && Name(a["S"]) == "GoTo" ? a["D"] : null);

    private void RemoveLinks()
    {
        foreach (var page in _pages.Where(p => p.ObjectNumber > 0 && !_deleted.Contains(p.Info.Number)))
        {
            var pageDict = (PdfDictionary)Lookup(page.ObjectNumber)!;
            if (_doc.Resolve(pageDict["Annots"]) is not PdfArray annots)
            {
                continue;
            }

            var kept = new PdfArray();
            foreach (var item in annots.Items)
            {
                var annot = _doc.ResolveDictionary(item);
                if (annot is not null && Name(annot["Subtype"]) == "Link" && DestinationOf(annot) is { } dest && PointsToDeletedPage(dest))
                {
                    Removed.Add($"The link on page {page.Info.Number} to deleted page {TargetPage(dest)} was removed.");
                    continue;
                }

                kept.Add(item);
            }

            if (kept.Count != annots.Count)
            {
                var copy = pageDict.ShallowClone();
                copy["Annots"] = kept;
                _edits.Replace(page.ObjectNumber, copy);
            }
        }
    }

    private void RemoveOutlineDestinations(PdfDictionary catalog)
    {
        var visited = new HashSet<int>();
        var stack = new Stack<(PdfObject? Node, int Depth)>();
        stack.Push((_doc.ResolveDictionary(catalog["Outlines"])?["First"], 0));
        var count = 0;
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            while (node is PdfReference r && depth <= _maxDepth && visited.Add(r.Id.Number) && ++count <= _maxEntries)
            {
                _ct.ThrowIfCancellationRequested();
                if (_doc.GetObject(r.Id.Number) is not PdfDictionary item)
                {
                    break;
                }

                if (DestinationOf(item) is { } dest && PointsToDeletedPage(dest))
                {
                    var copy = item.ShallowClone();
                    copy.Remove(PdfName.Get("Dest"));
                    copy.Remove(PdfName.Get("A"));
                    _edits.Replace(r.Id.Number, copy);
                    var title = _doc.Resolve(item["Title"]) is PdfString t ? TextString.Decode(t.Value) : "";
                    Removed.Add($"The bookmark \"{title}\" went to deleted page {TargetPage(dest)}; its destination was removed.");
                }

                if (item["First"] is { } child)
                {
                    stack.Push((child, depth + 1));
                }

                node = item["Next"];
            }
        }
    }

    private void RemoveNamedDestinations(PdfDictionary catalog)
    {
        var removed = 0;
        if (_doc.ResolveDictionary(catalog["Dests"]) is { } dests)
        {
            var copy = dests.ShallowClone();
            foreach (var (key, value) in dests.Entries)
            {
                if (PointsToDeletedPage(value))
                {
                    copy.Remove(key);
                    removed++;
                }
            }

            if (copy.Count != dests.Count)
            {
                catalog["Dests"] = copy;
            }
        }

        if (_doc.ResolveDictionary(catalog["Names"]) is { } names && names["Dests"] is { } tree)
        {
            var entries = NameTree.Read(_doc, tree, _maxDepth, _maxEntries);
            var kept = entries.Where(e => !PointsToDeletedPage(e.Value)).ToList();
            if (kept.Count != entries.Count)
            {
                removed += entries.Count - kept.Count;
                var namesCopy = names.ShallowClone();
                namesCopy["Dests"] = NameTree.Build(kept);
                if (catalog["Names"] is PdfReference namesRef)
                {
                    _edits.Replace(namesRef.Id.Number, namesCopy);
                }
                else
                {
                    catalog["Names"] = namesCopy;
                }
            }
        }

        if (removed > 0)
        {
            Removed.Add($"{removed} named destination(s) went to deleted pages and were removed.");
        }
    }

    // ------------------------------------------------------------------ form fields

    private void RemoveFormFields(PdfDictionary catalog)
    {
        if (_doc.ResolveDictionary(catalog["AcroForm"]) is not { } acroForm || _doc.Resolve(acroForm["Fields"]) is not PdfArray fields)
        {
            return;
        }

        var visited = new HashSet<int>();
        var kept = new PdfArray();
        foreach (var field in fields.Items)
        {
            if (Filter(field, "", 0, visited) is { } f)
            {
                kept.Add(f);
            }
        }

        if (kept.Count != fields.Count)
        {
            var copy = acroForm.ShallowClone();
            copy["Fields"] = kept;
            if (catalog["AcroForm"] is PdfReference formRef)
            {
                _edits.Replace(formRef.Id.Number, copy);
            }
            else
            {
                catalog["AcroForm"] = copy;
            }
        }
    }

    /// <summary>Returns the field (or widget) to keep, or null when all its widgets were on deleted pages.</summary>
    private PdfObject? Filter(PdfObject node, string parentName, int depth, HashSet<int> visited)
    {
        if (depth > _maxDepth || (node is PdfReference r && !visited.Add(r.Id.Number)) || _doc.ResolveDictionary(node) is not { } dict)
        {
            return null;
        }

        var partial = _doc.Resolve(dict["T"]) is PdfString t ? TextString.Decode(t.Value) : null;
        var name = partial is null ? parentName : parentName.Length == 0 ? partial : parentName + "." + partial;
        if (node is PdfReference self && _deletedObjects.Contains(self.Id.Number))
        {
            if (partial is not null)
            {
                Removed.Add($"The form field \"{name}\" was only on deleted pages and was removed.");
            }

            return null;
        }

        if (_doc.Resolve(dict["Kids"]) is not PdfArray kids)
        {
            return node;
        }

        var keptKids = new PdfArray();
        foreach (var kid in kids.Items)
        {
            if (Filter(kid, name, depth + 1, visited) is { } k)
            {
                keptKids.Add(k);
            }
        }

        if (keptKids.Count == 0 && kids.Count > 0)
        {
            Removed.Add($"The form field \"{name}\" was only on deleted pages and was removed.");
            return null;
        }

        if (keptKids.Count != kids.Count && node is PdfReference nodeRef)
        {
            var copy = dict.ShallowClone();
            copy["Kids"] = keptKids;
            _edits.Replace(nodeRef.Id.Number, copy);
        }

        return node;
    }

    // ------------------------------------------------------------------ page labels

    private void RebuildPageLabels(PdfDictionary catalog)
    {
        if (catalog["PageLabels"] is null)
        {
            return;
        }

        var ranges = NumberTree.Read(_doc, catalog["PageLabels"], _maxDepth, _maxEntries).Where(r => r.Key >= 0).OrderBy(r => r.Key).ToList();
        var nums = new PdfArray();
        PdfDictionary? previous = null;
        long previousNumber = 0;
        var index = 0;
        foreach (var page in _pages)
        {
            var source = page.Info.Number - 1;
            if (_deleted.Contains(page.Info.Number))
            {
                continue;
            }

            var range = ranges.LastOrDefault(r => r.Key <= source);
            var dict = range.Value is null ? null : _doc.ResolveDictionary(range.Value);
            var start = dict is not null && _doc.Resolve(dict["St"]) is PdfInteger st ? st.Value : 1;
            var number = start + (source - range.Key);
            if (index == 0 || !ReferenceEquals(dict, previous) || number != previousNumber + 1)
            {
                var label = new PdfDictionary();
                if (dict?["S"] is { } style)
                {
                    label["S"] = style;
                }

                if (dict?["P"] is { } prefix)
                {
                    label["P"] = prefix;
                }

                if (number != 1)
                {
                    label["St"] = new PdfInteger(number);
                }

                nums.Add(new PdfInteger(index));
                nums.Add(range.Value is null ? new PdfDictionary { ["S"] = PdfName.Get("D"), ["St"] = new PdfInteger(source + 1) } : label);
            }

            previous = dict;
            previousNumber = number;
            index++;
        }

        catalog["PageLabels"] = new PdfDictionary { ["Nums"] = nums };
    }

    // ------------------------------------------------------------------ other references

    /// <summary>Removes any remaining reference to a deleted page or one of its annotations (logical structure, threads, ...).</summary>
    private void SweepReferences()
    {
        var total = 0;
        foreach (var number in ObjectGraph.FindReachable(_doc, _ct, out _, lookup: Lookup))
        {
            if (_deletedObjects.Contains(number) || Lookup(number) is not { } obj)
            {
                continue;
            }

            var count = 0;
            var cleaned = Without(obj, 0, ref count);
            if (count > 0)
            {
                total += count;
                _edits.Replace(number, cleaned);
            }
        }

        if (total > 0)
        {
            Removed.Add($"{total} other reference(s) to deleted pages (logical structure, article threads, ...) were removed.");
        }
    }

    private PdfObject Without(PdfObject obj, int depth, ref int count)
    {
        if (depth > _maxDepth)
        {
            throw new PdfLimitExceededException("Maximum object nesting depth exceeded.");
        }

        bool Deleted(PdfObject o) => o is PdfReference r && _deletedObjects.Contains(r.Id.Number);
        switch (obj)
        {
            case PdfStream s:
            {
                var before = count;
                var dict = (PdfDictionary)Without(s.Dictionary, depth + 1, ref count);
                return count == before ? s : s.IsInMemory ? new PdfStream(dict, s.Data!) : new PdfStream(dict, s.SourceOffset, s.SourceLength);
            }

            case PdfDictionary d:
            {
                PdfDictionary? copy = null;
                foreach (var (key, value) in d.Entries.ToList())
                {
                    if (Deleted(value))
                    {
                        (copy ??= d.ShallowClone()).Remove(key);
                        count++;
                        continue;
                    }

                    var before = count;
                    var inner = Without(value, depth + 1, ref count);
                    if (count != before)
                    {
                        (copy ??= d.ShallowClone())[key] = inner;
                    }
                }

                return copy ?? d;
            }

            case PdfArray a:
            {
                PdfArray? copy = null;
                for (var i = 0; i < a.Count; i++)
                {
                    if (Deleted(a[i]))
                    {
                        copy ??= new PdfArray(a.Items);
                        copy.Items[i] = PdfNull.Instance;
                        count++;
                        continue;
                    }

                    var before = count;
                    var inner = Without(a[i], depth + 1, ref count);
                    if (count != before)
                    {
                        copy ??= new PdfArray(a.Items);
                        copy.Items[i] = inner;
                    }
                }

                return copy ?? a;
            }

            default:
                return obj;
        }
    }
}
