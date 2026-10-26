using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Filters;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;
using PdfUtil.Internal.Text;

namespace PdfUtil.Internal.Protection;

/// <summary>Document-level attachments (F10): the <c>/EmbeddedFiles</c> name tree, file specifications and embedded file streams.</summary>
internal sealed class AttachmentEngine
{
    private readonly PdfUtilOptions _lib;
    private readonly PdfLog _log;

    public AttachmentEngine(PdfUtilOptions lib, PdfLog log)
    {
        _lib = lib;
        _log = log;
    }

    public static void Validate(PdfAddAttachmentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(options.Name))
        {
            throw new ArgumentException("Name is required.", nameof(options));
        }

        if (options.Content is null)
        {
            throw new ArgumentException("Content is required.", nameof(options));
        }

        if (options.MimeType is { } mime && (mime.Length is 0 or > 255 || !mime.Contains('/') || mime.Any(c => c <= ' ' || c > '~')))
        {
            throw new ArgumentException("MimeType must be a MIME type such as 'application/xml'.", nameof(options));
        }

        if (options.Relationship is { } relationship && !Enum.IsDefined(relationship))
        {
            throw new ArgumentException("Relationship is not a defined value.", nameof(options));
        }

        EditEngine.ValidateSaveMode(options.SaveMode, options.Write, nameof(options));
    }

    public static void Validate(PdfRemoveAttachmentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(options.Name))
        {
            throw new ArgumentException("Name is required.", nameof(options));
        }

        EditEngine.ValidateSaveMode(options.SaveMode, options.Write, nameof(options));
    }

    public IReadOnlyList<PdfAttachmentInfo> List(PdfSource source, string? password, CancellationToken ct)
    {
        using var doc = PdfDocument.Open(source, _lib, _log, strict: false, ct);
        Unlock(doc, password);
        var pages = PageTreeReader.ReadPageNodes(doc, _lib.MaxNestingDepth, ct);
        return new DetailReader(doc, pages, _lib, ct).ReadAttachments();
    }

    public PdfEditResult Add(PdfSource input, Stream buffer, PdfAddAttachmentOptions options, CancellationToken ct)
    {
        Validate(options);
        using var doc = PdfDocument.Open(input, _lib, _log, strict: false, ct);
        if (options.SaveMode == PdfSaveMode.Incremental)
        {
            IncrementalEngine.EnsureSupported(doc);
        }

        using var security = EditEngine.KeepSecurity(doc, options.Password);
        var (catalogNumber, catalog) = Catalog(doc);
        var entries = Entries(doc, catalog);
        var existing = entries.Where(e => TextString.Decode(e.Key) == options.Name).ToList();
        if (existing.Count > 0 && !options.ReplaceExisting)
        {
            throw new ArgumentException($"The document already has an attachment named \"{options.Name}\". Set ReplaceExisting to replace it.", nameof(options));
        }

        entries.RemoveAll(existing.Contains);
        var warnings = new List<string>();
        var edits = new ObjectEdits(doc);
        var now = DateTimeOffset.Now;
        var content = options.Content;
        var parameters = new PdfDictionary
        {
            [PdfName.Size] = new PdfInteger(content.Length),
            ["CreationDate"] = new PdfString(Encoding.ASCII.GetBytes(PdfDate.Format(options.CreationDate ?? now))),
            ["ModDate"] = new PdfString(Encoding.ASCII.GetBytes(PdfDate.Format(options.ModificationDate ?? now))),
            ["CheckSum"] = new PdfString(MD5.HashData(content), isHex: true),
        };
        var fileDict = new PdfDictionary { [PdfName.Type] = PdfName.Get("EmbeddedFile") };
        if (options.MimeType is { } mime)
        {
            fileDict["Subtype"] = PdfName.Get(mime);
        }

        fileDict["Params"] = parameters;
        var data = content;
        if (options.Compress)
        {
            fileDict[PdfName.Filter] = PdfName.FlateDecode;
            data = Deflate(content);
        }

        var fileRef = new PdfReference(edits.Add(new PdfStream(fileDict, data)), 0);
        var fileSpec = new PdfDictionary
        {
            [PdfName.Type] = PdfName.Get("Filespec"),
            ["F"] = new PdfString(Encoding.ASCII.GetBytes(AsciiFileName(options.Name))),
            ["UF"] = new PdfString(TextString.Encode(options.Name)),
            ["EF"] = new PdfDictionary { ["F"] = fileRef, ["UF"] = fileRef },
        };
        if (options.Description is { } description)
        {
            fileSpec["Desc"] = new PdfString(TextString.Encode(description));
        }

        if (options.Relationship is { } relationship)
        {
            fileSpec["AFRelationship"] = PdfName.Get(relationship.ToString());
        }

        var fileSpecRef = new PdfReference(edits.Add(fileSpec), 0);
        entries.Add((TextString.Encode(options.Name), fileSpecRef));
        var treeRef = new PdfReference(edits.Add(NameTree.Build(entries)), 0);
        UpdateCatalog(doc, edits, catalogNumber, catalog, treeRef, existing.Select(e => e.Value), options.Relationship is null ? null : fileSpecRef);

        var pdfA = SafeXmp(doc);
        if (pdfA?.PdfAPart is 1 or 2)
        {
            warnings.Add($"{pdfA.PdfALevel} does not allow arbitrary embedded files; the output may no longer be PDF/A conformant.");
        }
        else if (pdfA?.PdfAPart == 3 && options.Relationship is null)
        {
            warnings.Add("PDF/A-3 requires every embedded file to have a relationship to the document; set Relationship to keep the output conformant.");
        }

        if (existing.Count > 0)
        {
            warnings.Add($"The existing attachment \"{options.Name}\" was replaced.");
        }

        return Rewrite(doc, security, edits, buffer, options.Progress, options.Write, options.SaveMode, warnings, ct);
    }

    public PdfEditResult Remove(PdfSource input, Stream buffer, PdfRemoveAttachmentOptions options, CancellationToken ct)
    {
        Validate(options);
        using var doc = PdfDocument.Open(input, _lib, _log, strict: false, ct);
        if (options.SaveMode == PdfSaveMode.Incremental)
        {
            IncrementalEngine.EnsureSupported(doc);
        }

        using var security = EditEngine.KeepSecurity(doc, options.Password);
        var (catalogNumber, catalog) = Catalog(doc);
        var entries = Entries(doc, catalog);
        var matches = entries.Where(e => TextString.Decode(e.Key) == options.Name).ToList();
        if (matches.Count == 0)
        {
            matches = entries.Where(e => DetailReader.FileNameOf(doc, e.Value) == options.Name).ToList();
        }

        if (matches.Count == 0)
        {
            throw new PdfAttachmentNotFoundException($"The document has no attachment named \"{options.Name}\".");
        }

        entries.RemoveAll(matches.Contains);
        var edits = new ObjectEdits(doc);
        var treeRef = entries.Count == 0 ? null : new PdfReference(edits.Add(NameTree.Build(entries)), 0);
        UpdateCatalog(doc, edits, catalogNumber, catalog, treeRef, matches.Select(m => m.Value), null);
        var removeWarnings = new List<string>();
        if (options.SaveMode == PdfSaveMode.Incremental)
        {
            removeWarnings.Add("The attachment was removed in an incremental update, so its data is still in the file's earlier revision. Use PdfSaveMode.Rewrite to remove it for good.");
        }

        return Rewrite(doc, security, edits, buffer, options.Progress, options.Write, options.SaveMode, removeWarnings, ct);
    }

    public byte[] Extract(PdfSource source, string name, string? password, CancellationToken ct)
    {
        using var doc = PdfDocument.Open(source, _lib, _log, strict: false, ct);
        Unlock(doc, password);
        var (_, catalog) = Catalog(doc);
        var entries = Entries(doc, catalog);
        var fileSpec = entries.FirstOrDefault(e => TextString.Decode(e.Key) == name).Value
            ?? entries.FirstOrDefault(e => DetailReader.FileNameOf(doc, e.Value) == name).Value
            ?? FindAnnotationAttachment(doc, name, ct)
            ?? throw new PdfAttachmentNotFoundException($"The document has no attachment named \"{name}\".");
        var embedded = DetailReader.EmbeddedStreamOf(doc, fileSpec)
            ?? throw new PdfAttachmentNotFoundException($"The attachment \"{name}\" refers to an external file; its data is not embedded in the document.");
        var (stream, number) = embedded;
        var data = doc.GetStreamData(stream, new ObjectId(number, doc.GetGeneration(number)));
        var filters = StreamFilters.GetFilterNames(stream.Dictionary, doc.Resolve).Where(f => f != "Crypt").ToList();
        if (filters.Count == 0)
        {
            return data;
        }

        return StreamFilters.DecodeFlateChain(data, stream.Dictionary, doc.Resolve, _lib.MaxDecompressedStreamBytes)
            ?? throw new PdfUtilException($"The attachment \"{name}\" is encoded with {string.Join(", ", filters)}, which PdfUtil does not decode.");
    }

    // ------------------------------------------------------------------ helpers

    private static void Unlock(PdfDocument doc, string? password)
    {
        if (doc.IsEncrypted && doc.Authenticate(password) == PdfPasswordMatch.None)
        {
            throw new PdfInvalidPasswordException(password is null ? "The document needs a password to open." : "The password does not open the document.");
        }
    }

    private static (int Number, PdfDictionary Catalog) Catalog(PdfDocument doc)
    {
        var catalogRef = doc.Trailer[PdfName.Root] as PdfReference ?? throw new PdfCorruptException("The trailer has no /Root reference.");
        var catalog = doc.GetObject(catalogRef.Id.Number) as PdfDictionary ?? throw new PdfCorruptException("The document catalog cannot be read.");
        return (catalogRef.Id.Number, catalog);
    }

    private List<(byte[] Key, PdfObject Value)> Entries(PdfDocument doc, PdfDictionary catalog) =>
        NameTree.Read(doc, doc.ResolveDictionary(catalog["Names"])?["EmbeddedFiles"], _lib.MaxNestingDepth, _lib.MaxObjectCount);

    private PdfObject? FindAnnotationAttachment(PdfDocument doc, string name, CancellationToken ct)
    {
        foreach (var page in PageTreeReader.ReadPageNodes(doc, _lib.MaxNestingDepth, ct))
        {
            if (doc.Resolve(page.Page["Annots"]) is not PdfArray annots)
            {
                continue;
            }

            foreach (var item in annots.Items)
            {
                if (doc.ResolveDictionary(item) is { } annot && (doc.Resolve(annot["Subtype"]) as PdfName)?.Value == "FileAttachment" && DetailReader.FileNameOf(doc, annot["FS"]) == name)
                {
                    return annot["FS"];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Points the catalog's <c>/Names /EmbeddedFiles</c> at <paramref name="tree"/> (or removes it), and updates the
    /// associated files (<c>/AF</c>): removed file specifications are taken out, <paramref name="addToAf"/> is added.
    /// </summary>
    private static void UpdateCatalog(PdfDocument doc, ObjectEdits edits, int catalogNumber, PdfDictionary catalog, PdfReference? tree, IEnumerable<PdfObject> removed, PdfReference? addToAf)
    {
        var copy = catalog.ShallowClone();
        var names = doc.ResolveDictionary(catalog["Names"])?.ShallowClone() ?? new PdfDictionary();
        if (tree is null)
        {
            names.Remove(PdfName.Get("EmbeddedFiles"));
        }
        else
        {
            names["EmbeddedFiles"] = tree;
        }

        if (names.Count == 0)
        {
            copy.Remove(PdfName.Get("Names"));
        }
        else if (catalog["Names"] is PdfReference namesRef)
        {
            edits.Replace(namesRef.Id.Number, names);
        }
        else
        {
            copy["Names"] = names;
        }

        var removedNumbers = removed.OfType<PdfReference>().Select(r => r.Id.Number).ToHashSet();
        var af = doc.Resolve(catalog["AF"]) as PdfArray;
        if ((af is not null && af.Items.Any(i => i is PdfReference r && removedNumbers.Contains(r.Id.Number))) || addToAf is not null)
        {
            var newAf = new PdfArray((af?.Items ?? new List<PdfObject>()).Where(i => !(i is PdfReference r && removedNumbers.Contains(r.Id.Number))));
            if (addToAf is not null)
            {
                newAf.Add(addToAf);
            }

            if (newAf.Count == 0)
            {
                copy.Remove(PdfName.Get("AF"));
            }
            else
            {
                copy["AF"] = newAf;
            }
        }

        edits.Replace(catalogNumber, copy);
    }

    private PdfEditResult Rewrite(PdfDocument doc, OutputSecurity security, ObjectEdits edits, Stream buffer, IProgress<PdfProgress>? progress, PdfWriteOptions? write, PdfSaveMode mode, List<string> warnings, CancellationToken ct)
    {
        if (mode == PdfSaveMode.Incremental)
        {
            var update = new IncrementalEngine(_lib, _log).Append(doc, new IncrementalSettings
            {
                Progress = new ProgressReporter(progress),
                Edits = edits,
                MinimumVersion = new PdfVersion(1, 3),
                InitialWarnings = warnings,
            }, buffer, ct);
            return new PdfEditResult(update.SourceVersion.ToString(), update.OutputVersion.ToString(), PdfEncryptionAlgorithm.None, 0, false, false, update.Warnings)
            {
                SaveMode = PdfSaveMode.Incremental,
            };
        }

        var outcome = new RewriteEngine(_lib, _log).Rewrite(doc, new RewriteSettings
        {
            Security = security,
            Progress = new ProgressReporter(progress),
            Edits = edits,
            MinimumVersion = new PdfVersion(1, 3), // embedded files are a PDF 1.3 feature
            InitialWarnings = warnings,
            UseObjectStreams = write?.UseObjectStreams == true,
        }, buffer, ct);
        var handler = doc.Security;
        return new PdfEditResult(outcome.SourceVersion.ToString(), outcome.OutputVersion.ToString(), handler?.Algorithm ?? PdfEncryptionAlgorithm.None, handler?.R ?? 0, false, false, outcome.Warnings);
    }

    private static PdfXmpMetadata? SafeXmp(PdfDocument doc)
    {
        try
        {
            return XmpReader.Read(doc);
        }
        catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException)
        {
            return null;
        }
    }

    /// <summary>The <c>/F</c> file name: a byte string, so characters outside printable ASCII become '_'. <c>/UF</c> carries the full name.</summary>
    private static string AsciiFileName(string name) => new(name.Select(c => c is >= ' ' and <= '~' ? c : '_').ToArray());

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }
}
