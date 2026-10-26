using System.Security.Cryptography;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;
using PdfUtil.Internal.Text;

namespace PdfUtil.Internal.Protection;

/// <summary>Repair (F8) and sanitize (F9), on top of the shared <see cref="RewriteEngine"/>.</summary>
internal sealed class CleanupEngine
{
    private readonly PdfUtilOptions _lib;
    private readonly PdfLog _log;

    public CleanupEngine(PdfUtilOptions lib, PdfLog log)
    {
        _lib = lib;
        _log = log;
    }

    public static void Validate(PdfSanitizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.RemovesAnything)
        {
            throw new ArgumentException("Select at least one kind of content to remove.", nameof(options));
        }
    }

    // ------------------------------------------------------------------ repair

    public PdfRepairResult Repair(PdfSource input, Stream buffer, PdfRepairOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var doc = PdfDocument.Open(input, _lib, _log, strict: false, ct);
        using var security = RepairSecurity(doc, options);
        var catalogRef = doc.Trailer[PdfName.Root] as PdfReference ?? throw new PdfCorruptException("The trailer has no /Root reference.");
        var catalog = doc.GetObject(catalogRef.Id.Number) as PdfDictionary ?? throw new PdfCorruptException("The document catalog cannot be read.");
        var pages = PageTreeReader.ReadPageNodes(doc, _lib.MaxNestingDepth, ct);
        var declared = doc.ResolveDictionary(catalog["Pages"]) is { } root && doc.Resolve(root["Count"]) is PdfInteger count ? (int?)Math.Clamp(count.Value, 0, int.MaxValue) : null;

        var edits = new ObjectEdits(doc);
        var fixes = new List<string>();
        new PageTreeNormalizer(doc, edits, catalogRef.Id.Number, _lib.MaxNestingDepth, fixes).Run();
        var dangling = ReplaceDanglingReferences(doc, edits, ct);
        if (dangling > 0)
        {
            fixes.Add($"Removed {dangling} reference(s) to objects that do not exist.");
        }

        var warnings = new List<string>();
        if (declared > pages.Count)
        {
            warnings.Add($"The page tree declares {declared} pages, but only {pages.Count} could be read; the output has {pages.Count} pages.");
        }

        var outcome = new RewriteEngine(_lib, _log).Rewrite(doc, new RewriteSettings
        {
            Security = security,
            Progress = new ProgressReporter(options.Progress),
            Edits = edits.IsEmpty ? null : edits,
            InitialWarnings = warnings,
            UseObjectStreams = options.Write?.UseObjectStreams == true,
        }, buffer, ct);

        // What the recovery reader repaired, from its diagnostics (taken before the scan below loads unused objects).
        var diagnostics = doc.Diagnostics.ToList();
        var repairs = DescribeDamage(diagnostics);
        var wasDamaged = repairs.Count > 0 || fixes.Count > 0;
        repairs.AddRange(fixes);

        // Normalization.
        if (doc.IncrementalUpdateCount > 0)
        {
            repairs.Add($"Merged {doc.IncrementalUpdateCount} incremental update(s) into a single revision.");
        }

        if (options.Write?.UseObjectStreams == true)
        {
            // The output uses object streams and a cross-reference stream itself: nothing to report.
        }
        else if (doc.HasObjectStreams)
        {
            repairs.Add("Rewrote object streams and cross-reference streams as plain objects with a classic cross-reference table.");
        }
        else if (doc.HasXrefStreams)
        {
            repairs.Add("Rewrote the cross-reference stream as a classic cross-reference table.");
        }

        if (doc.IsLinearized)
        {
            repairs.Add("Removed linearization (fast web view), which a rewritten file cannot keep.");
        }

        var (unused, orphans) = ScanUnusedObjects(doc, edits, pages, ct);
        if (unused > 0)
        {
            repairs.Add($"Dropped {unused} unused object(s) that nothing referred to.");
        }

        var resultWarnings = outcome.Warnings.ToList();
        if (orphans > 0)
        {
            resultWarnings.Add($"{orphans} page object(s) in the file are not part of the page tree and are not in the output. They may be pages deleted by an incremental update, or pages cut off by damage.");
        }

        var handler = security is PreservedOutputEncryption p ? p.Handler : null;
        return new PdfRepairResult
        {
            SourceVersion = outcome.SourceVersion.ToString(),
            OutputVersion = outcome.OutputVersion.ToString(),
            Algorithm = handler?.Algorithm ?? PdfEncryptionAlgorithm.None,
            Revision = handler?.R ?? 0,
            Repairs = repairs,
            WasDamaged = wasDamaged,
            DeclaredPageCount = declared,
            PageCount = pages.Count,
            OrphanedPageCount = orphans,
            Warnings = resultWarnings,
        };
    }

    private static OutputSecurity RepairSecurity(PdfDocument doc, PdfRepairOptions options)
    {
        if (!doc.IsEncrypted)
        {
            return NoOutputEncryption.Instance;
        }

        if (!options.RemoveProtection)
        {
            return EditEngine.PreserveSecurity(doc, options.Password);
        }

        var handler = doc.InitializeSecurity();
        var owner = EditEngine.FindOwnerPassword(doc, handler, options.Password, "Password", "removing protection");
        CryptographicOperations.ZeroMemory(owner);
        return NoOutputEncryption.Instance;
    }

    private static List<string> DescribeDamage(List<DocumentDiagnostic> diagnostics)
    {
        int Objects(string code) => diagnostics.Where(d => d.Code == code).Select(d => d.ObjectNumber ?? -1).Distinct().Count();
        bool Has(string code) => diagnostics.Any(d => d.Code == code);
        var result = new List<string>();
        if (Has(PdfValidationCodes.HeaderJunk))
        {
            result.Add("Removed data that preceded the %PDF- header.");
        }

        if (Has(PdfValidationCodes.XrefReconstructed))
        {
            result.Add("Rebuilt the damaged cross-reference information by scanning the file.");
        }

        if (Has(PdfValidationCodes.XrefPrevLoop))
        {
            result.Add("Cut a loop in the chain of cross-reference sections.");
        }

        if (Has(PdfValidationCodes.XrefStreamDamaged))
        {
            result.Add("Ignored a damaged /XRefStm cross-reference stream.");
        }

        if (Has(PdfValidationCodes.XrefOffsetWrong))
        {
            result.Add($"Corrected the cross-reference offsets of {Objects(PdfValidationCodes.XrefOffsetWrong)} object(s).");
        }

        if (Has(PdfValidationCodes.StreamLengthWrong))
        {
            result.Add($"Corrected the /Length of {Objects(PdfValidationCodes.StreamLengthWrong)} stream(s).");
        }

        if (Has(PdfValidationCodes.ObjectStreamDamaged))
        {
            result.Add($"Skipped {Objects(PdfValidationCodes.ObjectStreamDamaged)} object stream(s) that could not be read.");
        }

        if (Has(PdfValidationCodes.ObjectMissing))
        {
            result.Add($"{Objects(PdfValidationCodes.ObjectMissing)} object(s) listed in the cross-reference data are missing from the file and were left out.");
        }

        return result;
    }

    /// <summary>Drops references to objects that do not exist (dictionary entries are removed, array items become null).</summary>
    private int ReplaceDanglingReferences(PdfDocument doc, ObjectEdits edits, CancellationToken ct)
    {
        var total = 0;
        foreach (var number in ObjectGraph.FindReachable(doc, ct, out _, lookup: n => edits.Lookup(doc, n)))
        {
            var obj = edits.Lookup(doc, number);
            if (obj is null)
            {
                continue;
            }

            var count = 0;
            var cleaned = WithoutDangling(doc, edits, obj, 0, ref count);
            if (count > 0)
            {
                total += count;
                edits.Replace(number, cleaned);
            }
        }

        return total;
    }

    private PdfObject WithoutDangling(PdfDocument doc, ObjectEdits edits, PdfObject obj, int depth, ref int count)
    {
        if (depth > _lib.MaxNestingDepth)
        {
            throw new PdfLimitExceededException("Maximum object nesting depth exceeded.");
        }

        bool IsDangling(PdfObject o) => o is PdfReference r && edits.Lookup(doc, r.Id.Number) is null;
        switch (obj)
        {
            case PdfStream s:
            {
                var before = count;
                var dict = (PdfDictionary)WithoutDangling(doc, edits, s.Dictionary, depth + 1, ref count);
                return count == before ? s : s.IsInMemory ? new PdfStream(dict, s.Data!) : new PdfStream(dict, s.SourceOffset, s.SourceLength);
            }

            case PdfDictionary d:
            {
                PdfDictionary? copy = null;
                foreach (var (key, value) in d.Entries.ToList())
                {
                    if (key.Value == "Length" && value is PdfReference)
                    {
                        continue; // the writer writes direct lengths
                    }

                    if (IsDangling(value))
                    {
                        (copy ??= d.ShallowClone()).Remove(key);
                        count++;
                        continue;
                    }

                    var before = count;
                    var inner = WithoutDangling(doc, edits, value, depth + 1, ref count);
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
                    if (IsDangling(a[i]))
                    {
                        copy ??= new PdfArray(a.Items);
                        copy.Items[i] = PdfNull.Instance;
                        count++;
                        continue;
                    }

                    var before = count;
                    var inner = WithoutDangling(doc, edits, a[i], depth + 1, ref count);
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

    /// <summary>Counts objects that are not written (excluding cross-reference and object-stream containers and the encryption dictionary), and page objects outside the page tree.</summary>
    private static (int Unused, int Orphans) ScanUnusedObjects(PdfDocument doc, ObjectEdits edits, List<PageNode> pages, CancellationToken ct)
    {
        var reachable = ObjectGraph.FindReachable(doc, ct, out _, lookup: n => edits.Lookup(doc, n));
        var pageNumbers = pages.Select(p => p.ObjectNumber).ToHashSet();
        var unused = 0;
        var orphans = 0;
        foreach (var (number, entry) in doc.Xref.ToList())
        {
            if (entry.Type == XrefEntryType.Free || reachable.Contains(number) || number == doc.EncryptObjectNumber || number == doc.LinearizationObjectNumber)
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();
            PdfObject? obj;
            try
            {
                obj = doc.GetObject(number);
            }
            catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException)
            {
                continue;
            }

            var dict = obj switch
            {
                PdfStream s => s.Dictionary,
                PdfDictionary d => d,
                _ => null,
            };
            var type = (doc.Resolve(dict?[PdfName.Type]) as PdfName)?.Value;
            if (obj is null || type is "XRef" or "ObjStm")
            {
                continue;
            }

            unused++;
            if (type == "Page" && !pageNumbers.Contains(number))
            {
                orphans++;
            }
        }

        return (unused, orphans);
    }

    // ------------------------------------------------------------------ sanitize

    public PdfSanitizeResult Sanitize(PdfSource input, Stream buffer, PdfSanitizeOptions options, CancellationToken ct)
    {
        Validate(options);
        using var doc = PdfDocument.Open(input, _lib, _log, strict: false, ct);
        using var security = EditEngine.KeepSecurity(doc, options.Password);
        var sanitizer = new Sanitizer(doc, options, _lib.MaxNestingDepth, _lib.MaxObjectCount, ct);
        var edits = sanitizer.Run();
        var warnings = new List<string>();
        if (options.RemoveXmp && sanitizer.Removed.Any(r => r.Kind == PdfSanitizeItemKind.XmpMetadata))
        {
            try
            {
                if (XmpReader.Read(doc) is { IsPdfA: true } xmp)
                {
                    warnings.Add($"The document declared {xmp.PdfALevel} conformance; without its XMP metadata the output is no longer PDF/A.");
                }
            }
            catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException)
            {
                // Unreadable XMP: nothing to warn about.
            }
        }

        var outcome = new RewriteEngine(_lib, _log).Rewrite(doc, new RewriteSettings
        {
            Security = security,
            Progress = new ProgressReporter(options.Progress),
            Edits = edits.IsEmpty ? null : edits,
            RemoveInfo = options.RemoveDocumentInfo,
            InitialWarnings = warnings,
            UseObjectStreams = options.Write?.UseObjectStreams == true,
        }, buffer, ct);
        var handler = doc.Security;
        return new PdfSanitizeResult
        {
            SourceVersion = outcome.SourceVersion.ToString(),
            OutputVersion = outcome.OutputVersion.ToString(),
            Algorithm = handler?.Algorithm ?? PdfEncryptionAlgorithm.None,
            Revision = handler?.R ?? 0,
            Removed = sanitizer.Removed,
            Warnings = outcome.Warnings,
        };
    }
}

/// <summary>
/// Normalizes the page tree for repair: removes repeated (circular) and invalid <c>/Kids</c> entries, corrects
/// <c>/Count</c> and <c>/Parent</c>, and gives pages without a media box the US Letter size readers assume. The pages
/// and their order stay exactly as the page tree reader sees them.
/// </summary>
internal sealed class PageTreeNormalizer
{
    private readonly PdfDocument _doc;
    private readonly ObjectEdits _edits;
    private readonly int _catalogNumber;
    private readonly int _maxDepth;
    private readonly List<string> _fixes;
    private readonly HashSet<int> _visited = new();
    private int _repeated;
    private int _invalid;
    private int _counts;
    private int _parents;
    private bool _missingMediaBox;

    public PageTreeNormalizer(PdfDocument doc, ObjectEdits edits, int catalogNumber, int maxDepth, List<string> fixes)
    {
        _doc = doc;
        _edits = edits;
        _catalogNumber = catalogNumber;
        _maxDepth = maxDepth;
        _fixes = fixes;
    }

    public void Run()
    {
        if (_edits.Lookup(_doc, _catalogNumber) is not PdfDictionary catalog || catalog["Pages"] is not { } rootRef)
        {
            return;
        }

        var (count, replacement) = Fix(rootRef, null, 0, hasMediaBox: false);
        if (count < 0)
        {
            return; // no usable page tree: nothing to normalize
        }

        if (_missingMediaBox)
        {
            var root = _edits.Lookup(_doc, (replacement as PdfReference)?.Id.Number ?? -1) as PdfDictionary ?? replacement as PdfDictionary;
            if (root is not null)
            {
                var copy = root.ShallowClone();
                copy["MediaBox"] = new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfInteger(612), new PdfInteger(792) });
                replacement = Store(replacement, copy);
                _fixes.Add("Gave pages without a /MediaBox the US Letter size that readers assume.");
            }
        }

        if (!ReferenceEquals(replacement, rootRef))
        {
            var catalogCopy = catalog.ShallowClone();
            catalogCopy["Pages"] = replacement;
            _edits.Replace(_catalogNumber, catalogCopy);
        }

        if (_repeated > 0)
        {
            _fixes.Add($"Removed {_repeated} repeated or circular page tree reference(s).");
        }

        if (_invalid > 0)
        {
            _fixes.Add($"Removed {_invalid} page tree entr(ies) that were not pages.");
        }

        if (_counts > 0)
        {
            _fixes.Add($"Corrected the /Count of {_counts} page tree node(s).");
        }

        if (_parents > 0)
        {
            _fixes.Add($"Corrected the /Parent of {_parents} page tree node(s).");
        }
    }

    /// <summary>Stores a changed node: as an edit for indirect nodes (returning the same reference), or as the new direct dictionary.</summary>
    private PdfObject Store(PdfObject original, PdfDictionary changed)
    {
        if (original is PdfReference r)
        {
            _edits.Replace(r.Id.Number, changed);
            return original;
        }

        return changed;
    }

    /// <summary>Returns the number of pages below the node (-1 when it must be dropped) and the object to keep in the parent's /Kids.</summary>
    private (int Count, PdfObject Replacement) Fix(PdfObject nodeRef, int? parent, int depth, bool hasMediaBox)
    {
        if (depth > _maxDepth)
        {
            throw new PdfLimitExceededException("The page tree is nested too deeply.");
        }

        var number = (nodeRef as PdfReference)?.Id.Number;
        if (number is int n && !_visited.Add(n))
        {
            _repeated++;
            return (-1, nodeRef);
        }

        if (_doc.ResolveDictionary(nodeRef) is not { } node)
        {
            _invalid++;
            return (-1, nodeRef);
        }

        hasMediaBox |= _doc.Resolve(node["MediaBox"]) is PdfArray { Count: >= 4 };
        PdfDictionary? copy = null;
        if (parent is int p && (node["Parent"] as PdfReference)?.Id.Number != p)
        {
            copy = node.ShallowClone();
            copy["Parent"] = new PdfReference(p, _doc.GetGeneration(p));
            _parents++;
        }

        // Exactly the page tree reader's rules: a node with /Kids (and not /Type /Page) is intermediate; a /Pages node
        // without /Kids is empty; anything else is a page.
        var type = (_doc.Resolve(node[PdfName.Type]) as PdfName)?.Value;
        var kids = _doc.Resolve(node["Kids"]) as PdfArray;
        if (kids is null || type == "Page")
        {
            if (type == "Pages")
            {
                _invalid++;
                return (-1, nodeRef);
            }

            _missingMediaBox |= !hasMediaBox;
            return (1, copy is null ? nodeRef : Store(nodeRef, copy));
        }

        var newKids = new PdfArray();
        var kidsChanged = false;
        var total = 0;
        foreach (var kid in kids.Items)
        {
            var (count, replacement) = Fix(kid, number ?? parent, depth + 1, hasMediaBox);
            if (count < 0)
            {
                kidsChanged = true;
                continue;
            }

            kidsChanged |= !ReferenceEquals(replacement, kid);
            newKids.Add(replacement);
            total += count;
        }

        if (kidsChanged)
        {
            copy ??= node.ShallowClone();
            copy["Kids"] = newKids;
        }

        if (_doc.Resolve(node["Count"]) is not PdfInteger declared || declared.Value != total)
        {
            copy ??= node.ShallowClone();
            copy["Count"] = new PdfInteger(total);
            _counts++;
        }

        return (total, copy is null ? nodeRef : Store(nodeRef, copy));
    }
}

