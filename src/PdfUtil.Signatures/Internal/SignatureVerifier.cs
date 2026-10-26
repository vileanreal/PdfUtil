using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PdfUtil.Internal;
using PdfUtil.Internal.Content;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;
using PdfUtil.Internal.Text;

namespace PdfUtil.Signatures.Internal;

/// <summary>Verifies signatures and document timestamps (F17).</summary>
internal sealed class SignatureVerifier
{
    private const string SigningTimeOid = "1.2.840.113549.1.9.5";
    private const string MessageDigestOid = "1.2.840.113549.1.9.4";
    private const string TimestampTokenOid = "1.2.840.113549.1.9.16.2.14";

    private readonly PdfUtilOptions _lib;
    private readonly PdfLog _log;

    public SignatureVerifier(PdfUtilOptions lib, PdfLog log)
    {
        _lib = lib;
        _log = log;
    }

    public PdfSignatureVerificationResult Verify(PdfSource source, PdfSignatureVerificationOptions options, CancellationToken ct)
    {
        var warnings = new List<string>();
        using var doc = TextExtractionEngine.OpenForReading(source, options.Password, _lib, _log, warnings, ct);
        var pages = PageTreeReader.ReadPageNodes(doc, _lib.MaxNestingDepth, ct);
        var fields = new DetailReader(doc, pages, _lib, ct).ReadFieldNodes();
        var revisionEnds = RevisionEnds(source);
        var results = new List<PdfSignatureValidation>();
        foreach (var field in fields)
        {
            if (field.FieldType != "Sig" || doc.ResolveDictionary(field.Value) is not { } sig)
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();
            results.Add(VerifyOne(doc, source, field.FullName, sig, revisionEnds, options, ct));
        }

        return new PdfSignatureVerificationResult { Signatures = results, Warnings = warnings };
    }

    /// <summary>The file offsets just after each <c>%%EOF</c> (and its end-of-line), which end the revisions.</summary>
    internal static List<long> RevisionEnds(PdfSource source)
    {
        var ends = new List<long>();
        long from = 0;
        while (from < source.Length)
        {
            var at = source.IndexOf("%%EOF"u8, from, source.Length);
            if (at < 0)
            {
                break;
            }

            var end = at + 5;
            if (end < source.Length && source.PeekAt(end) == '\r')
            {
                end++;
            }

            if (end < source.Length && source.PeekAt(end) == '\n')
            {
                end++;
            }

            ends.Add(end);
            from = at + 5;
        }

        return ends;
    }

