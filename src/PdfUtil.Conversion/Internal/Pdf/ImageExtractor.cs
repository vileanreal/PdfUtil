using PdfUtil.Conversion.Internal.Model;
using PdfUtil.Internal.Content;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Filters;
using PdfUtil.Internal.Images;
using PdfUtil.Internal.Objects;

namespace PdfUtil.Conversion.Internal.Pdf;

/// <summary>
/// Turns PDF images into web images: JPEG (DCTDecode) data is passed through, and images stored with the content
/// filters (Flate, LZW, RunLength, ASCII) in DeviceGray, DeviceRGB, DeviceCMYK, CalGray, CalRGB, ICCBased or Indexed
/// colour are converted to PNG, with soft masks, stencil masks and colour-key masks as alpha. Other codecs (JPX,
/// JBIG2, CCITT) become placeholders. Each distinct image XObject is converted once.
/// </summary>
internal sealed class ImageExtractor
{
    private static readonly Dictionary<string, string> InlineKeys = new()
    {
        ["W"] = "Width", ["H"] = "Height", ["BPC"] = "BitsPerComponent", ["CS"] = "ColorSpace", ["F"] = "Filter",
        ["DP"] = "DecodeParms", ["IM"] = "ImageMask", ["D"] = "Decode", ["I"] = "Interpolate", ["L"] = "Length",
    };

    private static readonly Dictionary<string, string> InlineNames = new()
    {
        ["G"] = "DeviceGray", ["RGB"] = "DeviceRGB", ["CMYK"] = "DeviceCMYK", ["I"] = "Indexed",
        ["AHx"] = "ASCIIHexDecode", ["A85"] = "ASCII85Decode", ["LZW"] = "LZWDecode", ["Fl"] = "FlateDecode",
        ["RL"] = "RunLengthDecode", ["CCF"] = "CCITTFaxDecode", ["DCT"] = "DCTDecode",
    };

    private readonly PdfDocument _doc;
    private readonly long _maxPixels;
    private readonly long _maxBytes;
    private readonly List<string> _warnings;
    private readonly Dictionary<(int, string), ImageData> _cache = new();

    public ImageExtractor(PdfDocument doc, long maxPixels, long maxBytes, List<string> warnings)
    {
        _doc = doc;
        _maxPixels = maxPixels;
        _maxBytes = maxBytes;
        _warnings = warnings;
    }

    public ImageData Extract(ImageInfo info)
    {
        if (info.ObjectNumber is int n && _cache.TryGetValue((n, info.FillColor), out var cached))
        {
            return cached;
        }

        ImageData image;
        try
        {
            image = info.Stream is not null
                ? Convert(info.Stream.Dictionary, _doc.GetStreamData(info.Stream, new ObjectId(info.ObjectNumber ?? 0, _doc.GetGeneration(info.ObjectNumber ?? 0))), info.Resources, info.FillColor)
                : Convert(NormalizeInline(info.Dictionary), info.InlineData ?? Array.Empty<byte>(), info.Resources, info.FillColor);
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException or PdfCorruptException)
        {
            image = Placeholder("The image data is damaged.", 0, 0);
        }

        if (info.ObjectNumber is int number)
        {
            _cache[(number, info.FillColor)] = image;
        }

        return image;
    }

    private ImageData Placeholder(string reason, int width, int height)
    {
        Warn(reason + " A placeholder was written instead.");
        return new ImageData { Placeholder = reason, PixelWidth = width, PixelHeight = height };
    }

    private void Warn(string message)
    {
        if (!_warnings.Contains(message))
        {
            _warnings.Add(message);
        }
    }

    private PdfObject? R(PdfObject? o) => _doc.Resolve(o);

    private int Int(PdfObject? o) => R(o) switch
    {
        PdfInteger i => (int)Math.Clamp(i.Value, int.MinValue, int.MaxValue),
        PdfReal r when double.IsFinite(r.Value) => (int)Math.Clamp(r.Value, int.MinValue, int.MaxValue),
        _ => 0,
    };

    private double Num(PdfObject? o) => R(o) switch
    {
        PdfInteger i => i.Value,
        PdfReal r when double.IsFinite(r.Value) => r.Value,
        _ => 0,
    };

