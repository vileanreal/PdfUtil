using System.Security.Cryptography;

namespace PdfUtil.Internal.Security;

/// <summary>RC4 stream cipher, implemented from the algorithm description (it is not in the .NET BCL).</summary>
internal static class Rc4
{
    public static byte[] Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        var output = new byte[data.Length];
        Transform(key, data, output);
        return output;
    }

    public static void Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, Span<byte> output)
    {
        if (key.Length == 0)
        {
            throw new ArgumentException("RC4 key must not be empty.", nameof(key));
        }

        Span<byte> s = stackalloc byte[256];
        for (var i = 0; i < 256; i++)
        {
            s[i] = (byte)i;
        }

        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var x = 0;
        var y = 0;
        for (var k = 0; k < data.Length; k++)
        {
            x = (x + 1) & 0xFF;
            y = (y + s[x]) & 0xFF;
            (s[x], s[y]) = (s[y], s[x]);
            output[k] = (byte)(data[k] ^ s[(s[x] + s[y]) & 0xFF]);
        }

        CryptographicOperations.ZeroMemory(s);
    }
}

/// <summary>AES helpers for the PDF security handler (ISO 32000-2 §7.6.3).</summary>
internal static class AesCipher
{
    /// <summary>Encrypts with a fresh random 16-byte IV prepended and PKCS#7 padding. Empty input still yields IV + one padding block.</summary>
    public static byte[] EncryptWithRandomIv(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext)
    {
        Span<byte> iv = stackalloc byte[16];
        RandomNumberGenerator.Fill(iv);
        return EncryptWithIv(key, iv, plaintext);
    }

    public static byte[] EncryptWithIv(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        var cipherLength = aes.GetCiphertextLengthCbc(plaintext.Length, PaddingMode.PKCS7);
        var output = new byte[16 + cipherLength];
        iv.CopyTo(output);
        aes.EncryptCbc(plaintext, iv, output.AsSpan(16), PaddingMode.PKCS7);
        return output;
    }

    /// <summary>Decrypts IV-prefixed CBC data with PKCS#7 padding. Throws <see cref="CryptographicException"/> on malformed input.</summary>
    public static byte[] DecryptWithIv(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return Array.Empty<byte>(); // some writers emit empty strings unencrypted
        }

        if (data.Length < 32 || data.Length % 16 != 0)
        {
            throw new CryptographicException("AES ciphertext has an invalid length.");
        }

        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes.DecryptCbc(data[16..], data[..16], PaddingMode.PKCS7);
    }

    /// <summary>
    /// Lenient decryption used for reading third-party files: tolerates bad padding and lengths that are not a
    /// multiple of the block size, the way mainstream viewers do.
    /// </summary>
    public static byte[] DecryptWithIvLenient(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        try
        {
            return DecryptWithIv(key, data);
        }
        catch (CryptographicException)
        {
            if (data.Length < 16)
            {
                return Array.Empty<byte>();
            }

            var body = data[16..];
            body = body[..(body.Length - body.Length % 16)];
            if (body.Length == 0)
            {
                return Array.Empty<byte>();
            }

            using var aes = Aes.Create();
            aes.Key = key.ToArray();
            var plain = aes.DecryptCbc(body, data[..16], PaddingMode.None);
            var pad = plain[^1];
            if (pad is >= 1 and <= 16 && pad <= plain.Length)
            {
                return plain.AsSpan(0, plain.Length - pad).ToArray();
            }

            return plain;
        }
    }

    public static byte[] EncryptCbcNoPadding(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> data)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes.EncryptCbc(data, iv, PaddingMode.None);
    }

    public static byte[] DecryptCbcNoPadding(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> data)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes.DecryptCbc(data, iv, PaddingMode.None);
    }

    public static byte[] EncryptEcbNoPadding(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes.EncryptEcb(data, PaddingMode.None);
    }

    public static byte[] DecryptEcbNoPadding(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        return aes.DecryptEcb(data, PaddingMode.None);
    }
}
