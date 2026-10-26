using System.Buffers.Binary;
using System.IO.Compression;

namespace PdfUtil.Internal.Images;

/// <summary>A decoded raster image: 8-bit samples, one or three colour channels (or palette indices), and optional 8-bit alpha.</summary>
internal sealed class RasterImage
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>1 (gray or palette index) or 3 (RGB).</summary>
    public required int Channels { get; init; }

    /// <summary>Row-major samples, Width × Height × Channels bytes.</summary>
    public required byte[] Samples { get; init; }

    /// <summary>RGB palette (3 bytes per entry) when the samples are palette indices.</summary>
    public byte[]? Palette { get; init; }

    /// <summary>Alpha, Width × Height bytes, or null when the image is opaque.</summary>
    public byte[]? Alpha { get; init; }
}

/// <summary>What a JPEG file declares in its header; the data itself is passed through as DCTDecode.</summary>
internal sealed record JpegInfo(int Width, int Height, int Components, bool AdobeInverted);

/// <summary>CRC-32 (ISO 3309, as PNG uses it).</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }

        return c;
    }).ToArray();

    public static uint Compute(ReadOnlySpan<byte> data, uint crc = 0xFFFFFFFFu)
    {
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }
}

/// <summary>JPEG header reader and PNG decoder and encoder. Every reader treats its input as hostile.</summary>
internal static class ImageCodecs
{
    public static bool IsJpeg(ReadOnlySpan<byte> data) => data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8;

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length > 8 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

    /// <summary>Reads the frame header (SOFn) and the Adobe marker. Throws <see cref="InvalidDataException"/> for files that are not usable JPEGs.</summary>
    public static JpegInfo ReadJpeg(byte[] data)
    {
        if (!IsJpeg(data))
        {
            throw new InvalidDataException("The image is not a JPEG file.");
        }

        var adobe = false;
        var pos = 2;
        while (pos + 4 <= data.Length)
        {
            if (data[pos] != 0xFF)
            {
                throw new InvalidDataException("The JPEG file is damaged.");
            }

            var marker = data[pos + 1];
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7 || marker == 0xFF)
            {
                pos += marker == 0xFF ? 1 : 2;
                continue;
            }

            var length = (data[pos + 2] << 8) | data[pos + 3];
            if (length < 2 || pos + 2 + length > data.Length)
            {
                break;
            }

            if (marker == 0xEE && length >= 12 && data.AsSpan(pos + 4, 5).SequenceEqual("Adobe"u8))
            {
                adobe = true;
            }

            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (marker is 0xC3 or 0xC7 or 0xCB or 0xCF)
                {
                    throw new InvalidDataException("Lossless JPEG is not supported by PDF readers.");
                }

                var height = (data[pos + 5] << 8) | data[pos + 6];
                var width = (data[pos + 7] << 8) | data[pos + 8];
                var components = data[pos + 9];
                if (width == 0 || height == 0 || components is not (1 or 3 or 4))
                {
                    throw new InvalidDataException("The JPEG file has an unsupported frame header.");
                }

                return new JpegInfo(width, height, components, adobe && components == 4);
            }

