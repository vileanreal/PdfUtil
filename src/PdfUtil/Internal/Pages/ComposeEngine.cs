using System.Security.Cryptography;
using System.Text;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Filters;
using PdfUtil.Internal.IO;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;
using PdfUtil.Internal.Protection;
using PdfUtil.Internal.Text;
using PdfUtil.Internal.Writing;

namespace PdfUtil.Internal.Pages;

/// <summary>A document built in memory from objects copied out of one or more sources.</summary>
internal sealed class ComposedDocument
{
    private int _next = 1;

    public SortedDictionary<int, PdfObject> Objects { get; } = new();

    public int Reserve() => _next++;

    public int Add(PdfObject obj)
    {
        var n = Reserve();
        Objects[n] = obj;
        return n;
    }

    public void Set(int number, PdfObject obj) => Objects[number] = obj;

    public PdfObject? Get(int number) => Objects.TryGetValue(number, out var o) ? o : null;
}

/// <summary>
/// Copies objects from one source into a <see cref="ComposedDocument"/> with new numbers. References to blocked
/// objects (pages that are not copied, the old page tree, annotations of pages that are not copied) become null, so
/// a copy never drags in the rest of the source. Streams are copied byte for byte (decrypted, never re-encoded).
/// </summary>
internal sealed class SourceCopier
{
    private readonly PdfDocument _src;
    private readonly ComposedDocument _dst;
    private readonly int _maxDepth;
    private readonly Dictionary<int, int> _map = new();
    private readonly Queue<(int Source, int Target)> _queue = new();

    public SourceCopier(PdfDocument src, ComposedDocument dst, int maxDepth, HashSet<int> blocked, HashSet<int> widgets)
    {
        _src = src;
        _dst = dst;
        _maxDepth = maxDepth;
        Blocked = blocked;
        Widgets = widgets;
    }

    public HashSet<int> Blocked { get; }

    /// <summary>Widget annotations: copied without /Parent, which the form rebuild sets.</summary>
    public HashSet<int> Widgets { get; }

    public IReadOnlyDictionary<int, int> Map => _map;

    public void Assign(int source, int target) => _map[source] = target;

    public PdfReference? MapReference(PdfReference r)
    {
        if (_map.TryGetValue(r.Id.Number, out var mapped))
        {
            return new PdfReference(mapped, 0);
        }

        if (Blocked.Contains(r.Id.Number))
        {
            return null;
        }

        var obj = _src.GetObject(r.Id.Number);
        if (obj is null || (obj is PdfStream s && (s.Dictionary[PdfName.Type] as PdfName)?.Value is "XRef" or "ObjStm"))
        {
            return null;
        }

        var target = _dst.Reserve();
        _map[r.Id.Number] = target;
        _queue.Enqueue((r.Id.Number, target));
        return new PdfReference(target, 0);
    }

    /// <summary>A deep copy of a direct object; references are mapped (null for blocked ones).</summary>
    public PdfObject Copy(PdfObject obj, int depth = 0)
    {
        if (depth > _maxDepth)
        {
            throw new PdfLimitExceededException("Maximum object nesting depth exceeded while copying.");
        }

        switch (obj)
        {
            case PdfReference r:
                return (PdfObject?)MapReference(r) ?? PdfNull.Instance;
            case PdfArray a:
                return new PdfArray(a.Items.Select(i => Copy(i, depth + 1)));
            case PdfDictionary d:
                var copy = new PdfDictionary();
                foreach (var (key, value) in d.Entries)
                {
                    var c = Copy(value, depth + 1);
                    if (c is not PdfNull || value is PdfNull)
                    {
                        copy[key] = c;
                    }
                }

                return copy;
            default:
                return obj;
        }
    }

    /// <summary>Copies everything queued by <see cref="MapReference"/>.</summary>
    public void Drain(CancellationToken ct)
    {
        while (_queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (source, target) = _queue.Dequeue();
            var obj = _src.GetObject(source)!;
            switch (obj)
            {
                case PdfStream stream:
                    var dict = (PdfDictionary)Copy(WithoutCrypt(stream.Dictionary));
                    dict.Remove(PdfName.Length);
                    _dst.Set(target, new PdfStream(dict, _src.GetStreamData(stream, new ObjectId(source, _src.GetGeneration(source)))));
                    break;
                case PdfDictionary d when Widgets.Contains(source):
                    var widget = d.ShallowClone();
                    widget.Remove(PdfName.Get("Parent"));
                    _dst.Set(target, Copy(widget));
                    break;
                default:
                    _dst.Set(target, Copy(obj));
                    break;
            }
        }
    }

    /// <summary>The stream dictionary without /Crypt filters (the data is copied decrypted).</summary>
    private PdfDictionary WithoutCrypt(PdfDictionary dict)
    {
        var filters = StreamFilters.GetFilterNames(dict, _src.Resolve);
        if (!filters.Contains("Crypt"))
        {
            return dict;
        }

        var copy = dict.ShallowClone();
        var parms = _src.Resolve(dict[PdfName.DecodeParms]);
        var newFilters = new PdfArray();
        var newParms = new PdfArray();
        for (var i = 0; i < filters.Count; i++)
        {
            if (filters[i] == "Crypt")
            {
                continue;
            }

            newFilters.Add(PdfName.Get(filters[i]));
            newParms.Add(parms is PdfArray pa && i < pa.Count ? pa[i] : i == 0 && parms is PdfDictionary pd ? pd : PdfNull.Instance);
        }

        copy.Remove(PdfName.Filter);
        copy.Remove(PdfName.DecodeParms);
        if (newFilters.Count > 0)
        {
            copy[PdfName.Filter] = newFilters;
            if (newParms.Items.Any(p => p is not PdfNull))
            {
                copy[PdfName.DecodeParms] = newParms;
            }
        }

        return copy;
    }
}

