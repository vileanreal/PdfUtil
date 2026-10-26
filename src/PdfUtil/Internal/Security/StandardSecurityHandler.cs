using System.Security.Cryptography;
using PdfUtil.Internal.Objects;

namespace PdfUtil.Internal.Security;

internal enum CryptMethod
{
    Identity,
    Rc4,
    AesV2,
    AesV3,
}

/// <summary>
/// The Standard Security Handler for one document: parses or creates the <c>/Encrypt</c> dictionary, authenticates
/// passwords and encrypts/decrypts strings and streams. Holds the file key after authentication; dispose to zero it.
/// </summary>
internal sealed class StandardSecurityHandler : IDisposable
{
    private byte[]? _fileKey;

    private StandardSecurityHandler(int v, int r, int keyLengthBits, byte[] o, byte[] u, byte[] oe, byte[] ue, byte[] perms, int p, bool encryptMetadata, byte[] permanentId)
    {
        V = v;
        R = r;
        KeyLengthBits = keyLengthBits;
        O = o;
        U = u;
        OE = oe;
        UE = ue;
        Perms = perms;
        P = p;
        EncryptMetadata = encryptMetadata;
        PermanentId = permanentId;
    }

    public int V { get; }

    public int R { get; }

    public int KeyLengthBits { get; }

    public int KeyLengthBytes => KeyLengthBits / 8;

    public byte[] O { get; }

    public byte[] U { get; }

    public byte[] OE { get; }

    public byte[] UE { get; }

    public byte[] Perms { get; }

    public int P { get; }

    public bool EncryptMetadata { get; }

    /// <summary>The <c>/ID[0]</c> bytes used for key derivation (R2-R4). Never modified after construction.</summary>
    public byte[] PermanentId { get; }

    public Dictionary<string, CryptMethod> CryptFilters { get; } = new(StringComparer.Ordinal);

    public string StreamFilterName { get; private set; } = "Identity";

    public string StringFilterName { get; private set; } = "Identity";

    public string EmbeddedFileFilterName { get; private set; } = "Identity";

    public CryptMethod StreamMethod => ResolveMethod(StreamFilterName);

    public CryptMethod StringMethod => ResolveMethod(StringFilterName);

    public PdfPasswordMatch AuthenticatedAs { get; private set; }

    public bool IsAuthenticated => _fileKey is not null;

    /// <summary>Whether <c>/Perms</c> validated after authentication (R5/R6 only).</summary>
    public bool? PermsValid { get; private set; }

    public PdfEncryptionAlgorithm Algorithm
    {
        get
        {
            if (V >= 5)
            {
                return R == 5 ? PdfEncryptionAlgorithm.Aes256R5 : PdfEncryptionAlgorithm.Aes256;
            }

            if (V == 4)
            {
                return StreamMethod switch
                {
                    CryptMethod.AesV2 => PdfEncryptionAlgorithm.Aes128,
                    CryptMethod.AesV3 => PdfEncryptionAlgorithm.Aes256,
                    CryptMethod.Rc4 => PdfEncryptionAlgorithm.Rc4_128CryptFilter,
                    _ => StringMethod == CryptMethod.AesV2 ? PdfEncryptionAlgorithm.Aes128 : PdfEncryptionAlgorithm.Rc4_128CryptFilter,
                };
            }

            return KeyLengthBits <= 40 ? PdfEncryptionAlgorithm.Rc4_40 : PdfEncryptionAlgorithm.Rc4_128;
        }
    }

    /// <summary>
    /// A handler for a file key obtained by another security handler (public-key encryption): it encrypts and decrypts
    /// with the given crypt filters exactly like the Standard handler, without passwords. The key is copied.
    /// </summary>
    public static StandardSecurityHandler FromFileKey(int v, int keyLengthBits, byte[] fileKey, IReadOnlyDictionary<string, CryptMethod> filters, string streamFilter, string stringFilter, string embeddedFileFilter, int p, bool encryptMetadata)
    {
        var handler = new StandardSecurityHandler(v, 0, keyLengthBits, Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>(), p, encryptMetadata, Array.Empty<byte>())
        {
            StreamFilterName = streamFilter,
            StringFilterName = stringFilter,
            EmbeddedFileFilterName = embeddedFileFilter,
            AuthenticatedAs = PdfPasswordMatch.User,
            _fileKey = (byte[])fileKey.Clone(),
        };
        foreach (var (name, method) in filters)
        {
            handler.CryptFilters[name] = method;
        }

        return handler;
    }

