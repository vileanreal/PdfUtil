using System.Security.Cryptography;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Filters;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;
using PdfUtil.Internal.Security;

namespace PdfUtil.Internal.Protection;

/// <summary>
/// Mandatory self-verification of every rewrite: re-parses the output with the library's own reader in strict mode
/// (no repair) and proves that the security is exactly as planned (passwords, /ID, /Encrypt), every object decrypts to
/// exactly the planned plain object, stream bytes equal the source bytes, and the document structure matches.
/// The expected output is described by the <see cref="RewritePlan"/>.
/// </summary>
internal sealed class OutputVerifier
{
    private readonly PdfUtilOptions _lib;
    private readonly PdfLog _log;

    public OutputVerifier(PdfUtilOptions lib, PdfLog log)
    {
        _lib = lib;
        _log = log;
    }

    public void Verify(Stream output, RewritePlan plan, PdfDictionary? writtenEncryptDict, ProgressReporter progress, CancellationToken ct)
    {
        try
        {
            VerifyCore(output, plan, writtenEncryptDict, progress, ct);
        }
        catch (PdfVerificationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is PdfUtilException or CryptographicException or InvalidDataException or InvalidOperationException)
        {
            throw new PdfVerificationException("The output failed self-verification: " + ex.Message, ex);
        }
    }

    private static void Check(bool condition, string failure)
    {
        if (!condition)
        {
            throw new PdfVerificationException("The output failed self-verification: " + failure);
        }
    }

