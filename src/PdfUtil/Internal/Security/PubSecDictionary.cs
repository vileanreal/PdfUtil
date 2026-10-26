using PdfUtil.Internal.Objects;

namespace PdfUtil.Internal.Security;

/// <summary>
/// The parts of a public-key (<c>/Adobe.PubSec</c>) encryption dictionary (ISO 32000-1 §7.6.4): algorithm, key
/// length, crypt filters and the PKCS#7 recipient blobs. Reading it needs no key; opening the document needs one of
/// the recipients' private keys.
/// </summary>
internal sealed class PubSecDictionary
{
    public int V { get; private init; }

    public int KeyLengthBits { get; private init; }

    public string? SubFilter { get; private init; }

    public bool EncryptMetadata { get; private set; } = true;

    public Dictionary<string, CryptMethod> Filters { get; } = new(StringComparer.Ordinal);

    public string StreamFilter { get; private init; } = "Identity";

    public string StringFilter { get; private init; } = "Identity";

    public string EmbeddedFileFilter { get; private init; } = "Identity";

    /// <summary>The PKCS#7 enveloped-data blobs, in order (their order is part of the key derivation).</summary>
    public List<byte[]> Recipients { get; } = new();

    public PdfEncryptionAlgorithm Algorithm => V >= 5 ? PdfEncryptionAlgorithm.Aes256 : (Filters.TryGetValue(StreamFilter, out var m) ? m : CryptMethod.Rc4) switch
    {
        CryptMethod.AesV3 => PdfEncryptionAlgorithm.Aes256,
        CryptMethod.AesV2 => PdfEncryptionAlgorithm.Aes128,
        _ => KeyLengthBits <= 40 ? PdfEncryptionAlgorithm.Rc4_40 : V >= 4 ? PdfEncryptionAlgorithm.Rc4_128CryptFilter : PdfEncryptionAlgorithm.Rc4_128,
    };

    public static PubSecDictionary Parse(PdfDictionary dict, Func<PdfObject?, PdfObject?> resolve)
    {
        long Int(PdfDictionary d, string key, long fallback) => resolve(d[key]) is PdfInteger i ? i.Value : fallback;
        var v = (int)Int(dict, "V", 0);
        var length = v >= 5 ? 256 : (int)Int(dict, "Length", 40);
        if (length is > 0 and <= 16)
        {
            length *= 8;
        }

        if (v is not (1 or 2 or 4 or 5))
        {
            throw new PdfUnsupportedEncryptionException($"Public-key encryption with V={v} is not supported.");
        }

        var encryptMetadata = resolve(dict["EncryptMetadata"]) is not PdfBoolean { Value: false };
        var result = new PubSecDictionary
        {
            V = v,
            KeyLengthBits = Math.Clamp(length, 40, 256),
            SubFilter = (resolve(dict["SubFilter"]) as PdfName)?.Value,
            StreamFilter = (resolve(dict["StmF"]) as PdfName)?.Value ?? (v >= 4 ? "Identity" : "Identity"),
            StringFilter = (resolve(dict["StrF"]) as PdfName)?.Value ?? "Identity",
            EmbeddedFileFilter = (resolve(dict["EFF"]) as PdfName)?.Value ?? (resolve(dict["StmF"]) as PdfName)?.Value ?? "Identity",
            EncryptMetadata = encryptMetadata,
        };

        void AddRecipients(PdfObject? obj)
        {
            switch (resolve(obj))
            {
                case PdfString s:
                    result.Recipients.Add(s.Value);
                    break;
                case PdfArray a:
                    result.Recipients.AddRange(a.Items.Select(resolve).OfType<PdfString>().Select(s => s.Value));
                    break;
            }
        }

        if (v >= 4 && resolve(dict["CF"]) is PdfDictionary cf)
        {
            foreach (var (name, value) in cf.Entries)
            {
                if (resolve(value) is not PdfDictionary filter)
                {
                    continue;
                }

                result.Filters[name.Value] = (resolve(filter["CFM"]) as PdfName)?.Value switch
                {
                    "V2" => CryptMethod.Rc4,
                    "AESV2" => CryptMethod.AesV2,
                    "AESV3" => CryptMethod.AesV3,
                    _ => CryptMethod.Identity,
                };
                if (name.Value == result.StreamFilter)
                {
                    AddRecipients(filter["Recipients"]);
                    result.EncryptMetadata = resolve(filter["EncryptMetadata"]) is not PdfBoolean { Value: false } && encryptMetadata;
                }
            }
        }

        if (result.Recipients.Count == 0)
        {
            AddRecipients(dict["Recipients"]);
        }

        return result;
    }
}