/// <summary>A source document opened for merging or splitting.</summary>
internal sealed class ComposeSource : IDisposable
{
    public required PdfSource Data { get; init; }

    public required PdfMergeInput Input { get; init; }

    /// <summary>The opened input stream, which the source may read from directly.</summary>
    public required Stream Stream { get; init; }

    public PdfDocument? Doc { get; set; }

    public void Dispose()
    {
        Doc?.Dispose();
        Data.Dispose();
        Stream.Dispose();
    }
}

/// <summary>Merge and split (F14): copy the selected pages and what belongs to them into a new document, then write and verify it.</summary>
internal sealed class ComposeEngine
{
    private readonly PdfUtilOptions _lib;
    private readonly PdfLog _log;

    public ComposeEngine(PdfUtilOptions lib, PdfLog log)
    {
        _lib = lib;
        _log = log;
    }

    public static void Validate(PdfSplitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if ((options.Ranges is null) == (options.EveryNPages is null))
        {
            throw new ArgumentException("Set either Ranges or EveryNPages.", nameof(options));
        }

        if (options.EveryNPages is < 1)
        {
            throw new ArgumentException("EveryNPages must be at least 1.", nameof(options));
        }

        if (options.Ranges is { Count: 0 })
        {
            throw new ArgumentException("Ranges is empty.", nameof(options));
        }

        if (options.Protection is { } protection)
        {
            ProtectionEngine.ValidateOptions(protection);
        }
    }

    public static async Task<List<ComposeSource>> OpenInputsAsync(IReadOnlyList<PdfMergeInput> inputs, PdfUtilOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            throw new ArgumentException("At least one input is required.", nameof(inputs));
        }

        var sources = new List<ComposeSource>();
        try
        {
            foreach (var input in inputs)
            {
                ArgumentNullException.ThrowIfNull(input, nameof(inputs));
                var stream = input.Open();
                try
                {
                    sources.Add(new ComposeSource { Data = await InputBuffer.OpenAsync(stream, options, ct).ConfigureAwait(false), Input = input, Stream = stream });
                }
                catch
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            return sources;
        }
        catch
        {
            Dispose(sources);
            throw;
        }
    }

    public static void Dispose(List<ComposeSource> sources)
    {
        foreach (var s in sources)
        {
            s.Dispose();
        }
    }

    // ------------------------------------------------------------------ entry points

    public PdfMergeResult Merge(List<ComposeSource> sources, Stream buffer, PdfMergeOptions options, CancellationToken ct)
    {
        if (options.Protection is { } protection)
        {
            ProtectionEngine.ValidateOptions(protection);
        }

        var progress = new ProgressReporter(options.Progress);
        var parts = new List<(PdfDocument Doc, List<PageNode> Pages, IReadOnlyList<int> Selected, string Name)>();
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            source.Doc = Open(source.Data, source.Input.Password, options.AllowUserPassword, $"input {i + 1}", ct);
            var pages = PageTreeReader.ReadPageNodes(source.Doc, _lib.MaxNestingDepth, ct);
            var selected = (source.Input.Pages ?? PdfPageRange.All).Resolve(pages.Count);
            var title = source.Input.Name ?? InfoReader.Read(source.Doc)?.Title;
            parts.Add((source.Doc, pages, selected, string.IsNullOrWhiteSpace(title) ? $"Document {i + 1}" : title));
        }

