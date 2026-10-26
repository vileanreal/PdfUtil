using System.Buffers;
using System.IO.Compression;
using PdfUtil.Internal.Objects;

namespace PdfUtil.Internal.Filters;

/// <summary>
/// FlateDecode (with PNG and TIFF predictors). Every other filter is passed through untouched, so only Flate is
/// ever decoded, and only where the engine needs the decoded bytes (xref streams, object streams, metadata).
/// </summary>
internal static class StreamFilters
{
    /// <summary>Returns the filter names of a stream dictionary (Crypt filters included).</summary>
    public static List<string> GetFilterNames(PdfDictionary dict, Func<PdfObject?, PdfObject?> resolve)
    {
        var result = new List<string>();
        switch (resolve(dict[PdfName.Filter]))
        {
            case PdfName n:
                result.Add(n.Value);
                break;
            case PdfArray a:
                foreach (var item in a.Items)
                {
                    if (resolve(item) is PdfName fn)
                    {
                        result.Add(fn.Value);
                    }
                }

                break;
        }

        return result;
    }

    /// <summary>Returns the decode parameters dictionary for filter index <paramref name="index"/>, if any.</summary>
    public static PdfDictionary? GetDecodeParms(PdfDictionary dict, int index, Func<PdfObject?, PdfObject?> resolve)
    {
        var parms = resolve(dict[PdfName.DecodeParms]) ?? resolve(dict["DP"]);
        return parms switch
        {
            PdfDictionary d when index == 0 => d,
            PdfArray a when index < a.Count => resolve(a[index]) as PdfDictionary,
            _ => null,
        };
    }

    /// <summary>
    /// Decodes data whose filter chain consists only of FlateDecode (and Crypt, which the caller has already applied).
    /// Returns null if the chain contains any other filter.
    /// </summary>
    public static byte[]? DecodeFlateChain(byte[] data, PdfDictionary dict, Func<PdfObject?, PdfObject?> resolve, long maxDecompressed, bool strict = false)
    {
        var names = GetFilterNames(dict, resolve);
        var current = data;
        for (var i = 0; i < names.Count; i++)
        {
            switch (names[i])
            {
                case "FlateDecode":
                case "Fl":
                    current = FlateDecode(current, maxDecompressed, strict);
                    current = ApplyPredictor(current, GetDecodeParms(dict, i, resolve), maxDecompressed);
                    break;
                case "Crypt":
                    break;
                default:
                    return null;
            }
        }

        return current;
    }

    public static byte[] FlateDecode(byte[] data, long maxDecompressed, bool strict = false)
    {
        try
        {
            return Inflate(data, zlibHeader: true, maxDecompressed, strict);
        }
        catch (InvalidDataException) when (!strict)
        {
            // Some producers write raw deflate data without the zlib header.
            return Inflate(data, zlibHeader: false, maxDecompressed, strict);
        }
    }

    private static byte[] Inflate(byte[] data, bool zlibHeader, long maxDecompressed, bool strict)
    {
        using var input = new MemoryStream(data, writable: false);
        using Stream inflater = zlibHeader ? new ZLibStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                int n;
                try
                {
                    n = inflater.Read(buffer, 0, buffer.Length);
                }
                catch (InvalidDataException) when (!strict && output.Length > 0)
                {
                    // Truncated or trailing-garbage data: keep what was decoded, as viewers do.
                    break;
                }

                if (n <= 0)
                {
                    break;
                }

                if (output.Length + n > maxDecompressed)
                {
                    throw new PdfLimitExceededException("A stream exceeds the maximum decompressed size (possible zip bomb).");
                }

                output.Write(buffer, 0, n);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return output.ToArray();
    }