/// <summary>Builds the object edits for <see cref="CleanupEngine.Sanitize"/> and records everything it removes.</summary>
internal sealed class Sanitizer
{
    private readonly PdfDocument _doc;
    private readonly PdfSanitizeOptions _options;
    private readonly int _maxDepth;
    private readonly int _maxEntries;
    private readonly CancellationToken _ct;
    private readonly ObjectEdits _edits;
    private readonly List<PdfRemovedItem> _removed = new();
    private readonly Dictionary<int, int> _pageNumbers = new();
    private readonly Dictionary<int, int> _annotationPages = new();
    private int _catalogNumber;

    public Sanitizer(PdfDocument doc, PdfSanitizeOptions options, int maxDepth, int maxEntries, CancellationToken ct)
    {
        _doc = doc;
        _options = options;
        _maxDepth = maxDepth;
        _maxEntries = maxEntries;
        _ct = ct;
        _edits = new ObjectEdits(doc);
    }

    public IReadOnlyList<PdfRemovedItem> Removed => _removed;

    public ObjectEdits Run()
    {
        _catalogNumber = (_doc.Trailer[PdfName.Root] as PdfReference ?? throw new PdfCorruptException("The trailer has no /Root reference.")).Id.Number;
        var pages = PageTreeReader.ReadPageNodes(_doc, _maxDepth, _ct);
        foreach (var page in pages.Where(p => p.ObjectNumber > 0))
        {
            _pageNumbers.TryAdd(page.ObjectNumber, page.Info.Number);
            if (_doc.Resolve(page.Page["Annots"]) is PdfArray annots)
            {
                foreach (var a in annots.Items.OfType<PdfReference>())
                {
                    _annotationPages.TryAdd(a.Id.Number, page.Info.Number);
                }
            }
        }

        if (_options.RemoveDocumentInfo && _doc.Resolve(_doc.Trailer[PdfName.Info]) is PdfDictionary)
        {
            Record(PdfSanitizeItemKind.DocumentInfo, "Document information dictionary", (_doc.Trailer[PdfName.Info] as PdfReference)?.Id.Number);
        }

        // Phase 1: structure (catalog entries and annotations), so phase 2 only visits what is still in the document.
        CleanCatalog();
        CleanAnnotations(pages);

        // Phase 2: actions, metadata and associated files on every remaining object.
        var reachable = ObjectGraph.FindReachable(_doc, _ct, out _, lookup: n => _edits.Lookup(_doc, n), includeInfo: !_options.RemoveDocumentInfo);
        foreach (var number in reachable)
        {
            _ct.ThrowIfCancellationRequested();
            if (_edits.Lookup(_doc, number) is not { } obj)
            {
                continue;
            }

            var cleaned = Clean(obj, number, 0);
            if (!ReferenceEquals(cleaned, obj))
            {
                _edits.Replace(number, cleaned);
            }
        }

        return _edits;
    }