    /// <summary>Whether two authenticated handlers hold the same file key (compared in constant time).</summary>
    public bool HasSameKey(StandardSecurityHandler other) =>
        _fileKey is not null && other._fileKey is not null && CryptographicOperations.FixedTimeEquals(_fileKey, other._fileKey);

    /// <summary>Parses an <c>/Encrypt</c> dictionary. Throws <see cref="PdfUnsupportedEncryptionException"/> for non-Standard handlers.</summary>
    public static StandardSecurityHandler Parse(PdfDictionary dict, byte[] permanentId, Func<PdfObject?, PdfObject?> resolve)
    {
        var filter = (resolve(dict["Filter"]) as PdfName)?.Value;
        if (!string.Equals(filter, "Standard", StringComparison.Ordinal))
        {
            throw new PdfUnsupportedEncryptionException(filter is null
                ? "The document is encrypted with an unidentified security handler."
                : $"The document is encrypted with the '{filter}' security handler; only the Standard (password) security handler is supported.");
        }

        var v = (int)GetInt(dict, "V", 0, resolve);
        var r = (int)GetInt(dict, "R", 0, resolve);
        if (v is not (1 or 2 or 4 or 5))
        {
            throw new PdfUnsupportedEncryptionException($"Encryption algorithm version V={v} is not supported.");
        }

        if (r is < 2 or > 6)
        {
            throw new PdfUnsupportedEncryptionException($"Standard security handler revision R={r} is not supported.");
        }

        var lengthBits = v switch
        {
            1 => 40,
            5 => 256,
            _ => (int)GetInt(dict, "Length", 40, resolve),
        };

        // Some writers give /Length in bytes.
        if (lengthBits is > 0 and <= 16 && v is 2 or 4)
        {
            lengthBits *= 8;
        }

        if (v is 2 or 4 && (lengthBits < 40 || lengthBits > 128 || lengthBits % 8 != 0))
        {
            lengthBits = 128;
        }

        var o = GetBytes(dict, "O", resolve);
        var u = GetBytes(dict, "U", resolve);
        if (o.Length < 32 || u.Length < 32)
        {
            throw new PdfCorruptException("The encryption dictionary has invalid /O or /U entries.");
        }

        var p = unchecked((int)GetInt(dict, "P", -4, resolve));
        var encryptMetadata = resolve(dict["EncryptMetadata"]) is not PdfBoolean { Value: false };
        var handler = new StandardSecurityHandler(v, r, lengthBits, o, u, GetBytes(dict, "OE", resolve), GetBytes(dict, "UE", resolve), GetBytes(dict, "Perms", resolve), p, encryptMetadata, permanentId);

        if (v >= 4)
        {
            if (resolve(dict["CF"]) is PdfDictionary cf)
            {
                foreach (var entry in cf.Entries)
                {
                    if (resolve(entry.Value) is not PdfDictionary filterDict)
                    {
                        continue;
                    }

                    var cfm = (resolve(filterDict["CFM"]) as PdfName)?.Value;
                    var authEvent = (resolve(filterDict["AuthEvent"]) as PdfName)?.Value;
                    if (authEvent is not null && authEvent != "DocOpen" && authEvent != "EFOpen")
                    {
                        throw new PdfUnsupportedEncryptionException($"Crypt filter authentication event '{authEvent}' is not supported.");
                    }

                    handler.CryptFilters[entry.Key.Value] = cfm switch
                    {
                        "V2" => CryptMethod.Rc4,
                        "AESV2" => CryptMethod.AesV2,
                        "AESV3" => CryptMethod.AesV3,
                        "None" or null => CryptMethod.Identity,
                        _ => throw new PdfUnsupportedEncryptionException($"Crypt filter method '{cfm}' is not supported."),
                    };
                }
            }

            handler.StreamFilterName = (resolve(dict["StmF"]) as PdfName)?.Value ?? "Identity";
            handler.StringFilterName = (resolve(dict["StrF"]) as PdfName)?.Value ?? "Identity";
            handler.EmbeddedFileFilterName = (resolve(dict["EFF"]) as PdfName)?.Value ?? handler.StreamFilterName;
            foreach (var name in new[] { handler.StreamFilterName, handler.StringFilterName, handler.EmbeddedFileFilterName })
            {
                if (name != "Identity" && !handler.CryptFilters.ContainsKey(name))
                {
                    throw new PdfCorruptException($"The encryption dictionary references an undefined crypt filter '{name}'.");
                }
            }
        }

        if (v >= 5 && (o.Length < 48 || u.Length < 48 || handler.OE.Length < 32 || handler.UE.Length < 32))
        {
            throw new PdfCorruptException("The AES-256 encryption dictionary has invalid /O, /U, /OE or /UE entries.");
        }

        return handler;
    }