    /// <summary>Expands the abbreviated keys and names of an inline image dictionary (ISO 32000-1 Tables 93 and 94).</summary>
    private static PdfDictionary NormalizeInline(PdfDictionary dict)
    {
        static PdfObject Name(PdfObject o) => o switch
        {
            PdfName n when InlineNames.TryGetValue(n.Value, out var full) => PdfName.Get(full),
            PdfArray a => new PdfArray(a.Items.Select(Name)),
            _ => o,
        };

        var result = new PdfDictionary();
        foreach (var (key, value) in dict.Entries)
        {
            var name = InlineKeys.TryGetValue(key.Value, out var full) ? full : key.Value;
            result[PdfName.Get(name)] = name is "ColorSpace" or "Filter" ? Name(value) : value;
        }

        return result;
    }

    private ImageData Convert(PdfDictionary dict, byte[] raw, PdfDictionary? resources, string fillColor)
    {
        var width = Int(dict["Width"]);
        var height = Int(dict["Height"]);
        if (width <= 0 || height <= 0)
        {
            return Placeholder("An image has no valid size.", 0, 0);
        }

        if ((long)width * height > _maxPixels)
        {
            return Placeholder($"An image of {width} × {height} pixels is larger than the configured maximum of {_maxPixels} pixels.", width, height);
        }

        var filters = StreamFilters.GetFilterNames(dict, R).Select(f => InlineNames.TryGetValue(f, out var full) ? full : f).ToList();
        var codec = filters.LastOrDefault(f => f is "DCTDecode" or "JPXDecode" or "JBIG2Decode" or "CCITTFaxDecode");
        if (codec is "JPXDecode" or "JBIG2Decode" or "CCITTFaxDecode")
        {
            return Placeholder($"An image uses {codec[..^6]} compression, which PdfUtil cannot convert.", width, height);
        }

        if (codec == "DCTDecode")
        {
            var index = filters.IndexOf(codec);
            if (index != filters.Count - 1)
            {
                return Placeholder("An image applies filters after JPEG compression.", width, height);
            }

            var jpeg = index == 0 ? raw : DecodePrefix(dict, raw, index);
            if (jpeg is null || !ImageCodecs.IsJpeg(jpeg))
            {
                return Placeholder("An image's JPEG data is damaged.", width, height);
            }

            if (dict["SMask"] is not null || dict["Mask"] is not null)
            {
                Warn("The transparency of a JPEG image was dropped.");
            }

            return new ImageData { Data = jpeg, MimeType = "image/jpeg", PixelWidth = width, PixelHeight = height };
        }

        var data = StreamFilters.DecodeAll(raw, dict, R, _maxBytes);
        if (data is null)
        {
            return Placeholder($"An image uses a filter PdfUtil cannot decode ({string.Join(", ", filters)}).", width, height);
        }

        if (R(dict["ImageMask"]) is PdfBoolean { Value: true })
        {
            var mask = Unpack(data, width, height, 1, 1, out var truncated);
            var inverted = R(dict["Decode"]) is PdfArray { Count: >= 2 } d && Num(d[0]) > Num(d[1]);
            var (r, g, b) = ParseColor(fillColor);
            var rgba = new byte[width * height * 4];
            for (var i = 0; i < width * height; i++)
            {
                var paint = (mask[i] == 0) != inverted;
                rgba[(i * 4) + 0] = r;
                rgba[(i * 4) + 1] = g;
                rgba[(i * 4) + 2] = b;
                rgba[(i * 4) + 3] = paint ? (byte)255 : (byte)0;
            }

            WarnTruncated(truncated);
            return Png(width, height, 4, rgba, null);
        }

        var space = ReadColorSpace(dict["ColorSpace"], resources, 0);
        if (space is null)
        {
            return Placeholder("An image uses a colour space PdfUtil cannot convert.", width, height);
        }

        var bpc = Int(dict["BitsPerComponent"]);
        if (bpc is not (1 or 2 or 4 or 8 or 16))
        {
            return Placeholder("An image has an unsupported number of bits per component.", width, height);
        }

        var samples = Unpack(data, width, height, space.Components, bpc, out var short1);
        WarnTruncated(short1);
        ApplyDecode(samples, dict["Decode"], space, bpc);
        var alpha = ReadAlpha(dict, samples, space, width, height, resources);

        byte[] pixels;
        int channels;
        byte[]? palette = null;
        switch (space.Kind)
        {
            case SpaceKind.Gray:
                (pixels, channels) = (samples, 1);
                break;
            case SpaceKind.Rgb:
                (pixels, channels) = (samples, 3);
                break;
            case SpaceKind.Cmyk:
                (pixels, channels) = (CmykToRgb(samples), 3);
                break;
            case SpaceKind.Indexed:
                if (alpha is null)
                {
                    (pixels, channels, palette) = (samples, 1, space.Palette);
                }
                else
                {
                    pixels = new byte[width * height * 3];
                    for (var i = 0; i < width * height; i++)
                    {
                        var entry = Math.Min(samples[i], space.HighValue) * 3;
                        pixels[i * 3] = space.Palette![entry];
                        pixels[(i * 3) + 1] = space.Palette[entry + 1];
                        pixels[(i * 3) + 2] = space.Palette[entry + 2];
                    }

                    channels = 3;
                }

                break;
            default:
                throw new InvalidDataException("Unknown colour space kind.");
        }

        if (alpha is not null)
        {
            var combined = new byte[width * height * (channels + 1)];
            for (var i = 0; i < width * height; i++)
            {
                for (var c = 0; c < channels; c++)
                {
                    combined[(i * (channels + 1)) + c] = pixels[(i * channels) + c];
                }

                combined[(i * (channels + 1)) + channels] = alpha[i];
            }

            return Png(width, height, channels + 1, combined, null);
        }

        return Png(width, height, channels, pixels, palette);
    }

