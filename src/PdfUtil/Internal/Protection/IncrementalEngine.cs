using System.Security.Cryptography;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;
using PdfUtil.Internal.Writing;

namespace PdfUtil.Internal.Protection;

/// <summary>What an incremental update should contain.</summary>
internal sealed class IncrementalSettings
{
    public required ProgressReporter Progress { get; init; }

    public ObjectEdits? Edits { get; init; }

    public PdfInfoChanges? InfoChanges { get; init; }

    /// <summary>Raised through the catalog <c>/Version</c> (the header is part of the original bytes and stays).</summary>
    public PdfVersion MinimumVersion { get; init; } = new(1, 0);

    public IReadOnlyList<string> InitialWarnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Called after the update is written and before it is verified, to fill <see cref="ReservedSpace"/> placeholders
    /// (Phase 6 signing: hash the byte ranges, then write the signature value).
    /// </summary>
    public Action<IncrementalOutput>? Fill { get; init; }
}

/// <summary>The complete output of an incremental update, for filling reserved spaces before verification.</summary>
internal sealed class IncrementalOutput
{
    private readonly Stream _buffer;

    public IncrementalOutput(Stream buffer, long originalLength, IReadOnlyList<ReservedSpace> reserved)
    {
        _buffer = buffer;
        OriginalLength = originalLength;
        Reserved = reserved;
    }

    /// <summary>Length of the original document; everything after it is the update.</summary>
    public long OriginalLength { get; }

    /// <summary>Total length of the output.</summary>
    public long Length => _buffer.Length;

    /// <summary>The placeholders in the update, with their file offsets.</summary>
    public IReadOnlyList<ReservedSpace> Reserved { get; }

    /// <summary>Reads bytes of the output (for example a signature's byte ranges).</summary>
    public byte[] Read(long offset, int count)
    {
        var result = new byte[count];
        _buffer.Position = offset;
        _buffer.ReadExactly(result);
        return result;
    }

    /// <summary>Writes a value into a placeholder, padded to its full size.</summary>
    public void Fill(ReservedSpace space, byte[] value)
    {
        if (space.Offset < OriginalLength)
        {
            throw new InvalidOperationException("The reserved space has not been written by this update.");
        }

        var bytes = space.Encode(value);
        _buffer.Position = space.Offset;
        _buffer.Write(bytes);
    }
}

/// <summary>
/// Incremental save (F12): the original bytes are copied unchanged and the changed and new objects are appended with
/// a cross-reference section in the source's style (table or stream), a trailer with <c>/Prev</c>, and the same
/// <c>/ID[0]</c> with a new <c>/ID[1]</c>. The result is verified: the original bytes are unchanged, every appended
/// object reads back as planned, and every other object still resolves to the original revision.
/// </summary>
internal sealed class IncrementalEngine
{
    private readonly PdfUtilOptions _lib;
    private readonly PdfLog _log;

    public IncrementalEngine(PdfUtilOptions lib, PdfLog log)
    {
        _lib = lib;
        _log = log;
    }

    /// <summary>Throws <see cref="NotSupportedException"/> when the document cannot take an incremental update.</summary>
    public static void EnsureSupported(PdfDocument doc)
    {
        if (doc.IsEncrypted)
        {
            throw new NotSupportedException("Incremental saves are only supported for unencrypted documents. Use PdfSaveMode.Rewrite for protected documents.");
        }

        if (doc.WasRepaired || doc.StartXref < 0)
        {
            throw new NotSupportedException("The document's cross-reference information is damaged, so an update cannot be appended to it. Use PdfSaveMode.Rewrite, or repair the document first.");
        }
    }