    private void Record(PdfSanitizeItemKind kind, string description, int? objectNumber)
    {
        if (_removed.Count >= _maxEntries)
        {
            throw new PdfLimitExceededException("Too many items to remove.");
        }

        _removed.Add(new PdfRemovedItem(kind, description, objectNumber));
    }

    private string? Name(PdfObject? o) => (_doc.Resolve(o) as PdfName)?.Value;

    private bool IsJavaScript(PdfObject? action) => Name(_doc.ResolveDictionary(action)?["S"]) == "JavaScript";

    private void CleanCatalog()
    {
        if (_edits.Lookup(_doc, _catalogNumber) is not PdfDictionary catalog)
        {
            return;
        }

        var copy = catalog.ShallowClone();
        var changed = false;
        if (copy["OpenAction"] is { } openAction)
        {
            if (_options.RemoveActions)
            {
                copy.Remove(PdfName.Get("OpenAction"));
                changed = true;
                Record(PdfSanitizeItemKind.OpenAction, $"Open action ({(_doc.Resolve(openAction) is PdfArray ? "go to page" : Name(_doc.ResolveDictionary(openAction)?["S"]) ?? "unknown")})", _catalogNumber);
            }
            else if (_options.RemoveJavaScript)
            {
                var cleaned = CleanAction(openAction, 0, out var scripts);
                if (scripts > 0)
                {
                    copy["OpenAction"] = cleaned;
                    changed = true;
                    Record(PdfSanitizeItemKind.JavaScript, "JavaScript open action", _catalogNumber);
                }
            }
        }

        if (_doc.ResolveDictionary(copy["Names"]) is { } names)
        {
            var namesCopy = names.ShallowClone();
            var namesChanged = false;
            if (_options.RemoveJavaScript && namesCopy.ContainsKey("JavaScript"))
            {
                foreach (var (key, value) in NameTree.Read(_doc, namesCopy["JavaScript"], _maxDepth, _maxEntries))
                {
                    Record(PdfSanitizeItemKind.JavaScript, $"Document script \"{TextString.Decode(key)}\"", (value as PdfReference)?.Id.Number);
                }

                namesCopy.Remove(PdfName.Get("JavaScript"));
                namesChanged = true;
            }

            if (_options.RemoveAttachments && namesCopy.ContainsKey("EmbeddedFiles"))
            {
                foreach (var (key, value) in NameTree.Read(_doc, namesCopy["EmbeddedFiles"], _maxDepth, _maxEntries))
                {
                    Record(PdfSanitizeItemKind.Attachment, $"Embedded file \"{TextString.Decode(key)}\"", (value as PdfReference)?.Id.Number);
                }

                namesCopy.Remove(PdfName.Get("EmbeddedFiles"));
                namesChanged = true;
            }

            if (namesChanged)
            {
                changed = true;
                if (namesCopy.Count == 0)
                {
                    copy.Remove(PdfName.Get("Names"));
                }
                else if (copy["Names"] is PdfReference namesRef)
                {
                    _edits.Replace(namesRef.Id.Number, namesCopy);
                }
                else
                {
                    copy["Names"] = namesCopy;
                }
            }
        }

        if (_options.RemoveAttachments)
        {
            if (copy.Remove(PdfName.Get("AF")))
            {
                changed = true;
                Record(PdfSanitizeItemKind.Attachment, "Associated files of the document (/AF)", _catalogNumber);
            }

            if (copy.Remove(PdfName.Get("Collection")))
            {
                changed = true;
                Record(PdfSanitizeItemKind.Attachment, "Portfolio (/Collection)", _catalogNumber);
            }
        }

        if (_options.RemoveAnnotations && _doc.ResolveDictionary(copy["AcroForm"]) is { } acroForm)
        {
            var fields = _doc.Resolve(acroForm["Fields"]) is PdfArray f ? f.Count : 0;
            copy.Remove(PdfName.Get("AcroForm"));
            changed = true;
            Record(PdfSanitizeItemKind.Form, $"Interactive form ({fields} top-level field(s))", (catalog["AcroForm"] as PdfReference)?.Id.Number ?? _catalogNumber);
        }

        if (changed)
        {
            _edits.Replace(_catalogNumber, copy);
        }
    }