            pos += 2 + length;
        }

        throw new InvalidDataException("The JPEG file has no frame header.");
    }

    /// <summary>
    /// Decodes a non-interlaced PNG (every colour type and bit depth) to 8-bit samples, with alpha from the alpha
    /// channel or a tRNS chunk. Throws <see cref="InvalidDataException"/> for damaged or unsupported files.
    /// </summary>
    public static RasterImage DecodePng(byte[] data, long maxPixels)
    {
        if (!IsPng(data))
        {
            throw new InvalidDataException("The image is not a PNG file.");
        }

        int width = 0, height = 0, depth = 0, colorType = -1;
        byte[]? palette = null;
        byte[]? trns = null;
        using var idat = new MemoryStream();
        var pos = 8;
        while (pos + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
            if (length < 0 || pos + 12 + length > data.Length)
            {
                throw new InvalidDataException("The PNG file is damaged.");
            }

            var type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            var body = data.AsSpan(pos + 8, length);
            switch (type)
            {
                case "IHDR" when length >= 13:
                    width = BinaryPrimitives.ReadInt32BigEndian(body);
                    height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    depth = body[8];
                    colorType = body[9];
                    if (body[12] != 0)
                    {
                        throw new InvalidDataException("Interlaced PNG images are not supported; save the image without interlacing.");
                    }

                    break;
                case "PLTE":
                    palette = body.ToArray();
                    break;
                case "tRNS":
                    trns = body.ToArray();
                    break;
                case "IDAT":
                    idat.Write(body);
                    break;
                case "IEND":
                    pos = data.Length;
                    continue;
            }

            pos += 12 + length;
        }

        var channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException("The PNG file has an unsupported colour type."),
        };
        if (width <= 0 || height <= 0 || (long)width * height > maxPixels)
        {
            throw new InvalidDataException("The PNG image size is invalid or exceeds the pixel limit.");
        }

        if (depth is not (1 or 2 or 4 or 8 or 16) || (depth < 8 && colorType is not (0 or 3)) || (colorType == 3 && (depth == 16 || palette is null)))
        {
            throw new InvalidDataException("The PNG file has an unsupported bit depth.");
        }

        var bitsPerPixel = channels * depth;
        var stride = ((width * bitsPerPixel) + 7) / 8;
        var bpp = Math.Max(1, bitsPerPixel / 8);
        var raw = Inflate(idat.ToArray(), (long)(stride + 1) * height);
        if (raw.Length < (long)(stride + 1) * height)
        {
            throw new InvalidDataException("The PNG image data is truncated.");
        }

        // Undo the scanline filters (RFC 2083 §6).
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var src = y * (stride + 1) + 1;
            var dst = y * stride;
            for (var x = 0; x < stride; x++)
            {
                int a = x >= bpp ? pixels[dst + x - bpp] : 0;
                int b = y > 0 ? pixels[dst - stride + x] : 0;
                int c = x >= bpp && y > 0 ? pixels[dst - stride + x - bpp] : 0;
                var value = raw[src + x];
                pixels[dst + x] = filter switch
                {
                    0 => value,
                    1 => (byte)(value + a),
                    2 => (byte)(value + b),
                    3 => (byte)(value + ((a + b) / 2)),
                    4 => (byte)(value + Paeth(a, b, c)),
                    _ => throw new InvalidDataException("The PNG file uses an unknown filter."),
                };
            }
        }

        // Expand to 8-bit samples and split off alpha.
        var colorChannels = colorType is 2 or 6 ? 3 : 1;
        var samples = new byte[(long)width * height * colorChannels];
        byte[]? alpha = colorType is 4 or 6 || trns is not null ? new byte[width * height] : null;
        var max = (1 << depth) - 1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width) + x;
                for (var ch = 0; ch < channels; ch++)
                {
                    int value;
                    if (depth == 16)
                    {
                        value = pixels[(y * stride) + (((x * channels) + ch) * 2)];
                    }
                    else if (depth == 8)
                    {
                        value = pixels[(y * stride) + (x * channels) + ch];
                    }
                    else
                    {
                        var bit = x * depth;
                        value = (pixels[(y * stride) + (bit / 8)] >> (8 - depth - (bit % 8))) & max;
                        if (colorType == 0)
                        {
                            value = value * 255 / max;
                        }
                    }

                    if (ch < colorChannels)
                    {
                        samples[(i * colorChannels) + ch] = (byte)value;
                    }
                    else
                    {
                        alpha![i] = (byte)value;
                    }
                }

                if (trns is not null && alpha is not null && colorType is 0 or 2 or 3)
                {
                    alpha[i] = colorType switch
                    {
                        3 => samples[i] < trns.Length ? trns[samples[i]] : (byte)255,
                        0 when trns.Length >= 2 => RawSample(pixels, y, x, stride, depth, channels, 0) == ((trns[0] << 8) | trns[1]) ? (byte)0 : (byte)255,
                        2 when trns.Length >= 6 => Enumerable.Range(0, 3).All(ch => RawSample(pixels, y, x, stride, depth, channels, ch) == ((trns[ch * 2] << 8) | trns[(ch * 2) + 1])) ? (byte)0 : (byte)255,
                        _ => 255,
                    };
                }
            }
        }

        return new RasterImage { Width = width, Height = height, Channels = colorChannels, Samples = samples, Palette = colorType == 3 ? palette : null, Alpha = alpha };
    }

    private static int RawSample(byte[] pixels, int y, int x, int stride, int depth, int channels, int ch)
    {
        if (depth == 16)
        {
            var at = (y * stride) + (((x * channels) + ch) * 2);
            return (pixels[at] << 8) | pixels[at + 1];
        }

        if (depth == 8)
        {
            return pixels[(y * stride) + (x * channels) + ch];
        }

        var bit = x * depth;
        return (pixels[(y * stride) + (bit / 8)] >> (8 - depth - (bit % 8))) & ((1 << depth) - 1);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] Inflate(byte[] data, long expected)
    {
        using var input = new MemoryStream(data);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = z.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, n);
            if (output.Length > expected + 65536)
            {
                throw new InvalidDataException("The PNG image data is larger than its declared size.");
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// Encodes 8-bit samples as a PNG: gray (1 channel), RGB (3), gray + alpha (2) or RGBA (4), or palette indices with
    /// <paramref name="palette"/>. Scanlines use the Up filter, which compresses typical images well.
    /// </summary>
    public static byte[] EncodePng(int width, int height, int channels, byte[] samples, byte[]? palette = null)
    {
        var colorType = palette is not null ? 3 : channels switch { 1 => 0, 2 => 4, 3 => 2, 4 => 6, _ => throw new ArgumentOutOfRangeException(nameof(channels)) };
        var stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            raw[y * (stride + 1)] = 2; // Up
            for (var x = 0; x < stride; x++)
            {
                var value = samples[(y * stride) + x];
                var above = y > 0 ? samples[((y - 1) * stride) + x] : (byte)0;
                raw[(y * (stride + 1)) + 1 + x] = (byte)(value - above);
            }
        }

        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = (byte)colorType;
        WriteChunk(output, "IHDR", header);
        if (palette is not null)
        {
            WriteChunk(output, "PLTE", palette);
        }

        using (var compressed = new MemoryStream())
        {
            using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                z.Write(raw);
            }

            WriteChunk(output, "IDAT", compressed.ToArray());
        }

        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] body)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, body.Length);
        output.Write(number);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(body);
        var crc = Crc32.Compute(body, Crc32.Compute(typeBytes)) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }
}