        var composer = new Composer(_lib, options.Outlines, ct);
        var composed = composer.Compose(parts);
        var deduplicated = options.DeduplicateResources ? Deduplicate(composed.Document) : 0;
        var (version, encrypted, warnings) = WriteVerified(composed, buffer, options.Protection, options.Write, progress, ct);
        return new PdfMergeResult
        {
            OutputVersion = version,
            PageCount = composed.Pages.Count,
            PagesPerInput = parts.Select(p => p.Selected.Count).ToList(),
            IsEncrypted = encrypted,
            Renamed = composer.Renamed,
            Removed = composer.Removed,
            DeduplicatedObjects = deduplicated,
            Warnings = composer.Warnings.Concat(warnings).Distinct().ToList(),
        };
    }

    /// <summary>Splits; <paramref name="commit"/> receives each verified part and returns the file path written, if any.</summary>
    public PdfSplitResult Split(PdfSource source, PdfSplitOptions options, Func<PdfSplitPart, Stream, string?> commit, CancellationToken ct)
    {
        Validate(options);
        using var doc = Open(source, options.Password, options.AllowUserPassword, "the input", ct);
        var pages = PageTreeReader.ReadPageNodes(doc, _lib.MaxNestingDepth, ct);
        var selections = options.Ranges is { } ranges
            ? ranges.Select(r => r.Resolve(pages.Count)).ToList()
            : Enumerable.Range(0, (pages.Count + options.EveryNPages!.Value - 1) / options.EveryNPages.Value)
                .Select(i => (IReadOnlyList<int>)Enumerable.Range((i * options.EveryNPages.Value) + 1, Math.Min(options.EveryNPages.Value, pages.Count - (i * options.EveryNPages.Value))).ToList())
                .ToList();
        var progress = new ProgressReporter(options.Progress);
        var parts = new List<PdfSplitPart>();
        var files = new List<string>();
        var removed = new List<string>();
        var warnings = new List<string>();
        var title = InfoReader.Read(doc)?.Title ?? "Document";
        for (var i = 0; i < selections.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (selections[i].Count == 0)
            {
                throw new ArgumentException($"Part {i + 1} has no pages.", nameof(options));
            }

            var composer = new Composer(_lib, PdfMergeOutlineMode.Flat, ct);
            var composed = composer.Compose(new List<(PdfDocument, List<PageNode>, IReadOnlyList<int>, string)> { (doc, pages, selections[i], title) });
            using var buffer = InputBuffer.CreateOutputBuffer(0, _lib);
            var (_, _, partWarnings) = WriteVerified(composed, buffer, options.Protection, options.Write, progress, ct);
            var part = new PdfSplitPart(i + 1, selections[i]);
            parts.Add(part);
            removed.AddRange(composer.Removed.Select(r => $"Part {i + 1}: {r}"));
            warnings.AddRange(composer.Warnings.Concat(partWarnings));
            if (commit(part, buffer) is { } path)
            {
                files.Add(path);
            }
        }

        return new PdfSplitResult { Parts = parts, Files = files, Removed = removed, Warnings = warnings.Distinct().ToList() };
    }

    private PdfDocument Open(PdfSource data, string? password, bool allowUser, string what, CancellationToken ct)
    {
        var doc = PdfDocument.Open(data, _lib, _log, strict: false, ct);
        try
        {
            if (doc.IsEncrypted)
            {
                var handler = doc.InitializeSecurity();
                if (allowUser)
                {
                    if (doc.Authenticate(password) == PdfPasswordMatch.None)
                    {
                        throw new PdfInvalidPasswordException($"The password does not open {what}.");
                    }
                }
                else
                {
                    CryptographicOperations.ZeroMemory(EditEngine.FindOwnerPassword(doc, handler, password, $"The password of {what}", "copying its pages into a new document"));
                }
            }

            return doc;
        }
        catch
        {
            doc.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------ write and verify

    private (string Version, bool Encrypted, List<string> Warnings) WriteVerified(Composer.Result composed, Stream buffer, PdfProtectionOptions? protection, PdfWriteOptions? write, ProgressReporter progress, CancellationToken ct)
    {
        var warnings = new List<string>();
        var secondStage = protection is not null || write?.UseObjectStreams == true;
        using var first = secondStage ? new MemoryStream() : null;
        var target = first ?? buffer;
        WriteComposed(composed, target, progress, ct);
        target.Position = 0;
        VerifyComposed(composed, target, ct);
        _log.Info("The composed document passed self-verification.");
        if (!secondStage)
        {
            buffer.Position = 0;
            return (composed.Version.ToString(), false, warnings);
        }

        first!.Position = 0;
        using var source = new PdfSource(first, ownsStream: false);
        if (protection is not null)
        {
            var options = protection;
            if (write is not null && protection.Write is null)
            {
                options = Clone(protection);
                options.Write = write;
            }

            var result = new ProtectionEngine(_lib, _log).Protect(source, buffer, options, ct);
            warnings.AddRange(result.Warnings);
            return (result.OutputVersion, true, warnings);
        }

        using var doc = PdfDocument.Open(source, _lib, _log, strict: false, ct);
        var outcome = new RewriteEngine(_lib, _log).Rewrite(doc, new RewriteSettings { Security = NoOutputEncryption.Instance, Progress = progress, UseObjectStreams = true }, buffer, ct);
        return (outcome.OutputVersion.ToString(), false, warnings);
    }

    private static PdfProtectionOptions Clone(PdfProtectionOptions o) => new()
    {
        UserPassword = o.UserPassword,
        OwnerPassword = o.OwnerPassword,
        Permissions = o.Permissions,
        Algorithm = o.Algorithm,
        PdfAPolicy = o.PdfAPolicy,
        AlreadyEncryptedPolicy = o.AlreadyEncryptedPolicy,
        CurrentOwnerPassword = o.CurrentOwnerPassword,
        EncryptMetadata = o.EncryptMetadata,
        PasswordPolicy = o.PasswordPolicy,
        InfoChanges = o.InfoChanges,
        AllowPasswordTruncation = o.AllowPasswordTruncation,
        Progress = o.Progress,
        VerifyOutput = o.VerifyOutput,
        Write = o.Write,
    };

    private void WriteComposed(Composer.Result composed, Stream buffer, ProgressReporter progress, CancellationToken ct)
    {
        var doc = composed.Document;
        using var writer = new PdfWriter(buffer, _lib.MaxNestingDepth);
        writer.WriteHeader(composed.Version);
        progress.Report(PdfProgressStage.Writing, 0, doc.Objects.Count, force: true);
        var count = 0;
        foreach (var (number, obj) in doc.Objects)
        {
            if ((++count & 0xFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (obj is PdfStream stream)
            {
                var dict = stream.Dictionary.ShallowClone();
                dict[PdfName.Length] = new PdfInteger(stream.Data!.Length);
                writer.WriteIndirectObject(new ObjectId(number, 0), new PdfStream(dict, stream.Data), stream.Data);
            }
            else
            {
                writer.WriteIndirectObject(new ObjectId(number, 0), obj, null);
            }

            progress.Report(PdfProgressStage.Writing, count, doc.Objects.Count);
        }

        var trailer = new PdfDictionary { [PdfName.Root] = new PdfReference(composed.CatalogNumber, 0) };
        if (composed.InfoNumber is int info)
        {
            trailer[PdfName.Info] = new PdfReference(info, 0);
        }

        trailer[PdfName.ID] = new PdfArray(new PdfObject[] { new PdfString(composed.Id, isHex: true), new PdfString(composed.Id, isHex: true) });
        writer.WriteXrefAndTrailer(trailer);
        writer.Flush();
        progress.Report(PdfProgressStage.Writing, doc.Objects.Count, doc.Objects.Count, force: true);
    }

    private static void Check(bool condition, string failure)
    {
        if (!condition)
        {
            throw new PdfVerificationException("The composed document failed self-verification: " + failure);
        }
    }

    private void VerifyComposed(Composer.Result composed, Stream buffer, CancellationToken ct)
    {
        try
        {
            using var source = new PdfSource(buffer, ownsStream: false);
            using var outDoc = PdfDocument.Open(source, _lib, _log, strict: true, ct);
            Check(outDoc.XrefSectionCount == 1 && !outDoc.HasXrefStreams, "the output must have one classic cross-reference table.");
            outDoc.VerifyXrefOffsetsExact();
            Check(outDoc.HeaderVersion == composed.Version.ToString(), "unexpected header version.");
            foreach (var (number, expected) in composed.Document.Objects)
            {
                ct.ThrowIfCancellationRequested();
                var written = outDoc.GetObject(number);
                if (expected is PdfStream es)
                {
                    Check(written is PdfStream ws && ObjectGraph.DeepEquals(es.Dictionary, ws.Dictionary, ignoreKey: "Length") && outDoc.GetStreamData(ws, new ObjectId(number, 0)).AsSpan().SequenceEqual(es.Data), $"stream {number} did not round-trip.");
                }
                else
                {
                    Check(ObjectGraph.DeepEquals(expected, written), $"object {number} did not round-trip.");
                }
            }

            // The pages are the selected source pages, in order, with the same geometry and the same content bytes.
            var outPages = PageTreeReader.ReadPageNodes(outDoc, _lib.MaxNestingDepth, ct);
            Check(outPages.Count == composed.Pages.Count, "page count differs from the selection.");
            for (var i = 0; i < outPages.Count; i++)
            {
                var (srcDoc, srcPage) = composed.Pages[i];
                Check(outPages[i].Info.MediaBox == srcPage.Info.MediaBox && outPages[i].Info.CropBox == srcPage.Info.CropBox && outPages[i].Info.Rotate == srcPage.Info.Rotate, $"the geometry of page {i + 1} differs from the source.");
                Check(ContentBytes(srcDoc, srcPage.Page).SequenceEqual(ContentBytes(outDoc, outPages[i].Page)), $"the content of page {i + 1} differs from the source.");
            }
        }
        catch (PdfVerificationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is PdfUtilException or InvalidDataException or InvalidOperationException)
        {
            throw new PdfVerificationException("The composed document failed self-verification: " + ex.Message, ex);
        }
    }

    private static byte[] ContentBytes(PdfDocument doc, PdfDictionary page)
    {
        var streams = doc.Resolve(page["Contents"]) switch
        {
            PdfArray a => a.Items,
            null => new List<PdfObject>(),
            _ => new List<PdfObject> { page["Contents"]! },
        };
        using var ms = new MemoryStream();
        foreach (var item in streams)
        {
            if (item is PdfReference r && doc.GetObject(r.Id.Number) is PdfStream s)
            {
                ms.Write(doc.GetStreamData(s, new ObjectId(r.Id.Number, doc.GetGeneration(r.Id.Number))));
            }
        }

        return ms.ToArray();
    }

    // ------------------------------------------------------------------ deduplication

    /// <summary>Replaces identical streams and identical font, encoding and graphics-state dictionaries by one copy. Returns how many objects were removed.</summary>
    private int Deduplicate(ComposedDocument doc)
    {
        var removed = 0;
        for (var round = 0; round < 8; round++)
        {
            var canonical = new Dictionary<string, int>(StringComparer.Ordinal);
            var replace = new Dictionary<int, int>();
            foreach (var (number, obj) in doc.Objects)
            {
                var key = DedupKey(obj);
                if (key is null)
                {
                    continue;
                }

                if (canonical.TryGetValue(key, out var existing))
                {
                    replace[number] = existing;
                }
                else
                {
                    canonical[key] = number;
                }
            }

            if (replace.Count == 0)
            {
                break;
            }

            foreach (var n in replace.Keys)
            {
                doc.Objects.Remove(n);
            }

            removed += replace.Count;
            foreach (var number in doc.Objects.Keys.ToList())
            {
                doc.Objects[number] = Renumber(doc.Objects[number], replace, 0);
            }
        }

        return removed;
    }

    private string? DedupKey(PdfObject obj)
    {
        switch (obj)
        {
            case PdfStream s:
                var d = s.Dictionary.ShallowClone();
                d.Remove(PdfName.Length);
                return "S" + Encoding.Latin1.GetString(PdfWriter.Serialize(d, _lib.MaxNestingDepth)) + Convert.ToHexString(SHA256.HashData(s.Data!));
            case PdfDictionary dict when (dict[PdfName.Type] as PdfName)?.Value is "Font" or "FontDescriptor" or "Encoding" or "ExtGState":
                return "D" + Encoding.Latin1.GetString(PdfWriter.Serialize(dict, _lib.MaxNestingDepth));
            default:
                return null;
        }
    }

    private PdfObject Renumber(PdfObject obj, Dictionary<int, int> replace, int depth) => obj switch
    {
        _ when depth > _lib.MaxNestingDepth => throw new PdfLimitExceededException("Maximum object nesting depth exceeded."),
        PdfReference r when replace.TryGetValue(r.Id.Number, out var to) => new PdfReference(to, 0),
        PdfArray a => new PdfArray(a.Items.Select(i => Renumber(i, replace, depth + 1))),
        PdfStream s => new PdfStream((PdfDictionary)Renumber(s.Dictionary, replace, depth + 1), s.Data!),
        PdfDictionary d => RenumberDict(d, replace, depth),
        _ => obj,
    };

    private PdfDictionary RenumberDict(PdfDictionary d, Dictionary<int, int> replace, int depth)
    {
        var copy = new PdfDictionary();
        foreach (var (key, value) in d.Entries)
        {
            copy[key] = Renumber(value, replace, depth + 1);
        }

        return copy;
    }
}

/// <summary>Builds the composed document: pages, page tree, bookmarks, named destinations, form, attachments, catalog.</summary>
internal sealed class Composer
{
    private readonly PdfUtilOptions _lib;
    private readonly PdfMergeOutlineMode _outlineMode;
    private readonly CancellationToken _ct;
    private readonly ComposedDocument _dst = new();
    private readonly HashSet<string> _usedDestNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedFieldNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedAttachmentNames = new(StringComparer.Ordinal);
    private readonly List<(byte[] Key, PdfObject Value)> _dests = new();
    private readonly List<(byte[] Key, PdfObject Value)> _attachments = new();
    private readonly PdfArray _fields = new();
    private readonly PdfDictionary _resources = new();
    private readonly List<int> _outlineItems = new();
    private readonly PdfArray _ocgs = new();
    private bool _needAppearances;
    private PdfObject? _da;

    public Composer(PdfUtilOptions lib, PdfMergeOutlineMode outlineMode, CancellationToken ct)
    {
        _lib = lib;
        _outlineMode = outlineMode;
        _ct = ct;
    }

    public sealed record Result(ComposedDocument Document, int CatalogNumber, int? InfoNumber, PdfVersion Version, byte[] Id, List<(PdfDocument Doc, PageNode Page)> Pages);

    public List<string> Renamed { get; } = new();

    public List<string> Removed { get; } = new();

    public List<string> Warnings { get; } = new();

    public Result Compose(List<(PdfDocument Doc, List<PageNode> Pages, IReadOnlyList<int> Selected, string Name)> sources)
    {
        var catalogNumber = _dst.Reserve();
        var pagesNumber = _dst.Reserve();
        var kids = new PdfArray();
        var pages = new List<(PdfDocument, PageNode)>();
        var version = new PdfVersion(1, 0);
        int? infoNumber = null;
        PdfDictionary? firstCatalog = null;
        SourceCopier? firstCopier = null;

        for (var index = 0; index < sources.Count; index++)
        {
            var (doc, allPages, selected, name) = sources[index];
            var catalog = doc.Catalog ?? throw new PdfCorruptException("The document catalog cannot be read.");
            PdfVersion.TryParse(doc.HeaderVersion, out var v);
            if (doc.Resolve(catalog["Version"]) is PdfName cv && PdfVersion.TryParse(cv.Value, out var catalogVersion))
            {
                v = PdfVersion.Max(v, catalogVersion);
            }

            version = PdfVersion.Max(version, v);
            var copier = CreateCopier(doc, allPages, selected);
            var newPages = new List<int>();
            foreach (var number in selected)
            {
                var node = allPages[number - 1];
                var target = _dst.Reserve();
                if (node.ObjectNumber > 0)
                {
                    copier.Assign(node.ObjectNumber, target);
                }

                newPages.Add(target);
            }

            for (var i = 0; i < selected.Count; i++)
            {
                var node = allPages[selected[i] - 1];
                var page = node.Page.ShallowClone();
                page.Remove(PdfName.Get("Parent"));
                page.Remove(PdfName.Get("B"));
                foreach (var (key, value) in node.Inherited.Entries)
                {
                    if (!page.ContainsKey(key))
                    {
                        page[key] = value;
                    }
                }

                var copy = (PdfDictionary)copier.Copy(page);
                copy["Parent"] = new PdfReference(pagesNumber, 0);
                _dst.Set(newPages[i], copy);
                kids.Add(new PdfReference(newPages[i], 0));
                pages.Add((doc, node));
            }

            copier.Drain(_ct);
            var renames = CopyNamedDestinations(doc, copier, index);
            FixLinks(copier, newPages, renames, index);
            CopyOutline(doc, copier, renames, name, newPages, index);
            CopyForm(doc, copier, index);
            CopyAttachments(doc, copier, index);
            if (doc.Resolve(catalog["OCProperties"]) is PdfDictionary oc && doc.Resolve(oc["OCGs"]) is PdfArray groups)
            {
                foreach (var g in groups.Items)
                {
                    if (copier.Copy(g) is PdfReference r && !_ocgs.Items.OfType<PdfReference>().Any(x => x.Id == r.Id))
                    {
                        _ocgs.Add(r);
                    }
                }
            }

            if (catalog.ContainsKey("StructTreeRoot"))
            {
                Warnings.Add("The logical structure (tags) of the inputs is not kept, so the output is not a tagged PDF.");
            }

            if (catalog.ContainsKey("PageLabels"))
            {
                Warnings.Add("Page labels are not kept; the output uses plain page numbers.");
            }

            if (doc.ResolveDictionary(catalog["Names"])?.ContainsKey("JavaScript") == true)
            {
                Warnings.Add("Document-level JavaScript is not kept.");
            }

            try
            {
                if (XmpReader.Read(doc) is { IsPdfA: true } xmp)
                {
                    Warnings.Add($"An input declared {xmp.PdfALevel} conformance; the output has no XMP metadata and is not PDF/A.");
                }
            }
            catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException)
            {
                // Unreadable XMP: not copied anyway.
            }

            if (index == 0)
            {
                firstCatalog = catalog;
                firstCopier = copier;
                if (doc.Resolve(doc.Trailer[PdfName.Info]) is PdfDictionary info)
                {
                    infoNumber = _dst.Add(copier.Copy(info));
                }
            }

            copier.Drain(_ct);
        }

        // Page tree, catalog and the shared trees.
        _dst.Set(pagesNumber, new PdfDictionary { [PdfName.Type] = PdfName.Get("Pages"), ["Kids"] = kids, ["Count"] = new PdfInteger(kids.Count) });
        var newCatalog = new PdfDictionary { [PdfName.Type] = PdfName.Get("Catalog"), ["Pages"] = new PdfReference(pagesNumber, 0) };
        if (_outlineItems.Count > 0)
        {
            newCatalog["Outlines"] = new PdfReference(LinkOutline(_outlineItems, null), 0);
        }

        var names = new PdfDictionary();
        if (_dests.Count > 0)
        {
            names["Dests"] = NameTree.Build(_dests);
        }

        if (_attachments.Count > 0)
        {
            names["EmbeddedFiles"] = NameTree.Build(_attachments);
        }

        if (names.Count > 0)
        {
            newCatalog["Names"] = names;
        }

        if (_fields.Count > 0)
        {
            var form = new PdfDictionary { ["Fields"] = _fields };
            if (_resources.Count > 0)
            {
                form["DR"] = _resources;
            }

            if (_da is not null)
            {
                form["DA"] = _da;
            }

            if (_needAppearances)
            {
                form["NeedAppearances"] = PdfBoolean.True;
            }

            newCatalog["AcroForm"] = form;
        }

        if (_ocgs.Count > 0)
        {
            newCatalog["OCProperties"] = new PdfDictionary { ["OCGs"] = _ocgs, ["D"] = new PdfDictionary { ["ON"] = new PdfArray(_ocgs.Items) } };
        }

        foreach (var key in new[] { "PageLayout", "PageMode", "Lang", "ViewerPreferences" })
        {
            if (firstCatalog?[key] is { } value)
            {
                newCatalog[key] = firstCopier!.Copy(value);
            }
        }

        if (_outlineItems.Count > 0 && newCatalog["PageMode"] is null && sources.Count > 1)
        {
            newCatalog["PageMode"] = PdfName.Get("UseOutlines");
        }

        _dst.Set(catalogNumber, newCatalog);
        firstCopier?.Drain(_ct);
        return new Result(_dst, catalogNumber, infoNumber, version, RandomNumberGenerator.GetBytes(16), pages);
    }

    private SourceCopier CreateCopier(PdfDocument doc, List<PageNode> pages, IReadOnlyList<int> selected)
    {
        var selectedSet = selected.ToHashSet();
        var blocked = new HashSet<int>();
        var widgets = new HashSet<int>();
        if (doc.Trailer[PdfName.Root] is PdfReference root)
        {
            blocked.Add(root.Id.Number);
        }

        // The page tree's intermediate nodes.
        var stack = new Stack<(PdfObject?, int)>();
        stack.Push((doc.Catalog?["Pages"], 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (depth > _lib.MaxNestingDepth || node is not PdfReference r || !blocked.Add(r.Id.Number))
            {
                continue;
            }

            if (doc.ResolveDictionary(node) is { } d && doc.Resolve(d["Kids"]) is PdfArray kids && (d[PdfName.Type] as PdfName)?.Value != "Page")
            {
                foreach (var k in kids.Items)
                {
                    stack.Push((k, depth + 1));
                }
            }
        }

        foreach (var page in pages)
        {
            if (page.ObjectNumber > 0)
            {
                blocked.Remove(page.ObjectNumber);
            }

            var keep = selectedSet.Contains(page.Info.Number);
            if (!keep && page.ObjectNumber > 0)
            {
                blocked.Add(page.ObjectNumber);
            }

            if (doc.Resolve(page.Page["Annots"]) is PdfArray annots)
            {
                foreach (var a in annots.Items.OfType<PdfReference>())
                {
                    if (!keep)
                    {
                        blocked.Add(a.Id.Number);
                    }

                    if ((doc.ResolveDictionary(a)?["Subtype"] as PdfName)?.Value == "Widget")
                    {
                        widgets.Add(a.Id.Number);
                    }
                }
            }
        }

        if (doc.Catalog?["AcroForm"] is PdfReference form)
        {
            blocked.Add(form.Id.Number);
        }

        return new SourceCopier(doc, _dst, _lib.MaxNestingDepth, blocked, widgets);
    }

    private static string UniqueName(string name, HashSet<string> used)
    {
        if (used.Add(name))
        {
            return name;
        }

        for (var i = 2; ; i++)
        {
            var candidate = name + "_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    // ------------------------------------------------------------------ destinations and links

    /// <summary>Copies the named destinations whose page was copied; returns old name → new name.</summary>
    private Dictionary<string, string> CopyNamedDestinations(PdfDocument doc, SourceCopier copier, int index)
    {
        var all = new List<(string Name, PdfObject Value)>();
        var catalog = doc.Catalog!;
        if (doc.ResolveDictionary(catalog["Dests"]) is { } dests)
        {
            all.AddRange(dests.Entries.Select(e => (e.Key.Value, e.Value)));
        }

        all.AddRange(NameTree.Read(doc, doc.ResolveDictionary(catalog["Names"])?["Dests"], _lib.MaxNestingDepth, _lib.MaxObjectCount).Select(e => (Encoding.Latin1.GetString(e.Key), e.Value)));
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in all)
        {
            if (renames.ContainsKey(name))
            {
                continue;
            }

            var dest = doc.Resolve(value) is PdfDictionary d ? d["D"] : value;
            var copied = copier.Copy(doc.Resolve(dest) ?? PdfNull.Instance);
            if (copied is not PdfArray { Count: > 0 } array || array[0] is PdfNull)
            {
                continue; // its page is not in the output
            }

            var newName = UniqueName(name, _usedDestNames);
            if (newName != name)
            {
                Renamed.Add($"The named destination \"{name}\" of input {index + 1} was renamed to \"{newName}\".");
            }

            renames[name] = newName;
            _dests.Add((Encoding.Latin1.GetBytes(newName), copied));
        }

        return renames;
    }

    private static string? DestName(PdfObject? dest) => dest switch
    {
        PdfName n => n.Value,
        PdfString s => Encoding.Latin1.GetString(s.Value),
        _ => null,
    };

    /// <summary>
    /// A copied destination: explicit arrays whose page was not copied, and names that were not copied, give null; names
    /// are renamed.
    /// </summary>
    private PdfObject? RemapDestination(PdfObject? dest, Dictionary<string, string> renames)
    {
        switch (dest is PdfReference r ? _dst.Get(r.Id.Number) : dest)
        {
            case PdfArray { Count: > 0 } a:
                return a[0] is PdfNull ? null : dest;
            case var named when DestName(named) is { } name:
                return renames.TryGetValue(name, out var newName) ? new PdfString(Encoding.Latin1.GetBytes(newName)) : null;
            default:
                return null;
        }
    }

    private void FixLinks(SourceCopier copier, List<int> pageNumbers, Dictionary<string, string> renames, int index)
    {
        for (var p = 0; p < pageNumbers.Count; p++)
        {
            var page = (PdfDictionary)_dst.Get(pageNumbers[p])!;
            if ((page["Annots"] is PdfReference ar ? _dst.Get(ar.Id.Number) : page["Annots"]) is not PdfArray annots)
            {
                continue;
            }

            var kept = new PdfArray();
            foreach (var item in annots.Items)
            {
                if (item is PdfReference r && _dst.Get(r.Id.Number) is PdfDictionary annot && (annot["Subtype"] as PdfName)?.Value == "Link")
                {
                    var action = annot["A"] is PdfReference aref ? _dst.Get(aref.Id.Number) as PdfDictionary : annot["A"] as PdfDictionary;
                    var isGoTo = (action?["S"] as PdfName)?.Value == "GoTo";
                    var dest = annot["Dest"] ?? (isGoTo ? action!["D"] : null);
                    if (annot.ContainsKey("Dest") || isGoTo)
                    {
                        var remapped = RemapDestination(dest, renames);
                        if (remapped is null)
                        {
                            Removed.Add($"A link on page {p + 1} of input {index + 1} went to a page that is not in the output and was removed.");
                            continue;
                        }

                        if (!ReferenceEquals(remapped, dest))
                        {
                            if (annot.ContainsKey("Dest"))
                            {
                                annot["Dest"] = remapped;
                            }
                            else
                            {
                                var newAction = action!.ShallowClone();
                                newAction["D"] = remapped;
                                annot["A"] = newAction;
                            }
                        }
                    }
                }
                else if (item is PdfNull)
                {
                    continue;
                }

                kept.Add(item);
            }

            if (page["Annots"] is PdfReference annotsRef)
            {
                _dst.Set(annotsRef.Id.Number, kept);
            }
            else
            {
                page["Annots"] = kept;
            }
        }
    }

    // ------------------------------------------------------------------ outline

    private void CopyOutline(PdfDocument doc, SourceCopier copier, Dictionary<string, string> renames, string name, List<int> pages, int index)
    {
        if (_outlineMode == PdfMergeOutlineMode.None)
        {
            return;
        }

        var visited = new HashSet<int>();
        var count = 0;
        var items = CopyOutlineLevel(doc, copier, renames, doc.ResolveDictionary(doc.Catalog!["Outlines"])?["First"], 0, visited, ref count, index);
        if (_outlineMode == PdfMergeOutlineMode.PerDocument)
        {
            if (pages.Count == 0)
            {
                return;
            }

            var top = _dst.Reserve();
            _dst.Set(top, new PdfDictionary
            {
                ["Title"] = new PdfString(TextString.Encode(name)),
                ["Dest"] = new PdfArray(new PdfObject[] { new PdfReference(pages[0], 0), PdfName.Get("Fit") }),
            });
            if (items.Count > 0)
            {
                LinkOutline(items, top, open: false);
            }

            _outlineItems.Add(top);
        }
        else
        {
            _outlineItems.AddRange(items);
        }
    }

    private List<int> CopyOutlineLevel(PdfDocument doc, SourceCopier copier, Dictionary<string, string> renames, PdfObject? first, int depth, HashSet<int> visited, ref int count, int index)
    {
        var result = new List<int>();
        var node = first;
        while (node is not null && depth <= _lib.MaxNestingDepth)
        {
            _ct.ThrowIfCancellationRequested();
            if ((node is PdfReference r && !visited.Add(r.Id.Number)) || doc.ResolveDictionary(node) is not { } item || ++count > _lib.MaxObjectCount)
            {
                break;
            }

            var copy = new PdfDictionary { ["Title"] = item["Title"] is { } title ? copier.Copy(title) : new PdfString(Array.Empty<byte>()) };
            var titleText = doc.Resolve(item["Title"]) is PdfString ts ? TextString.Decode(ts.Value) : "";
            if (item["Dest"] is { } dest)
            {
                if (RemapDestination(copier.Copy(dest), renames) is { } d)
                {
                    copy["Dest"] = d;
                }
                else
                {
                    Removed.Add($"The bookmark \"{titleText}\" of input {index + 1} went to a page that is not in the output; its destination was removed.");
                }
            }
            else if (doc.ResolveDictionary(item["A"]) is { } action)
            {
                var copiedAction = (PdfDictionary)copier.Copy(action);
                if ((copiedAction["S"] as PdfName)?.Value == "GoTo")
                {
                    if (RemapDestination(copiedAction["D"], renames) is { } d)
                    {
                        copiedAction["D"] = d;
                        copy["A"] = copiedAction;
                    }
                    else
                    {
                        Removed.Add($"The bookmark \"{titleText}\" of input {index + 1} went to a page that is not in the output; its destination was removed.");
                    }
                }
                else
                {
                    copy["A"] = copiedAction;
                }
            }

            foreach (var key in new[] { "C", "F" })
            {
                if (item[key] is { } v)
                {
                    copy[key] = copier.Copy(v);
                }
            }

            var number = _dst.Add(copy);
            var children = CopyOutlineLevel(doc, copier, renames, item["First"], depth + 1, visited, ref count, index);
            if (children.Count > 0)
            {
                LinkOutline(children, number, open: doc.Resolve(item["Count"]) is PdfInteger { Value: > 0 });
            }

            result.Add(number);
            node = item["Next"];
        }

        return result;
    }

    /// <summary>
    /// Links outline items as the children of <paramref name="parent"/> (or of a new outline root, whose number is
    /// returned): /Parent, /Prev, /Next on the items, /First, /Last and /Count on the parent.
    /// </summary>
    private int LinkOutline(List<int> items, int? parent, bool open = true)
    {
        var parentNumber = parent ?? _dst.Add(new PdfDictionary { [PdfName.Type] = PdfName.Get("Outlines") });
        for (var i = 0; i < items.Count; i++)
        {
            var item = (PdfDictionary)_dst.Get(items[i])!;
            item["Parent"] = new PdfReference(parentNumber, 0);
            if (i > 0)
            {
                item["Prev"] = new PdfReference(items[i - 1], 0);
            }

            if (i < items.Count - 1)
            {
                item["Next"] = new PdfReference(items[i + 1], 0);
            }
        }

        var parentDict = (PdfDictionary)_dst.Get(parentNumber)!;
        parentDict["First"] = new PdfReference(items[0], 0);
        parentDict["Last"] = new PdfReference(items[^1], 0);
        var visible = items.Count + items.Sum(n => _dst.Get(n) is PdfDictionary d && d["Count"] is PdfInteger { Value: > 0 } c ? (int)c.Value : 0);
        parentDict["Count"] = new PdfInteger(parent is null || open ? visible : -items.Count);
        return parentNumber;
    }

    // ------------------------------------------------------------------ form

    private void CopyForm(PdfDocument doc, SourceCopier copier, int index)
    {
        if (doc.ResolveDictionary(doc.Catalog!["AcroForm"]) is not { } form)
        {
            return;
        }

        _needAppearances |= doc.Resolve(form["NeedAppearances"]) is PdfBoolean { Value: true };
        _da ??= form["DA"] is { } da ? copier.Copy(da) : null;
        if (form.ContainsKey("XFA"))
        {
            Warnings.Add("XFA form data is not kept; the AcroForm fields are.");
        }

        if (doc.ResolveDictionary(form["DR"]) is { } dr && copier.Copy(dr) is PdfDictionary resources)
        {
            foreach (var (category, value) in resources.Entries)
            {
                if (value is not PdfDictionary entries)
                {
                    _resources[category] ??= value;
                    continue;
                }

                var merged = _resources[category] as PdfDictionary ?? new PdfDictionary();
                foreach (var (name, resource) in entries.Entries)
                {
                    merged[name] ??= resource;
                }

                _resources[category] = merged;
            }
        }

        var visited = new HashSet<int>();
        if (doc.Resolve(form["Fields"]) is PdfArray fields)
        {
            foreach (var field in fields.Items)
            {
                if (CopyField(doc, copier, field, null, 0, visited, index) is { } copied)
                {
                    _fields.Add(copied);
                }
            }
        }

        copier.Drain(_ct);
    }

    private PdfReference? CopyField(PdfDocument doc, SourceCopier copier, PdfObject node, int? parent, int depth, HashSet<int> visited, int index)
    {
        if (depth > _lib.MaxNestingDepth || node is not PdfReference r || !visited.Add(r.Id.Number) || doc.ResolveDictionary(node) is not { } dict)
        {
            return null;
        }

        if (copier.Widgets.Contains(r.Id.Number))
        {
            // A widget (possibly merged with its field): kept if its page was copied.
            if (!copier.Map.TryGetValue(r.Id.Number, out var mapped) || _dst.Get(mapped) is not PdfDictionary widget)
            {
                return null;
            }

            if (parent is int p)
            {
                widget["Parent"] = new PdfReference(p, 0);
            }
            else
            {
                RenameTopLevel(widget, index);
            }

            return new PdfReference(mapped, 0);
        }

        var target = _dst.Reserve();
        var kids = doc.Resolve(dict["Kids"]) as PdfArray;
        var newKids = new PdfArray();
        foreach (var kid in kids?.Items ?? new List<PdfObject>())
        {
            if (CopyField(doc, copier, kid, target, depth + 1, visited, index) is { } k)
            {
                newKids.Add(k);
            }
        }

        if (kids is { Count: > 0 } && newKids.Count == 0)
        {
            return null; // every widget was on a page that is not in the output
        }

        var copy = dict.ShallowClone();
        copy.Remove(PdfName.Get("Kids"));
        copy.Remove(PdfName.Get("Parent"));
        var field = (PdfDictionary)copier.Copy(copy);
        if (newKids.Count > 0)
        {
            field["Kids"] = newKids;
        }

        if (parent is int pp)
        {
            field["Parent"] = new PdfReference(pp, 0);
        }
        else
        {
            RenameTopLevel(field, index);
        }

        _dst.Set(target, field);
        return new PdfReference(target, 0);
    }

    private void RenameTopLevel(PdfDictionary field, int index)
    {
        if (field["T"] is not PdfString t)
        {
            return;
        }

        var name = TextString.Decode(t.Value);
        var unique = UniqueName(name, _usedFieldNames);
        if (unique != name)
        {
            field["T"] = new PdfString(TextString.Encode(unique));
            Renamed.Add($"The form field \"{name}\" of input {index + 1} was renamed to \"{unique}\".");
        }
    }

    // ------------------------------------------------------------------ attachments

    private void CopyAttachments(PdfDocument doc, SourceCopier copier, int index)
    {
        foreach (var (key, value) in NameTree.Read(doc, doc.ResolveDictionary(doc.Catalog!["Names"])?["EmbeddedFiles"], _lib.MaxNestingDepth, _lib.MaxObjectCount))
        {
            var name = TextString.Decode(key);
            var unique = UniqueName(name, _usedAttachmentNames);
            if (unique != name)
            {
                Renamed.Add($"The attachment \"{name}\" of input {index + 1} was renamed to \"{unique}\".");
            }

            _attachments.Add((unique == name ? key : TextString.Encode(unique), copier.Copy(value)));
        }

        copier.Drain(_ct);
    }
}