    private void CleanAnnotations(List<PageNode> pages)
    {
        if (!_options.RemoveAnnotations && !_options.RemoveAttachments)
        {
            return;
        }

        foreach (var page in pages.Where(p => p.ObjectNumber > 0))
        {
            if (_doc.Resolve(page.Page["Annots"]) is not PdfArray annots)
            {
                continue;
            }

            var kept = new PdfArray();
            foreach (var item in annots.Items)
            {
                var subtype = Name(_doc.ResolveDictionary(item)?["Subtype"]) ?? "Unknown";
                var asAttachment = _options.RemoveAttachments && subtype == "FileAttachment";
                var asAnnotation = _options.RemoveAnnotations && !(_options.KeepLinks && subtype == "Link");
                if (asAttachment || asAnnotation)
                {
                    Record(asAttachment ? PdfSanitizeItemKind.Attachment : PdfSanitizeItemKind.Annotation, $"Annotation /{subtype} on page {page.Info.Number}", (item as PdfReference)?.Id.Number ?? page.ObjectNumber);
                }
                else
                {
                    kept.Add(item);
                }
            }

            if (kept.Count == annots.Count || _edits.Lookup(_doc, page.ObjectNumber) is not PdfDictionary pageDict)
            {
                continue;
            }

            var copy = pageDict.ShallowClone();
            if (kept.Count == 0)
            {
                copy.Remove(PdfName.Get("Annots"));
            }
            else
            {
                copy["Annots"] = kept;
            }

            _edits.Replace(page.ObjectNumber, copy);
        }
    }