    private PdfSignatureValidation VerifyOne(PdfDocument doc, PdfSource source, string fieldName, PdfDictionary sig, List<long> revisionEnds, PdfSignatureVerificationOptions options, CancellationToken ct)
    {
        var problems = new List<string>();
        var subFilter = (doc.Resolve(sig["SubFilter"]) as PdfName)?.Value;
        var isTimestamp = subFilter == "ETSI.RFC3161" || (doc.Resolve(sig[PdfName.Type]) as PdfName)?.Value == "DocTimeStamp";
        var range = (doc.Resolve(sig["ByteRange"]) as PdfArray)?.Items.Select(i => doc.Resolve(i) is PdfInteger n ? n.Value : -1).ToArray() ?? Array.Empty<long>();
        var contents = (doc.Resolve(sig["Contents"]) as PdfString)?.Value ?? Array.Empty<byte>();
        var claimed = doc.Resolve(sig["M"]) is PdfString m ? PdfDate.Parse(TextString.Decode(m.Value)) : null;

        // The signed bytes.
        byte[]? data = null;
        if (range.Length < 4 || range.Length % 2 != 0 || range.Any(v => v < 0) || Enumerable.Range(0, range.Length / 2).Any(i => range[2 * i] + range[(2 * i) + 1] > source.Length))
        {
            problems.Add("The byte range is missing or invalid.");
        }
        else
        {
            using var ms = new MemoryStream();
            for (var i = 0; i < range.Length; i += 2)
            {
                ms.Write(source.ReadBytes(range[i], range[i + 1]));
            }

            data = ms.ToArray();
        }

        var signedEnd = range.Length >= 4 ? range[^2] + range[^1] : 0;
        var covers = range.Length == 4 && range[0] == 0 && signedEnd == source.Length;
        var revision = revisionEnds.Count(e => e <= signedEnd);
        if (revision == 0 || !revisionEnds.Contains(signedEnd))
        {
            problems.Add("The signature does not end at the end of a revision.");
        }

        var modified = signedEnd < source.Length;
        var changes = modified && data is not null ? ChangesAfter(doc, source, signedEnd, ct) : new List<string>();

        bool digestMatches = false, signatureValid = false;
        X509Certificate2? signer = null;
        string? digestAlgorithm = null;
        DateTimeOffset? signingTime = null, timestampTime = null;
        bool? timestampValid = null;
        bool? chainValid = null;
        var chainStatus = new List<string>();
        var der = TrimDer(contents);
        if (data is not null && der.Length > 0)
        {
            try
            {
                if (isTimestamp)
                {
                    if (!Rfc3161TimestampToken.TryDecode(der, out var token, out _))
                    {
                        throw new CryptographicException("The document timestamp is not an RFC 3161 timestamp token.");
                    }

                    signatureValid = digestMatches = token.VerifySignatureForData(data, out signer);
                    timestampTime = token.TokenInfo.Timestamp;
                    timestampValid = signatureValid;
                    digestAlgorithm = HashName(token.TokenInfo.HashAlgorithmId.Value);
                    signer ??= token.AsSignedCms().SignerInfos.Count > 0 ? token.AsSignedCms().SignerInfos[0].Certificate : null;
                }
                else
                {
                    var detached = subFilter != "adbe.pkcs7.sha1";
                    var cms = detached ? new SignedCms(new ContentInfo(data), detached: true) : new SignedCms();
                    cms.Decode(der);
                    var info = cms.SignerInfos[0];
                    signer = info.Certificate;
                    digestAlgorithm = HashName(info.DigestAlgorithm.Value);
                    var hasSignedAttributes = info.SignedAttributes.Count > 0;
                    if (detached && hasSignedAttributes)
                    {
                        var expected = info.SignedAttributes.Cast<CryptographicAttributeObject>().FirstOrDefault(a => a.Oid.Value == MessageDigestOid)?.Values[0];
                        var digest = expected is null ? null : ReadOctetString(expected.RawData);
                        digestMatches = digest is not null && Hash(info.DigestAlgorithm.Value, data) is { } actual && CryptographicOperations.FixedTimeEquals(actual, digest);
                    }
                    else
                    {
                        digestMatches = CryptographicOperations.FixedTimeEquals(SHA1.HashData(data), ReadOctetString(cms.ContentInfo.Content));
                    }

                    try
                    {
                        info.CheckSignature(verifySignatureOnly: true);
                        signatureValid = true;
                    }
                    catch (CryptographicException ex)
                    {
                        problems.Add("The signature does not verify: " + ex.Message);
                    }

                    if (detached && !hasSignedAttributes)
                    {
                        // Without signed attributes the signature is computed over the content itself.
                        digestMatches = signatureValid;
                    }

                    if (info.SignedAttributes.Cast<CryptographicAttributeObject>().FirstOrDefault(a => a.Oid.Value == SigningTimeOid) is { } time)
                    {
                        signingTime = new Pkcs9SigningTime(time.Values[0].RawData).SigningTime;
                    }

                    if (info.UnsignedAttributes.Cast<CryptographicAttributeObject>().FirstOrDefault(a => a.Oid.Value == TimestampTokenOid) is { } tsa
                        && Rfc3161TimestampToken.TryDecode(tsa.Values[0].RawData, out var stamp, out _))
                    {
                        timestampTime = stamp.TokenInfo.Timestamp;
                        timestampValid = stamp.VerifySignatureForSignerInfo(info, out _);
                    }
                }
            }
            catch (CryptographicException ex)
            {
                problems.Add("The signature value cannot be decoded: " + ex.Message);
            }
        }
        else if (data is not null)
        {
            problems.Add("The signature has no value.");
        }

        if (data is not null && !digestMatches && der.Length > 0)
        {
            problems.Add("The document was changed after signing: the digest of the signed bytes does not match.");
        }

        if (options.ChainPolicy is { } policy && signer is not null)
        {
            using var chain = new X509Chain { ChainPolicy = policy.Clone() };
            chainValid = chain.Build(signer);
            chainStatus.AddRange(chain.ChainStatus.Select(s => $"{s.Status}: {s.StatusInformation.Trim()}"));
            if (chainValid == false)
            {
                problems.Add("The certificate chain is not valid.");
            }
        }

        if (timestampValid == false)
        {
            problems.Add("The timestamp does not verify.");
        }

        return new PdfSignatureValidation
        {
            FieldName = fieldName,
            SubFilter = subFilter,
            IsDocumentTimestamp = isTimestamp,
            DigestMatches = digestMatches,
            SignatureIsValid = signatureValid,
            CoversWholeFile = covers && revision > 0,
            Revision = revision,
            ModifiedAfterSigning = modified,
            ChangesAfterSigning = changes,
            Signer = signer,
            SignerName = signer?.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
            DigestAlgorithm = digestAlgorithm,
            SigningTime = signingTime,
            ClaimedTime = claimed,
            TimestampTime = timestampTime,
            TimestampIsValid = timestampValid,
            ChainIsValid = chainValid,
            ChainStatus = chainStatus,
            Problems = problems,
        };
    }