    public RewriteOutcome Append(PdfDocument doc, IncrementalSettings settings, Stream buffer, CancellationToken ct)
    {
        EnsureSupported(doc);
        var source = doc.Source;
        var progress = settings.Progress;
        var warnings = new List<string>(settings.InitialWarnings);
        var catalogRef = doc.Trailer[PdfName.Root] as PdfReference ?? throw new PdfCorruptException("The trailer has no /Root reference.");
        var edits = settings.Edits;
        PdfObject? Lookup(int n) => edits is null ? doc.GetObject(n) : edits.Lookup(doc, n);

        // ---- What to append.
        var objects = new SortedDictionary<int, PdfObject>();
        var next = Math.Max(doc.Xref.Keys.DefaultIfEmpty(0).Max(), doc.Trailer[PdfName.Size] is PdfInteger sz ? (int)Math.Clamp(sz.Value - 1, 0, int.MaxValue - 2) : 0) + 1;
        foreach (var (number, obj) in edits?.Entries ?? Enumerable.Empty<KeyValuePair<int, PdfObject>>())
        {
            objects[number] = obj is PdfStream { IsInMemory: false } kept
                ? new PdfStream(kept.Dictionary, doc.GetStreamData((PdfStream)doc.GetObject(number)!, new ObjectId(number, doc.GetGeneration(number))))
                : obj;
            next = Math.Max(next, number + 1);
        }

        var infoRef = doc.Trailer[PdfName.Info] as PdfReference;
        int? infoNumber = infoRef is not null && doc.GetObject(infoRef.Id.Number) is PdfDictionary ? infoRef.Id.Number : null;
        byte[]? xmpOverride = null;
        int? xmpNumber = null;
        var now = DateTimeOffset.Now;
        if (settings.InfoChanges is { HasChanges: true } changes)
        {
            var sourceInfo = doc.Resolve(doc.Trailer[PdfName.Info]) as PdfDictionary;
            infoNumber ??= next++;
            objects[infoNumber.Value] = InfoEditor.Apply(sourceInfo, changes, now);
            if (changes.UpdateXmp && (Lookup(catalogRef.Id.Number) as PdfDictionary)?["Metadata"] is PdfReference metadataRef && doc.GetObject(metadataRef.Id.Number) is PdfStream metadata)
            {
                byte[]? raw = null;
                try
                {
                    raw = XmpReader.ReadRaw(doc);
                }
                catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException)
                {
                    // Reported below.
                }

                xmpOverride = raw is null ? null : XmpEditor.Apply(raw, changes, now);
                if (xmpOverride is null)
                {
                    warnings.Add("The XMP metadata could not be updated, so viewers that read XMP may still show the old document information.");
                }
                else
                {
                    var dict = metadata.Dictionary.ShallowClone();
                    foreach (var key in new[] { "Filter", "DecodeParms", "DL", "Length" })
                    {
                        dict.Remove(PdfName.Get(key));
                    }

                    xmpNumber = metadataRef.Id.Number;
                    objects[xmpNumber.Value] = new PdfStream(dict, xmpOverride);
                }
            }
        }

        // Versions: the header is original bytes, so a higher version goes into the catalog.
        PdfVersion.TryParse(doc.HeaderVersion, out var header);
        var sourceVersion = header;
        var catalog = Lookup(catalogRef.Id.Number) as PdfDictionary ?? throw new PdfCorruptException("The document catalog cannot be read.");
        if (doc.Resolve(catalog["Version"]) is PdfName cv && PdfVersion.TryParse(cv.Value, out var catalogVersion))
        {
            sourceVersion = PdfVersion.Max(header, catalogVersion);
        }

        var outputVersion = PdfVersion.Max(sourceVersion, settings.MinimumVersion);
        if (outputVersion > sourceVersion)
        {
            var copy = catalog.ShallowClone();
            copy["Version"] = PdfName.Get(outputVersion.ToString());
            objects[catalogRef.Id.Number] = copy;
        }

        if (doc.IsLinearized)
        {
            warnings.Add("The input was linearized (fast web view); after an incremental update the linearization no longer applies, so viewers load the file normally.");
        }

        if (ObjectGraph.HasSignedFields(doc, _lib.MaxNestingDepth))
        {
            warnings.Add("The document is signed. The incremental update keeps the signed bytes intact; viewers will show that the document was changed after signing.");
        }