    private static ImageData Png(int width, int height, int channels, byte[] samples, byte[]? palette) =>
        new() { Data = ImageCodecs.EncodePng(width, height, channels, samples, palette), MimeType = "image/png", PixelWidth = width, PixelHeight = height };

    private void WarnTruncated(bool truncated)
    {
        if (truncated)
        {
            Warn("An image's data is shorter than its size requires; the missing pixels are black.");
        }
    }

    /// <summary>Decodes the filters before the codec filter at <paramref name="index"/>.</summary>
    private byte[]? DecodePrefix(PdfDictionary dict, byte[] raw, int index)
    {
        var prefix = dict.ShallowClone();
        var filters = R(dict["Filter"]) as PdfArray;
        var parms = R(dict["DecodeParms"]) as PdfArray;
        prefix[PdfName.Get("Filter")] = new PdfArray(filters!.Items.Take(index));
        if (parms is not null)
        {
            prefix[PdfName.Get("DecodeParms")] = new PdfArray(parms.Items.Take(index));
        }

        return StreamFilters.DecodeAll(raw, prefix, R, _maxBytes);
    }

    /// <summary>Unpacks samples of 1–16 bits (rows padded to whole bytes) into one byte per sample; 16-bit samples keep their high byte, and other depths are not scaled yet.</summary>
    private static byte[] Unpack(byte[] data, int width, int height, int components, int bpc, out bool truncated)
    {
        var perRow = width * components;
        var rowBytes = (((long)perRow * bpc) + 7) / 8;
        truncated = data.LongLength < rowBytes * height;
        var samples = new byte[perRow * height];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * rowBytes;
            for (var x = 0; x < perRow; x++)
            {
                int value;
                if (bpc == 8)
                {
                    var p = rowStart + x;
                    value = p < data.Length ? data[p] : 0;
                }
                else if (bpc == 16)
                {
                    var p = rowStart + (x * 2L);
                    value = p < data.Length ? data[p] : 0;
                }
                else
                {
                    var bit = (long)x * bpc;
                    var p = rowStart + (bit >> 3);
                    var b = p < data.Length ? data[p] : 0;
                    var shift = 8 - bpc - (int)(bit & 7);
                    value = (b >> shift) & ((1 << bpc) - 1);
                }

                samples[(y * perRow) + x] = (byte)value;
            }
        }

