using System.Security.Cryptography;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Security;
using PdfUtil.Internal.Text;

namespace PdfUtil.Internal.Protection;

/// <summary>
/// The trailer /ID pair as raw bytes. The permanent part is fixed before any key is derived and is never changed
/// afterwards; the same bytes feed Algorithm 2 and the output trailer.
/// </summary>
internal sealed class FileIdentifier
{
    private readonly byte[] _permanent;
    private readonly byte[] _changing;

    public FileIdentifier(byte[] permanent, byte[] changing, bool preserved)
    {
        _permanent = (byte[])permanent.Clone();
        _changing = (byte[])changing.Clone();
        Preserved = preserved;
    }

    /// <summary>Whether the permanent ID was taken from the source (rather than generated).</summary>
    public bool Preserved { get; }

    public ReadOnlySpan<byte> Permanent => _permanent;

    public ReadOnlySpan<byte> Changing => _changing;

    public byte[] PermanentCopy() => (byte[])_permanent.Clone();

    public byte[] ChangingCopy() => (byte[])_changing.Clone();
}

/// <summary>
/// An object as it is written: the plain object, the source stream (for streams) and, when the stream's data is
/// replaced, the new data.
/// </summary>
internal readonly record struct TransformedObject(PdfObject Plain, PdfStream? SourceStream, byte[]? Data);

/// <summary>How the output of a rewrite is secured.</summary>
internal abstract class OutputSecurity : IDisposable
{
    public virtual void Dispose()
    {
    }
}

/// <summary>The output is not encrypted.</summary>
internal sealed class NoOutputEncryption : OutputSecurity
{
    public static readonly NoOutputEncryption Instance = new();

    private NoOutputEncryption()
    {
    }
}

/// <summary>The output gets a new Standard Security Handler (R3, R4 or R6).</summary>
internal sealed class NewOutputEncryption : OutputSecurity
{
    public required PdfEncryptionAlgorithm Algorithm { get; init; }

    public required int Revision { get; init; }

    public required byte[] UserPassword { get; init; }

    public required byte[] OwnerPassword { get; init; }

    public required bool OwnerPasswordGenerated { get; init; }

    public required int P { get; init; }

    public required bool EncryptMetadata { get; init; }

    /// <summary>The user password as the caller typed it, to verify through the public string path; null if only bytes are known.</summary>
    public string? UserPasswordText { get; init; }

    /// <summary>The owner password as the caller typed it; null if generated or only bytes are known.</summary>
    public string? OwnerPasswordText { get; init; }

    public override void Dispose()
    {
        CryptographicOperations.ZeroMemory(UserPassword);
        CryptographicOperations.ZeroMemory(OwnerPassword);
    }
}

/// <summary>
/// The output keeps the source's encryption exactly: the same <c>/Encrypt</c> dictionary and file key, so all
/// passwords and permissions stay the same. Requires the source to be authenticated with the owner password.
/// </summary>
internal sealed class PreservedOutputEncryption : OutputSecurity
{
    /// <summary>The source's authenticated handler (owned by the source document; not disposed here).</summary>
    public required StandardSecurityHandler Handler { get; init; }

    /// <summary>A fully resolved, direct copy of the source <c>/Encrypt</c> dictionary.</summary>
    public required PdfDictionary EncryptDictionary { get; init; }

    /// <summary>The owner password that authenticated the source.</summary>
    public required string OwnerPasswordText { get; init; }

    /// <summary>R2-R4: the user password recovered from the owner password, to verify the output; null for R5/R6.</summary>
    public byte[]? RecoveredUserPassword { get; init; }

    public override void Dispose()
    {
        if (RecoveredUserPassword is not null)
        {
            CryptographicOperations.ZeroMemory(RecoveredUserPassword);
        }
    }
}

/// <summary>
/// Object-level changes made by an operation before writing: existing objects replaced by new plain objects, and new
/// objects. Objects that are no longer reachable after the changes are not written. "Plain" means unencrypted; the
/// writer encrypts as planned. A replaced stream either keeps the source data (its data is a slice of the source) or
/// carries new, already-filtered data in memory.
/// </summary>
internal sealed class ObjectEdits
{
    private readonly Dictionary<int, PdfObject> _objects = new();
    private readonly int _firstNew;
    private int _next;

    public ObjectEdits(PdfDocument doc)
    {
        var max = doc.Xref.Count == 0 ? 0 : doc.Xref.Keys.Max();
        if (max >= int.MaxValue - 1_000_000)
        {
            throw new PdfLimitExceededException("The document uses object numbers too large to add new objects.");
        }

        _firstNew = _next = max + 1;
    }

    public bool IsEmpty => _objects.Count == 0;

    /// <summary>Every replaced or new object.</summary>
    public IEnumerable<KeyValuePair<int, PdfObject>> Entries => _objects;

    /// <summary>Replaces an existing object.</summary>
    public void Replace(int number, PdfObject obj) => _objects[number] = obj;

    /// <summary>Adds a new object and returns its number (generation 0).</summary>
    public int Add(PdfObject obj)
    {
        var number = _next++;
        _objects[number] = obj;
        return number;
    }