    /// <summary>
    /// Creates a new handler and its <c>/Encrypt</c> dictionary. The permanent ID must be final: it is part of the key
    /// for R2-R4 and is never changed afterwards.
    /// </summary>
    public static (StandardSecurityHandler Handler, PdfDictionary EncryptDictionary) Create(int revision, byte[] userPassword, byte[] ownerPassword, int p, byte[] permanentId, bool encryptMetadata)
    {
        PdfDictionary dict = new();
        dict["Filter"] = PdfName.Get("Standard");
        StandardSecurityHandler handler;
        switch (revision)
        {
            case 2:
            case 3:
            case 4:
            {
                var v = revision switch { 2 => 1, 3 => 2, _ => 4 };
                var lengthBits = revision == 2 ? 40 : 128;
                var keyBytes = lengthBits / 8;
                var o = SecurityAlgorithms.ComputeOwnerValueR2to4(ownerPassword, userPassword, revision, keyBytes);
                var meta = revision < 4 || encryptMetadata;
                var key = SecurityAlgorithms.ComputeFileKeyR2to4(userPassword, o, p, permanentId, revision, keyBytes, meta);
                var u = SecurityAlgorithms.ComputeUserValueR2to4(key, permanentId, revision);
                handler = new StandardSecurityHandler(v, revision, lengthBits, o, u, Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>(), p, meta, (byte[])permanentId.Clone())
                {
                    _fileKey = key,
                    AuthenticatedAs = PdfPasswordMatch.Owner,
                };
                dict["V"] = new PdfInteger(v);
                dict["R"] = new PdfInteger(revision);
                if (revision >= 3)
                {
                    dict["Length"] = new PdfInteger(lengthBits);
                }

                if (revision == 4)
                {
                    handler.CryptFilters["StdCF"] = CryptMethod.AesV2;
                    handler.StreamFilterName = handler.StringFilterName = handler.EmbeddedFileFilterName = "StdCF";
                    dict["CF"] = BuildCryptFilterDict("AESV2", 16);
                    dict["StmF"] = PdfName.Get("StdCF");
                    dict["StrF"] = PdfName.Get("StdCF");
                }

                dict["O"] = new PdfString(o, isHex: true);
                dict["U"] = new PdfString(u, isHex: true);
                dict["P"] = new PdfInteger(p);
                if (revision == 4 && !encryptMetadata)
                {
                    dict["EncryptMetadata"] = PdfBoolean.False;
                }

                break;
            }

            case 5:
            case 6:
            {
                var fileKey = RandomNumberGenerator.GetBytes(32);
                var salts = RandomNumberGenerator.GetBytes(32);
                var (u, ue) = SecurityAlgorithms.ComputeUserValuesR6(revision, userPassword, fileKey, salts.AsSpan(0, 8), salts.AsSpan(8, 8));
                var (o, oe) = SecurityAlgorithms.ComputeOwnerValuesR6(revision, ownerPassword, fileKey, u, salts.AsSpan(16, 8), salts.AsSpan(24, 8));
                var perms = SecurityAlgorithms.ComputePerms(fileKey, p, encryptMetadata, RandomNumberGenerator.GetBytes(4));
                CryptographicOperations.ZeroMemory(salts);
                handler = new StandardSecurityHandler(5, revision, 256, o, u, oe, ue, perms, p, encryptMetadata, (byte[])permanentId.Clone())
                {
                    _fileKey = fileKey,
                    AuthenticatedAs = PdfPasswordMatch.Owner,
                };
                handler.CryptFilters["StdCF"] = CryptMethod.AesV3;
                handler.StreamFilterName = handler.StringFilterName = handler.EmbeddedFileFilterName = "StdCF";
                dict["V"] = new PdfInteger(5);
                dict["R"] = new PdfInteger(revision);
                dict["Length"] = new PdfInteger(256);
                dict["CF"] = BuildCryptFilterDict("AESV3", 32);
                dict["StmF"] = PdfName.Get("StdCF");
                dict["StrF"] = PdfName.Get("StdCF");
                dict["O"] = new PdfString(o, isHex: true);
                dict["U"] = new PdfString(u, isHex: true);
                dict["OE"] = new PdfString(oe, isHex: true);
                dict["UE"] = new PdfString(ue, isHex: true);
                dict["Perms"] = new PdfString(perms, isHex: true);
                dict["P"] = new PdfInteger(p);
                if (!encryptMetadata)
                {
                    dict["EncryptMetadata"] = PdfBoolean.False;
                }

                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(revision));
        }

        return (handler, dict);
    }