    private void VerifyCore(Stream output, RewritePlan plan, PdfDictionary? writtenEncryptDict, ProgressReporter progress, CancellationToken ct)
    {
        using var source = new PdfSource(output, ownsStream: false);
        using var outDoc = PdfDocument.Open(source, _lib, _log, strict: true, ct);

        // Structure: header, exact xref offsets, single consistent trailer.
        Check(outDoc.HeaderVersion == plan.OutputVersion.ToString(), "unexpected header version.");
        if (plan.UseObjectStreams)
        {
            Check(outDoc.XrefSectionCount == 1 && outDoc.LatestSectionIsStream && outDoc.HasObjectStreams == (plan.Packed.Count > 0), "the output must have exactly one cross-reference stream, and object streams for the packed objects.");
        }
        else
        {
            Check(outDoc.XrefSectionCount == 1 && !outDoc.HasXrefStreams && !outDoc.HasObjectStreams, "the output must have exactly one classic cross-reference table.");
        }

        outDoc.VerifyXrefOffsetsExact();
        Check(outDoc.Trailer[PdfName.Size] is PdfInteger size && size.Value == plan.WrittenSize, "wrong trailer /Size.");
        Check(!outDoc.IsLinearized, "the output must not be linearized.");

        // /ID: two hex byte strings; /ID[0] equals the planned bytes (the key-derivation input when encrypted).
        Check(outDoc.Trailer[PdfName.ID] is PdfArray { Count: 2 } ids && ids[0] is PdfString { IsHex: true } && ids[1] is PdfString { IsHex: true }, "the trailer /ID must be an array of two hex strings.");
        var permanent = outDoc.GetPermanentId() ?? Array.Empty<byte>();
        Check(permanent.AsSpan().SequenceEqual(plan.Identifier.Permanent), "/ID[0] differs from the planned identifier.");

        switch (plan.Security)
        {
            case NoOutputEncryption:
                Check(!outDoc.IsEncrypted && writtenEncryptDict is null, "the output must not be encrypted.");
                break;
            case NewOutputEncryption n:
                VerifyNewEncryption(outDoc, plan, n, writtenEncryptDict!);
                break;
            case PreservedOutputEncryption p:
                VerifyPreservedEncryption(outDoc, plan, p, writtenEncryptDict!);
                break;
            case KeyedOutputEncryption k:
                Check(outDoc.Trailer[PdfName.Encrypt] is PdfReference kr && kr.Id.Number == plan.EncryptNumber, "wrong /Encrypt reference.");
                Check(outDoc.GetEncryptDictionary() is { } written && ObjectGraph.DeepEquals(written, writtenEncryptDict), "the /Encrypt dictionary did not round-trip.");
                var reopened = k.Reauthenticate(outDoc);
                Check(reopened is not null && reopened.HasSameKey(k.Handler), "the output cannot be opened with the recipient certificate.");
                outDoc.InstallSecurity(reopened!);
                break;
        }

        // Content: every object decrypts to exactly the intended plain object; stream bytes equal the source bytes.
        var objects = plan.AllObjects.ToList();
        Check(outDoc.Xref.Count(e => e.Value.Type != XrefEntryType.Free) == objects.Count + (plan.IsEncrypted ? 1 : 0) + plan.ContainerNumbers.Count, "unexpected number of objects.");
        progress.Report(PdfProgressStage.Verifying, 0, objects.Count, force: true);
        var count = 0;
        foreach (var number in objects)
        {
            if ((++count & 0xFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            VerifyObject(outDoc, plan, number);
            progress.Report(PdfProgressStage.Verifying, count, objects.Count);
        }

        // Document-level comparison with the source.
        var srcPages = plan.ExpectedPages ?? PageTreeReader.ReadPages(plan.Source, _lib.MaxNestingDepth, ct);
        var outPages = PageTreeReader.ReadPages(outDoc, _lib.MaxNestingDepth, ct);
        Check(srcPages.Count == outPages.Count, "page count differs from the source.");
        for (var i = 0; i < srcPages.Count; i++)
        {
            Check(srcPages[i].MediaBox == outPages[i].MediaBox && srcPages[i].Rotate == outPages[i].Rotate, "page geometry differs from the source.");
        }

        // The Info dictionary must decode to the source values, with any requested changes applied.
        var expectedInfo = plan.InfoNumber is int infoNumber && plan.Transform(infoNumber).Plain is PdfDictionary infoDict
            ? InfoReader.Read(infoDict, plan.Source.Resolve)
            : null;
        var outInfo = InfoReader.Read(outDoc);
        Check((expectedInfo is null) == (outInfo is null), "the Info dictionary presence differs.");
        if (expectedInfo is not null && outInfo is not null)
        {
            Check(expectedInfo.Entries.Count == outInfo.Entries.Count && expectedInfo.Entries.All(e => outInfo.Entries.TryGetValue(e.Key, out var v) && v == e.Value), "the Info dictionary differs from the expected values.");
        }

        var plannedCatalog = plan.Transform(plan.CatalogNumber).Plain as PdfDictionary;
        var srcXmp = plan.XmpOverride ?? (plannedCatalog?["Metadata"] is PdfReference ? SafeXmp(plan.Source) : null);
        var outXmp = XmpReader.ReadRaw(outDoc);
        Check((srcXmp is null) == (outXmp is null) && (srcXmp is null || srcXmp.AsSpan().SequenceEqual(outXmp)), "the XMP metadata differs from the expected packet.");
        progress.Report(PdfProgressStage.Verifying, objects.Count, objects.Count, force: true);
    }

    private static void VerifyNewEncryption(PdfDocument outDoc, RewritePlan plan, NewOutputEncryption security, PdfDictionary writtenEncryptDict)
    {
        Check(outDoc.Trailer[PdfName.Encrypt] is PdfReference er && er.Id.Number == plan.EncryptNumber, "wrong /Encrypt reference.");
        var encrypt = outDoc.GetEncryptDictionary();
        Check(encrypt is not null && ObjectGraph.DeepEquals(encrypt, writtenEncryptDict), "the /Encrypt dictionary did not round-trip.");
        VerifyEncryptDictionary(encrypt!, security);

        var handler = outDoc.InitializeSecurity();
        Check(handler.PermanentId.AsSpan().SequenceEqual(plan.Identifier.Permanent), "the security handler read a different /ID[0].");

        // Passwords: wrong fails; user and owner authenticate as intended, through the exact bytes and the public string path.
        var sameBytes = security.UserPassword.AsSpan().SequenceEqual(security.OwnerPassword);
        Check(outDoc.Authenticate(CreateWrongPassword(security.UserPasswordText, security.OwnerPasswordText)) == PdfPasswordMatch.None, "a wrong password was accepted.");

        var expectedUser = sameBytes ? PdfPasswordMatch.Owner : PdfPasswordMatch.User;
        Check(handler.Authenticate(security.UserPassword) == expectedUser, "the user password (exact bytes) does not authenticate.");
        if (security.UserPasswordText is not null)
        {
            Check(outDoc.Authenticate(security.UserPasswordText) == expectedUser, "the user password does not authenticate.");
        }

        if (security.OwnerPasswordText is not null)
        {
            Check(outDoc.Authenticate(security.OwnerPasswordText) == PdfPasswordMatch.Owner, "the owner password does not authenticate.");
        }

        Check(outDoc.Authenticate(new[] { security.OwnerPassword }) == PdfPasswordMatch.Owner, "the owner password (exact bytes) does not authenticate.");
        if (handler.R >= 5)
        {
            Check(handler.PermsValid == true, "/Perms does not validate.");
        }

        Check(handler.P == security.P, "/P mismatch.");
    }

    private static void VerifyPreservedEncryption(PdfDocument outDoc, RewritePlan plan, PreservedOutputEncryption security, PdfDictionary writtenEncryptDict)
    {
        Check(outDoc.Trailer[PdfName.Encrypt] is PdfReference er && er.Id.Number == plan.EncryptNumber, "wrong /Encrypt reference.");
        var encrypt = outDoc.GetEncryptDictionary();
        Check(encrypt is not null && ObjectGraph.DeepEquals(encrypt, writtenEncryptDict) && ObjectGraph.DeepEquals(encrypt, security.EncryptDictionary), "the preserved /Encrypt dictionary did not round-trip unchanged.");

        var handler = outDoc.InitializeSecurity();
        Check(handler.PermanentId.AsSpan().SequenceEqual(plan.Identifier.Permanent), "the security handler read a different /ID[0].");
        Check(outDoc.Authenticate(CreateWrongPassword(security.OwnerPasswordText, null)) == PdfPasswordMatch.None, "a wrong password was accepted.");
        if (security.RecoveredUserPassword is { } user)
        {
            Check(handler.Authenticate(user) != PdfPasswordMatch.None, "the preserved user password does not authenticate.");
        }

        Check(outDoc.Authenticate(security.OwnerPasswordText) == PdfPasswordMatch.Owner, "the owner password does not authenticate.");
        if (handler.R >= 5)
        {
            Check(handler.PermsValid == true, "/Perms does not validate.");
        }

        Check(handler.P == security.Handler.P && handler.EncryptMetadata == security.Handler.EncryptMetadata, "the preserved permissions changed.");
    }

    private static byte[]? SafeXmp(PdfDocument doc)
    {
        try
        {
            return XmpReader.ReadRaw(doc);
        }
        catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException)
        {
            return null;
        }
    }

    private void VerifyObject(PdfDocument outDoc, RewritePlan plan, int number)
    {
        var id = plan.IdOf(number);
        if (plan.Packed.TryGetValue(number, out var packed))
        {
            Check(outDoc.Xref.TryGetValue(number, out var compressed) && compressed.Type == XrefEntryType.Compressed && compressed.StreamNumber == packed.Stream && compressed.StreamIndex == packed.Index, $"object {number} is not in its object stream.");
        }
        else
        {
            Check(outDoc.Xref.TryGetValue(number, out var entry) && entry.Type == XrefEntryType.InFile && entry.Generation == id.Generation, $"object {number} is missing from the output.");
        }

        var written = outDoc.GetObject(number);
        var (expected, sourceStream, replacementData) = plan.Transform(number);
        Check(written is not null, $"object {number} cannot be read back.");

        if (expected is PdfStream expectedStream)
        {
            Check(written is PdfStream, $"object {number} should be a stream.");
            var writtenStream = (PdfStream)written!;
            Check(ObjectGraph.DeepEquals(expectedStream.Dictionary, writtenStream.Dictionary, ignoreKey: "Length"), $"the dictionary of stream {number} did not round-trip.");
            var writtenLength = writtenStream.Dictionary[PdfName.Length] as PdfInteger;
            Check(writtenLength is not null && writtenLength.Value == writtenStream.SourceLength, $"the /Length of stream {number} is wrong.");

            var outData = outDoc.GetStreamData(writtenStream, id);
            var srcData = replacementData ?? plan.Source.GetStreamData(sourceStream!, id);
            Check(outData.AsSpan().SequenceEqual(srcData), $"the data of stream {number} differs from the source.");

            var filters = StreamFilters.GetFilterNames(writtenStream.Dictionary, outDoc.Resolve);
            if (filters.Count > 0 && filters[0] is "FlateDecode" or "Fl")
            {
                if (!TryInflate(outData) && TryInflate(srcData))
                {
                    Check(false, $"stream {number} does not decompress.");
                }
            }

            return;
        }

        Check(ObjectGraph.DeepEquals(expected, written), $"object {number} did not round-trip.");
    }

    private bool TryInflate(byte[] data)
    {
        try
        {
            StreamFilters.FlateDecode(data, _lib.MaxDecompressedStreamBytes, strict: false);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (PdfLimitExceededException)
        {
            // Too large to inflate within limits; not a decryption failure.
            return true;
        }
    }

    private static string CreateWrongPassword(string? first, string? second)
    {
        while (true)
        {
            var candidate = "wrong-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
            if (candidate != first && candidate != second)
            {
                return candidate;
            }
        }
    }

    private static void VerifyEncryptDictionary(PdfDictionary encrypt, NewOutputEncryption security)
    {
        long Int(string key) => encrypt[key] is PdfInteger i ? i.Value : -1;
        string? Name(PdfDictionary d, string key) => (d[key] as PdfName)?.Value;
        int Len(string key) => encrypt[key] is PdfString s ? s.Value.Length : -1;

        Check(Name(encrypt, "Filter") == "Standard", "/Filter must be /Standard.");
        Check(Int("P") == security.P, "/P mismatch.");
        switch (security.Revision)
        {
            case 6:
                Check(Int("V") == 5 && Int("R") == 6 && Int("Length") == 256, "R6 requires /V 5 /R 6 /Length 256.");
                Check(Len("O") == 48 && Len("U") == 48 && Len("OE") == 32 && Len("UE") == 32 && Len("Perms") == 16, "R6 /O /U /OE /UE /Perms have wrong lengths.");
                VerifyCryptFilter(encrypt, "AESV3", 32);
                break;
            case 4:
                Check(Int("V") == 4 && Int("R") == 4 && Int("Length") == 128, "R4 requires /V 4 /R 4 /Length 128.");
                Check(Len("O") == 32 && Len("U") == 32, "R4 /O /U have wrong lengths.");
                VerifyCryptFilter(encrypt, "AESV2", 16);
                break;
            case 3:
                Check(Int("V") == 2 && Int("R") == 3 && Int("Length") == 128, "R3 requires /V 2 /R 3 /Length 128.");
                Check(Len("O") == 32 && Len("U") == 32, "R3 /O /U have wrong lengths.");
                Check(!encrypt.ContainsKey("CF"), "R3 must not have crypt filters.");
                break;
        }

        foreach (var key in new[] { "O", "U", "OE", "UE", "Perms" })
        {
            Check(encrypt[key] is null or PdfString { IsHex: true }, $"/{key} must be a hex string.");
        }

        if (security.EncryptMetadata)
        {
            Check(!encrypt.ContainsKey("EncryptMetadata"), "/EncryptMetadata must be omitted when metadata is encrypted.");
        }
        else
        {
            Check(encrypt["EncryptMetadata"] is PdfBoolean { Value: false }, "/EncryptMetadata false expected.");
        }

        static void VerifyCryptFilter(PdfDictionary encrypt, string cfm, int length)
        {
            Check((encrypt["StmF"] as PdfName)?.Value == "StdCF" && (encrypt["StrF"] as PdfName)?.Value == "StdCF", "/StmF and /StrF must be /StdCF.");
            Check(encrypt["CF"] is PdfDictionary cf && cf["StdCF"] is PdfDictionary std
                && (std["CFM"] as PdfName)?.Value == cfm
                && (std["AuthEvent"] as PdfName)?.Value == "DocOpen"
                && std["Length"] is PdfInteger { Value: var l } && l == length, $"/CF /StdCF must be << /CFM /{cfm} /AuthEvent /DocOpen /Length {length} >>.");
        }
    }
}
