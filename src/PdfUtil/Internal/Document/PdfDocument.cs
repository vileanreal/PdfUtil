using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PdfUtil.Internal.Filters;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;
using PdfUtil.Internal.Security;

namespace PdfUtil.Internal.Document;

internal enum XrefEntryType
{
    Free,
    InFile,
    Compressed,
}

internal readonly record struct XrefEntry(XrefEntryType Type, long Offset, int Generation, int StreamNumber, int StreamIndex)
{
    public static XrefEntry Free(int generation) => new(XrefEntryType.Free, 0, generation, 0, 0);

    public static XrefEntry InFile(long offset, int generation) => new(XrefEntryType.InFile, offset, generation, 0, 0);

    public static XrefEntry Compressed(int streamNumber, int index) => new(XrefEntryType.Compressed, 0, 0, streamNumber, index);
}

/// <summary>
/// A parsed PDF file: header, merged cross-reference information, trailer, lazy object loading (with object
/// streams), recovery for damaged files, and transparent decryption once a password has been authenticated.
/// One instance per operation; not thread-safe.
/// </summary>
internal sealed class PdfDocument : IDisposable
{
    private static ReadOnlySpan<byte> HeaderMarker => "%PDF-"u8;

    private readonly PdfSource _source;
    private readonly PdfLexer _lexer;
    private readonly PdfParser _parser;
    private readonly PdfUtilOptions _options;
    private readonly PdfLog _log;
    private readonly bool _strict;
    private readonly CancellationToken _ct;
    private readonly Dictionary<int, XrefEntry> _xref = new();
    private readonly Dictionary<int, PdfObject?> _cache = new();
    private readonly Dictionary<int, ObjectStreamContent> _objectStreams = new();
    private readonly HashSet<int> _loading = new();
    private readonly List<string> _warnings = new();
    private readonly List<DocumentDiagnostic> _diagnostics = new();
    private readonly List<int> _pendingRecoveredObjectStreams = new();
    private Dictionary<int, XrefEntry>? _recoveryIndex;
    private int? _encryptObjectNumber;

    /// <summary>Object number of the /Encrypt dictionary, if it is indirect.</summary>
    public int? EncryptObjectNumber => _encryptObjectNumber;

    private PdfDocument(PdfSource source, PdfUtilOptions options, PdfLog log, bool strict, CancellationToken ct)
    {
        _source = source;
        _options = options;
        _log = log;
        _strict = strict;
        _ct = ct;
        _lexer = new PdfLexer(source);
        _parser = new PdfParser(_lexer, options.MaxNestingDepth);
        Trailer = new PdfDictionary();
    }

    public PdfSource Source => _source;

    public string HeaderVersion { get; private set; } = "1.4";

    public long HeaderOffset { get; private set; }

    public PdfDictionary Trailer { get; private set; }

    public IReadOnlyDictionary<int, XrefEntry> Xref => _xref;

    public int XrefSectionCount { get; private set; }

    /// <summary>The <c>startxref</c> offset of the latest revision (meaningful only when <see cref="WasRepaired"/> is false).</summary>
    public long StartXref { get; private set; } = -1;

    /// <summary>Whether the latest cross-reference section is a cross-reference stream (not a table or a hybrid table).</summary>
    public bool LatestSectionIsStream { get; private set; }

    /// <summary>The <c>/XRefStm</c> offset of the latest section, when it is a hybrid table whose stream was read.</summary>
    public long? LatestXrefStm { get; private set; }

    public bool HasXrefStreams { get; private set; }

    public bool HasObjectStreams { get; private set; }

    public bool WasRepaired { get; private set; }

    public bool IsLinearized { get; private set; }

    public int? LinearizationObjectNumber { get; private set; }

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Every problem found while reading, with a validation code and the object concerned (capped at <see cref="MaxDiagnostics"/>).</summary>
    public IReadOnlyList<DocumentDiagnostic> Diagnostics => _diagnostics;

    public const int MaxDiagnostics = 10_000;

    public StandardSecurityHandler? Security { get; private set; }

    public bool IsEncrypted => Trailer.ContainsKey(PdfName.Encrypt);

    public int IncrementalUpdateCount => Math.Max(0, XrefSectionCount - 1 - (IsLinearized && XrefSectionCount >= 2 ? 1 : 0));

    public long MaxDecompressedBytes => _options.MaxDecompressedStreamBytes;

    /// <summary>Opens a document. In strict mode no repair is attempted and any inconsistency throws.</summary>
    public static PdfDocument Open(PdfSource source, PdfUtilOptions options, PdfLog log, bool strict, CancellationToken ct)
    {
        if (source.Length == 0)
        {
            throw new PdfCorruptException("The input is empty.");
        }

        if (source.Length > options.MaxFileSizeBytes)
        {
            throw new PdfLimitExceededException($"The input is larger than the configured maximum of {options.MaxFileSizeBytes} bytes.");
        }

        var doc = new PdfDocument(source, options, log, strict, ct);
        try
        {
            doc.ReadHeader();
            doc.LoadCrossReference();
            doc.DetectLinearization();
            if (doc.Trailer[PdfName.Encrypt] is PdfReference er)
            {
                doc._encryptObjectNumber = er.Id.Number;
            }

            return doc;
        }
        catch
        {
            doc.Dispose();
            throw;
        }
    }

    /// <summary>Records a problem: the message once in <see cref="Warnings"/>, and every occurrence (with its object) in <see cref="Diagnostics"/>.</summary>
    public void AddWarning(string message, string? code = null, int? objectNumber = null)
    {
        if (code is not null && _diagnostics.Count < MaxDiagnostics)
        {
            _diagnostics.Add(new DocumentDiagnostic(code, message, objectNumber));
        }

        if (!_warnings.Contains(message))
        {
            _warnings.Add(message);
            _log.Warn(message);
        }
    }

