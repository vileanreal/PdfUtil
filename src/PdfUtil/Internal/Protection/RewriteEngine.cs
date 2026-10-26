using System.Security.Cryptography;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Security;
using PdfUtil.Internal.Writing;

namespace PdfUtil.Internal.Protection;

/// <summary>What a rewrite should produce, besides copying the document.</summary>
internal sealed class RewriteSettings
{
    public required OutputSecurity Security { get; init; }

    public required ProgressReporter Progress { get; init; }

    public PdfInfoChanges? InfoChanges { get; init; }

    /// <summary>The output version is at least this (the algorithm's requirement); it is never lowered below the source.</summary>
    public PdfVersion MinimumVersion { get; init; } = new(1, 0);

    /// <summary>When set, PDF/A input is refused or accepted with a warning (only relevant when adding encryption).</summary>
    public PdfAPolicy? PdfAPolicy { get; init; }

    public bool Verify { get; init; } = true;

    /// <summary>Write object streams and a cross-reference stream (PDF 1.5+).</summary>
    public bool UseObjectStreams { get; init; }

    /// <summary>Object changes to apply (sanitizing, attachments, repairs).</summary>
    public ObjectEdits? Edits { get; init; }

    /// <summary>The pages the output must have when the operation changes them.</summary>
    public IReadOnlyList<PdfPageInfo>? ExpectedPages { get; init; }

    /// <summary>Leave the document information dictionary out of the output.</summary>
    public bool RemoveInfo { get; init; }

    /// <summary>Warnings decided by the caller before the rewrite (they come first in the result).</summary>
    public IReadOnlyList<string> InitialWarnings { get; init; } = Array.Empty<string>();
}

internal sealed record RewriteOutcome(PdfVersion SourceVersion, PdfVersion OutputVersion, IReadOnlyList<string> Warnings, bool InfoChanged, bool XmpUpdated, int ObjectCount = 0);

/// <summary>
/// The shared rewrite pipeline: plan (objects, version, identifier, document information) → write (optionally
/// encrypting) → self-verify. Every operation that writes a PDF goes through here.
/// </summary>
internal sealed class RewriteEngine
{
    private readonly PdfUtilOptions _lib;
    private readonly PdfLog _log;

    public RewriteEngine(PdfUtilOptions lib, PdfLog log)
    {
        _lib = lib;
        _log = log;
    }

    /// <summary>
    /// Writes the rewritten document to <paramref name="buffer"/> (seekable, empty) and verifies it. The source must
    /// already be authenticated if it is encrypted. Throws if any check fails; the caller then discards the buffer.
    /// </summary>
    public RewriteOutcome Rewrite(PdfDocument doc, RewriteSettings settings, Stream buffer, CancellationToken ct)
    {
        var plan = BuildPlan(doc, settings, ct);
        StandardSecurityHandler? handler = null;
        PdfDictionary? encryptDict = null;
        var ownsHandler = false;
        switch (settings.Security)
        {
            case NewOutputEncryption n:
                (handler, encryptDict) = StandardSecurityHandler.Create(n.Revision, n.UserPassword, n.OwnerPassword, n.P, plan.Identifier.PermanentCopy(), n.EncryptMetadata);
                ownsHandler = true;
                break;
            case PreservedOutputEncryption p:
                handler = p.Handler;
                encryptDict = p.EncryptDictionary;
                break;
            case KeyedOutputEncryption k:
                handler = k.Handler;
                encryptDict = k.EncryptDictionary;
                break;
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            Write(plan, handler, encryptDict, buffer, settings.Progress, ct);
        }
        finally
        {
            if (ownsHandler)
            {
                handler!.Dispose();
            }
        }

        if (settings.Verify)
        {
            buffer.Position = 0;
            new OutputVerifier(_lib, _log).Verify(buffer, plan, encryptDict, settings.Progress, ct);
            _log.Info("The output passed self-verification.");
        }

        buffer.Position = 0;
        var warnings = new List<string>(settings.InitialWarnings);
        foreach (var w in plan.Warnings.Concat(doc.Warnings))
        {
            if (!warnings.Contains(w))
            {
                warnings.Add(w);
            }
        }

        return new RewriteOutcome(plan.SourceVersion, plan.OutputVersion, warnings, settings.InfoChanges?.HasChanges == true, plan.XmpOverride is not null, plan.AllObjects.Count());
    }

