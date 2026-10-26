using PdfUtil.Internal.Document;
using PdfUtil.Internal.Filters;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;
using PdfUtil.Internal.Text;

namespace PdfUtil.Internal.Analysis;

/// <summary>
/// Builds a <see cref="PdfValidationReport"/>: reads the file with the recovery reader (so a damaged file is reported,
/// not rejected), loads every object, and checks references, stream lengths and compression, dates, the page tree and
/// the encryption dictionary.
/// </summary>
internal sealed class Validator
{
    private readonly PdfUtilOptions _options;
    private readonly PdfLog _log;

    public Validator(PdfUtilOptions options, PdfLog log)
    {
        _options = options;
        _log = log;
    }

    private sealed class IssueList
    {
        private readonly int _max;
        private readonly List<PdfValidationIssue> _issues = new();
        private readonly HashSet<(string, string, int?)> _seen = new();
        private bool _truncated;

        public IssueList(int max) => _max = Math.Max(1, max);

        public void Add(PdfValidationSeverity severity, string code, string message, int? objectNumber = null)
        {
            if (!_seen.Add((code, message, objectNumber)))
            {
                return;
            }

            if (_issues.Count >= _max)
            {
                _truncated = true;
                return;
            }

            _issues.Add(new PdfValidationIssue(severity, code, message, objectNumber));
        }

        public IReadOnlyList<PdfValidationIssue> Sorted()
        {
            var result = _issues.Select((issue, index) => (issue, index)).OrderByDescending(x => x.issue.Severity).ThenBy(x => x.index).Select(x => x.issue).ToList();
            if (_truncated)
            {
                result.Add(new PdfValidationIssue(PdfValidationSeverity.Info, PdfValidationCodes.TooManyIssues, $"More than {_max} issues were found; the rest were not reported."));
            }

            return result;
        }
    }

    public PdfValidationReport Validate(PdfSource source, PdfValidationOptions options, CancellationToken ct)
    {
        var issues = new IssueList(options.MaxIssues);
        var progress = new ProgressReporter(options.Progress);
        PdfDocument doc;
        try
        {
            doc = PdfDocument.Open(source, _options, _log, strict: false, ct);
        }
        catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException)
        {
            issues.Add(PdfValidationSeverity.Error, PdfValidationCodes.Unreadable, ex.Message);
            return new PdfValidationReport(false, null, 0, issues.Sorted());
        }