    public bool TryGet(int number, out PdfObject obj) => _objects.TryGetValue(number, out obj!);

    public bool IsNew(int number) => number >= _firstNew;

    /// <summary>The object as the output will contain it: the edit if there is one, otherwise the source object.</summary>
    public PdfObject? Lookup(PdfDocument doc, int number) => _objects.TryGetValue(number, out var o) ? o : doc.GetObject(number);
}

/// <summary>
/// The output is encrypted with a file key from another security handler (public-key encryption): the handler holds
/// the key, the dictionary is written as given, and <see cref="Reauthenticate"/> derives the key again from the written
/// output (with the recipient's certificate) so self-verification proves that the output can be opened.
/// </summary>
internal sealed class KeyedOutputEncryption : OutputSecurity
{
    public required StandardSecurityHandler Handler { get; init; }

    public required PdfDictionary EncryptDictionary { get; init; }

    /// <summary>Opens the written output: returns a handler for it (with its key), or null if it cannot be opened.</summary>
    public required Func<PdfDocument, StandardSecurityHandler?> Reauthenticate { get; init; }

    public override void Dispose() => Handler.Dispose();
}

/// <summary>Everything decided before writing; shared by the writer and the self-verification.</summary>
internal sealed class RewritePlan
{
    public required PdfDocument Source { get; init; }

    public required OutputSecurity Security { get; init; }

    public required FileIdentifier Identifier { get; init; }

    public required PdfVersion SourceVersion { get; init; }

    public required PdfVersion OutputVersion { get; init; }

    public required bool AddAdbeExtension { get; init; }

    public required SortedSet<int> Objects { get; init; }

    public required int CatalogNumber { get; init; }

    /// <summary>Object changes made by the operation, if any.</summary>
    public ObjectEdits? Edits { get; init; }

    public int? InfoNumber { get; init; }

    /// <summary>The Info dictionary to write instead of the source object (direct Info, or requested changes).</summary>
    public PdfDictionary? InfoOverride { get; init; }

    /// <summary>Whether <see cref="InfoNumber"/> is a new object number (no indirect Info in the source).</summary>
    public bool InfoIsNew { get; init; }

    /// <summary>Object number of the catalog XMP stream whose data is replaced by <see cref="XmpOverride"/>.</summary>
    public int? XmpNumber { get; init; }

    /// <summary>Updated XMP packet (uncompressed), when document information changes were applied to it.</summary>
    public byte[]? XmpOverride { get; init; }

    /// <summary>Object number of the <c>/Encrypt</c> dictionary, or null when the output is not encrypted.</summary>
    public int? EncryptNumber { get; init; }

    public List<string> Warnings { get; } = new();

    /// <summary>The pages the output must have, when the operation changes them (rotate, delete); null means the source's pages.</summary>
    public IReadOnlyList<PdfPageInfo>? ExpectedPages { get; init; }

    /// <summary>Pack eligible objects into object streams and write a cross-reference stream.</summary>
    public bool UseObjectStreams { get; init; }

    /// <summary>Set by the writer: objects written inside object streams, number → (object stream, index).</summary>
    public Dictionary<int, (int Stream, int Index)> Packed { get; } = new();

    /// <summary>Set by the writer: the object stream containers and the cross-reference stream.</summary>
    public List<int> ContainerNumbers { get; } = new();

    /// <summary>Set by the writer: the trailer /Size.</summary>
    public int WrittenSize { get; set; }

    public bool IsEncrypted => Security is not NoOutputEncryption;

    public ObjectId IdOf(int number) => InfoIsNew && number == InfoNumber ? new ObjectId(number, 0) : new ObjectId(number, Source.GetGeneration(number));

    /// <summary>All object numbers written, except the encryption dictionary.</summary>
    public IEnumerable<int> AllObjects => InfoIsNew ? Objects.Append(InfoNumber!.Value) : Objects;

    /// <summary>Highest object number written, including the encryption dictionary.</summary>
    public int MaxObjectNumber => Math.Max(EncryptNumber ?? 0, AllObjects.DefaultIfEmpty(0).Max());