    private RewritePlan BuildPlan(PdfDocument doc, RewriteSettings settings, CancellationToken ct)
    {
        var warnings = new List<string>();
        var progress = settings.Progress;
        var catalogRef = doc.Trailer[PdfName.Root] as PdfReference ?? throw new PdfCorruptException("The trailer has no /Root reference.");
        var edits = settings.Edits;
        var catalog = (edits is null ? doc.GetObject(catalogRef.Id.Number) : edits.Lookup(doc, catalogRef.Id.Number)) as PdfDictionary ?? throw new PdfCorruptException("The document catalog cannot be read.");

        PdfXmpMetadata? xmp = null;
        try
        {
            xmp = XmpReader.Read(doc);
        }
        catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException)
        {
            doc.AddWarning("The XMP metadata stream could not be read.");
        }

        // PDF/A forbids encryption.
        if (settings.PdfAPolicy is { } pdfAPolicy && xmp?.IsPdfA == true)
        {
            if (pdfAPolicy == PdfAPolicy.Throw)
            {
                throw new PdfComplianceException($"The document declares {xmp.PdfALevel} conformance, and PDF/A forbids encryption. Set PdfAPolicy = Proceed to encrypt anyway; the output will no longer be PDF/A conformant.");
            }

            warnings.Add($"The document declared {xmp.PdfALevel} conformance; encryption makes the output non-conformant with PDF/A.");
        }

        var totalObjects = Math.Max(1, doc.Xref.Count);
        progress.Report(PdfProgressStage.Reading, 0, totalObjects, force: true);
        var reachable = ObjectGraph.FindReachable(
            doc,
            ct,
            out var hasSignatureDicts,
            count => progress.Report(PdfProgressStage.Reading, count, totalObjects),
            edits is null ? null : number => edits.Lookup(doc, number),
            includeInfo: !settings.RemoveInfo);
        progress.Report(PdfProgressStage.Reading, totalObjects, totalObjects, force: true);
        if (hasSignatureDicts || ObjectGraph.HasSignedFields(doc, _lib.MaxNestingDepth))
        {
            warnings.Add("The document is digitally signed. Rewriting it invalidates the existing signatures.");
        }

        if (doc.IsLinearized)
        {
            warnings.Add("The input was linearized (fast web view). The output is a full rewrite and is not linearized.");
        }

        // Versions: output = max(source, required); never lowered.
        PdfVersion.TryParse(doc.HeaderVersion, out var header);
        var sourceVersion = header;
        if (doc.Resolve(catalog["Version"]) is PdfName cv && PdfVersion.TryParse(cv.Value, out var catalogVersion))
        {
            sourceVersion = PdfVersion.Max(header, catalogVersion);
        }

        var outputVersion = PdfVersion.Max(sourceVersion, settings.MinimumVersion);
        if (settings.UseObjectStreams)
        {
            outputVersion = PdfVersion.Max(outputVersion, new PdfVersion(1, 5));
        }

        var newEncryption = settings.Security as NewOutputEncryption;
        var addExtension = newEncryption is { Revision: 6 } && outputVersion < PdfVersion.V2_0;
        if (newEncryption is not null && sourceVersion >= PdfVersion.V2_0 && newEncryption.Algorithm != PdfEncryptionAlgorithm.Aes256)
        {
            warnings.Add($"{newEncryption.Algorithm} is deprecated in PDF 2.0; AES-256 is recommended.");
        }

        // The permanent ID: preserved byte-for-byte, or generated if absent or malformed. Fixed before key derivation.
        var sourceId = doc.GetPermanentId();
        if (sourceId is null && settings.Security is PreservedOutputEncryption)
        {
            // The source key was derived from an empty /ID[0]; keep it empty so the preserved key stays valid.
            sourceId = Array.Empty<byte>();
        }

        var identifier = new FileIdentifier(sourceId ?? RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(16), preserved: sourceId is not null);
        if (sourceId is null)
        {
            _log.Info("The input has no valid /ID; a new permanent identifier was generated.");
        }

        // Object numbers: originals are kept; new objects follow the highest number.
        var next = (reachable.Count == 0 ? 0 : reachable.Max) + 1;
        int? infoNumber = null;
        PdfDictionary? sourceInfo = null;
        PdfDictionary? infoOverride = null;
        var infoIsNew = false;
        switch (settings.RemoveInfo ? null : doc.Trailer[PdfName.Info])
        {
            case PdfReference ir when reachable.Contains(ir.Id.Number) && doc.GetObject(ir.Id.Number) is PdfDictionary existing:
                infoNumber = ir.Id.Number;
                sourceInfo = existing;
                break;
            case PdfReference ir:
                reachable.Remove(ir.Id.Number);
                break;
            case PdfDictionary d:
                // A direct Info dictionary in the trailer becomes an indirect object.
                sourceInfo = infoOverride = d;
                infoNumber = next++;
                infoIsNew = true;
                break;
        }