    private static PdfDictionary BuildCryptFilterDict(string cfm, int lengthBytes)
    {
        var std = new PdfDictionary
        {
            ["CFM"] = PdfName.Get(cfm),
            ["AuthEvent"] = PdfName.Get("DocOpen"),
            ["Length"] = new PdfInteger(lengthBytes),
        };
        var cf = new PdfDictionary { ["StdCF"] = std };
        return cf;
    }

    /// <summary>Tries each candidate encoding of a password; owner before user. Sets the file key on success.</summary>
    public PdfPasswordMatch Authenticate(IEnumerable<byte[]> candidates)
    {
        foreach (var candidate in candidates)
        {
            var match = Authenticate(candidate);
            if (match != PdfPasswordMatch.None)
            {
                return match;
            }
        }

        return PdfPasswordMatch.None;
    }

    /// <summary>Authenticates one encoded password. Owner is checked first.</summary>
    public PdfPasswordMatch Authenticate(ReadOnlySpan<byte> password)
    {
        byte[]? key;
        PdfPasswordMatch match;
        if (R >= 5)
        {
            (key, match) = SecurityAlgorithms.AuthenticateR6(R, password, O, U, OE, UE);
        }
        else
        {
            key = SecurityAlgorithms.AuthenticateOwnerR2to4(password, O, U, P, PermanentId, R, KeyLengthBytes, EncryptMetadata);
            match = PdfPasswordMatch.Owner;
            if (key is null)
            {
                key = SecurityAlgorithms.AuthenticateUserR2to4(password, O, U, P, PermanentId, R, KeyLengthBytes, EncryptMetadata);
                match = key is null ? PdfPasswordMatch.None : PdfPasswordMatch.User;
            }
        }

        if (key is null)
        {
            return PdfPasswordMatch.None;
        }

        if (_fileKey is not null)
        {
            CryptographicOperations.ZeroMemory(_fileKey);
        }

        _fileKey = key;
        AuthenticatedAs = match;
        if (R >= 5)
        {
            PermsValid = Perms.Length >= 16 && SecurityAlgorithms.ValidatePerms(key, Perms, P, EncryptMetadata);
        }

        return match;
    }

    /// <summary>Encrypts a string of object <paramref name="id"/>.</summary>
    public byte[] EncryptString(byte[] data, ObjectId id) => Encrypt(data, id, StringMethod);

    /// <summary>Encrypts a stream of object <paramref name="id"/> with the default stream filter.</summary>
    public byte[] EncryptStream(byte[] data, ObjectId id) => Encrypt(data, id, StreamMethod);

    /// <summary>Encrypts a stream with a named crypt filter (for example <c>/EFF</c> for embedded files).</summary>
    public byte[] EncryptStream(byte[] data, ObjectId id, string cryptFilterName) => Encrypt(data, id, ResolveMethod(cryptFilterName));

