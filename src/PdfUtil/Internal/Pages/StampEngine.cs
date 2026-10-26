using System.Globalization;
using System.Text;
using PdfUtil.Internal.Content;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Fonts;
using PdfUtil.Internal.Images;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;
using PdfUtil.Internal.Protection;
using PdfUtil.Internal.Writing;

namespace PdfUtil.Internal.Pages;

/// <summary>A stamp prepared once for all pages: encoded text lines and their size, or an image XObject and its soft mask.</summary>
internal sealed class PreparedStamp
{
    public string? FontName { get; init; }

    /// <summary>The embedded TrueType font, when the stamp uses one.</summary>
    public EmbeddedFont? Embedded { get; init; }

    public List<(byte[] Bytes, double Width)> Lines { get; } = new();

    public double Width { get; init; }

    public double Height { get; init; }

    public PdfDictionary? ImageDictionary { get; init; }

    public byte[]? ImageData { get; init; }

    public PdfDictionary? MaskDictionary { get; init; }

    public byte[]? MaskData { get; init; }
}

/// <summary>
/// Text and image stamps (F16). Each stamped page gets new resources (font or image, and an ExtGState for the opacity)
/// and new content streams; existing content streams are never re-encoded. "Over" wraps the existing content in
/// <c>q … Q</c> so its graphics state cannot leak into the stamp.
/// </summary>
internal sealed class StampEngine
{
    private const double LineHeight = 1.2;

    private readonly PdfUtilOptions _lib;
    private readonly PdfLog _log;

    public StampEngine(PdfUtilOptions lib, PdfLog log)
    {
        _lib = lib;
        _log = log;
    }

    public static string FontName(PdfStandardFont font) => font switch
    {
        PdfStandardFont.HelveticaBold => "Helvetica-Bold",
        PdfStandardFont.HelveticaOblique => "Helvetica-Oblique",
        PdfStandardFont.HelveticaBoldOblique => "Helvetica-BoldOblique",
        PdfStandardFont.TimesRoman => "Times-Roman",
        PdfStandardFont.TimesBold => "Times-Bold",
        PdfStandardFont.TimesItalic => "Times-Italic",
        PdfStandardFont.TimesBoldItalic => "Times-BoldItalic",
        PdfStandardFont.Courier => "Courier",
        PdfStandardFont.CourierBold => "Courier-Bold",
        PdfStandardFont.CourierOblique => "Courier-Oblique",
        PdfStandardFont.CourierBoldOblique => "Courier-BoldOblique",
        _ => "Helvetica",
    };

    /// <summary>Validates the options and prepares the stamp. Throws <see cref="ArgumentException"/> for anything unusable.</summary>
    public static PreparedStamp Prepare(PdfStampOptions options, PdfUtilOptions lib)
    {
        ArgumentNullException.ThrowIfNull(options);
        if ((options.Text is null) == (options.Image is null))
        {
            throw new ArgumentException("Set either Text or Image.", nameof(options));
        }

        if (!Enum.IsDefined(options.Font) || !Enum.IsDefined(options.Position) || !Enum.IsDefined(options.Layer))
        {
            throw new ArgumentException("Font, Position and Layer must be defined values.", nameof(options));
        }

        if (options.Opacity is < 0 or > 1 || double.IsNaN(options.Opacity))
        {
            throw new ArgumentException("Opacity must be between 0 and 1.", nameof(options));
        }

        if (!options.Color.IsValid)
        {
            throw new ArgumentException("Color components must be between 0 and 1.", nameof(options));
        }

        if (!double.IsFinite(options.Rotation) || !double.IsFinite(options.X) || !double.IsFinite(options.Y) || !double.IsFinite(options.Margin))
        {
            throw new ArgumentException("Rotation, X, Y and Margin must be finite numbers.", nameof(options));
        }

        if (options.Text is { } text)
        {
            if (options.FontSize is <= 0 or > 10_000 || double.IsNaN(options.FontSize))
            {
                throw new ArgumentException("FontSize must be between 0 and 10000 points.", nameof(options));
            }

            if (text.Length == 0)
            {
                throw new ArgumentException("Text is empty.", nameof(options));
            }

            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            if (options.FontData is { } fontData)
            {
                return PrepareEmbedded(options, fontData, lines);
            }

            var font = FontName(options.Font);
            var stamp = new PreparedStamp { FontName = font };
            var unsupported = new List<char>();
            foreach (var line in lines)
            {
                var bytes = FontEncodings.EncodeWinAnsi(line, out var bad);
                unsupported.AddRange(bad);
                stamp.Lines.Add((bytes, line.Sum(c => StandardFonts.Width(font, c)) / 1000.0 * options.FontSize));
            }

            if (unsupported.Count > 0)
            {
                throw new ArgumentException($"The text contains characters that the standard fonts cannot show: {string.Join(" ", unsupported.Distinct().Take(10).Select(c => $"'{c}' (U+{(int)c:X4})"))}. Only Windows-1252 (WinAnsi) characters are supported; set FontData to a TrueType font for other scripts.", nameof(options));
            }

            return new PreparedStamp
            {
                FontName = font,
                Width = stamp.Lines.Max(l => l.Width),
                Height = ((lines.Length - 1) * LineHeight * options.FontSize) + (0.93 * options.FontSize),
            }.WithLines(stamp.Lines);
        }

        return PrepareImage(options, lib);
    }