        // Requested document information changes: Info dictionary, and the XMP packet when there is one.
        int? xmpNumber = null;
        byte[]? xmpOverride = null;
        if (settings.InfoChanges is { HasChanges: true } changes)
        {
            var now = DateTimeOffset.Now;
            infoOverride = InfoEditor.Apply(sourceInfo, changes, now);
            if (infoNumber is null)
            {
                infoNumber = next++;
                infoIsNew = true;
            }

            if (changes.UpdateXmp && catalog["Metadata"] is PdfReference metadataRef && reachable.Contains(metadataRef.Id.Number))
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
                    xmpNumber = metadataRef.Id.Number;
                }
            }
        }

        var plan = new RewritePlan
        {
            Source = doc,
            Security = settings.Security,
            Identifier = identifier,
            SourceVersion = sourceVersion,
            OutputVersion = outputVersion,
            AddAdbeExtension = addExtension,
            Objects = reachable,
            CatalogNumber = catalogRef.Id.Number,
            Edits = edits,
            UseObjectStreams = settings.UseObjectStreams,
            ExpectedPages = settings.ExpectedPages,
            InfoNumber = infoNumber,
            InfoOverride = infoOverride,
            InfoIsNew = infoIsNew,
            XmpNumber = xmpNumber,
            XmpOverride = xmpOverride,
            EncryptNumber = settings.Security is NoOutputEncryption ? null : next,
        };
        plan.Warnings.AddRange(warnings);
        return plan;
    }

    private void Write(RewritePlan plan, StandardSecurityHandler? handler, PdfDictionary? encryptDict, Stream buffer, ProgressReporter progress, CancellationToken ct)
    {
        using var writer = new PdfWriter(buffer, _lib.MaxNestingDepth);
        writer.WriteHeader(plan.OutputVersion);
        var objects = plan.AllObjects.ToList();
        progress.Report(PdfProgressStage.Writing, 0, objects.Count, force: true);
        var packable = plan.UseObjectStreams ? objects.Where(n => CanPack(plan, n)).ToHashSet() : new HashSet<int>();
        var count = 0;
        foreach (var number in objects.Where(n => !packable.Contains(n)))
        {
            if ((++count & 0xFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            WriteObject(plan, handler, writer, plan.IdOf(number), number);
            progress.Report(PdfProgressStage.Writing, count, objects.Count);
        }

        if (plan.UseObjectStreams)
        {
            WriteObjectStreams(plan, handler, writer, packable.OrderBy(n => n).ToList(), ref count, objects.Count, progress, ct);
        }

        var trailer = new PdfDictionary
        {
            [PdfName.Root] = new PdfReference(plan.IdOf(plan.CatalogNumber)),
        };
        if (plan.InfoNumber is int info)
        {
            trailer[PdfName.Info] = new PdfReference(plan.IdOf(info));
        }

        // /ID strings are byte strings: written as hex, never encrypted, never text-converted.
        trailer[PdfName.ID] = new PdfArray(new PdfObject[]
        {
            new PdfString(plan.Identifier.PermanentCopy(), isHex: true),
            new PdfString(plan.Identifier.ChangingCopy(), isHex: true),
        });

        if (plan.EncryptNumber is int encryptNumber && encryptDict is not null)
        {
            // The encryption dictionary itself is never encrypted.
            writer.WriteIndirectObject(new ObjectId(encryptNumber, 0), encryptDict, null);
            trailer[PdfName.Encrypt] = new PdfReference(encryptNumber, 0);
        }

        if (plan.UseObjectStreams)
        {
            // The cross-reference stream takes the next number and is never encrypted (ISO 32000-1 §7.6.1).
            var xrefNumber = Math.Max(plan.MaxObjectNumber, plan.ContainerNumbers.DefaultIfEmpty(0).Max()) + 1;
            plan.ContainerNumbers.Add(xrefNumber);
            plan.WrittenSize = xrefNumber + 1;
            writer.WriteXrefStream(xrefNumber, plan.WrittenSize, trailer, full: true);
        }
        else
        {
            plan.WrittenSize = plan.MaxObjectNumber + 1;
            writer.WriteXrefAndTrailer(trailer);
        }

        writer.Flush();
        progress.Report(PdfProgressStage.Writing, objects.Count, objects.Count, force: true);
    }

    private const int ObjectsPerStream = 100;

    /// <summary>
    /// Objects that may go into an object stream (ISO 32000-1 §7.5.7): not streams, generation 0, and not signature
    /// dictionaries, whose /Contents must stay unencrypted while the whole object stream is encrypted.
    /// </summary>
    private static bool CanPack(RewritePlan plan, int number)
    {
        if (plan.IdOf(number).Generation != 0)
        {
            return false;
        }

        return plan.Transform(number).Plain switch
        {
            PdfStream => false,
            PdfDictionary d => !SignatureHelper.IsSignatureDictionary(d),
            _ => true,
        };
    }

    /// <summary>
    /// Writes object streams of up to <see cref="ObjectsPerStream"/> objects each. The objects inside are written in
    /// plain form; when the output is encrypted, the object stream as a whole is encrypted instead (§7.6.1).
    /// </summary>
    private void WriteObjectStreams(RewritePlan plan, StandardSecurityHandler? handler, PdfWriter writer, List<int> members, ref int count, int total, ProgressReporter progress, CancellationToken ct)
    {
        var next = Math.Max(plan.MaxObjectNumber, 0) + 1;
        for (var start = 0; start < members.Count; start += ObjectsPerStream)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = members.Skip(start).Take(ObjectsPerStream).ToList();
            var streamNumber = next++;
            var header = new System.Text.StringBuilder();
            using var body = new MemoryStream();
            for (var i = 0; i < chunk.Count; i++)
            {
                header.Append(chunk[i]).Append(' ').Append(body.Length).Append(' ');
                body.Write(PdfWriter.Serialize(plan.Transform(chunk[i]).Plain, _lib.MaxNestingDepth));
                body.WriteByte((byte)'\n');
                plan.Packed[chunk[i]] = (streamNumber, i);
                writer.RecordCompressed(chunk[i], streamNumber, i);
            }

            var headerBytes = System.Text.Encoding.ASCII.GetBytes(header.ToString());
            var content = new byte[headerBytes.Length + body.Length];
            headerBytes.CopyTo(content, 0);
            body.ToArray().CopyTo(content, headerBytes.Length);
            var dict = new PdfDictionary
            {
                [PdfName.Type] = PdfName.Get("ObjStm"),
                ["N"] = new PdfInteger(chunk.Count),
                ["First"] = new PdfInteger(headerBytes.Length),
                [PdfName.Filter] = PdfName.FlateDecode,
            };
            var id = new ObjectId(streamNumber, 0);
            var data = PdfWriter.Deflate(content);
            if (handler is not null)
            {
                data = handler.EncryptStream(data, id);
            }

            dict[PdfName.Length] = new PdfInteger(data.Length);
            writer.WriteIndirectObject(id, new PdfStream(dict, data), data);
            plan.ContainerNumbers.Add(streamNumber);
            count += chunk.Count;
            progress.Report(PdfProgressStage.Writing, count, total);
        }
    }

    private void WriteObject(RewritePlan plan, StandardSecurityHandler? handler, PdfWriter writer, ObjectId id, int number)
    {
        var (plain, sourceStream, replacementData) = plan.Transform(number);
        Func<byte[], PdfString>? encrypt = handler is null ? null : bytes => new PdfString(handler.EncryptString(bytes, id), isHex: true);

        if (plain is PdfStream plainStream && sourceStream is not null)
        {
            var dict = encrypt is null ? plainStream.Dictionary.ShallowClone() : (PdfDictionary)ObjectGraph.MapStrings(plainStream.Dictionary, encrypt, 0, _lib.MaxNestingDepth);
            var data = replacementData ?? plan.Source.GetStreamData(sourceStream, id);
            var outData = handler is not null && plan.IsOutputStreamEncrypted(plainStream.Dictionary) ? EncryptStream(plan, handler, plainStream.Dictionary, data, id) : data;
            dict[PdfName.Length] = new PdfInteger(outData.Length);
            writer.WriteIndirectObject(id, new PdfStream(dict, outData), outData);
            return;
        }

        writer.WriteIndirectObject(id, encrypt is null ? plain : ObjectGraph.MapStrings(plain, encrypt, 0, _lib.MaxNestingDepth), null);
    }

    /// <summary>Embedded files use <c>/EFF</c>, which may differ from <c>/StmF</c> in preserved V4+ encryption.</summary>
    internal static byte[] EncryptStream(RewritePlan plan, StandardSecurityHandler handler, PdfDictionary plainDict, byte[] data, ObjectId id) =>
        handler.V >= 4 && (plan.Source.Resolve(plainDict[PdfName.Type]) as PdfName)?.Value == "EmbeddedFile"
            ? handler.EncryptStream(data, id, handler.EmbeddedFileFilterName)
            : handler.EncryptStream(data, id);
}