        using (doc)
        {
            var contentReadable = CheckSecurity(doc, options.Password, issues);
            CheckTrailer(doc, issues);

            var objectCount = 0;
            int? pageCount = null;
            if (contentReadable)
            {
                objectCount = CheckObjects(doc, options.CheckStreams, issues, progress, ct);
                pageCount = CheckPageTree(doc, issues, ct);
                CheckXmp(doc, issues);
            }

            if (doc.IncrementalUpdateCount > 0)
            {
                issues.Add(PdfValidationSeverity.Info, PdfValidationCodes.IncrementalUpdates, $"The file has {doc.IncrementalUpdateCount} incremental update(s); earlier revisions are still in the file.");
            }

            // Problems the reader found (and repaired) while loading.
            foreach (var d in doc.Diagnostics)
            {
                issues.Add(SeverityOf(d.Code), d.Code, d.Message, d.ObjectNumber);
            }

            return new PdfValidationReport(true, pageCount, objectCount, issues.Sorted());
        }
    }

    private static PdfValidationSeverity SeverityOf(string code) => code switch
    {
        PdfValidationCodes.HeaderJunk => PdfValidationSeverity.Warning,
        PdfValidationCodes.PageMediaBoxMissing => PdfValidationSeverity.Warning,
        _ => PdfValidationSeverity.Error,
    };

    // ------------------------------------------------------------------ encryption

    /// <summary>Checks the encryption dictionary; returns whether objects can be checked (not encrypted, or authenticated).</summary>
    private static bool CheckSecurity(PdfDocument doc, string? password, IssueList issues)
    {
        if (!doc.IsEncrypted)
        {
            return true;
        }

        var encryptNumber = doc.EncryptObjectNumber;
        if (doc.GetPermanentId() is null)
        {
            issues.Add(PdfValidationSeverity.Error, PdfValidationCodes.MissingId, "The document is encrypted but its trailer has no valid /ID; the key is derived from an empty identifier, which some readers reject.");
        }

        if (doc.GetEncryptDictionary() is not { } encrypt)
        {
            issues.Add(PdfValidationSeverity.Error, PdfValidationCodes.EncryptionInconsistent, "The /Encrypt dictionary cannot be read.", encryptNumber);
            return false;
        }

        CheckEncryptDictionary(doc, encrypt, encryptNumber, issues);
        try
        {
            doc.InitializeSecurity();
        }
        catch (PdfUnsupportedEncryptionException ex)
        {
            issues.Add(PdfValidationSeverity.Info, PdfValidationCodes.EncryptedContentNotChecked, ex.Message + " Objects and streams were not checked.", encryptNumber);
            return false;
        }
        catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException)
        {
            issues.Add(PdfValidationSeverity.Error, PdfValidationCodes.EncryptionInconsistent, ex.Message, encryptNumber);
            return false;
        }

        if (doc.Authenticate(password) != PdfPasswordMatch.None)
        {
            return true;
        }

        if (password is not null)
        {
            throw new PdfInvalidPasswordException("The password does not open the document.");
        }

        issues.Add(PdfValidationSeverity.Info, PdfValidationCodes.EncryptedContentNotChecked, "The document needs a password to open, so only its structure was checked. Pass the password to check objects and streams.");
        return false;
    }

    private static void CheckEncryptDictionary(PdfDocument doc, PdfDictionary encrypt, int? number, IssueList issues)
    {
        void Problem(string message) => issues.Add(PdfValidationSeverity.Error, PdfValidationCodes.EncryptionInconsistent, message, number);
        long? Int(string key) => doc.Resolve(encrypt[key]) is PdfInteger i ? i.Value : null;
        int? Len(string key) => doc.Resolve(encrypt[key]) is PdfString s ? s.Value.Length : null;

        if ((doc.Resolve(encrypt["Filter"]) as PdfName)?.Value != "Standard")
        {
            return; // other handlers are reported when the handler is initialized
        }

        var v = Int("V") ?? 0;
        var r = Int("R");
        if (r is null)
        {
            Problem("The /Encrypt dictionary has no /R (revision).");
            return;
        }

        if (Int("P") is null)
        {
            Problem("The /Encrypt dictionary has no /P (permissions).");
        }

        var expectedHash = r >= 5 ? 48 : 32;
        foreach (var key in new[] { "O", "U" })
        {
            if (Len(key) is not int len)
            {
                Problem($"The /Encrypt dictionary has no /{key} string.");
            }
            else if (r >= 5 ? len < expectedHash : len != expectedHash)
            {
                Problem($"/{key} is {len} bytes; revision {r} requires {expectedHash}.");
            }
        }

        var expectedV = r switch
        {
            2 => new long[] { 1 },
            3 => new long[] { 2, 3 },
            4 => new long[] { 4 },
            5 or 6 => new long[] { 5 },
            _ => Array.Empty<long>(),
        };
        if (expectedV.Length == 0)
        {
            Problem($"Revision {r} is not defined by the Standard Security Handler.");
        }
        else if (!expectedV.Contains(v) && !(r == 2 && v == 0))
        {
            Problem($"/V {v} is inconsistent with /R {r}.");
        }

        if (v is 2 or 3 && Int("Length") is long length && (length < 40 || length > 128 || length % 8 != 0))
        {
            Problem($"/Length {length} is not a multiple of 8 between 40 and 128.");
        }

        if (r >= 5)
        {
            foreach (var (key, size) in new[] { ("OE", 32), ("UE", 32), ("Perms", 16) })
            {
                if (Len(key) != size)
                {
                    Problem($"Revision {r} requires a {size}-byte /{key}.");
                }
            }
        }

        if (v >= 4)
        {
            var cf = doc.ResolveDictionary(encrypt["CF"]);
            foreach (var key in new[] { "StmF", "StrF", "EFF" })
            {
                var name = (doc.Resolve(encrypt[key]) as PdfName)?.Value;
                if (name is null or "Identity")
                {
                    continue;
                }

                if (doc.ResolveDictionary(cf?[name]) is not { } filter)
                {
                    Problem($"/{key} names the crypt filter /{name}, which is not in /CF.");
                    continue;
                }

                var method = (doc.Resolve(filter["CFM"]) as PdfName)?.Value;
                var allowed = v >= 5 ? new[] { "AESV3", "None" } : new[] { "V2", "AESV2", "None" };
                if (method is not null && !allowed.Contains(method))
                {
                    Problem($"The crypt filter /{name} uses /CFM /{method}, which is not valid for /V {v}.");
                }
            }
        }

        if (encrypt["EncryptMetadata"] is { } em && doc.Resolve(em) is not PdfBoolean)
        {
            Problem("/EncryptMetadata is not a boolean.");
        }
    }

    // ------------------------------------------------------------------ trailer

    private static void CheckTrailer(PdfDocument doc, IssueList issues)
    {
        var maxNumber = doc.Xref.Where(e => e.Value.Type != XrefEntryType.Free).Select(e => e.Key).DefaultIfEmpty(0).Max();
        if (!doc.WasRepaired && doc.Trailer[PdfName.Size] is PdfInteger size && size.Value <= maxNumber)
        {
            issues.Add(PdfValidationSeverity.Warning, PdfValidationCodes.TrailerSizeWrong, $"The trailer /Size is {size.Value}, but the file uses object number {maxNumber}.");
        }

        PdfVersion.TryParse(doc.HeaderVersion, out var header);
        if (!doc.IsEncrypted && doc.GetPermanentId() is null && header >= PdfVersion.V2_0)
        {
            issues.Add(PdfValidationSeverity.Warning, PdfValidationCodes.MissingId, "PDF 2.0 requires a trailer /ID, but the file has none.");
        }

        if (doc.Trailer[PdfName.Info] is PdfReference info && doc.GetObject(info.Id.Number) is null)
        {
            issues.Add(PdfValidationSeverity.Warning, PdfValidationCodes.DanglingReference, $"The trailer /Info refers to object {info.Id.Number}, which does not exist.");
        }
    }

    // ------------------------------------------------------------------ objects

    private int CheckObjects(PdfDocument doc, bool checkStreams, IssueList issues, ProgressReporter progress, CancellationToken ct)
    {
        var numbers = doc.Xref.Where(e => e.Value.Type != XrefEntryType.Free).Select(e => e.Key).OrderBy(n => n).ToList();
        var present = new HashSet<int>();
        var loaded = new List<(int Number, PdfObject Object)>();
        progress.Report(PdfProgressStage.Reading, 0, numbers.Count * 2, force: true);
        for (var i = 0; i < numbers.Count; i++)
        {
            if ((i & 0xFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            var number = numbers[i];
            try
            {
                if (doc.GetObject(number) is { } obj)
                {
                    present.Add(number);
                    loaded.Add((number, obj));
                }
            }
            catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException)
            {
                present.Add(number); // unreadable, but it exists: references to it are not dangling
                issues.Add(PdfValidationSeverity.Error, PdfValidationCodes.ObjectUnreadable, ex.Message, number);
            }

            progress.Report(PdfProgressStage.Reading, i + 1, numbers.Count * 2);
        }

        for (var i = 0; i < loaded.Count; i++)
        {
            if ((i & 0xFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            var (number, obj) = loaded[i];
            Scan(doc, obj, number, present, issues, 0);
            if (checkStreams && obj is PdfStream stream)
            {
                CheckStream(doc, stream, number, issues);
            }

            progress.Report(PdfProgressStage.Reading, numbers.Count + i + 1, numbers.Count * 2);
        }

        progress.Report(PdfProgressStage.Reading, numbers.Count * 2, numbers.Count * 2, force: true);
        return loaded.Count;
    }

    private static readonly HashSet<string> DateKeys = new(StringComparer.Ordinal) { "CreationDate", "ModDate" };

    /// <summary>Walks one object: dangling references and invalid dates.</summary>
    private void Scan(PdfDocument doc, PdfObject obj, int owner, HashSet<int> present, IssueList issues, int depth)
    {
        if (depth > _options.MaxNestingDepth)
        {
            return;
        }

        switch (obj)
        {
            case PdfReference r:
                if (!present.Contains(r.Id.Number))
                {
                    issues.Add(PdfValidationSeverity.Warning, PdfValidationCodes.DanglingReference, $"Object {owner} refers to object {r.Id.Number}, which does not exist (readers treat it as null).", owner);
                }

                break;
            case PdfArray a:
                foreach (var item in a.Items)
                {
                    Scan(doc, item, owner, present, issues, depth + 1);
                }

                break;
            case PdfStream s:
                Scan(doc, s.Dictionary, owner, present, issues, depth + 1);
                break;
            case PdfDictionary d:
                var isDated = (d[PdfName.Type] as PdfName)?.Value is "Annot" or "Sig" or "DocTimeStamp" || d.ContainsKey("Subtype") && d.ContainsKey("Rect");
                foreach (var (key, value) in d.Entries)
                {
                    if (value is PdfString date && (DateKeys.Contains(key.Value) || (key.Value == "M" && isDated)))
                    {
                        var text = TextString.Decode(date.Value);
                        if (PdfDate.Check(text) is { } reason)
                        {
                            issues.Add(PdfValidationSeverity.Warning, PdfValidationCodes.InvalidDate, $"The /{key.Value} date of object {owner} is invalid: {reason}.", owner);
                        }
                    }

                    Scan(doc, value, owner, present, issues, depth + 1);
                }

                break;
        }
    }

    private void CheckStream(PdfDocument doc, PdfStream stream, int number, IssueList issues)
    {
        var filters = StreamFilters.GetFilterNames(stream.Dictionary, doc.Resolve).Where(f => f != "Crypt").ToList();
        if (filters.Count == 0 || filters[0] is not ("FlateDecode" or "Fl"))
        {
            return;
        }

        byte[] data;
        try
        {
            data = doc.GetStreamData(stream, new ObjectId(number, doc.GetGeneration(number)));
        }
        catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException)
        {
            issues.Add(PdfValidationSeverity.Error, PdfValidationCodes.ObjectUnreadable, ex.Message, number);
            return;
        }

        try
        {
            var decoded = StreamFilters.FlateDecode(data, _options.MaxDecompressedStreamBytes, strict: true);
            if (!HasMatchingChecksum(data, decoded))
            {
                issues.Add(PdfValidationSeverity.Warning, PdfValidationCodes.FlateDecodeTruncated, $"The FlateDecode stream in object {number} is truncated or its checksum does not match; viewers may show only part of it.", number);
            }
        }
        catch (InvalidDataException)
        {
            try
            {
                var partial = StreamFilters.FlateDecode(data, _options.MaxDecompressedStreamBytes, strict: false);
                issues.Add(
                    partial.Length > 0 ? PdfValidationSeverity.Warning : PdfValidationSeverity.Error,
                    partial.Length > 0 ? PdfValidationCodes.FlateDecodeTruncated : PdfValidationCodes.FlateDecodeFailed,
                    partial.Length > 0
                        ? $"The FlateDecode stream in object {number} is damaged (truncated, trailing data or no zlib header); only {partial.Length} bytes decompress."
                        : $"The FlateDecode stream in object {number} cannot be decompressed.",
                    number);
            }
            catch (InvalidDataException)
            {
                issues.Add(PdfValidationSeverity.Error, PdfValidationCodes.FlateDecodeFailed, $"The FlateDecode stream in object {number} cannot be decompressed.", number);
            }
            catch (PdfLimitExceededException)
            {
                issues.Add(PdfValidationSeverity.Info, PdfValidationCodes.StreamNotChecked, $"The stream in object {number} decompresses to more than the configured limit, so it was not checked.", number);
            }
        }
        catch (PdfLimitExceededException)
        {
            issues.Add(PdfValidationSeverity.Info, PdfValidationCodes.StreamNotChecked, $"The stream in object {number} decompresses to more than the configured limit, so it was not checked.", number);
        }
    }

    /// <summary>
    /// The zlib stream ends with the Adler-32 checksum of the uncompressed data (RFC 1950). .NET's inflater does not
    /// check it, so a truncated stream would otherwise pass. Trailing end-of-line bytes are tolerated.
    /// </summary>
    private static bool HasMatchingChecksum(byte[] data, byte[] decoded)
    {
        uint a = 1, b = 0;
        foreach (var x in decoded)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }

        var adler = (b << 16) | a;
        var end = data.Length;
        for (var trimmed = 0; trimmed <= 2 && end >= 6; trimmed++)
        {
            var stored = ((uint)data[end - 4] << 24) | ((uint)data[end - 3] << 16) | ((uint)data[end - 2] << 8) | data[end - 1];
            if (stored == adler)
            {
                return true;
            }

            if (data[end - 1] is not ((byte)'\n' or (byte)'\r' or (byte)' '))
            {
                break;
            }

            end--;
        }

        return false;
    }

    // ------------------------------------------------------------------ page tree

    private int? CheckPageTree(PdfDocument doc, IssueList issues, CancellationToken ct)
    {
        var catalogNumber = (doc.Trailer[PdfName.Root] as PdfReference)?.Id.Number;
        if (doc.Catalog is not { } catalog)
        {
            issues.Add(PdfValidationSeverity.Error, PdfValidationCodes.CatalogInvalid, "The document catalog cannot be read.", catalogNumber);
            return null;
        }

        if ((doc.Resolve(catalog[PdfName.Type]) as PdfName)?.Value != "Catalog")
        {
            issues.Add(PdfValidationSeverity.Warning, PdfValidationCodes.CatalogInvalid, "The document catalog is not /Type /Catalog.", catalogNumber);
        }

        if (doc.ResolveDictionary(catalog["Pages"]) is null)
        {
            issues.Add(PdfValidationSeverity.Error, PdfValidationCodes.PageTreeMissing, "The catalog has no /Pages page tree.", catalogNumber);
            return 0;
        }

        var visited = new HashSet<int>();
        CheckPageNode(doc, catalog["Pages"]!, null, 0, visited, issues, ct);
        return PageTreeReader.ReadPageNodes(doc, _options.MaxNestingDepth, ct).Count;
    }

    /// <summary>Checks one page tree node; returns the number of pages below it.</summary>
    private int CheckPageNode(PdfDocument doc, PdfObject nodeRef, int? parent, int depth, HashSet<int> visited, IssueList issues, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var number = (nodeRef as PdfReference)?.Id.Number;
        var where = number is int n ? $"Page tree node {n}" : parent is int p ? $"A direct page tree node in object {p}" : "The page tree root";
        if (depth > _options.MaxNestingDepth || (number is int num && !visited.Add(num)))
        {
            return 0; // cycles are reported by the page tree reader
        }

        if (doc.ResolveDictionary(nodeRef) is not { } node)
        {
            issues.Add(PdfValidationSeverity.Error, PdfValidationCodes.PageTreeNodeInvalid, parent is int pp ? $"Object {pp} has a /Kids entry that is not a page or page tree node." : "The page tree root is not a dictionary.", parent);
            return 0;
        }

        if (parent is int expectedParent && (node["Parent"] as PdfReference)?.Id.Number != expectedParent)
        {
            issues.Add(PdfValidationSeverity.Warning, PdfValidationCodes.PageTreeNodeInvalid, $"{where} does not have /Parent {expectedParent}.", number ?? parent);
        }

        var type = (doc.Resolve(node[PdfName.Type]) as PdfName)?.Value;
        var kids = doc.Resolve(node["Kids"]) as PdfArray;
        if (type == "Page" || (type is null && kids is null))
        {
            if (type is null)
            {
                issues.Add(PdfValidationSeverity.Warning, PdfValidationCodes.PageTreeNodeInvalid, $"{where} has no /Type; it was treated as a page.", number ?? parent);
            }

            return 1;
        }

        if (type is not ("Pages" or null))
        {
            issues.Add(PdfValidationSeverity.Error, PdfValidationCodes.PageTreeNodeInvalid, $"{where} has /Type /{type}; expected /Pages or /Page.", number ?? parent);
            return 0;
        }

        if (type is null)
        {
            issues.Add(PdfValidationSeverity.Warning, PdfValidationCodes.PageTreeNodeInvalid, $"{where} has /Kids but no /Type /Pages.", number ?? parent);
        }

        var count = 0;
        foreach (var kid in kids?.Items ?? new List<PdfObject>())
        {
            count += CheckPageNode(doc, kid, number ?? parent, depth + 1, visited, issues, ct);
        }

        var declared = doc.Resolve(node["Count"]) as PdfInteger;
        if (declared is null || declared.Value != count)
        {
            issues.Add(PdfValidationSeverity.Error, PdfValidationCodes.PageTreeCountWrong, $"{where} declares /Count {(declared is null ? "(missing)" : declared.Value)}, but {count} page(s) were found below it.", number ?? parent);
        }

        return count;
    }

    private static void CheckXmp(PdfDocument doc, IssueList issues)
    {
        try
        {
            if (XmpReader.Read(doc) is { IsWellFormed: false })
            {
                issues.Add(PdfValidationSeverity.Warning, PdfValidationCodes.XmpMalformed, "The XMP metadata is not well-formed XML.", (doc.Catalog?["Metadata"] as PdfReference)?.Id.Number);
            }
        }
        catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException)
        {
            issues.Add(PdfValidationSeverity.Warning, PdfValidationCodes.XmpMalformed, "The XMP metadata stream cannot be read: " + ex.Message, (doc.Catalog?["Metadata"] as PdfReference)?.Id.Number);
        }
    }
}
