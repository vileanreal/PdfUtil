using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PdfUtil.Internal.Security;

/// <summary>
/// The Standard Security Handler algorithms of ISO 32000-1 §7.6.3/§7.6.4 (R2-R4) and ISO 32000-2 §7.6.4.3 (R5/R6),
/// implemented from the specification. All functions are pure so that they can be tested against known answers.
/// </summary>
internal static class SecurityAlgorithms
{
    /// <summary>The 32-byte padding string of Algorithm 2, step a.</summary>
    public static ReadOnlySpan<byte> PasswordPadding => new byte[]
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    };

    private static ReadOnlySpan<byte> SaltAes => "sAlT"u8;

    /// <summary>Algorithm 2 step a: pads or truncates the password to exactly 32 bytes.</summary>
    public static byte[] PadPassword(ReadOnlySpan<byte> password)
    {
        var result = new byte[32];
        var n = Math.Min(32, password.Length);
        password[..n].CopyTo(result);
        PasswordPadding[..(32 - n)].CopyTo(result.AsSpan(n));
        return result;
    }

    /// <summary>
    /// Computes the <c>/P</c> value: permission bits plus the reserved bits that must be 1 (7, 8, 13-32), with bits 1-2 clear.
    /// </summary>
    public static int ComputeP(PdfPermissions permissions)
    {
        var bits = (uint)permissions & (uint)PdfPermissions.All;
        bits |= 0xFFFFF0C0u; // bits 7-8 and 13-32
        return unchecked((int)bits);
    }

    /// <summary>Algorithm 1: object key for RC4 or AES-128 from the file key, object number and generation.</summary>
    public static byte[] ComputeObjectKey(ReadOnlySpan<byte> fileKey, int objectNumber, int generation, bool aes)
    {
        Span<byte> input = stackalloc byte[fileKey.Length + 9];
        fileKey.CopyTo(input);
        var p = fileKey.Length;
        input[p++] = (byte)objectNumber;
        input[p++] = (byte)(objectNumber >> 8);
        input[p++] = (byte)(objectNumber >> 16);
        input[p++] = (byte)generation;
        input[p++] = (byte)(generation >> 8);
        if (aes)
        {
            SaltAes.CopyTo(input[p..]);
            p += 4;
        }

        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(input[..p], hash);
        var keyLength = Math.Min(fileKey.Length + 5, 16);
        var key = hash[..keyLength].ToArray();
        CryptographicOperations.ZeroMemory(input);
        CryptographicOperations.ZeroMemory(hash);
        return key;
    }

    /// <summary>Algorithm 2: file encryption key for R2-R4.</summary>
    public static byte[] ComputeFileKeyR2to4(ReadOnlySpan<byte> password, ReadOnlySpan<byte> ownerValue, int p, ReadOnlySpan<byte> permanentId, int revision, int keyLengthBytes, bool encryptMetadata)
    {
        var padded = PadPassword(password);
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(padded);
        md5.AppendData(ownerValue.Length > 32 ? ownerValue[..32] : ownerValue);
        Span<byte> pBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(pBytes, p);
        md5.AppendData(pBytes);
        md5.AppendData(permanentId);
        if (revision >= 4 && !encryptMetadata)
        {
            md5.AppendData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        }

        var hash = md5.GetHashAndReset();
        var n = revision == 2 ? 5 : keyLengthBytes;
        if (revision >= 3)
        {
            for (var i = 0; i < 50; i++)
            {
                hash = MD5.HashData(hash.AsSpan(0, n));
            }
        }

        CryptographicOperations.ZeroMemory(padded);
        var key = hash.AsSpan(0, n).ToArray();
        CryptographicOperations.ZeroMemory(hash);
        return key;
    }

    /// <summary>Algorithm 3 steps a-d: the RC4 key derived from the owner password.</summary>
    private static byte[] ComputeOwnerRc4Key(ReadOnlySpan<byte> ownerPassword, int revision, int keyLengthBytes)
    {
        var padded = PadPassword(ownerPassword);
        var hash = MD5.HashData(padded);
        if (revision >= 3)
        {
            for (var i = 0; i < 50; i++)
            {
                hash = MD5.HashData(hash);
            }
        }

        var n = revision == 2 ? 5 : keyLengthBytes;
        var key = hash.AsSpan(0, n).ToArray();
        CryptographicOperations.ZeroMemory(padded);
        CryptographicOperations.ZeroMemory(hash);
        return key;
    }

    /// <summary>Algorithm 3: the <c>/O</c> value for R2-R4. An empty owner password falls back to the user password.</summary>
    public static byte[] ComputeOwnerValueR2to4(ReadOnlySpan<byte> ownerPassword, ReadOnlySpan<byte> userPassword, int revision, int keyLengthBytes)
    {
        var key = ComputeOwnerRc4Key(ownerPassword.Length > 0 ? ownerPassword : userPassword, revision, keyLengthBytes);
        var result = Rc4.Transform(key, PadPassword(userPassword));
        if (revision >= 3)
        {
            var iterKey = new byte[key.Length];
            for (var i = 1; i <= 19; i++)
            {
                for (var k = 0; k < key.Length; k++)
                {
                    iterKey[k] = (byte)(key[k] ^ i);
                }

                result = Rc4.Transform(iterKey, result);
            }

            CryptographicOperations.ZeroMemory(iterKey);
        }

        CryptographicOperations.ZeroMemory(key);
        return result;
    }

    /// <summary>Algorithm 4 (R2) and Algorithm 5 (R3/R4): the <c>/U</c> value from the file key. R3+ fills the last 16 bytes with zeros.</summary>
    public static byte[] ComputeUserValueR2to4(ReadOnlySpan<byte> fileKey, ReadOnlySpan<byte> permanentId, int revision)
    {
        if (revision == 2)
        {
            return Rc4.Transform(fileKey, PasswordPadding);
        }

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(PasswordPadding);
        md5.AppendData(permanentId);
        var value = Rc4.Transform(fileKey, md5.GetHashAndReset());
        var iterKey = new byte[fileKey.Length];
        for (var i = 1; i <= 19; i++)
        {
            for (var k = 0; k < fileKey.Length; k++)
            {
                iterKey[k] = (byte)(fileKey[k] ^ i);
            }

            value = Rc4.Transform(iterKey, value);
        }

        CryptographicOperations.ZeroMemory(iterKey);
        var result = new byte[32];
        value.CopyTo(result, 0);
        return result;
    }

    /// <summary>Algorithm 6: authenticates a user password (R2-R4). Returns the file key, or null.</summary>
    public static byte[]? AuthenticateUserR2to4(ReadOnlySpan<byte> password, ReadOnlySpan<byte> ownerValue, ReadOnlySpan<byte> userValue, int p, ReadOnlySpan<byte> permanentId, int revision, int keyLengthBytes, bool encryptMetadata)
    {
        var key = ComputeFileKeyR2to4(password, ownerValue, p, permanentId, revision, keyLengthBytes, encryptMetadata);
        var computed = ComputeUserValueR2to4(key, permanentId, revision);
        var compareLength = revision == 2 ? 32 : 16;
        var ok = userValue.Length >= compareLength && CryptographicOperations.FixedTimeEquals(computed.AsSpan(0, compareLength), userValue[..compareLength]);
        if (ok)
        {
            return key;
        }

        CryptographicOperations.ZeroMemory(key);
        return null;
    }

    /// <summary>Algorithm 7: authenticates an owner password (R2-R4). Returns the file key, or null.</summary>
    public static byte[]? AuthenticateOwnerR2to4(ReadOnlySpan<byte> password, ReadOnlySpan<byte> ownerValue, ReadOnlySpan<byte> userValue, int p, ReadOnlySpan<byte> permanentId, int revision, int keyLengthBytes, bool encryptMetadata)
    {
        var userPassword = DecryptOwnerValueR2to4(password, ownerValue, revision, keyLengthBytes);
        if (userPassword is null)
        {
            return null;
        }

        // The decrypted value is the padded user password; padding the padded value is a no-op.
        var result = AuthenticateUserR2to4(userPassword, ownerValue, userValue, p, permanentId, revision, keyLengthBytes, encryptMetadata);
        CryptographicOperations.ZeroMemory(userPassword);
        return result;
    }

    /// <summary>Algorithm 7 steps a-b: decrypts <c>/O</c> with an owner password, giving the padded user password.</summary>
    public static byte[]? DecryptOwnerValueR2to4(ReadOnlySpan<byte> password, ReadOnlySpan<byte> ownerValue, int revision, int keyLengthBytes)
    {
        if (ownerValue.Length < 32)
        {
            return null;
        }

        var key = ComputeOwnerRc4Key(password, revision, keyLengthBytes);
        var userPassword = ownerValue[..32].ToArray();
        if (revision == 2)
        {
            userPassword = Rc4.Transform(key, userPassword);
        }
        else
        {
            var iterKey = new byte[key.Length];
            for (var i = 19; i >= 0; i--)
            {
                for (var k = 0; k < key.Length; k++)
                {
                    iterKey[k] = (byte)(key[k] ^ i);
                }

                userPassword = Rc4.Transform(iterKey, userPassword);
            }

            CryptographicOperations.ZeroMemory(iterKey);
        }

        CryptographicOperations.ZeroMemory(key);
        return userPassword;
    }

    /// <summary>
    /// Removes the standard padding from a 32-byte padded password: the longest suffix that equals a prefix of the
    /// padding string is the padding.
    /// </summary>
    public static byte[] UnpadPassword(ReadOnlySpan<byte> padded)
    {
        for (var start = 0; start < padded.Length; start++)
        {
            if (padded[start..].SequenceEqual(PasswordPadding[..(padded.Length - start)]))
            {
                return padded[..start].ToArray();
            }
        }

        return padded.ToArray();
    }

    /// <summary>R5 hash (Adobe extension level 3): SHA-256(password || salt || userData).</summary>
    public static byte[] HashR5(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> userData)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(password);
        sha.AppendData(salt);
        sha.AppendData(userData);
        return sha.GetHashAndReset();
    }

    /// <summary>Algorithm 2.B (ISO 32000-2): the R6 hardened hash. Returns 32 bytes.</summary>
    public static byte[] HashR6(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> userData)
    {
        var k = HashR5(password, salt, userData);
        var round = 0;
        byte[] e;
        while (true)
        {
            // K1 = 64 repetitions of (password || K || userData).
            var unitLength = password.Length + k.Length + userData.Length;
            var k1 = new byte[unitLength * 64];
            for (var i = 0; i < 64; i++)
            {
                var offset = i * unitLength;
                password.CopyTo(k1.AsSpan(offset));
                k.CopyTo(k1, offset + password.Length);
                userData.CopyTo(k1.AsSpan(offset + password.Length + k.Length));
            }

            e = AesCipher.EncryptCbcNoPadding(k.AsSpan(0, 16), k.AsSpan(16, 16), k1);
            CryptographicOperations.ZeroMemory(k1);

            // The first 16 bytes of E as a big-endian number, mod 3, equals the byte sum mod 3 (256 ≡ 1 mod 3).
            var sum = 0;
            for (var i = 0; i < 16; i++)
            {
                sum += e[i];
            }

            CryptographicOperations.ZeroMemory(k);
            k = (sum % 3) switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };

            round++;
            var last = e[^1];
            CryptographicOperations.ZeroMemory(e);
            if (round >= 64 && last <= round - 32)
            {
                break;
            }
        }

        var result = k.AsSpan(0, 32).ToArray();
        CryptographicOperations.ZeroMemory(k);
        return result;
    }

    public static byte[] Hash(int revision, ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> userData) =>
        revision >= 6 ? HashR6(password, salt, userData) : HashR5(password, salt, userData);

    /// <summary>Algorithm 8: computes <c>/U</c> (48 bytes) and <c>/UE</c> (32 bytes) for R5/R6.</summary>
    public static (byte[] U, byte[] UE) ComputeUserValuesR6(int revision, ReadOnlySpan<byte> userPassword, ReadOnlySpan<byte> fileKey, ReadOnlySpan<byte> validationSalt, ReadOnlySpan<byte> keySalt)
    {
        var u = new byte[48];
        var hash = Hash(revision, userPassword, validationSalt, ReadOnlySpan<byte>.Empty);
        hash.CopyTo(u, 0);
        validationSalt.CopyTo(u.AsSpan(32));
        keySalt.CopyTo(u.AsSpan(40));
        var intermediate = Hash(revision, userPassword, keySalt, ReadOnlySpan<byte>.Empty);
        var ue = AesCipher.EncryptCbcNoPadding(intermediate, new byte[16], fileKey);
        CryptographicOperations.ZeroMemory(hash);
        CryptographicOperations.ZeroMemory(intermediate);
        return (u, ue);
    }

    /// <summary>Algorithm 9: computes <c>/O</c> (48 bytes) and <c>/OE</c> (32 bytes) for R5/R6, using the complete 48-byte <c>/U</c>.</summary>
    public static (byte[] O, byte[] OE) ComputeOwnerValuesR6(int revision, ReadOnlySpan<byte> ownerPassword, ReadOnlySpan<byte> fileKey, ReadOnlySpan<byte> u48, ReadOnlySpan<byte> validationSalt, ReadOnlySpan<byte> keySalt)
    {
        var o = new byte[48];
        var hash = Hash(revision, ownerPassword, validationSalt, u48);
        hash.CopyTo(o, 0);
        validationSalt.CopyTo(o.AsSpan(32));
        keySalt.CopyTo(o.AsSpan(40));
        var intermediate = Hash(revision, ownerPassword, keySalt, u48);
        var oe = AesCipher.EncryptCbcNoPadding(intermediate, new byte[16], fileKey);
        CryptographicOperations.ZeroMemory(hash);
        CryptographicOperations.ZeroMemory(intermediate);
        return (o, oe);
    }

    /// <summary>Algorithm 10: computes <c>/Perms</c> (16 bytes). <paramref name="random4"/> fills bytes 12-15.</summary>
    public static byte[] ComputePerms(ReadOnlySpan<byte> fileKey, int p, bool encryptMetadata, ReadOnlySpan<byte> random4)
    {
        var block = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(block, p);
        block[4] = 0xFF;
        block[5] = 0xFF;
        block[6] = 0xFF;
        block[7] = 0xFF;
        block[8] = encryptMetadata ? (byte)'T' : (byte)'F';
        block[9] = (byte)'a';
        block[10] = (byte)'d';
        block[11] = (byte)'b';
        random4[..4].CopyTo(block.AsSpan(12));
        var result = AesCipher.EncryptEcbNoPadding(fileKey, block);
        CryptographicOperations.ZeroMemory(block);
        return result;
    }

    /// <summary>Algorithm 13: validates <c>/Perms</c> against <c>/P</c> and <c>/EncryptMetadata</c>.</summary>
    public static bool ValidatePerms(ReadOnlySpan<byte> fileKey, ReadOnlySpan<byte> perms, int p, bool encryptMetadata)
    {
        if (perms.Length < 16)
        {
            return false;
        }

        var block = AesCipher.DecryptEcbNoPadding(fileKey, perms[..16]);
        var ok = block[9] == 'a' && block[10] == 'd' && block[11] == 'b'
            && BinaryPrimitives.ReadInt32LittleEndian(block) == p
            && block[8] == (encryptMetadata ? 'T' : 'F');
        CryptographicOperations.ZeroMemory(block);
        return ok;
    }

    /// <summary>
    /// Algorithm 2.A: authenticates an R5/R6 password and retrieves the file key.
    /// Owner is tried first so that a password equal to both reports Owner.
    /// </summary>
    public static (byte[]? Key, PdfPasswordMatch Match) AuthenticateR6(int revision, ReadOnlySpan<byte> password, ReadOnlySpan<byte> o, ReadOnlySpan<byte> u, ReadOnlySpan<byte> oe, ReadOnlySpan<byte> ue)
    {
        if (o.Length < 48 || u.Length < 48)
        {
            return (null, PdfPasswordMatch.None);
        }

        var u48 = u[..48];
        var ownerHash = Hash(revision, password, o.Slice(32, 8), u48);
        var ownerOk = CryptographicOperations.FixedTimeEquals(ownerHash, o[..32]);
        CryptographicOperations.ZeroMemory(ownerHash);
        if (ownerOk && oe.Length >= 32)
        {
            var intermediate = Hash(revision, password, o.Slice(40, 8), u48);
            var key = AesCipher.DecryptCbcNoPadding(intermediate, new byte[16], oe[..32]);
            CryptographicOperations.ZeroMemory(intermediate);
            return (key, PdfPasswordMatch.Owner);
        }

        var userHash = Hash(revision, password, u.Slice(32, 8), ReadOnlySpan<byte>.Empty);
        var userOk = CryptographicOperations.FixedTimeEquals(userHash, u[..32]);
        CryptographicOperations.ZeroMemory(userHash);
        if (userOk && ue.Length >= 32)
        {
            var intermediate = Hash(revision, password, u.Slice(40, 8), ReadOnlySpan<byte>.Empty);
            var key = AesCipher.DecryptCbcNoPadding(intermediate, new byte[16], ue[..32]);
            CryptographicOperations.ZeroMemory(intermediate);
            return (key, PdfPasswordMatch.User);
        }

        return (null, PdfPasswordMatch.None);
    }
}