    public static byte[] FlateEncode(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>Undoes a PNG (10-15) or TIFF (2) predictor (ISO 32000-1 §7.4.4.4).</summary>
    public static byte[] ApplyPredictor(byte[] data, PdfDictionary? parms, long maxDecompressed)
    {
        if (parms is null)
        {
            return data;
        }

        var predictor = GetInt(parms, "Predictor", 1);
        if (predictor <= 1)
        {
            return data;
        }

        var colors = Math.Clamp(GetInt(parms, "Colors", 1), 1, 32);
        var bpc = GetInt(parms, "BitsPerComponent", 8);
        if (bpc is not (1 or 2 or 4 or 8 or 16))
        {
            bpc = 8;
        }

        var columns = Math.Max(1, GetInt(parms, "Columns", 1));
        var bytesPerPixel = Math.Max(1, (colors * bpc + 7) / 8);
        var rowLength = (int)Math.Min(int.MaxValue / 4, ((long)colors * bpc * columns + 7) / 8);

        if (predictor == 2)
        {
            return TiffPredictor(data, rowLength, bytesPerPixel, bpc, colors);
        }

        if (predictor < 10)
        {
            return data;
        }

        var rows = data.Length / (rowLength + 1);
        if ((long)rows * rowLength > maxDecompressed)
        {
            throw new PdfLimitExceededException("A stream exceeds the maximum decompressed size (possible zip bomb).");
        }

        var output = new byte[(long)rows * rowLength];
        var prior = new byte[rowLength];
        var current = new byte[rowLength];
        for (var r = 0; r < rows; r++)
        {
            var offset = r * (rowLength + 1);
            var type = data[offset];
            Buffer.BlockCopy(data, offset + 1, current, 0, rowLength);
            for (var i = 0; i < rowLength; i++)
            {
                var left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                var up = prior[i];
                var upLeft = i >= bytesPerPixel ? prior[i - bytesPerPixel] : 0;
                current[i] = type switch
                {
                    1 => (byte)(current[i] + left),
                    2 => (byte)(current[i] + up),
                    3 => (byte)(current[i] + ((left + up) >> 1)),
                    4 => (byte)(current[i] + Paeth(left, up, upLeft)),
                    _ => current[i],
                };
            }

            Buffer.BlockCopy(current, 0, output, r * rowLength, rowLength);
            (prior, current) = (current, prior);
        }

        return output;
    }

    private static byte[] TiffPredictor(byte[] data, int rowLength, int bytesPerPixel, int bpc, int colors)
    {
        var output = (byte[])data.Clone();
        if (bpc == 8)
        {
            for (var rowStart = 0; rowStart + rowLength <= output.Length; rowStart += rowLength)
            {
                for (var i = bytesPerPixel; i < rowLength; i++)
                {
                    output[rowStart + i] = (byte)(output[rowStart + i] + output[rowStart + i - bytesPerPixel]);
                }
            }
        }
        else if (bpc == 16)
        {
            for (var rowStart = 0; rowStart + rowLength <= output.Length; rowStart += rowLength)
            {
                for (var i = bytesPerPixel; i + 1 < rowLength; i += 2)
                {
                    var prev = (output[rowStart + i - bytesPerPixel] << 8) | output[rowStart + i - bytesPerPixel + 1];
                    var cur = (output[rowStart + i] << 8) | output[rowStart + i + 1];
                    var sum = (prev + cur) & 0xFFFF;
                    output[rowStart + i] = (byte)(sum >> 8);
                    output[rowStart + i + 1] = (byte)sum;
                }
            }
        }
        else
        {
            // Sub-byte components: operate on the bit stream component by component.
            var mask = (1 << bpc) - 1;
            for (var rowStart = 0; rowStart + rowLength <= output.Length; rowStart += rowLength)
            {
                var componentsPerRow = rowLength * 8 / bpc;
                var previous = new int[colors];
                for (var c = 0; c < componentsPerRow; c++)
                {
                    var bitPos = c * bpc;
                    var byteIndex = rowStart + (bitPos >> 3);
                    var shift = 8 - bpc - (bitPos & 7);
                    var value = (output[byteIndex] >> shift) & mask;
                    var channel = c % colors;
                    if (c >= colors)
                    {
                        value = (value + previous[channel]) & mask;
                    }

                    previous[channel] = value;
                    output[byteIndex] = (byte)((output[byteIndex] & ~(mask << shift)) | (value << shift));
                }
            }
        }

        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static int GetInt(PdfDictionary d, string key, int fallback) => d[key] switch
    {
        PdfInteger i => (int)Math.Clamp(i.Value, int.MinValue, int.MaxValue),
        PdfReal r => (int)r.Value,
        _ => fallback,
    };
    /// <summary>
    /// Decodes every filter needed to read content streams: Flate, LZW (with predictors), ASCIIHex, ASCII85 and
    /// RunLength. Returns null when the chain contains another filter (image codecs).
    /// </summary>
    public static byte[]? DecodeAll(byte[] data, PdfDictionary dict, Func<PdfObject?, PdfObject?> resolve, long maxDecompressed)
    {
        var names = GetFilterNames(dict, resolve);
        var current = data;
        for (var i = 0; i < names.Count; i++)
        {
            var parms = GetDecodeParms(dict, i, resolve);
            switch (names[i])
            {
                case "FlateDecode" or "Fl":
                    current = ApplyPredictor(FlateDecode(current, maxDecompressed), parms, maxDecompressed);
                    break;
                case "LZWDecode" or "LZW":
                    var early = resolve(parms?["EarlyChange"]) is PdfInteger { Value: 0 } ? 0 : 1;
                    current = ApplyPredictor(LzwDecode(current, early, maxDecompressed), parms, maxDecompressed);
                    break;
                case "ASCIIHexDecode" or "AHx":
                    current = AsciiHexDecode(current);
                    break;
                case "ASCII85Decode" or "A85":
                    current = Ascii85Decode(current);
                    break;
                case "RunLengthDecode" or "RL":
                    current = RunLengthDecode(current, maxDecompressed);
                    break;
                case "Crypt":
                    break;
                default:
                    return null;
            }
        }

        return current;
    }

    public static byte[] AsciiHexDecode(byte[] data)
    {
        var output = new List<byte>(data.Length / 2);
        var high = -1;
        foreach (var b in data)
        {
            if (b == '>')
            {
                break;
            }

            var v = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                _ => -1,
            };
            if (v < 0)
            {
                continue;
            }

            if (high < 0)
            {
                high = v;
            }
            else
            {
                output.Add((byte)((high << 4) | v));
                high = -1;
            }
        }

        if (high >= 0)
        {
            output.Add((byte)(high << 4));
        }

        return output.ToArray();
    }

    public static byte[] Ascii85Decode(byte[] data)
    {
        var output = new List<byte>(data.Length);
        var group = new uint[5];
        var n = 0;
        var start = data.Length >= 2 && data[0] == '<' && data[1] == '~' ? 2 : 0;
        for (var i = start; i < data.Length; i++)
        {
            var b = data[i];
            if (b == '~')
            {
                break;
            }

            if (b == 'z' && n == 0)
            {
                output.AddRange(new byte[4]);
                continue;
            }

            if (b < '!' || b > 'u')
            {
                continue;
            }

            group[n++] = (uint)(b - '!');
            if (n == 5)
            {
                var v = (((((group[0] * 85) + group[1]) * 85 + group[2]) * 85) + group[3]) * 85 + group[4];
                output.Add((byte)(v >> 24));
                output.Add((byte)(v >> 16));
                output.Add((byte)(v >> 8));
                output.Add((byte)v);
                n = 0;
            }
        }

        if (n > 1)
        {
            for (var k = n; k < 5; k++)
            {
                group[k] = 84;
            }

            var v = (((((group[0] * 85) + group[1]) * 85 + group[2]) * 85) + group[3]) * 85 + group[4];
            for (var k = 0; k < n - 1; k++)
            {
                output.Add((byte)(v >> (24 - (8 * k))));
            }
        }

        return output.ToArray();
    }

    public static byte[] RunLengthDecode(byte[] data, long maxDecompressed)
    {
        var output = new List<byte>(data.Length * 2);
        var i = 0;
        while (i < data.Length)
        {
            var length = data[i++];
            if (length == 128)
            {
                break;
            }

            if (length < 128)
            {
                for (var k = 0; k <= length && i < data.Length; k++)
                {
                    output.Add(data[i++]);
                }
            }
            else if (i < data.Length)
            {
                var b = data[i++];
                for (var k = 0; k < 257 - length; k++)
                {
                    output.Add(b);
                }
            }

            if (output.Count > maxDecompressed)
            {
                throw new PdfLimitExceededException("A stream exceeds the maximum decompressed size (possible zip bomb).");
            }
        }

        return output.ToArray();
    }

    public static byte[] LzwDecode(byte[] data, int earlyChange, long maxDecompressed)
    {
        var output = new MemoryStream(data.Length * 3);
        var table = new List<byte[]>(4096);
        void Reset()
        {
            table.Clear();
            for (var i = 0; i < 256; i++)
            {
                table.Add(new[] { (byte)i });
            }

            table.Add(Array.Empty<byte>()); // 256: clear
            table.Add(Array.Empty<byte>()); // 257: end
        }

        Reset();
        var codeLength = 9;
        long bitBuffer = 0;
        var bits = 0;
        byte[]? previous = null;
        foreach (var b in data)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bits += 8;
            while (bits >= codeLength)
            {
                var code = (int)((bitBuffer >> (bits - codeLength)) & ((1 << codeLength) - 1));
                bits -= codeLength;
                if (code == 256)
                {
                    Reset();
                    codeLength = 9;
                    previous = null;
                    continue;
                }

                if (code == 257)
                {
                    return output.ToArray();
                }

                byte[] entry;
                if (code < table.Count)
                {
                    entry = table[code];
                    if (previous is not null)
                    {
                        table.Add(previous.Append(entry[0]).ToArray());
                    }
                }
                else if (previous is not null && code == table.Count)
                {
                    entry = previous.Append(previous[0]).ToArray();
                    table.Add(entry);
                }
                else
                {
                    return output.ToArray(); // damaged data: keep what was decoded
                }

                output.Write(entry);
                if (output.Length > maxDecompressed)
                {
                    throw new PdfLimitExceededException("A stream exceeds the maximum decompressed size (possible zip bomb).");
                }

                previous = entry;
                if (table.Count + earlyChange >= (1 << codeLength) && codeLength < 12)
                {
                    codeLength++;
                }
            }
        }

        return output.ToArray();
    }
}