    // ------------------------------------------------------------------ header

    private void ReadHeader()
    {
        var pos = _source.IndexOf(HeaderMarker, 0, Math.Min(_source.Length, 1024 + HeaderMarker.Length));
        if (pos < 0)
        {
            throw new PdfCorruptException("The input is not a PDF file: no '%PDF-' header was found in the first 1024 bytes.");
        }

        HeaderOffset = pos;
        Span<byte> buf = stackalloc byte[8];
        var n = _source.ReadAt(pos + HeaderMarker.Length, buf);
        var sb = new StringBuilder();
        for (var i = 0; i < n && (char.IsAsciiDigit((char)buf[i]) || buf[i] == '.'); i++)
        {
            sb.Append((char)buf[i]);
        }

        HeaderVersion = PdfVersion.TryParse(sb.ToString(), out var v) ? v.ToString() : "1.4";
        if (pos > 0)
        {
            AddWarning($"{pos} bytes of data precede the %PDF- header.", PdfValidationCodes.HeaderJunk);
        }
    }

    // ------------------------------------------------------------------ cross-reference

    private void LoadCrossReference()
    {
        try
        {
            var startXref = FindStartXref();
            if (startXref < 0)
            {
                throw new PdfCorruptException("The 'startxref' keyword is missing.");
            }

            StartXref = startXref;
            ReadXrefChain(startXref);
            ValidateRoot();
        }
        catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException && !_strict)
        {
            _log.Warn("Cross-reference information is damaged; rebuilding it by scanning the file.");
            Recover();
        }
    }

    private void ValidateRoot()
    {
        if (Trailer[PdfName.Root] is not PdfReference root)
        {
            throw new PdfCorruptException("The trailer has no /Root entry.");
        }

        if (!_xref.TryGetValue(root.Id.Number, out var e) || e.Type == XrefEntryType.Free)
        {
            throw new PdfCorruptException("The document catalog is not in the cross-reference table.");
        }

        // Without a password the catalog of an encrypted file may sit in an (encrypted) object stream.
        if (!IsEncrypted && GetObject(root.Id.Number) is not PdfDictionary)
        {
            throw new PdfCorruptException("The document catalog cannot be read.");
        }
    }

    private long FindStartXref()
    {
        var tailStart = Math.Max(0, _source.Length - 4096);
        var pos = _source.LastIndexOf("startxref"u8, tailStart, _source.Length);
        if (pos < 0)
        {
            return -1;
        }

        _lexer.Position = pos + 9;
        var offset = _lexer.TryReadInteger();
        return offset is >= 0 ? offset.Value : -1;
    }

    private void ReadXrefChain(long startXref)
    {
        var visited = new HashSet<long>();
        long? offset = startXref;
        var first = true;
        while (offset is long off)
        {
            _ct.ThrowIfCancellationRequested();
            if (!visited.Add(off))
            {
                AddWarning("The /Prev chain of cross-reference sections contains a loop; it was cut.", PdfValidationCodes.XrefPrevLoop);
                break;
            }

            if (visited.Count > _options.MaxXrefSections)
            {
                throw new PdfLimitExceededException("Too many cross-reference sections.");
            }

            var section = ReadSectionAt(off);
            XrefSectionCount++;
            if (first)
            {
                LatestSectionIsStream = section.IsStream;
                Trailer = section.Trailer.ShallowClone();
                Trailer.Remove(PdfName.Prev);
                Trailer.Remove(PdfName.XRefStm);
                first = false;
            }
            else
            {
                foreach (var key in new[] { PdfName.Root, PdfName.Info, PdfName.ID, PdfName.Encrypt, PdfName.Size })
                {
                    if (!Trailer.ContainsKey(key) && section.Trailer[key] is { } value)
                    {
                        Trailer[key] = value;
                    }
                }
            }

            // Hybrid-reference files: in-use table entries, then the /XRefStm entries, then free table entries.
            foreach (var (num, entry) in section.Entries)
            {
                if (entry.Type != XrefEntryType.Free)
                {
                    _xref.TryAdd(num, entry);
                }
            }

            if (!section.IsStream && section.Trailer[PdfName.XRefStm] is PdfInteger xrefStm)
            {
                try
                {
                    var hidden = ReadSectionAt(xrefStm.Value);
                    if (hidden.IsStream)
                    {
                        foreach (var (num, entry) in hidden.Entries)
                        {
                            _xref.TryAdd(num, entry);
                        }

                        if (XrefSectionCount == 1)
                        {
                            LatestXrefStm = xrefStm.Value;
                        }
                    }
                }
                catch (PdfCorruptException) when (!_strict)
                {
                    AddWarning("The /XRefStm cross-reference stream of a hybrid file is damaged and was ignored.", PdfValidationCodes.XrefStreamDamaged);
                }
            }

            foreach (var (num, entry) in section.Entries)
            {
                if (entry.Type == XrefEntryType.Free)
                {
                    _xref.TryAdd(num, entry);
                }
            }

            if (_xref.Count > _options.MaxObjectCount)
            {
                throw new PdfLimitExceededException("The document has more objects than the configured maximum.");
            }

            offset = section.Trailer[PdfName.Prev] switch
            {
                PdfInteger p => p.Value,
                PdfReal r => (long)r.Value,
                _ => null,
            };
        }
    }

    private sealed record XrefSection(List<(int Number, XrefEntry Entry)> Entries, PdfDictionary Trailer, bool IsStream);

    private XrefSection ReadSectionAt(long offset)
    {
        var section = TryReadSectionAt(offset);
        if (section is null && HeaderOffset > 0 && !_strict)
        {
            section = TryReadSectionAt(offset + HeaderOffset);
        }

        return section ?? throw new PdfCorruptException($"No cross-reference section at offset {offset}.");
    }

    private XrefSection? TryReadSectionAt(long offset)
    {
        if (offset < 0 || offset >= _source.Length)
        {
            return null;
        }

        _lexer.Position = offset;
        var saved = _lexer.Position;
        var t = _lexer.Next();
        if (t.IsKeyword("xref"))
        {
            return ReadClassicTable();
        }

        _lexer.Position = saved;
        if (_parser.TryReadObjectHeader(out var id))
        {
            var obj = ParseObjectBody(id, allowLengthResolution: false);
            if (obj is PdfStream stream && (stream.Dictionary[PdfName.Type] as PdfName)?.Value == "XRef")
            {
                HasXrefStreams = true;
                return ReadXrefStream(stream);
            }
        }

        return null;
    }

    private XrefSection ReadClassicTable()
    {
        var entries = new List<(int, XrefEntry)>();
        var firstSubsection = true;
        while (true)
        {
            _ct.ThrowIfCancellationRequested();
            var saved = _lexer.Position;
            var t = _lexer.Next();
            if (t.IsKeyword("trailer"))
            {
                break;
            }

            if (t.Type != TokenType.Integer)
            {
                throw new PdfCorruptException("Malformed cross-reference table.");
            }

            var countToken = _lexer.Next();
            if (countToken.Type != TokenType.Integer || t.Integer < 0 || countToken.Integer < 0)
            {
                // Some files have a subsection header without the count: treat as the end.
                _lexer.Position = saved;
                throw new PdfCorruptException("Malformed cross-reference subsection header.");
            }

            var start = t.Integer;
            var count = countToken.Integer;
            if (start + count > _options.MaxObjectCount)
            {
                throw new PdfLimitExceededException("The cross-reference table declares more objects than the configured maximum.");
            }

            for (long i = 0; i < count; i++)
            {
                // Tokenised reading accepts 19-, 20- and 21-byte entries and any whitespace.
                var offTok = _lexer.Next();
                var genTok = _lexer.Next();
                var typeTok = _lexer.Next();
                if (offTok.Type != TokenType.Integer || genTok.Type != TokenType.Integer || typeTok.Type != TokenType.Keyword)
                {
                    throw new PdfCorruptException("Malformed cross-reference entry.");
                }

                // Known writer bug: a single subsection starting at 1 whose first entry is the free head of the list.
                if (firstSubsection && i == 0 && start == 1 && typeTok.Text == "f" && offTok.Integer == 0 && genTok.Integer == 65535)
                {
                    start = 0;
                }

                var num = (int)(start + i);
                var gen = (int)Math.Clamp(genTok.Integer, 0, 65535);
                switch (typeTok.Text)
                {
                    case "n":
                        // An in-use entry with offset 0 is invalid; treat it as free.
                        entries.Add((num, offTok.Integer > 0 ? XrefEntry.InFile(offTok.Integer, gen) : XrefEntry.Free(gen)));
                        break;
                    case "f":
                        entries.Add((num, XrefEntry.Free(gen)));
                        break;
                    default:
                        throw new PdfCorruptException("Malformed cross-reference entry type.");
                }
            }

            firstSubsection = false;
        }

        if (_parser.ParseObject() is not PdfDictionary trailer)
        {
            throw new PdfCorruptException("The trailer is not a dictionary.");
        }

        return new XrefSection(entries, trailer, IsStream: false);
    }

    private XrefSection ReadXrefStream(PdfStream stream)
    {
        var dict = stream.Dictionary;
        var data = StreamFilters.DecodeFlateChain(_source.ReadBytes(stream.SourceOffset, stream.SourceLength), dict, o => o, _options.MaxDecompressedStreamBytes, _strict)
            ?? throw new PdfCorruptException("A cross-reference stream uses an unsupported filter.");
        if (dict["W"] is not PdfArray wArray || wArray.Count < 3)
        {
            throw new PdfCorruptException("A cross-reference stream has no valid /W entry.");
        }

        var w = new int[3];
        for (var i = 0; i < 3; i++)
        {
            w[i] = wArray[i] is PdfInteger wi && wi.Value is >= 0 and <= 8 ? (int)wi.Value : throw new PdfCorruptException("Invalid /W entry.");
        }

        var size = dict[PdfName.Size] is PdfInteger s ? s.Value : 0;
        var index = new List<(long Start, long Count)>();
        if (dict["Index"] is PdfArray ia && ia.Count >= 2)
        {
            for (var i = 0; i + 1 < ia.Count; i += 2)
            {
                if (ia[i] is PdfInteger a && ia[i + 1] is PdfInteger b)
                {
                    index.Add((a.Value, b.Value));
                }
            }
        }
        else
        {
            index.Add((0, size));
        }

        var entries = new List<(int, XrefEntry)>();
        var rowLength = w[0] + w[1] + w[2];
        var pos = 0;
        foreach (var (start, count) in index)
        {
            if (start < 0 || count < 0 || start + count > _options.MaxObjectCount)
            {
                throw new PdfLimitExceededException("The cross-reference stream declares more objects than the configured maximum.");
            }

            for (long i = 0; i < count; i++)
            {
                if (rowLength == 0 || pos + rowLength > data.Length)
                {
                    break;
                }

                var type = w[0] == 0 ? 1 : ReadField(data, pos, w[0]);
                var f2 = ReadField(data, pos + w[0], w[1]);
                var f3 = ReadField(data, pos + w[0] + w[1], w[2]);
                pos += rowLength;
                var num = (int)(start + i);
                switch (type)
                {
                    case 0:
                        entries.Add((num, XrefEntry.Free((int)Math.Clamp(f3, 0, 65535))));
                        break;
                    case 1:
                        entries.Add((num, f2 > 0 ? XrefEntry.InFile(f2, (int)Math.Clamp(f3, 0, 65535)) : XrefEntry.Free(0)));
                        break;
                    case 2:
                        HasObjectStreams = true;
                        entries.Add((num, XrefEntry.Compressed((int)Math.Clamp(f2, 0, int.MaxValue), (int)Math.Clamp(f3, 0, int.MaxValue))));
                        break;
                }
            }
        }

        return new XrefSection(entries, dict, IsStream: true);
    }

    private static long ReadField(byte[] data, int offset, int width)
    {
        long v = 0;
        for (var i = 0; i < width; i++)
        {
            v = (v << 8) | data[offset + i];
        }

        return v;
    }

    // ------------------------------------------------------------------ recovery

    private void Recover()
    {
        if (_strict)
        {
            throw new PdfCorruptException("The cross-reference information is invalid.");
        }

        WasRepaired = true;
        AddWarning("The cross-reference information was damaged and has been reconstructed.", PdfValidationCodes.XrefReconstructed);
        _xref.Clear();
        _cache.Clear();
        _objectStreams.Clear();
        Trailer = new PdfDictionary();
        var index = ScanForObjects(out var trailerOffsets);
        foreach (var (num, entry) in index)
        {
            _xref[num] = entry;
        }

        // Merge candidate trailers (classic trailers and cross-reference stream dictionaries) in file order.
        var candidates = new List<(long Offset, PdfDictionary Dict)>();
        foreach (var off in trailerOffsets)
        {
            try
            {
                _lexer.Position = off + 7;
                if (_parser.ParseObject() is PdfDictionary d)
                {
                    candidates.Add((off, d));
                }
            }
            catch (PdfCorruptException)
            {
                // ignore unreadable trailer
            }
        }

        var catalogs = new List<int>();
        foreach (var (num, entry) in _xref.OrderBy(e => e.Value.Offset))
        {
            _ct.ThrowIfCancellationRequested();
            PdfObject? obj;
            try
            {
                obj = GetObject(num);
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
            var type = (dict?[PdfName.Type] as PdfName)?.Value;
            switch (type)
            {
                case "XRef":
                    HasXrefStreams = true;
                    candidates.Add((entry.Offset, dict!));
                    break;
                case "ObjStm":
                    HasObjectStreams = true;
                    _pendingRecoveredObjectStreams.Add(num);
                    break;
                case "Catalog":
                    catalogs.Add(num);
                    break;
            }
        }

        foreach (var (_, dict) in candidates.OrderBy(c => c.Offset))
        {
            foreach (var key in new[] { PdfName.Root, PdfName.Info, PdfName.ID, PdfName.Encrypt })
            {
                if (dict[key] is { } value)
                {
                    Trailer[key] = value;
                }
            }
        }

        XrefSectionCount = Math.Max(1, candidates.Count);
        if (Trailer[PdfName.Encrypt] is PdfReference er)
        {
            _encryptObjectNumber = er.Id.Number;
        }

        if (!IsEncrypted)
        {
            IndexPendingObjectStreams();
        }

        var rootOk = Trailer[PdfName.Root] is PdfReference root && (IsEncrypted || GetObject(root.Id.Number) is PdfDictionary);
        if (!rootOk)
        {
            if (catalogs.Count == 0)
            {
                foreach (var num in _xref.Keys.ToList())
                {
                    if (GetObject(num) is PdfDictionary d && (d[PdfName.Type] as PdfName)?.Value == "Catalog")
                    {
                        catalogs.Add(num);
                    }
                }
            }

            if (catalogs.Count == 0)
            {
                throw new PdfCorruptException("The document catalog could not be found; the file is damaged beyond repair.");
            }

            var cat = catalogs[^1];
            Trailer[PdfName.Root] = new PdfReference(cat, _xref[cat].Generation);
        }

        Trailer[PdfName.Size] = new PdfInteger(_xref.Count == 0 ? 1 : _xref.Keys.Max() + 1);
    }

    /// <summary>Scans the whole file for "<c>n g obj</c>" headers (later definitions win) and "trailer" keywords.</summary>
    private Dictionary<int, XrefEntry> ScanForObjects(out List<long> trailerOffsets)
    {
        const int chunk = 1 << 20;
        const int lookBehind = 48;
        var result = new Dictionary<int, XrefEntry>();
        trailerOffsets = new List<long>();
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(chunk + lookBehind + 16);
        try
        {
            for (long windowStart = 0; windowStart < _source.Length; windowStart += chunk)
            {
                _ct.ThrowIfCancellationRequested();
                var readStart = Math.Max(0, windowStart - lookBehind);
                var n = _source.ReadAt(readStart, buffer.AsSpan(0, (int)Math.Min(chunk + lookBehind + 16, _source.Length - readStart)));
                var span = buffer.AsSpan(0, n);
                var searchFrom = (int)(windowStart - readStart);
                var searchTo = (int)Math.Min(n, searchFrom + chunk);

                for (var i = searchFrom; i < searchTo; i++)
                {
                    var b = span[i];
                    if (b == 'o' && i + 2 < n && span[i + 1] == 'b' && span[i + 2] == 'j' && (i + 3 >= n || !PdfLexer.IsRegular(span[i + 3])))
                    {
                        if (TryBacktrackObjectHeader(span, i, out var numStart, out var num, out var gen))
                        {
                            result[num] = XrefEntry.InFile(readStart + numStart, gen);
                            if (result.Count > _options.MaxObjectCount)
                            {
                                throw new PdfLimitExceededException("The document has more objects than the configured maximum.");
                            }
                        }
                    }
                    else if (b == 't' && i + 7 <= n && span.Slice(i, 7).SequenceEqual("trailer"u8) && (i == 0 || !PdfLexer.IsRegular(span[i - 1])))
                    {
                        trailerOffsets.Add(readStart + i);
                    }
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        return result;
    }

    private static bool TryBacktrackObjectHeader(ReadOnlySpan<byte> span, int objIndex, out int numStart, out int num, out int gen)
    {
        numStart = 0;
        num = 0;
        gen = 0;
        var p = objIndex - 1;
        if (p < 0 || !PdfLexer.IsWhitespace(span[p]))
        {
            return false;
        }

        while (p >= 0 && PdfLexer.IsWhitespace(span[p]))
        {
            p--;
        }

        var genEnd = p;
        while (p >= 0 && span[p] >= '0' && span[p] <= '9')
        {
            p--;
        }

        if (p == genEnd || genEnd - p > 5 || p < 0 || !PdfLexer.IsWhitespace(span[p]))
        {
            return false;
        }

        var genStart = p + 1;
        while (p >= 0 && PdfLexer.IsWhitespace(span[p]))
        {
            p--;
        }

        var numEnd = p;
        while (p >= 0 && span[p] >= '0' && span[p] <= '9')
        {
            p--;
        }

        if (p == numEnd || numEnd - p > 10)
        {
            return false;
        }

        numStart = p + 1;
        if (!int.TryParse(Encoding.ASCII.GetString(span.Slice(numStart, numEnd - p)), NumberStyles.None, CultureInfo.InvariantCulture, out num)
            || !int.TryParse(Encoding.ASCII.GetString(span.Slice(genStart, genEnd - genStart + 1)), NumberStyles.None, CultureInfo.InvariantCulture, out gen)
            || gen > 65535)
        {
            return false;
        }

        return true;
    }

    private void IndexPendingObjectStreams()
    {
        if (_pendingRecoveredObjectStreams.Count == 0 || (IsEncrypted && Security?.IsAuthenticated != true))
        {
            return;
        }

        var pending = _pendingRecoveredObjectStreams.ToList();
        _pendingRecoveredObjectStreams.Clear();
        foreach (var stmNum in pending)
        {
            try
            {
                var content = GetObjectStream(stmNum);
                for (var i = 0; i < content.Numbers.Length; i++)
                {
                    _xref.TryAdd(content.Numbers[i], XrefEntry.Compressed(stmNum, i));
                }
            }
            catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException)
            {
                AddWarning("An object stream could not be read during repair.", PdfValidationCodes.ObjectStreamDamaged, stmNum);
            }
        }
    }

    private void DetectLinearization()
    {
        try
        {
            // Skip the rest of the header line, then any comment lines (such as the binary marker).
            _source.Position = HeaderOffset;
            var b = _source.Peek();
            while (b >= 0 && b != '\r' && b != '\n')
            {
                _source.Position++;
                b = _source.Peek();
            }

            _lexer.SkipWhitespaceAndComments();
            if (_parser.TryReadObjectHeader(out var id) && _parser.ParseObject() is PdfDictionary d && d.ContainsKey("Linearized"))
            {
                IsLinearized = true;
                LinearizationObjectNumber = id.Number;
            }
        }
        catch (PdfCorruptException)
        {
            // Not linearized, or unreadable first object.
        }
    }

    // ------------------------------------------------------------------ objects

    public PdfObject? Resolve(PdfObject? obj) => obj is PdfReference r ? GetObject(r.Id.Number) : obj;

    public PdfDictionary? ResolveDictionary(PdfObject? obj) => Resolve(obj) switch
    {
        PdfDictionary d => d,
        PdfStream s => s.Dictionary,
        _ => null,
    };

    public PdfDictionary? Catalog => Resolve(Trailer[PdfName.Root]) as PdfDictionary;

    /// <summary>Loads (and caches) an object by number, decrypting its strings when the document is authenticated.</summary>
    public PdfObject? GetObject(int number)
    {
        if (_cache.TryGetValue(number, out var cached))
        {
            return cached;
        }

        if (!_loading.Add(number))
        {
            return null; // circular dependency (e.g. a /Length that refers back to its own stream)
        }

        try
        {
            var entry = LookupEntry(number);
            PdfObject? obj = entry?.Type switch
            {
                XrefEntryType.InFile => LoadInFile(number, entry.Value),
                XrefEntryType.Compressed => LoadCompressed(number, entry.Value),
                _ => null,
            };
            _cache[number] = obj;
            return obj;
        }
        finally
        {
            _loading.Remove(number);
        }
    }

    public int GetGeneration(int number) => _xref.TryGetValue(number, out var e) && e.Type == XrefEntryType.InFile ? e.Generation : 0;

    /// <summary>Clears the object cache (needed after authentication, so that strings are decrypted).</summary>
    public void ClearCache()
    {
        _cache.Clear();
        _objectStreams.Clear();
    }

    private XrefEntry? LookupEntry(int number)
    {
        if (_xref.TryGetValue(number, out var e))
        {
            return e;
        }

        if (_pendingRecoveredObjectStreams.Count > 0)
        {
            IndexPendingObjectStreams();
            if (_xref.TryGetValue(number, out e))
            {
                return e;
            }
        }

        return null;
    }

    private PdfObject? LoadInFile(int number, XrefEntry entry)
    {
        var result = TryParseIndirectAt(entry.Offset, number);
        if (result is null && !_strict)
        {
            if (HeaderOffset > 0)
            {
                result = TryParseIndirectAt(entry.Offset + HeaderOffset, number);
            }

            if (result is null)
            {
                _recoveryIndex ??= ScanForObjects(out _);
                if (_recoveryIndex.TryGetValue(number, out var recovered) && recovered.Offset != entry.Offset)
                {
                    result = TryParseIndirectAt(recovered.Offset, number);
                }
            }

            if (result is not null)
            {
                WasRepaired = true;
                AddWarning("Some cross-reference offsets were wrong and have been corrected.", PdfValidationCodes.XrefOffsetWrong, number);
            }
        }

        if (result is null)
        {
            if (_strict)
            {
                throw new PdfCorruptException($"Object {number} is not at the offset given by the cross-reference table.");
            }

            AddWarning("Some objects referenced by the cross-reference table are missing.", PdfValidationCodes.ObjectMissing, number);
            return null;
        }

        var (id, obj) = result.Value;
        return DecryptLoadedObject(obj, id);
    }

    private (ObjectId Id, PdfObject Obj)? TryParseIndirectAt(long offset, int expectedNumber)
    {
        if (offset < 0 || offset >= _source.Length)
        {
            return null;
        }

        try
        {
            _lexer.Position = offset;
            if (!_parser.TryReadObjectHeader(out var id) || id.Number != expectedNumber)
            {
                return null;
            }

            return (id, ParseObjectBody(id, allowLengthResolution: true));
        }
        catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException && !_strict)
        {
            return null;
        }
    }

    /// <summary>Parses the object after "<c>n g obj</c>", including a stream body.</summary>
    private PdfObject ParseObjectBody(ObjectId id, bool allowLengthResolution)
    {
        var obj = _parser.ParseObject();
        if (obj is not PdfDictionary dict)
        {
            return obj;
        }

        var saved = _lexer.Position;
        var t = _lexer.Next();
        if (!t.IsKeyword("stream"))
        {
            _lexer.Position = saved;
            return dict;
        }

        // The keyword "stream" is followed by CRLF or LF (a lone CR is tolerated, as are spaces before the EOL).
        var p = t.Position + 6;
        while (_source.PeekAt(p) == ' ')
        {
            p++;
        }

        if (_source.PeekAt(p) == '\r')
        {
            p++;
            if (_source.PeekAt(p) == '\n')
            {
                p++;
            }
        }
        else if (_source.PeekAt(p) == '\n')
        {
            p++;
        }

        var dataStart = p;
        long length = -1;
        var lengthObj = dict[PdfName.Length];
        if (lengthObj is PdfReference && allowLengthResolution)
        {
            lengthObj = GetObject(((PdfReference)lengthObj).Id.Number);
        }

        if (lengthObj is PdfInteger li)
        {
            length = li.Value;
        }
        else if (lengthObj is PdfReal lr)
        {
            length = (long)lr.Value;
        }

        if (length < 0 || dataStart + length > _source.Length || !IsEndStreamAt(dataStart + length))
        {
            if (_strict)
            {
                throw new PdfCorruptException($"The /Length of the stream in object {id.Number} is wrong.");
            }

            var end = _source.IndexOf("endstream"u8, dataStart, _source.Length);
            if (end < 0)
            {
                end = _source.Length;
            }

            var actual = end - dataStart;
            // Remove the end-of-line marker that precedes "endstream".
            if (actual > 0 && _source.PeekAt(dataStart + actual - 1) == '\n')
            {
                actual--;
            }

            if (actual > 0 && _source.PeekAt(dataStart + actual - 1) == '\r')
            {
                actual--;
            }

            if (length >= 0)
            {
                AddWarning("A stream had a wrong /Length; the actual length was determined by locating 'endstream'.", PdfValidationCodes.StreamLengthWrong, id.Number);
            }

            length = Math.Max(0, actual);
        }

        _lexer.Position = dataStart + length;
        return new PdfStream(dict, dataStart, length);
    }

    private bool IsEndStreamAt(long pos)
    {
        var p = pos;
        while (p < _source.Length && p < pos + 4 && PdfLexer.IsWhitespace(_source.PeekAt(p)))
        {
            p++;
        }

        Span<byte> buf = stackalloc byte[9];
        var n = _source.ReadAt(p, buf);
        return (n == 9 && buf.SequenceEqual("endstream"u8)) || (n >= 6 && buf[..6].SequenceEqual("endobj"u8));
    }

    private PdfObject? LoadCompressed(int number, XrefEntry entry)
    {
        var content = GetObjectStream(entry.StreamNumber);
        var idx = entry.StreamIndex;
        if (idx >= content.Numbers.Length || content.Numbers[idx] != number)
        {
            idx = Array.IndexOf(content.Numbers, number);
            if (idx < 0)
            {
                if (_strict)
                {
                    throw new PdfCorruptException($"Object {number} is missing from its object stream.");
                }

                AddWarning("An object stream does not contain an object that the cross-reference data points to.", PdfValidationCodes.ObjectMissing, number);
                return null;
            }
        }

        var source = PdfSource.FromBytes(content.Data);
        using (source)
        using (var lexer = new PdfLexer(source))
        {
            lexer.Position = content.First + content.Offsets[idx];
            var parser = new PdfParser(lexer, _options.MaxNestingDepth);
            // Objects inside object streams are not individually encrypted: the stream as a whole was.
            return parser.ParseObject();
        }
    }

    private sealed record ObjectStreamContent(byte[] Data, int First, int[] Numbers, long[] Offsets);

    private ObjectStreamContent GetObjectStream(int streamNumber)
    {
        if (_objectStreams.TryGetValue(streamNumber, out var content))
        {
            return content;
        }

        if (GetObject(streamNumber) is not PdfStream stream)
        {
            throw new PdfCorruptException($"Object stream {streamNumber} is missing.");
        }

        var dict = stream.Dictionary;
        var data = GetDecodedStreamData(stream, new ObjectId(streamNumber, GetGeneration(streamNumber)))
            ?? throw new PdfCorruptException("An object stream uses an unsupported filter.");
        var n = Resolve(dict["N"]) is PdfInteger ni ? ni.Value : 0;
        var first = Resolve(dict["First"]) is PdfInteger fi ? fi.Value : 0;
        if (n < 0 || n > _options.MaxObjectCount || first < 0 || first > data.Length)
        {
            throw new PdfCorruptException("An object stream has an invalid header.");
        }

        var numbers = new List<int>();
        var offsets = new List<long>();
        using (var source = PdfSource.FromBytes(data))
        using (var lexer = new PdfLexer(source))
        {
            for (var i = 0; i < n; i++)
            {
                var a = lexer.Next();
                var b = lexer.Next();
                if (a.Type != TokenType.Integer || b.Type != TokenType.Integer || lexer.Position > first + 1)
                {
                    break;
                }

                numbers.Add((int)Math.Clamp(a.Integer, 0, int.MaxValue));
                offsets.Add(b.Integer);
            }
        }

        content = new ObjectStreamContent(data, (int)first, numbers.ToArray(), offsets.ToArray());
        _objectStreams[streamNumber] = content;
        return content;
    }

    // ------------------------------------------------------------------ streams

    /// <summary>The stream bytes exactly as stored (still encrypted if the file is).</summary>
    public byte[] GetRawStreamData(PdfStream stream)
    {
        if (stream.Data is not null)
        {
            return stream.Data;
        }

        if (stream.SourceLength > _options.MaxFileSizeBytes)
        {
            throw new PdfLimitExceededException("A stream is larger than the configured maximum file size.");
        }

        return _source.ReadBytes(stream.SourceOffset, stream.SourceLength);
    }

    /// <summary>The stream bytes after decryption (filters other than Crypt are not applied).</summary>
    public byte[] GetStreamData(PdfStream stream, ObjectId id)
    {
        var raw = GetRawStreamData(stream);
        if (stream.IsInMemory || Security is not { IsAuthenticated: true } || !IsStreamEncrypted(stream.Dictionary, id.Number, Security))
        {
            return raw;
        }

        try
        {
            var filterName = GetCryptFilterName(stream.Dictionary);
            if (filterName is null && Security.V >= 4 && (Resolve(stream.Dictionary[PdfName.Type]) as PdfName)?.Value == "EmbeddedFile")
            {
                filterName = Security.EmbeddedFileFilterName;
            }

            return Security.DecryptStream(raw, id, filterName, _strict);
        }
        catch (CryptographicException ex)
        {
            throw new PdfCorruptException($"The stream of object {id.Number} cannot be decrypted.", ex);
        }
    }

    /// <summary>Decrypted data with every content filter applied (Flate, LZW, ASCIIHex, ASCII85, RunLength), or null for image codecs.</summary>
    public byte[]? GetFullyDecodedStreamData(PdfStream stream, ObjectId id) =>
        StreamFilters.DecodeAll(GetStreamData(stream, id), stream.Dictionary, Resolve, _options.MaxDecompressedStreamBytes);

    /// <summary>Decrypted and Flate-decoded data, or null when another filter is involved.</summary>
    public byte[]? GetDecodedStreamData(PdfStream stream, ObjectId id) =>
        StreamFilters.DecodeFlateChain(GetStreamData(stream, id), stream.Dictionary, Resolve, _options.MaxDecompressedStreamBytes, _strict);

    /// <summary>Whether a stream of this file is encrypted under the given handler (xref streams and unencrypted metadata are not).</summary>
    public bool IsStreamEncrypted(PdfDictionary dict, int objectNumber, StandardSecurityHandler handler)
    {
        if (objectNumber == _encryptObjectNumber)
        {
            return false;
        }

        var type = (Resolve(dict[PdfName.Type]) as PdfName)?.Value;
        if (type == "XRef")
        {
            return false;
        }

        if (type == "Metadata" && !handler.EncryptMetadata)
        {
            return false;
        }

        return true;
    }

    /// <summary>For streams with a <c>/Crypt</c> filter: the crypt filter name (default Identity); otherwise null.</summary>
    public string? GetCryptFilterName(PdfDictionary dict)
    {
        var names = StreamFilters.GetFilterNames(dict, Resolve);
        var idx = names.IndexOf("Crypt");
        if (idx < 0)
        {
            return null;
        }

        var parms = StreamFilters.GetDecodeParms(dict, idx, Resolve);
        return (Resolve(parms?[PdfName.Name]) as PdfName)?.Value ?? "Identity";
    }

    // ------------------------------------------------------------------ security

    /// <summary>The permanent identifier (<c>/ID[0]</c>) as raw bytes, or null if absent or malformed.</summary>
    public byte[]? GetPermanentId() => Resolve(Trailer[PdfName.ID]) is PdfArray a && a.Count >= 1 && Resolve(a[0]) is PdfString s && s.Value.Length > 0 ? s.Value : null;

    public byte[]? GetChangingId() => Resolve(Trailer[PdfName.ID]) is PdfArray a && a.Count >= 2 && Resolve(a[1]) is PdfString s ? s.Value : null;

    /// <summary>Parses the <c>/Encrypt</c> dictionary. Throws <see cref="PdfUnsupportedEncryptionException"/> for unsupported handlers.</summary>
    public StandardSecurityHandler InitializeSecurity()
    {
        if (Security is not null)
        {
            return Security;
        }

        var encryptDict = GetEncryptDictionary() ?? throw new PdfCorruptException("The /Encrypt dictionary cannot be read.");
        Security = StandardSecurityHandler.Parse(encryptDict, GetPermanentId() ?? Array.Empty<byte>(), Resolve);
        return Security;
    }

    public PdfDictionary? GetEncryptDictionary() => Resolve(Trailer[PdfName.Encrypt]) as PdfDictionary;

    /// <summary>The <c>/Filter</c> of the encryption dictionary (<c>Standard</c>, <c>Adobe.PubSec</c>, ...), or null.</summary>
    public string? SecurityFilter => (Resolve(GetEncryptDictionary()?["Filter"]) as PdfName)?.Value;

    /// <summary>Installs a handler that already holds the file key (public-key encryption) and enables decryption.</summary>
    public void InstallSecurity(StandardSecurityHandler handler)
    {
        Security?.Dispose();
        Security = handler;
        ClearCache();
        IndexPendingObjectStreams();
    }

    /// <summary>Authenticates with the given candidate encodings and, on success, enables decryption.</summary>
    public PdfPasswordMatch Authenticate(IEnumerable<byte[]> candidates)
    {
        var handler = InitializeSecurity();
        var match = handler.Authenticate(candidates);
        if (match != PdfPasswordMatch.None)
        {
            ClearCache();
            IndexPendingObjectStreams();
            if (handler.PermsValid == false)
            {
                AddWarning("The /Perms entry does not match the permissions; the document may have been tampered with.", PdfValidationCodes.EncryptionPermsMismatch, _encryptObjectNumber);
            }
        }

        return match;
    }

    public PdfPasswordMatch Authenticate(string? password)
    {
        var handler = InitializeSecurity();
        return Authenticate(PasswordEncoder.EncodeForVerification(password, handler.R));
    }

    private PdfObject DecryptLoadedObject(PdfObject obj, ObjectId id)
    {
        if (Security is not { IsAuthenticated: true } handler || id.Number == _encryptObjectNumber)
        {
            return obj;
        }

        var dict = obj switch
        {
            PdfStream s => s.Dictionary,
            PdfDictionary d => d,
            _ => null,
        };
        if (dict is not null && (dict[PdfName.Type] as PdfName)?.Value == "XRef")
        {
            return obj; // cross-reference streams are never encrypted
        }

        try
        {
            return DecryptStrings(obj, id, handler, 0);
        }
        catch (CryptographicException ex)
        {
            throw new PdfCorruptException($"A string in object {id.Number} cannot be decrypted.", ex);
        }
    }

    private PdfObject DecryptStrings(PdfObject obj, ObjectId id, StandardSecurityHandler handler, int depth)
    {
        if (depth > _options.MaxNestingDepth)
        {
            throw new PdfLimitExceededException("Maximum object nesting depth exceeded.");
        }

        switch (obj)
        {
            case PdfString s:
                return new PdfString(handler.DecryptString(s.Value, id, _strict), s.IsHex);
            case PdfArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    a.Items[i] = DecryptStrings(a.Items[i], id, handler, depth + 1);
                }

                return a;
            case PdfDictionary d:
                var isSignature = SignatureHelper.IsSignatureDictionary(d);
                foreach (var key in d.Keys.ToList())
                {
                    if (isSignature && key.Value == "Contents")
                    {
                        continue; // signature /Contents are never encrypted
                    }

                    d[key] = DecryptStrings(d[key]!, id, handler, depth + 1);
                }

                return d;
            case PdfStream st:
                DecryptStrings(st.Dictionary, id, handler, depth + 1);
                return st;
            default:
                return obj;
        }
    }

    /// <summary>Strict check used by output verification: every in-use entry points exactly at its "<c>n g obj</c>".</summary>
    public void VerifyXrefOffsetsExact()
    {
        foreach (var (num, entry) in _xref)
        {
            if (entry.Type != XrefEntryType.InFile)
            {
                continue;
            }

            _lexer.Position = entry.Offset;
            if (!_parser.TryReadObjectHeader(out var id) || id.Number != num || id.Generation != entry.Generation || _lexer.Position <= entry.Offset)
            {
                throw new PdfCorruptException($"The cross-reference offset of object {num} is wrong.");
            }

            var b = _source.PeekAt(entry.Offset);
            if (b < '0' || b > '9')
            {
                throw new PdfCorruptException($"The cross-reference offset of object {num} does not point at its header.");
            }
        }
    }

    public void Dispose()
    {
        Security?.Dispose();
        _lexer.Dispose();
    }
}

/// <summary>A problem found while reading, for the validation report.</summary>
internal readonly record struct DocumentDiagnostic(string Code, string Message, int? ObjectNumber);

internal static class SignatureHelper
{
    /// <summary>Signature dictionaries (and document timestamps) whose /Contents is never encrypted.</summary>
    public static bool IsSignatureDictionary(PdfDictionary d)
    {
        var type = (d[PdfName.Type] as PdfName)?.Value;
        if (type is "Sig" or "DocTimeStamp")
        {
            return d.ContainsKey("Contents");
        }

        return d.ContainsKey("ByteRange") && d.ContainsKey("Contents") && d.ContainsKey("Filter");
    }
}