    /// <summary>What the revisions after <paramref name="signedEnd"/> changed, by kind of object.</summary>
    private List<string> ChangesAfter(PdfDocument doc, PdfSource source, long signedEnd, CancellationToken ct)
    {
        var kinds = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            using var signedSource = PdfSource.FromBytes(source.ReadBytes(0, signedEnd));
            using var signed = PdfDocument.Open(signedSource, _lib, PdfLog.None, strict: false, ct);
            var pageNumbers = PageTreeReader.ReadPageNodes(doc, _lib.MaxNestingDepth, ct).Select(p => p.ObjectNumber).ToHashSet();
            foreach (var (number, entry) in doc.Xref)
            {
                if (entry.Type == XrefEntryType.Free || (signed.Xref.TryGetValue(number, out var before) && before == entry))
                {
                    continue;
                }

                var obj = doc.GetObject(number);
                var dict = obj switch { PdfStream s => s.Dictionary, PdfDictionary d => d, _ => null };
                var type = (doc.Resolve(dict?[PdfName.Type]) as PdfName)?.Value;
                var subtype = (doc.Resolve(dict?["Subtype"]) as PdfName)?.Value;
                var kind = type switch
                {
                    "XRef" or "ObjStm" => null,
                    "Sig" or "DocTimeStamp" => "signatures added",
                    "Catalog" => "document structure (catalog) changed",
                    "Page" or "Pages" => "pages changed",
                    "Metadata" => "document information or metadata changed",
                    "Annot" when subtype == "Widget" && (doc.Resolve(dict!["FT"]) as PdfName)?.Value == "Sig" => "signatures added",
                    "Annot" when subtype == "Widget" => "form fields filled or changed",
                    "Annot" => "annotations added or changed",
                    _ when dict?.ContainsKey("FT") == true || dict?.ContainsKey("T") == true && dict.ContainsKey("V") => "form fields filled or changed",
                    _ when doc.Trailer[PdfName.Info] is PdfReference info && info.Id.Number == number => "document information or metadata changed",
                    _ when obj is PdfStream && !pageNumbers.Contains(number) => "page content or other streams changed",
                    _ => "other objects changed",
                };
                if (kind is not null)
                {
                    kinds.Add(kind);
                }
            }
        }
        catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException)
        {
            kinds.Add("the signed revision cannot be read on its own");
        }

        return kinds.ToList();
    }

    /// <summary>The DER-encoded value without the zero padding that fills the reserved space.</summary>
    internal static byte[] TrimDer(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 0x30)
        {
            return bytes;
        }

        long length;
        int header;
        if (bytes[1] < 0x80)
        {
            length = bytes[1];
            header = 2;
        }
        else
        {
            var n = bytes[1] & 0x7F;
            if (n is 0 or > 4 || bytes.Length < 2 + n)
            {
                return bytes;
            }

            length = 0;
            for (var i = 0; i < n; i++)
            {
                length = (length << 8) | bytes[2 + i];
            }

            header = 2 + n;
        }

        return header + length <= bytes.Length ? bytes[..(int)(header + length)] : bytes;
    }

    private static byte[] ReadOctetString(byte[] der)
    {
        try
        {
            return System.Formats.Asn1.AsnDecoder.ReadOctetString(der, System.Formats.Asn1.AsnEncodingRules.BER, out _);
        }
        catch (System.Formats.Asn1.AsnContentException)
        {
            return der;
        }
    }

    internal static string? HashName(string? oid) => oid switch
    {
        "1.3.14.3.2.26" => "SHA1",
        "2.16.840.1.101.3.4.2.1" => "SHA256",
        "2.16.840.1.101.3.4.2.2" => "SHA384",
        "2.16.840.1.101.3.4.2.3" => "SHA512",
        _ => oid,
    };

    private static byte[]? Hash(string? oid, byte[] data) => HashName(oid) switch
    {
        "SHA1" => SHA1.HashData(data),
        "SHA256" => SHA256.HashData(data),
        "SHA384" => SHA384.HashData(data),
        "SHA512" => SHA512.HashData(data),
        _ => null,
    };
}