    private string Describe(int owner)
    {
        if (owner == _catalogNumber)
        {
            return "the document";
        }

        if (_pageNumbers.TryGetValue(owner, out var page))
        {
            return $"page {page}";
        }

        if (_annotationPages.TryGetValue(owner, out var annotPage))
        {
            return $"annotation /{Name((_edits.Lookup(_doc, owner) as PdfDictionary)?["Subtype"]) ?? "Unknown"} on page {annotPage}";
        }

        return $"object {owner}";
    }

    /// <summary>Copy-on-write cleaning: returns <paramref name="obj"/> itself when nothing changed.</summary>
    private PdfObject Clean(PdfObject obj, int owner, int depth)
    {
        if (depth > _maxDepth)
        {
            throw new PdfLimitExceededException("Maximum object nesting depth exceeded.");
        }

        switch (obj)
        {
            case PdfStream s:
                var dict = CleanDictionary(s.Dictionary, owner, depth + 1);
                return ReferenceEquals(dict, s.Dictionary) ? s : s.IsInMemory ? new PdfStream(dict, s.Data!) : new PdfStream(dict, s.SourceOffset, s.SourceLength);
            case PdfDictionary d:
                return CleanDictionary(d, owner, depth);
            case PdfArray a:
                PdfArray? copy = null;
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is PdfDictionary or PdfArray)
                    {
                        var inner = Clean(a[i], owner, depth + 1);
                        if (!ReferenceEquals(inner, a[i]))
                        {
                            copy ??= new PdfArray(a.Items);
                            copy.Items[i] = inner;
                        }
                    }
                }