    private static PreparedStamp PrepareEmbedded(PdfStampOptions options, byte[] fontData, string[] lines)
    {
        TrueTypeFont font;
        EmbeddedFont embedded;
        try
        {
            font = TrueTypeFont.Load(fontData)[0];
            embedded = new EmbeddedFont(font);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            throw new ArgumentException("FontData is not a usable TrueType font: " + ex.Message, nameof(options), ex);
        }

        var missing = lines.SelectMany(l => l.EnumerateRunes()).Where(r => r.Value >= 32 && !font.Covers(r.Value)).Distinct().ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"The font has no glyphs for: {string.Join(" ", missing.Take(10).Select(r => $"'{r}' (U+{r.Value:X4})"))}.", nameof(options));
        }

        var encoded = lines.Select(l => (embedded.Encode(l, out _), embedded.Width(l) / 1000.0 * options.FontSize)).ToList();
        var ascent = font.Ascent / (double)font.UnitsPerEm;
        return new PreparedStamp
        {
            Embedded = embedded,
            Width = encoded.Max(l => l.Item2),
            Height = ((lines.Length - 1) * LineHeight * options.FontSize) + (Math.Clamp(ascent, 0.5, 1.2) * options.FontSize),
        }.WithLines(encoded);
    }

    private static PreparedStamp PrepareImage(PdfStampOptions options, PdfUtilOptions lib)
    {
        var data = options.Image!;
        if (options.ImageWidth is <= 0 || options.ImageWidth is double w && !double.IsFinite(w))
        {
            throw new ArgumentException("ImageWidth must be positive.", nameof(options));
        }

        try
        {
            if (ImageCodecs.IsJpeg(data))
            {
                var jpeg = ImageCodecs.ReadJpeg(data);
                var dict = new PdfDictionary
                {
                    [PdfName.Type] = PdfName.Get("XObject"),
                    ["Subtype"] = PdfName.Get("Image"),
                    ["Width"] = new PdfInteger(jpeg.Width),
                    ["Height"] = new PdfInteger(jpeg.Height),
                    ["ColorSpace"] = PdfName.Get(jpeg.Components switch { 1 => "DeviceGray", 4 => "DeviceCMYK", _ => "DeviceRGB" }),
                    ["BitsPerComponent"] = new PdfInteger(8),
                    [PdfName.Filter] = PdfName.Get("DCTDecode"),
                };
                if (jpeg.AdobeInverted)
                {
                    dict["Decode"] = new PdfArray(Enumerable.Range(0, 8).Select(i => (PdfObject)new PdfInteger(i % 2 == 0 ? 1 : 0)));
                }

                return Sized(options, jpeg.Width, jpeg.Height, dict, data, null, null);
            }

            if (ImageCodecs.IsPng(data))
            {
                var png = ImageCodecs.DecodePng(data, lib.MaxDecompressedStreamBytes / 4);
                PdfObject colorSpace = png.Palette is { } palette
                    ? new PdfArray(new PdfObject[] { PdfName.Get("Indexed"), PdfName.Get("DeviceRGB"), new PdfInteger((palette.Length / 3) - 1), new PdfString(palette, isHex: true) })
                    : PdfName.Get(png.Channels == 3 ? "DeviceRGB" : "DeviceGray");
                var dict = new PdfDictionary
                {
                    [PdfName.Type] = PdfName.Get("XObject"),
                    ["Subtype"] = PdfName.Get("Image"),
                    ["Width"] = new PdfInteger(png.Width),
                    ["Height"] = new PdfInteger(png.Height),
                    ["ColorSpace"] = colorSpace,
                    ["BitsPerComponent"] = new PdfInteger(8),
                    [PdfName.Filter] = PdfName.FlateDecode,
                };
                PdfDictionary? mask = null;
                if (png.Alpha is not null)
                {
                    mask = new PdfDictionary
                    {
                        [PdfName.Type] = PdfName.Get("XObject"),
                        ["Subtype"] = PdfName.Get("Image"),
                        ["Width"] = new PdfInteger(png.Width),
                        ["Height"] = new PdfInteger(png.Height),
                        ["ColorSpace"] = PdfName.Get("DeviceGray"),
                        ["BitsPerComponent"] = new PdfInteger(8),
                        [PdfName.Filter] = PdfName.FlateDecode,
                    };
                }

                return Sized(options, png.Width, png.Height, dict, PdfWriter.Deflate(png.Samples), mask, png.Alpha is null ? null : PdfWriter.Deflate(png.Alpha));
            }
        }
        catch (InvalidDataException ex)
        {
            throw new ArgumentException(ex.Message, nameof(options), ex);
        }

        throw new ArgumentException("Image must be a JPEG or PNG file.", nameof(options));
    }

    private static PdfDictionary StreamDictionary(PdfDictionary dict)
    {
        dict[PdfName.Filter] = PdfName.FlateDecode;
        return dict;
    }

    private static PreparedStamp Sized(PdfStampOptions options, int pixelWidth, int pixelHeight, PdfDictionary dict, byte[] data, PdfDictionary? mask, byte[]? maskData)
    {
        var width = options.ImageWidth ?? pixelWidth;
        return new PreparedStamp
        {
            Width = width,
            Height = width * pixelHeight / pixelWidth,
            ImageDictionary = dict,
            ImageData = data,
            MaskDictionary = mask,
            MaskData = maskData,
        };
    }

    public PdfStampResult Stamp(PdfSource input, Stream buffer, PdfStampOptions options, PreparedStamp stamp, CancellationToken ct)
    {
        using var doc = PdfDocument.Open(input, _lib, _log, strict: false, ct);
        using var security = EditEngine.KeepSecurity(doc, options.Password);
        var pages = PageTreeReader.ReadPageNodes(doc, _lib.MaxNestingDepth, ct);
        var selected = (options.Pages ?? PdfPageRange.All).Resolve(pages.Count);
        var edits = new ObjectEdits(doc);
        var warnings = new List<string>();

        // Shared objects: the font or image, the opacity state, and the "q" that opens the wrapped content.
        PdfReference? fontRef = null;
        PdfReference? imageRef = null;
        if (stamp.Embedded is { } embeddedFont)
        {
            fontRef = new PdfReference(edits.Add(embeddedFont.Build("PDFUTL", (obj, data) => data is null
                ? new PdfReference(edits.Add(obj), 0)
                : new PdfReference(edits.Add(new PdfStream(StreamDictionary((PdfDictionary)obj), PdfWriter.Deflate(data))), 0))), 0);
        }
        else if (stamp.FontName is { } fontName)
        {
            fontRef = new PdfReference(edits.Add(new PdfDictionary
            {
                [PdfName.Type] = PdfName.Get("Font"),
                ["Subtype"] = PdfName.Get("Type1"),
                ["BaseFont"] = PdfName.Get(fontName),
                ["Encoding"] = PdfName.Get("WinAnsiEncoding"),
            }), 0);
        }
        else
        {
            var image = stamp.ImageDictionary!.ShallowClone();
            if (stamp.MaskDictionary is { } maskDict)
            {
                image["SMask"] = new PdfReference(edits.Add(new PdfStream(maskDict.ShallowClone(), stamp.MaskData!)), 0);
            }

            imageRef = new PdfReference(edits.Add(new PdfStream(image, stamp.ImageData!)), 0);
        }

        var stateRef = new PdfReference(edits.Add(new PdfDictionary
        {
            [PdfName.Type] = PdfName.Get("ExtGState"),
            ["ca"] = new PdfReal(options.Opacity),
            ["CA"] = new PdfReal(options.Opacity),
        }), 0);
        var openRef = options.Layer == PdfStampLayer.Over ? new PdfReference(edits.Add(new PdfStream(new PdfDictionary(), "q\n"u8.ToArray())), 0) : null;

        var stamped = new List<int>();
        foreach (var number in selected)
        {
            ct.ThrowIfCancellationRequested();
            var page = pages[number - 1];
            if (page.ObjectNumber == 0)
            {
                warnings.Add($"Page {number} is a direct object inside the page tree and could not be stamped.");
                continue;
            }

            var resources = page.Resources?.ShallowClone() ?? new PdfDictionary();
            string Add(string category, string prefix, PdfObject value)
            {
                var dict = doc.ResolveDictionary(resources[category])?.ShallowClone() ?? new PdfDictionary();
                var name = prefix;
                for (var i = 2; dict.ContainsKey(name); i++)
                {
                    name = prefix + i.ToString(CultureInfo.InvariantCulture);
                }

                dict[name] = value;
                resources[category] = dict;
                return name;
            }

            var state = Add("ExtGState", "PdfUtilGS", stateRef);
            var resource = fontRef is not null ? Add("Font", "PdfUtilF", fontRef) : Add("XObject", "PdfUtilImg", imageRef!);
            var content = StampContent(page.Info, options, stamp, state, resource);
            var stampRef = new PdfReference(edits.Add(new PdfStream(new PdfDictionary(), content)), 0);

            var existing = doc.Resolve(page.Page["Contents"]) switch
            {
                PdfArray a => a.Items.ToList(),
                null => new List<PdfObject>(),
                _ => new List<PdfObject> { page.Page["Contents"]! },
            };
            var contents = new PdfArray();
            if (options.Layer == PdfStampLayer.Over)
            {
                contents.Add(openRef!);
                contents.Items.AddRange(existing);
                contents.Add(stampRef);
            }
            else
            {
                contents.Add(stampRef);
                contents.Items.AddRange(existing);
            }

            var copy = page.Page.ShallowClone();
            copy["Resources"] = resources;
            copy["Contents"] = contents;
            edits.Replace(page.ObjectNumber, copy);
            stamped.Add(number);
        }

        var outcome = new RewriteEngine(_lib, _log).Rewrite(doc, new RewriteSettings
        {
            Security = security,
            Progress = new ProgressReporter(options.Progress),
            Edits = edits,
            MinimumVersion = new PdfVersion(1, 4), // transparency (ExtGState /ca) and soft masks
            UseObjectStreams = options.Write?.UseObjectStreams == true,
            InitialWarnings = warnings,
        }, buffer, ct);
        return new PdfStampResult
        {
            SourceVersion = outcome.SourceVersion.ToString(),
            OutputVersion = outcome.OutputVersion.ToString(),
            Algorithm = doc.Security?.Algorithm ?? PdfEncryptionAlgorithm.None,
            StampedPages = stamped,
            Warnings = outcome.Warnings,
        };
    }

    private static string F(double v) => PdfReal.Format(Math.Round(v, 4));

    /// <summary>
    /// The stamp's content: closes the wrapped content (for "over"), maps display space to user space, moves to the
    /// stamp centre, rotates, and draws the text lines or the image centred on the origin.
    /// </summary>
    private static byte[] StampContent(PdfPageInfo page, PdfStampOptions options, PreparedStamp stamp, string state, string resource)
    {
        var box = PageGeometry.VisibleBox(page);
        var m = PageGeometry.DisplayToUser(page.Rotate, box);
        var displayWidth = page.DisplayWidth;
        var displayHeight = page.DisplayHeight;
        var radians = options.Rotation * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var boundsWidth = (Math.Abs(stamp.Width * cos)) + Math.Abs(stamp.Height * sin);
        var boundsHeight = (Math.Abs(stamp.Width * sin)) + Math.Abs(stamp.Height * cos);
        var margin = options.Margin;
        double Left() => margin + (boundsWidth / 2);
        double Right() => displayWidth - margin - (boundsWidth / 2);
        double Top() => displayHeight - margin - (boundsHeight / 2);
        double Bottom() => margin + (boundsHeight / 2);
        var (cx, cy) = options.Position switch
        {
            PdfStampPosition.TopLeft => (Left(), Top()),
            PdfStampPosition.TopCenter => (displayWidth / 2, Top()),
            PdfStampPosition.TopRight => (Right(), Top()),
            PdfStampPosition.MiddleLeft => (Left(), displayHeight / 2),
            PdfStampPosition.MiddleRight => (Right(), displayHeight / 2),
            PdfStampPosition.BottomLeft => (Left(), Bottom()),
            PdfStampPosition.BottomCenter => (displayWidth / 2, Bottom()),
            PdfStampPosition.BottomRight => (Right(), Bottom()),
            PdfStampPosition.Custom => (options.X, options.Y),
            _ => (displayWidth / 2, displayHeight / 2),
        };

        var sb = new StringBuilder();
        if (options.Layer == PdfStampLayer.Over)
        {
            sb.Append("Q\n");
        }

        sb.Append("q\n/").Append(state).Append(" gs\n");
        sb.Append(F(m.A)).Append(' ').Append(F(m.B)).Append(' ').Append(F(m.C)).Append(' ').Append(F(m.D)).Append(' ').Append(F(m.E)).Append(' ').Append(F(m.F)).Append(" cm\n");
        sb.Append("1 0 0 1 ").Append(F(cx)).Append(' ').Append(F(cy)).Append(" cm\n");
        sb.Append(F(cos)).Append(' ').Append(F(sin)).Append(' ').Append(F(-sin)).Append(' ').Append(F(cos)).Append(" 0 0 cm\n");
        using var output = new MemoryStream();
        if (stamp.FontName is not null || stamp.Embedded is not null)
        {
            var c = options.Color;
            sb.Append(F(c.Red)).Append(' ').Append(F(c.Green)).Append(' ').Append(F(c.Blue)).Append(" rg\nBT\n/").Append(resource).Append(' ').Append(F(options.FontSize)).Append(" Tf\n");
            var firstBaseline = (stamp.Height / 2) - (0.72 * options.FontSize);
            output.Write(Encoding.ASCII.GetBytes(sb.ToString()));
            for (var i = 0; i < stamp.Lines.Count; i++)
            {
                var (bytes, width) = stamp.Lines[i];
                var y = firstBaseline - (i * LineHeight * options.FontSize);
                output.Write(Encoding.ASCII.GetBytes($"1 0 0 1 {F(-width / 2)} {F(y)} Tm\n"));
                output.Write(PdfWriter.Serialize(new PdfString(bytes), 4));
                output.Write(" Tj\n"u8);
            }

            output.Write("ET\nQ\n"u8);
        }
        else
        {
            sb.Append(F(stamp.Width)).Append(" 0 0 ").Append(F(stamp.Height)).Append(' ').Append(F(-stamp.Width / 2)).Append(' ').Append(F(-stamp.Height / 2)).Append(" cm\n/").Append(resource).Append(" Do\nQ\n");
            output.Write(Encoding.ASCII.GetBytes(sb.ToString()));
        }

        return output.ToArray();
    }
}

internal static class PreparedStampExtensions
{
    public static PreparedStamp WithLines(this PreparedStamp stamp, List<(byte[] Bytes, double Width)> lines)
    {
        stamp.Lines.AddRange(lines);
        return stamp;
    }
}