        return samples;
    }

    /// <summary>Scales samples to 0–255 and applies the <c>/Decode</c> array (for Indexed images it maps indices).</summary>
    private void ApplyDecode(byte[] samples, PdfObject? decodeObj, ColorSpace space, int bpc)
    {
        var max = bpc == 16 ? 255 : (1 << bpc) - 1;
        var decode = R(decodeObj) as PdfArray;
        var n = space.Components;
        if (space.Kind == SpaceKind.Indexed)
        {
            var inverted = decode is { Count: >= 2 } && Num(decode[0]) > Num(decode[1]);
            for (var i = 0; i < samples.Length; i++)
            {
                var index = Math.Min(samples[i], space.HighValue);
                samples[i] = (byte)(inverted ? space.HighValue - index : index);
            }

            return;
        }

        var lookup = new byte[n][];
        for (var c = 0; c < n; c++)
        {
            var dMin = decode is not null && decode.Count >= (2 * c) + 2 ? Num(decode[2 * c]) : 0;
            var dMax = decode is not null && decode.Count >= (2 * c) + 2 ? Num(decode[(2 * c) + 1]) : 1;
            if (space.Inverted)
            {
                (dMin, dMax) = (1 - dMin, 1 - dMax);
            }

            lookup[c] = new byte[256];
            for (var v = 0; v <= Math.Min(max, 255); v++)
            {
                var f = dMin + (v * (dMax - dMin) / max);
                lookup[c][v] = (byte)Math.Clamp(Math.Round(f * 255), 0, 255);
            }
        }

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = lookup[i % n][samples[i]];
        }
    }

    /// <summary>The alpha channel from <c>/SMask</c>, a stencil <c>/Mask</c> image or a colour-key <c>/Mask</c> array, or null.</summary>
    private byte[]? ReadAlpha(PdfDictionary dict, byte[] samples, ColorSpace space, int width, int height, PdfDictionary? resources)
    {
        if (R(dict["SMask"]) is PdfStream smask)
        {
            return ReadMaskImage(dict["SMask"], smask, width, height, soft: true);
        }

        switch (R(dict["Mask"]))
        {
            case PdfStream stencil:
                return ReadMaskImage(dict["Mask"], stencil, width, height, soft: false);
            case PdfArray { Count: > 0 } key when key.Count >= 2 * space.Components:
                // Colour-key masking compares the original sample values; the scaled samples are compared against
                // scaled ranges, which is exact for 8-bit images.
                var bpc = Int(dict["BitsPerComponent"]);
                var max = bpc is 16 or 8 ? 255.0 : (1 << bpc) - 1;
                var alpha = new byte[width * height];
                for (var i = 0; i < width * height; i++)
                {
                    var masked = true;
                    for (var c = 0; c < space.Components && masked; c++)
                    {
                        var value = samples[(i * space.Components) + c];
                        var scale = space.Kind == SpaceKind.Indexed ? 1 : 255 / max;
                        masked = value >= Math.Floor(Num(key[2 * c]) * scale) && value <= Math.Ceiling(Num(key[(2 * c) + 1]) * scale);
                    }

                    alpha[i] = masked ? (byte)0 : (byte)255;
                }

                return alpha;
            default:
                return null;
        }
    }

    private byte[]? ReadMaskImage(PdfObject? reference, PdfStream mask, int width, int height, bool soft)
    {
        var number = (reference as PdfReference)?.Id.Number ?? 0;
        var mw = Int(mask.Dictionary["Width"]);
        var mh = Int(mask.Dictionary["Height"]);
        if (mw <= 0 || mh <= 0 || (long)mw * mh > _maxPixels)
        {
            return null;
        }

        var data = StreamFilters.DecodeAll(_doc.GetStreamData(mask, new ObjectId(number, _doc.GetGeneration(number))), mask.Dictionary, R, _maxBytes);
        if (data is null)
        {
            Warn("An image's mask uses a filter PdfUtil cannot decode; the image is shown without it.");
            return null;
        }

        var bpc = soft ? Int(mask.Dictionary["BitsPerComponent"]) : 1;
        if (bpc is not (1 or 2 or 4 or 8 or 16))
        {
            return null;
        }

        var values = Unpack(data, mw, mh, 1, bpc, out _);
        var max = bpc == 16 ? 255 : (1 << bpc) - 1;
        var inverted = R(mask.Dictionary["Decode"]) is PdfArray { Count: >= 2 } d && Num(d[0]) > Num(d[1]);
        var alpha = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            var my = (int)((long)y * mh / height);
            for (var x = 0; x < width; x++)
            {
                var mx = (int)((long)x * mw / width);
                var v = values[(my * mw) + mx] * 255 / max;
                if (inverted)
                {
                    v = 255 - v;
                }

                // A soft mask is opacity; in a stencil mask, 1 marks pixels that are masked out.
                alpha[(y * width) + x] = (byte)(soft ? v : 255 - v);
            }
        }

        return alpha;
    }

    private static byte[] CmykToRgb(byte[] cmyk)
    {
        var rgb = new byte[cmyk.Length / 4 * 3];
        for (int i = 0, o = 0; i + 3 < cmyk.Length; i += 4, o += 3)
        {
            var k = 255 - cmyk[i + 3];
            rgb[o] = (byte)((255 - cmyk[i]) * k / 255);
            rgb[o + 1] = (byte)((255 - cmyk[i + 1]) * k / 255);
            rgb[o + 2] = (byte)((255 - cmyk[i + 2]) * k / 255);
        }

        return rgb;
    }

    private static (byte R, byte G, byte B) ParseColor(string hex) =>
        hex.Length == 7 ? (System.Convert.ToByte(hex[1..3], 16), System.Convert.ToByte(hex[3..5], 16), System.Convert.ToByte(hex[5..7], 16)) : ((byte)0, (byte)0, (byte)0);

    private enum SpaceKind
    {
        Gray,
        Rgb,
        Cmyk,
        Indexed,
    }

    /// <summary>A colour space as converted. <see cref="Inverted"/> marks spot colours, where 1 is full ink (dark).</summary>
    private sealed record ColorSpace(SpaceKind Kind, int Components, byte[]? Palette = null, int HighValue = 0, bool Inverted = false);

    private ColorSpace? ReadColorSpace(PdfObject? obj, PdfDictionary? resources, int depth)
    {
        if (depth > 4)
        {
            return null;
        }

        var cs = R(obj);
        if (cs is PdfName name && _doc.ResolveDictionary(resources?["ColorSpace"])?[name.Value] is { } named && name.Value is not ("DeviceGray" or "DeviceRGB" or "DeviceCMYK"))
        {
            cs = R(named);
        }

        switch (cs)
        {
            case PdfName n:
                return Simple(n.Value);
            case PdfArray { Count: > 0 } a:
                var family = (R(a[0]) as PdfName)?.Value;
                switch (family)
                {
                    case "DeviceGray" or "CalGray" or "DeviceRGB" or "CalRGB" or "DeviceCMYK":
                        return Simple(family);
                    case "ICCBased" when a.Count > 1 && R(a[1]) is PdfStream icc:
                        return Int(icc.Dictionary["N"]) switch
                        {
                            1 => new ColorSpace(SpaceKind.Gray, 1),
                            3 => new ColorSpace(SpaceKind.Rgb, 3),
                            4 => new ColorSpace(SpaceKind.Cmyk, 4),
                            _ => null,
                        };
                    case "Indexed" or "I" when a.Count >= 4:
                        return Indexed(a, resources, depth);
                    case "Separation" or "DeviceN":
                        Warn("An image uses a spot colour space; it is shown in shades of gray.");
                        return family == "Separation" ? new ColorSpace(SpaceKind.Gray, 1, Inverted: true) : null;
                    default:
                        return null;
                }

            default:
                return null;
        }

        static ColorSpace? Simple(string name) => name switch
        {
            "DeviceGray" or "CalGray" or "G" => new ColorSpace(SpaceKind.Gray, 1),
            "DeviceRGB" or "CalRGB" or "RGB" => new ColorSpace(SpaceKind.Rgb, 3),
            "DeviceCMYK" or "CMYK" => new ColorSpace(SpaceKind.Cmyk, 4),
            _ => null,
        };
    }

    private ColorSpace? Indexed(PdfArray a, PdfDictionary? resources, int depth)
    {
        var baseSpace = ReadColorSpace(a[1], resources, depth + 1);
        if (baseSpace is null or { Kind: SpaceKind.Indexed })
        {
            return null;
        }

        var high = Math.Clamp(Int(a[2]), 0, 255);
        var lookup = R(a[3]) switch
        {
            PdfString s => s.Value,
            PdfStream st => StreamFilters.DecodeAll(_doc.GetStreamData(st, new ObjectId((a[3] as PdfReference)?.Id.Number ?? 0, _doc.GetGeneration((a[3] as PdfReference)?.Id.Number ?? 0))), st.Dictionary, R, _maxBytes) ?? Array.Empty<byte>(),
            _ => Array.Empty<byte>(),
        };
        var entries = new byte[(high + 1) * baseSpace.Components];
        Array.Copy(lookup, entries, Math.Min(lookup.Length, entries.Length));
        var palette = baseSpace.Kind switch
        {
            SpaceKind.Gray => entries.SelectMany(g => new[] { g, g, g }).ToArray(),
            SpaceKind.Cmyk => CmykToRgb(entries),
            _ => entries,
        };
        return new ColorSpace(SpaceKind.Indexed, 1, palette, high);
    }
}