    /// <summary>
    /// R2-R4 only: recovers the user password bytes from the owner password (Algorithm 7 yields the padded user
    /// password). Returns null for R5/R6, where the user password cannot be recovered, or if the owner password is wrong.
    /// </summary>
    public byte[]? RecoverUserPassword(ReadOnlySpan<byte> ownerPassword)
    {
        if (R >= 5)
        {
            return null;
        }

        var padded = SecurityAlgorithms.DecryptOwnerValueR2to4(ownerPassword, O, R, KeyLengthBytes);
        if (padded is null)
        {
            return null;
        }

        var user = SecurityAlgorithms.UnpadPassword(padded);
        CryptographicOperations.ZeroMemory(padded);
        var key = SecurityAlgorithms.AuthenticateUserR2to4(user, O, U, P, PermanentId, R, KeyLengthBytes, EncryptMetadata);
        if (key is null)
        {
            CryptographicOperations.ZeroMemory(user);
            return null;
        }

        CryptographicOperations.ZeroMemory(key);
        return user;
    }

    public byte[] DecryptString(byte[] data, ObjectId id, bool strict) => Decrypt(data, id, StringMethod, strict);

    /// <summary>Decrypts stream data, using a named crypt filter when the stream has a <c>/Crypt</c> filter.</summary>
    public byte[] DecryptStream(byte[] data, ObjectId id, string? cryptFilterName, bool strict)
    {
        var method = cryptFilterName is null ? StreamMethod : ResolveMethod(cryptFilterName);
        return Decrypt(data, id, method, strict);
    }

    public CryptMethod ResolveMethod(string filterName)
    {
        if (V < 4)
        {
            return CryptMethod.Rc4;
        }

        if (filterName == "Identity")
        {
            return CryptMethod.Identity;
        }

        return CryptFilters.TryGetValue(filterName, out var m) ? m : CryptMethod.Identity;
    }

    private byte[] Encrypt(byte[] data, ObjectId id, CryptMethod method)
    {
        var fileKey = _fileKey ?? throw new InvalidOperationException("The security handler is not authenticated.");
        switch (method)
        {
            case CryptMethod.Identity:
                return data;
            case CryptMethod.AesV3:
                return AesCipher.EncryptWithRandomIv(fileKey, data);
            case CryptMethod.AesV2:
            {
                var key = SecurityAlgorithms.ComputeObjectKey(fileKey, id.Number, id.Generation, aes: true);
                var result = AesCipher.EncryptWithRandomIv(key, data);
                CryptographicOperations.ZeroMemory(key);
                return result;
            }

            default:
            {
                var key = SecurityAlgorithms.ComputeObjectKey(fileKey, id.Number, id.Generation, aes: false);
                var result = Rc4.Transform(key, data);
                CryptographicOperations.ZeroMemory(key);
                return result;
            }
        }
    }

    private byte[] Decrypt(byte[] data, ObjectId id, CryptMethod method, bool strict)
    {
        var fileKey = _fileKey ?? throw new InvalidOperationException("The security handler is not authenticated.");
        switch (method)
        {
            case CryptMethod.Identity:
                return data;
            case CryptMethod.AesV3:
                return strict ? AesCipher.DecryptWithIv(fileKey, data) : AesCipher.DecryptWithIvLenient(fileKey, data);
            case CryptMethod.AesV2:
            {
                var key = SecurityAlgorithms.ComputeObjectKey(fileKey, id.Number, id.Generation, aes: true);
                try
                {
                    return strict ? AesCipher.DecryptWithIv(key, data) : AesCipher.DecryptWithIvLenient(key, data);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }

            default:
            {
                var key = SecurityAlgorithms.ComputeObjectKey(fileKey, id.Number, id.Generation, aes: false);
                var result = Rc4.Transform(key, data);
                CryptographicOperations.ZeroMemory(key);
                return result;
            }
        }
    }

    private static long GetInt(PdfDictionary d, string key, long fallback, Func<PdfObject?, PdfObject?> resolve) => resolve(d[key]) switch
    {
        PdfInteger i => i.Value,
        PdfReal r => (long)r.Value,
        _ => fallback,
    };

    private static byte[] GetBytes(PdfDictionary d, string key, Func<PdfObject?, PdfObject?> resolve) =>
        resolve(d[key]) is PdfString s ? s.Value : Array.Empty<byte>();

    public void Dispose()
    {
        if (_fileKey is not null)
        {
            CryptographicOperations.ZeroMemory(_fileKey);
            _fileKey = null;
        }
    }
}