                return copy ?? a;
            default:
                return obj;
        }
    }

    private PdfDictionary CleanDictionary(PdfDictionary d, int owner, int depth)
    {
        PdfDictionary? copy = null;
        PdfDictionary Copy() => copy ??= d.ShallowClone();
        var isTop = depth == 0;

        if (d["AA"] is { } additional)
        {
            if (_options.RemoveActions)
            {
                Copy().Remove(PdfName.Get("AA"));
                Record(PdfSanitizeItemKind.AdditionalActions, $"Additional actions of {Describe(owner)}", owner);
            }
            else if (_options.RemoveJavaScript && _doc.ResolveDictionary(additional) is { } aa)
            {
                PdfDictionary? aaCopy = null;
                foreach (var (trigger, action) in aa.Entries)
                {
                    var cleaned = CleanAction(action, 0, out var scripts);
                    if (scripts == 0)
                    {
                        continue;
                    }

                    aaCopy ??= aa.ShallowClone();
                    if (cleaned is null)
                    {
                        aaCopy.Remove(trigger);
                    }
                    else
                    {
                        aaCopy[trigger] = cleaned;
                    }

                    Record(PdfSanitizeItemKind.JavaScript, $"JavaScript action /AA /{trigger.Value} of {Describe(owner)}", owner);
                }

                if (aaCopy is not null)
                {
                    if (aaCopy.Count == 0)
                    {
                        Copy().Remove(PdfName.Get("AA"));
                    }
                    else
                    {
                        Copy()["AA"] = aaCopy;
                    }
                }
            }
        }

        if (_options.RemoveJavaScript && d["A"] is { } a)
        {
            var cleaned = CleanAction(a, 0, out var scripts);
            if (scripts > 0)
            {
                if (cleaned is null)
                {
                    Copy().Remove(PdfName.Get("A"));
                }
                else
                {
                    Copy()["A"] = cleaned;
                }

                Record(PdfSanitizeItemKind.JavaScript, $"JavaScript action of {Describe(owner)}", owner);
            }
        }

        if (_options.RemoveXmp && d["Metadata"] is PdfReference metadata && _doc.Resolve(metadata) is PdfStream xmp && Name(xmp.Dictionary[PdfName.Type]) == "Metadata")
        {
            Copy().Remove(PdfName.Metadata);
            Record(PdfSanitizeItemKind.XmpMetadata, isTop && owner == _catalogNumber ? "Document XMP metadata" : $"XMP metadata of {Describe(owner)}", metadata.Id.Number);
        }

        if (_options.RemoveAttachments && d.ContainsKey("AF"))
        {
            Copy().Remove(PdfName.Get("AF"));
            Record(PdfSanitizeItemKind.Attachment, $"Associated files (/AF) of {Describe(owner)}", owner);
        }

        foreach (var (key, value) in (copy ?? d).Entries.ToList())
        {
            if (value is PdfDictionary or PdfArray)
            {
                var inner = Clean(value, owner, depth + 1);
                if (!ReferenceEquals(inner, value))
                {
                    Copy()[key] = inner;
                }
            }
        }

        return copy ?? d;
    }

    /// <summary>
    /// Removes JavaScript from an action and its <c>/Next</c> chain. Returns null when the action itself is JavaScript
    /// (removed with its chain), the action unchanged when there is no script, or a direct copy without the scripts.
    /// </summary>
    private PdfObject? CleanAction(PdfObject? action, int depth, out int scripts)
    {
        scripts = 0;
        if (depth > _maxDepth || _doc.ResolveDictionary(action) is not { } d)
        {
            return action;
        }

        if (IsJavaScript(d))
        {
            scripts = 1;
            return null;
        }

        PdfDictionary? copy = null;
        if (Name(d["S"]) == "Rendition" && d.ContainsKey("JS"))
        {
            copy = d.ShallowClone();
            copy.Remove(PdfName.Get("JS"));
            scripts++;
        }

        switch (_doc.Resolve(d["Next"]))
        {
            case PdfDictionary:
            {
                var next = CleanAction(d["Next"], depth + 1, out var nextScripts);
                if (nextScripts > 0)
                {
                    scripts += nextScripts;
                    copy ??= d.ShallowClone();
                    if (next is null)
                    {
                        copy.Remove(PdfName.Get("Next"));
                    }
                    else
                    {
                        copy["Next"] = next;
                    }
                }

                break;
            }

            case PdfArray chain:
            {
                var kept = new PdfArray();
                var removedAny = false;
                foreach (var item in chain.Items)
                {
                    var next = CleanAction(item, depth + 1, out var nextScripts);
                    scripts += nextScripts;
                    removedAny |= nextScripts > 0;
                    if (next is not null)
                    {
                        kept.Add(next);
                    }
                }

                if (removedAny)
                {
                    copy ??= d.ShallowClone();
                    if (kept.Count == 0)
                    {
                        copy.Remove(PdfName.Get("Next"));
                    }
                    else
                    {
                        copy["Next"] = kept;
                    }
                }

                break;
            }
        }

        return copy ?? action;
    }
}