    /// <summary>
    /// The plain (unencrypted) object written for <paramref name="number"/>; for streams, the source stream whose
    /// decrypted bytes are its data, or replacement data. Deterministic: the verifier calls it again to know what
    /// to expect.
    /// </summary>
    public TransformedObject Transform(int number)
    {
        if (InfoOverride is not null && number == InfoNumber)
        {
            return new(TransformInfo(InfoOverride), null, null);
        }

        PdfObject src;
        if (Edits is not null && Edits.TryGet(number, out var edited))
        {
            if (edited is PdfStream editedStream)
            {
                var editedDict = editedStream.Dictionary.ShallowClone();
                editedDict.Remove(PdfName.Length);
                if (editedStream.IsInMemory)
                {
                    return new(new PdfStream(editedDict, editedStream.Data!), editedStream, editedStream.Data);
                }

                // A new dictionary over the source data.
                var original = Source.GetObject(number) as PdfStream ?? throw new PdfCorruptException($"Object {number} disappeared while writing.");
                RemoveCryptFilter(editedDict);
                return new(new PdfStream(editedDict, original.SourceOffset, original.SourceLength), original, null);
            }

            src = edited;
        }
        else
        {
            src = Source.GetObject(number) ?? throw new PdfCorruptException($"Object {number} disappeared while writing.");
        }

        if (number == CatalogNumber && src is PdfDictionary catalog)
        {
            return new(TransformCatalog(catalog), null, null);
        }

        if (number == InfoNumber && src is PdfDictionary info)
        {
            return new(TransformInfo(info), null, null);
        }

        if (src is PdfStream stream)
        {
            var dict = stream.Dictionary.ShallowClone();
            dict.Remove(PdfName.Length);
            RemoveCryptFilter(dict);
            if (XmpOverride is not null && number == XmpNumber)
            {
                // The updated packet is written uncompressed, as XMP is meant to be readable by any tool.
                dict.Remove(PdfName.Filter);
                dict.Remove(PdfName.DecodeParms);
                dict.Remove(PdfName.Get("DL"));
                return new(new PdfStream(dict, XmpOverride), stream, XmpOverride);
            }

            return new(new PdfStream(dict, stream.SourceOffset, stream.SourceLength), stream, null);
        }

        return new(src, null, null);
    }

    /// <summary>Whether an output stream is encrypted: never without encryption, and not for metadata when metadata stays readable.</summary>
    public bool IsOutputStreamEncrypted(PdfDictionary plainDict)
    {
        var encryptMetadata = Security switch
        {
            NewOutputEncryption n => n.EncryptMetadata,
            PreservedOutputEncryption p => p.Handler.EncryptMetadata,
            KeyedOutputEncryption k => k.Handler.EncryptMetadata,
            _ => false,
        };
        if (!IsEncrypted)
        {
            return false;
        }

        return encryptMetadata || (Source.Resolve(plainDict[PdfName.Type]) as PdfName)?.Value != "Metadata";
    }

    private PdfDictionary TransformCatalog(PdfDictionary source)
    {
        var catalog = source.ShallowClone();
        if (catalog.ContainsKey("Version"))
        {
            // Keep the catalog version consistent with the header.
            catalog["Version"] = PdfName.Get(OutputVersion.ToString());
        }

        if (AddAdbeExtension)
        {
            var extensions = Source.ResolveDictionary(catalog["Extensions"])?.ShallowClone() ?? new PdfDictionary();
            var adbe = Source.ResolveDictionary(extensions["ADBE"]);
            var level = Source.Resolve(adbe?["ExtensionLevel"]) is PdfInteger l ? l.Value : 0;
            var baseVersion = (Source.Resolve(adbe?["BaseVersion"]) as PdfName)?.Value;
            if (adbe is null || level < 8 || baseVersion != "1.7")
            {
                extensions["ADBE"] = new PdfDictionary
                {
                    ["BaseVersion"] = PdfName.Get("1.7"),
                    ["ExtensionLevel"] = new PdfInteger(8),
                };
            }

            catalog["Extensions"] = extensions;
        }

        return catalog;
    }

    private PdfDictionary TransformInfo(PdfDictionary source)
    {
        if (OutputVersion >= PdfVersion.V2_0)
        {
            return source;
        }

        // UTF-8 text strings are a PDF 2.0 feature: rewrite them as UTF-16BE for older readers.
        PdfDictionary? copy = null;
        foreach (var (key, value) in source.Entries)
        {
            if (value is PdfString s && TextString.HasUtf8Bom(s.Value))
            {
                copy ??= source.ShallowClone();
                copy[key] = new PdfString(TextString.DowngradeUtf8(s.Value), s.IsHex);
            }
        }

        return copy ?? source;
    }

    private void RemoveCryptFilter(PdfDictionary dict)
    {
        var filter = Source.Resolve(dict[PdfName.Filter]);
        if (filter is PdfName { Value: "Crypt" })
        {
            dict.Remove(PdfName.Filter);
            dict.Remove(PdfName.DecodeParms);
            return;
        }

        if (filter is not PdfArray filters)
        {
            return;
        }

        var parms = Source.Resolve(dict[PdfName.DecodeParms]) as PdfArray;
        var newFilters = new PdfArray();
        var newParms = new PdfArray();
        var changed = false;
        for (var i = 0; i < filters.Count; i++)
        {
            if (Source.Resolve(filters[i]) is PdfName { Value: "Crypt" })
            {
                changed = true;
                continue;
            }

            newFilters.Add(filters[i]);
            if (parms is not null)
            {
                newParms.Add(i < parms.Count ? parms[i] : PdfNull.Instance);
            }
        }

        if (!changed)
        {
            return;
        }

        if (newFilters.Count == 0)
        {
            dict.Remove(PdfName.Filter);
            dict.Remove(PdfName.DecodeParms);
            return;
        }

        dict[PdfName.Filter] = newFilters;
        if (parms is not null)
        {
            dict[PdfName.DecodeParms] = newParms;
        }
    }
}