        // ---- Write: original bytes, then the update.
        progress.Report(PdfProgressStage.Writing, 0, objects.Count, force: true);
        buffer.SetLength(0);
        source.CopyTo(0, source.Length, buffer);
        var permanentId = doc.GetPermanentId() ?? RandomNumberGenerator.GetBytes(16);
        var changingId = NewChangingId(doc.GetChangingId());
        long xrefOffset;
        Dictionary<int, long> offsets;
        IReadOnlyList<ReservedSpace> reserved;
        using (var writer = new PdfWriter(buffer, _lib.MaxNestingDepth, source.Length))
        {
            if (source.Length > 0 && source.PeekAt(source.Length - 1) is not ('\n' or '\r'))
            {
                writer.WriteEol();
            }

            var count = 0;
            foreach (var (number, obj) in objects)
            {
                ct.ThrowIfCancellationRequested();
                var id = new ObjectId(number, doc.GetGeneration(number));
                if (obj is PdfStream stream)
                {
                    var dict = stream.Dictionary.ShallowClone();
                    dict[PdfName.Length] = new PdfInteger(stream.Data!.Length);
                    writer.WriteIndirectObject(id, new PdfStream(dict, stream.Data), stream.Data);
                }
                else
                {
                    writer.WriteIndirectObject(id, obj, null);
                }

                progress.Report(PdfProgressStage.Writing, ++count, objects.Count);
            }

            var size = Math.Max(next, objects.Keys.DefaultIfEmpty(0).Max() + 1);
            var trailer = new PdfDictionary { [PdfName.Root] = catalogRef };
            if (infoNumber is int info)
            {
                trailer[PdfName.Info] = new PdfReference(info, doc.GetGeneration(info));
            }

            trailer[PdfName.ID] = new PdfArray(new PdfObject[] { new PdfString(permanentId, isHex: true), new PdfString(changingId, isHex: true) });
            trailer[PdfName.Prev] = new PdfInteger(doc.StartXref);
            xrefOffset = writer.Position;
            if (doc.LatestSectionIsStream)
            {
                writer.WriteXrefStream(size, size + 1, trailer, full: false);
            }
            else
            {
                var full = new PdfDictionary { [PdfName.Size] = new PdfInteger(size) };
                foreach (var e in trailer.Entries)
                {
                    full[e.Key] = e.Value;
                }

                // A hybrid file's objects in compressed object streams are listed only in its /XRefStm stream.
                // Readers such as MuPDF look for it in the latest trailer only, so the update repeats it; the
                // update's own table entries still take precedence.
                if (doc.LatestXrefStm is long xrefStm)
                {
                    full[PdfName.XRefStm] = new PdfInteger(xrefStm);
                }

                writer.WriteIncrementalXrefTable(full);
            }

            writer.Flush();
            offsets = writer.Offsets.ToDictionary(o => o.Key, o => o.Value.Offset);
            reserved = writer.Reserved.ToList();
        }

        progress.Report(PdfProgressStage.Writing, objects.Count, objects.Count, force: true);
        settings.Fill?.Invoke(new IncrementalOutput(buffer, source.Length, reserved));

        buffer.Position = 0;
        Verify(doc, buffer, objects, offsets, xrefOffset, permanentId, changingId, infoNumber, xmpOverride, reserved, progress, ct);
        _log.Info("The incremental update passed self-verification.");
        buffer.Position = 0;
        foreach (var w in doc.Warnings)
        {
            if (!warnings.Contains(w))
            {
                warnings.Add(w);
            }
        }

        return new RewriteOutcome(sourceVersion, outputVersion, warnings, settings.InfoChanges?.HasChanges == true, xmpOverride is not null, objects.Count);
    }

    private static byte[] NewChangingId(byte[]? previous)
    {
        while (true)
        {
            var id = RandomNumberGenerator.GetBytes(16);
            if (previous is null || !id.AsSpan().SequenceEqual(previous))
            {
                return id;
            }
        }
    }

    private static void Check(bool condition, string failure)
    {
        if (!condition)
        {
            throw new PdfVerificationException("The incremental update failed self-verification: " + failure);
        }
    }

    private void Verify(PdfDocument doc, Stream buffer, SortedDictionary<int, PdfObject> objects, Dictionary<int, long> offsets, long xrefOffset, byte[] permanentId, byte[] changingId, int? infoNumber, byte[]? xmpOverride, IReadOnlyList<ReservedSpace> reserved, ProgressReporter progress, CancellationToken ct)
    {
        try
        {
            var source = doc.Source;
            progress.Report(PdfProgressStage.Verifying, 0, objects.Count, force: true);

            // 1. The original bytes are unchanged.
            var chunk = new byte[1 << 16];
            var original = new byte[1 << 16];
            for (long at = 0; at < source.Length; at += chunk.Length)
            {
                ct.ThrowIfCancellationRequested();
                var n = (int)Math.Min(chunk.Length, source.Length - at);
                buffer.Position = at;
                buffer.ReadExactly(chunk, 0, n);
                source.ReadAt(at, original.AsSpan(0, n));
                Check(chunk.AsSpan(0, n).SequenceEqual(original.AsSpan(0, n)), $"the original bytes changed near offset {at}.");
            }

            // 2. The new revision reads without repair and chains to the original one.
            buffer.Position = 0;
            using var outSource = new PdfSource(buffer, ownsStream: false);
            using var outDoc = PdfDocument.Open(outSource, _lib, _log, strict: false, ct);
            Check(!outDoc.WasRepaired, "the output needed repair to read.");
            Check(outDoc.StartXref == xrefOffset && outDoc.XrefSectionCount == doc.XrefSectionCount + 1, "the new cross-reference section is not chained to the original one.");
            Check(outDoc.LatestSectionIsStream == doc.LatestSectionIsStream, "the cross-reference style differs from the source.");
            Check((outDoc.GetPermanentId() ?? Array.Empty<byte>()).AsSpan().SequenceEqual(permanentId), "/ID[0] differs from the source.");
            Check((outDoc.GetChangingId() ?? Array.Empty<byte>()).AsSpan().SequenceEqual(changingId), "/ID[1] is not the new identifier.");

            // 3. Every appended object is where the cross-reference section says and reads back as planned.
            var starts = offsets.Values.Append(xrefOffset).OrderBy(o => o).ToList();
            var count = 0;
            foreach (var (number, expected) in objects)
            {
                ct.ThrowIfCancellationRequested();
                Check(outDoc.Xref.TryGetValue(number, out var entry) && entry.Type == XrefEntryType.InFile && entry.Offset == offsets[number], $"the cross-reference entry of object {number} is wrong.");
                var written = outDoc.GetObject(number);
                var end = starts.First(o => o > offsets[number]);
                var hasReserved = reserved.Any(r => r.Offset >= offsets[number] && r.Offset < end);
                if (expected is PdfStream expectedStream)
                {
                    Check(written is PdfStream ws && ObjectGraph.DeepEquals(expectedStream.Dictionary, ws.Dictionary, ignoreKey: "Length")
                        && outDoc.GetStreamData(ws, new ObjectId(number, outDoc.GetGeneration(number))).AsSpan().SequenceEqual(expectedStream.Data), $"stream {number} did not round-trip.");
                }
                else
                {
                    Check(written is not null && (hasReserved || ObjectGraph.DeepEquals(expected, written)), $"object {number} did not round-trip.");
                }

                progress.Report(PdfProgressStage.Verifying, ++count, objects.Count);
            }

            // 4. Every other object still comes from the original revision.
            foreach (var (number, entry) in doc.Xref)
            {
                if (!objects.ContainsKey(number))
                {
                    Check(outDoc.Xref.TryGetValue(number, out var outEntry) && outEntry == entry, $"the cross-reference entry of unchanged object {number} differs.");
                }
            }

            // 5. Document-level checks.
            var srcPages = PageTreeReader.ReadPages(doc, _lib.MaxNestingDepth, ct);
            var outPages = PageTreeReader.ReadPages(outDoc, _lib.MaxNestingDepth, ct);
            Check(srcPages.Count == outPages.Count && srcPages.Zip(outPages).All(p => p.First.MediaBox == p.Second.MediaBox && p.First.Rotate == p.Second.Rotate), "the pages differ from the source.");
            var expectedInfo = infoNumber is int i && objects.TryGetValue(i, out var newInfo) ? InfoReader.Read((PdfDictionary)newInfo, doc.Resolve) : InfoReader.Read(doc);
            var outInfo = InfoReader.Read(outDoc);
            Check((expectedInfo is null) == (outInfo is null) && (expectedInfo is null || (expectedInfo.Entries.Count == outInfo!.Entries.Count && expectedInfo.Entries.All(e => outInfo.Entries.TryGetValue(e.Key, out var v) && v == e.Value))), "the Info dictionary differs from the expected values.");
            if (xmpOverride is not null)
            {
                Check(XmpReader.ReadRaw(outDoc) is { } xmp && xmp.AsSpan().SequenceEqual(xmpOverride), "the XMP metadata differs from the expected packet.");
            }

            progress.Report(PdfProgressStage.Verifying, objects.Count, objects.Count, force: true);
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
            throw new PdfVerificationException("The incremental update failed self-verification: " + ex.Message, ex);
        }
    }
}
