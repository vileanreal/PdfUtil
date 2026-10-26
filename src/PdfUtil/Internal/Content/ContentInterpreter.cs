using System.Globalization;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Fonts;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;

namespace PdfUtil.Internal.Content;

/// <summary>An affine matrix [a b c d e f] in PDF's row-vector convention: [x y 1] × M.</summary>
internal readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
{
    public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

    public static Matrix Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    /// <summary>this × other (apply this, then other).</summary>
    public Matrix Multiply(Matrix o) => new(
        (A * o.A) + (B * o.C), (A * o.B) + (B * o.D),
        (C * o.A) + (D * o.C), (C * o.B) + (D * o.D),
        (E * o.A) + (F * o.C) + o.E, (E * o.B) + (F * o.D) + o.F);

    public (double X, double Y) Transform(double x, double y) => ((A * x) + (C * y) + E, (B * x) + (D * y) + F);

    public (double X, double Y) TransformVector(double x, double y) => ((A * x) + (C * y), (B * x) + (D * y));

    public Matrix Inverse()
    {
        var det = (A * D) - (B * C);
        if (Math.Abs(det) < 1e-12)
        {
            return Identity;
        }

        return new Matrix(D / det, -B / det, -C / det, A / det, ((C * F) - (D * E)) / det, ((B * E) - (A * F)) / det);
    }

    public static Matrix FromArray(PdfDocument doc, PdfObject? obj)
    {
        if (doc.Resolve(obj) is PdfArray { Count: 6 } a)
        {
            var v = a.Items.Select(i => doc.Resolve(i) switch { PdfInteger n => (double)n.Value, PdfReal r => r.Value, _ => double.NaN }).ToArray();
            if (v.All(double.IsFinite))
            {
                return new Matrix(v[0], v[1], v[2], v[3], v[4], v[5]);
            }
        }

        return Identity;
    }
}

/// <summary>Page geometry: the visible box and the mapping between user space and display space (the page as shown, rotation applied, origin bottom-left).</summary>
internal static class PageGeometry
{
    /// <summary>Display space → user space for a page with <paramref name="rotate"/> (clockwise) and visible <paramref name="box"/>.</summary>
    public static Matrix DisplayToUser(int rotate, PdfRectangle box) => (((rotate % 360) + 360) % 360) switch
    {
        90 => new Matrix(0, 1, -1, 0, box.UpperRightX, box.LowerLeftY),
        180 => new Matrix(-1, 0, 0, -1, box.UpperRightX, box.UpperRightY),
        270 => new Matrix(0, -1, 1, 0, box.LowerLeftX, box.UpperRightY),
        _ => Matrix.Translate(box.LowerLeftX, box.LowerLeftY),
    };

    public static PdfRectangle VisibleBox(PdfPageInfo page) => page.CropBox ?? page.MediaBox;
}

/// <summary>A glyph as shown on the page, in display space.</summary>
internal sealed class GlyphInfo
{
    public required string Text { get; init; }

    public required double X { get; init; }

    public required double Y { get; init; }

    /// <summary>Distance to the next glyph's origin along the baseline (includes character and word spacing).</summary>
    public required double Advance { get; init; }

    /// <summary>The glyph's own width along the baseline.</summary>
    public required double Width { get; init; }

    public required double Size { get; init; }

    public required double Angle { get; init; }

    public required FontDecoder Font { get; init; }

    public required string Color { get; init; }

    public required bool IsUnknown { get; init; }

    /// <summary>Drawn with text render mode 3 or 7 (neither filled nor stroked), as OCR text layers are.</summary>
    public bool IsInvisible { get; init; }

    /// <summary>Index of the text-showing operation, so glyphs shown together can be grouped.</summary>
    public required int Operation { get; init; }
}

/// <summary>An image drawn on the page.</summary>
internal sealed class ImageInfo
{
    public required PdfRectangle Bounds { get; init; }

    public int? ObjectNumber { get; init; }

    public required PdfDictionary Dictionary { get; init; }

    public PdfStream? Stream { get; init; }

    public byte[]? InlineData { get; init; }

    public PdfDictionary? Resources { get; init; }

    /// <summary>The fill colour when the image was drawn (the colour of stencil masks).</summary>
    public string FillColor { get; init; } = "#000000";
}

/// <summary>A path segment or rectangle that was stroked or filled (ruling lines, table borders), in display space.</summary>
internal readonly record struct RuleInfo(double X1, double Y1, double X2, double Y2, bool IsRectangle, bool Filled = false);

/// <summary>Everything read from one page's content.</summary>
internal sealed class PageContent
{
    public required double Width { get; init; }

    public required double Height { get; init; }

    public List<GlyphInfo> Glyphs { get; } = new();

    public List<ImageInfo> Images { get; } = new();

    public List<RuleInfo> Rules { get; } = new();

    public int UnknownGlyphs { get; set; }

    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Interprets page content streams (ISO 32000-1 §8–9): the graphics state (<c>q Q cm</c>, colours), the text state
/// (<c>BT ET Tf Td TD Tm T* Tj TJ ' " Tc Tw Tz TL Ts Tr</c>), form XObjects (recursively), images (including inline
/// images) and paths, producing glyphs, image placements and ruling lines in display space.
/// </summary>
internal sealed class ContentInterpreter
{
    private const int MaxOperations = 20_000_000;
    private const int MaxStateDepth = 1024;
    private const int MaxRules = 100_000;

    private readonly PdfDocument _doc;
    private readonly int _maxDepth;
    private readonly CancellationToken _ct;
    private readonly Dictionary<object, FontDecoder> _fonts = new();
    private PageContent _page = null!;
    private Matrix _userToDisplay;
    private int _operations;
    private int _textOperation;

    public ContentInterpreter(PdfDocument doc, int maxDepth, CancellationToken ct)
    {
        _doc = doc;
        _maxDepth = maxDepth;
        _ct = ct;
    }

    private sealed class GraphicsState
    {
        public Matrix Ctm = Matrix.Identity;
        public FontDecoder? Font;
        public double FontSize = 1;
        public double CharSpacing;
        public double WordSpacing;
        public double HorizontalScale = 1;
        public double Leading;
        public double Rise;
        public string FillColor = "#000000";
        public string FillSpace = "DeviceGray";
        public int RenderMode;

        public GraphicsState Clone() => (GraphicsState)MemberwiseClone();
    }

    public PageContent Read(PageNode page)
    {
        var box = PageGeometry.VisibleBox(page.Info);
        _userToDisplay = PageGeometry.DisplayToUser(page.Info.Rotate, box).Inverse();
        _page = new PageContent { Width = page.Info.DisplayWidth, Height = page.Info.DisplayHeight };
        _operations = 0;
        var content = ReadContents(page.Page["Contents"]);
        Execute(content, page.Resources, new GraphicsState(), 0, new HashSet<int>());
        return _page;
    }

    private byte[] ReadContents(PdfObject? contents)
    {
        var items = _doc.Resolve(contents) is PdfArray a ? a.Items : contents is null ? new List<PdfObject>() : new List<PdfObject> { contents };
        using var ms = new MemoryStream();
        foreach (var item in items)
        {
            if (item is PdfReference r && _doc.GetObject(r.Id.Number) is PdfStream s)
            {
                if (Decode(s, r.Id.Number) is { } data)
                {
                    ms.Write(data);
                    ms.WriteByte((byte)'\n');
                }
            }
        }

        return ms.ToArray();
    }

    private byte[]? Decode(PdfStream stream, int number)
    {
        try
        {
            var data = _doc.GetFullyDecodedStreamData(stream, new ObjectId(number, _doc.GetGeneration(number)));
            if (data is null)
            {
                Warn("A content stream uses a filter PdfUtil cannot decode; its text was skipped.");
            }

            return data;
        }
        catch (InvalidDataException)
        {
            Warn("A content stream is damaged; its text was skipped.");
            return null;
        }
    }

    private void Warn(string message)
    {
        if (!_page.Warnings.Contains(message))
        {
            _page.Warnings.Add(message);
        }
    }

    private void Execute(byte[] content, PdfDictionary? resources, GraphicsState initial, int depth, HashSet<int> forms)
    {
        if (depth > 32)
        {
            Warn("Form XObjects are nested too deeply; the innermost ones were skipped.");
            return;
        }

        var gs = initial;
        var stack = new Stack<GraphicsState>();
        var tm = Matrix.Identity;
        var tlm = Matrix.Identity;
        var operands = new List<PdfObject>();
        var path = new List<(double X1, double Y1, double X2, double Y2, bool Rect)>();
        (double X, double Y) current = (0, 0);
        (double X, double Y) subpathStart = (0, 0);
        using var source = PdfSource.FromBytes(content);
        using var lexer = new PdfLexer(source);
        var parser = new PdfParser(lexer, _maxDepth);
        while (true)
        {
            Token token;
            try
            {
                token = lexer.Next();
                if (token.Type == TokenType.EndOfFile)
                {
                    break;
                }

                if (token.Type != TokenType.Keyword || token.Text is "true" or "false" or "null")
                {
                    operands.Add(parser.ParseObject(token));
                    if (operands.Count > 64)
                    {
                        operands.RemoveAt(0);
                    }

                    continue;
                }
            }
            catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException)
            {
                Warn("A content stream has a syntax error; the rest of it was skipped.");
                break;
            }

            if (++_operations > MaxOperations)
            {
                throw new PdfLimitExceededException("A page has too many content operations.");
            }

            if ((_operations & 0xFFF) == 0)
            {
                _ct.ThrowIfCancellationRequested();
            }

            var op = token.Text!;
            try
            {
                switch (op)
                {
                    case "q":
                        if (stack.Count < MaxStateDepth)
                        {
                            stack.Push(gs.Clone());
                        }

                        break;
                    case "Q":
                        if (stack.Count > 0)
                        {
                            gs = stack.Pop();
                        }

                        break;
                    case "cm" when operands.Count >= 6:
                        gs.Ctm = MatrixOf(operands).Multiply(gs.Ctm);
                        break;
                    case "BT":
                        tm = tlm = Matrix.Identity;
                        break;
                    case "Tf" when operands.Count >= 2:
                        gs.Font = FontFor(resources, operands[^2]);
                        gs.FontSize = Num(operands[^1]);
                        break;
                    case "Tc" when operands.Count >= 1:
                        gs.CharSpacing = Num(operands[^1]);
                        break;
                    case "Tw" when operands.Count >= 1:
                        gs.WordSpacing = Num(operands[^1]);
                        break;
                    case "Tz" when operands.Count >= 1:
                        gs.HorizontalScale = Num(operands[^1]) / 100;
                        break;
                    case "TL" when operands.Count >= 1:
                        gs.Leading = Num(operands[^1]);
                        break;
                    case "Tr" when operands.Count >= 1:
                        gs.RenderMode = (int)Num(operands[^1]);
                        break;
                    case "Ts" when operands.Count >= 1:
                        gs.Rise = Num(operands[^1]);
                        break;
                    case "Td" when operands.Count >= 2:
                        tlm = Matrix.Translate(Num(operands[^2]), Num(operands[^1])).Multiply(tlm);
                        tm = tlm;
                        break;
                    case "TD" when operands.Count >= 2:
                        gs.Leading = -Num(operands[^1]);
                        tlm = Matrix.Translate(Num(operands[^2]), Num(operands[^1])).Multiply(tlm);
                        tm = tlm;
                        break;
                    case "Tm" when operands.Count >= 6:
                        tm = tlm = MatrixOf(operands);
                        break;
                    case "T*":
                        tlm = Matrix.Translate(0, -gs.Leading).Multiply(tlm);
                        tm = tlm;
                        break;
                    case "Tj" when operands.Count >= 1 && operands[^1] is PdfString s:
                        tm = Show(gs, tm, s.Value);
                        break;
                    case "'" when operands.Count >= 1 && operands[^1] is PdfString s1:
                        tlm = Matrix.Translate(0, -gs.Leading).Multiply(tlm);
                        tm = Show(gs, tlm, s1.Value);
                        break;
                    case "\"" when operands.Count >= 3 && operands[^1] is PdfString s2:
                        gs.WordSpacing = Num(operands[^3]);
                        gs.CharSpacing = Num(operands[^2]);
                        tlm = Matrix.Translate(0, -gs.Leading).Multiply(tlm);
                        tm = Show(gs, tlm, s2.Value);
                        break;
                    case "TJ" when operands.Count >= 1 && operands[^1] is PdfArray array:
                        foreach (var item in array.Items)
                        {
                            if (item is PdfString part)
                            {
                                tm = Show(gs, tm, part.Value);
                            }
                            else if (item is PdfInteger or PdfReal)
                            {
                                tm = Matrix.Translate(-Num(item) / 1000 * gs.FontSize * gs.HorizontalScale, 0).Multiply(tm);
                            }
                        }

                        _textOperation++;
                        break;
                    case "g" when operands.Count >= 1:
                        gs.FillColor = Gray(Num(operands[^1]));
                        gs.FillSpace = "DeviceGray";
                        break;
                    case "rg" when operands.Count >= 3:
                        gs.FillColor = Rgb(Num(operands[^3]), Num(operands[^2]), Num(operands[^1]));
                        gs.FillSpace = "DeviceRGB";
                        break;
                    case "k" when operands.Count >= 4:
                        gs.FillColor = Cmyk(Num(operands[^4]), Num(operands[^3]), Num(operands[^2]), Num(operands[^1]));
                        gs.FillSpace = "DeviceCMYK";
                        break;
                    case "cs" when operands.Count >= 1:
                        gs.FillSpace = (operands[^1] as PdfName)?.Value ?? gs.FillSpace;
                        gs.FillColor = "#000000";
                        break;
                    case "sc" or "scn":
                        var numbers = operands.Where(o => o is PdfInteger or PdfReal).Select(Num).ToList();
                        gs.FillColor = numbers.Count switch
                        {
                            1 => Gray(numbers[0]),
                            3 => Rgb(numbers[0], numbers[1], numbers[2]),
                            4 => Cmyk(numbers[0], numbers[1], numbers[2], numbers[3]),
                            _ => gs.FillColor,
                        };
                        break;
                    case "Do" when operands.Count >= 1 && operands[^1] is PdfName name:
                        DoXObject(resources, name.Value, gs, depth, forms);
                        break;
                    case "BI":
                        InlineImage(lexer, parser, source, gs, resources);
                        break;
                    case "m" when operands.Count >= 2:
                        current = subpathStart = (Num(operands[^2]), Num(operands[^1]));
                        break;
                    case "l" when operands.Count >= 2:
                        var to = (Num(operands[^2]), Num(operands[^1]));
                        path.Add((current.X, current.Y, to.Item1, to.Item2, false));
                        current = to;
                        break;
                    case "c" when operands.Count >= 6:
                        current = (Num(operands[^2]), Num(operands[^1]));
                        break;
                    case "v" or "y" when operands.Count >= 4:
                        current = (Num(operands[^2]), Num(operands[^1]));
                        break;
                    case "h":
                        if (current != subpathStart)
                        {
                            path.Add((current.X, current.Y, subpathStart.X, subpathStart.Y, false));
                        }

                        current = subpathStart;
                        break;
                    case "re" when operands.Count >= 4:
                        var (rx, ry, rw, rh) = (Num(operands[^4]), Num(operands[^3]), Num(operands[^2]), Num(operands[^1]));
                        path.Add((rx, ry, rx + rw, ry + rh, true));
                        current = subpathStart = (rx, ry);
                        break;
                    case "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*":
                        foreach (var segment in path)
                        {
                            AddRule(gs.Ctm, segment, op is not ("S" or "s"));
                        }

                        path.Clear();
                        break;
                    case "n":
                        path.Clear();
                        break;
                }
            }
            catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException)
            {
                Warn("A content operation could not be interpreted and was skipped.");
            }

            operands.Clear();
        }
    }

    private static double Num(PdfObject o) => o switch
    {
        PdfInteger i => i.Value,
        PdfReal r when double.IsFinite(r.Value) => r.Value,
        _ => 0,
    };

    private static Matrix MatrixOf(List<PdfObject> operands) => new(Num(operands[^6]), Num(operands[^5]), Num(operands[^4]), Num(operands[^3]), Num(operands[^2]), Num(operands[^1]));

    private static string Hex(double v) => ((int)Math.Round(Math.Clamp(v, 0, 1) * 255)).ToString("X2", CultureInfo.InvariantCulture);

    private static string Gray(double g) => "#" + Hex(g) + Hex(g) + Hex(g);

    private static string Rgb(double r, double g, double b) => "#" + Hex(r) + Hex(g) + Hex(b);

    private static string Cmyk(double c, double m, double y, double k) => Rgb((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));

    private FontDecoder FontFor(PdfDictionary? resources, PdfObject nameObj)
    {
        var fontRef = nameObj is PdfName name ? _doc.ResolveDictionary(resources?["Font"])?[name] : null;
        object key = fontRef is PdfReference r ? r.Id.Number : (object?)_doc.Resolve(fontRef) ?? "default";
        if (!_fonts.TryGetValue(key, out var decoder))
        {
            if (fontRef is null)
            {
                Warn("Text uses a font that is not in the page resources; it was decoded as Helvetica.");
            }

            decoder = FontDecoder.Create(_doc, _doc.ResolveDictionary(fontRef), _maxDepth);
            if (decoder.IsVertical)
            {
                Warn("Vertical text is extracted as if it were horizontal.");
            }

            _fonts[key] = decoder;
        }

        return decoder;
    }

    private Matrix Show(GraphicsState gs, Matrix tm, byte[] bytes)
    {
        var font = gs.Font ?? FontFor(null, PdfNull.Instance);
        var glyphs = font.Decode(bytes);
        foreach (var glyph in glyphs)
        {
            var trm = new Matrix(gs.FontSize * gs.HorizontalScale, 0, 0, gs.FontSize, 0, gs.Rise).Multiply(tm).Multiply(gs.Ctm);
            var toDisplay = trm.Multiply(_userToDisplay);
            var (x, y) = toDisplay.Transform(0, 0);
            var tx = ((glyph.Width * gs.FontSize) + gs.CharSpacing + (glyph.IsSpaceCode ? gs.WordSpacing : 0)) * gs.HorizontalScale;
            var lineToDisplay = tm.Multiply(gs.Ctm).Multiply(_userToDisplay);
            var (ax, ay) = lineToDisplay.TransformVector(tx, 0);
            var (wx, wy) = lineToDisplay.TransformVector(glyph.Width * gs.FontSize * gs.HorizontalScale, 0);
            var (ux, uy) = toDisplay.TransformVector(0, 1);
            var (bx, by) = toDisplay.TransformVector(1, 0);
            var angle = Math.Atan2(by, bx);
            if (glyph.IsUnknown)
            {
                _page.UnknownGlyphs++;
            }

            _page.Glyphs.Add(new GlyphInfo
            {
                Text = glyph.Text,
                X = x,
                Y = y,
                Advance = Math.Sqrt((ax * ax) + (ay * ay)) * Math.Sign(tx == 0 ? 1 : tx),
                Width = Math.Sqrt((wx * wx) + (wy * wy)),
                Size = Math.Sqrt((ux * ux) + (uy * uy)),
                Angle = angle,
                Font = font,
                Color = gs.FillColor,
                IsUnknown = glyph.IsUnknown,
                IsInvisible = gs.RenderMode is 3 or 7,
                Operation = _textOperation,
            });
            tm = Matrix.Translate(tx, 0).Multiply(tm);
        }

        _textOperation++;
        return tm;
    }

    private PdfRectangle Bounds(Matrix toUser, double x1, double y1, double x2, double y2)
    {
        var m = toUser.Multiply(_userToDisplay);
        var points = new[] { m.Transform(x1, y1), m.Transform(x2, y1), m.Transform(x1, y2), m.Transform(x2, y2) };
        return new PdfRectangle(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
    }

    private void AddRule(Matrix ctm, (double X1, double Y1, double X2, double Y2, bool Rect) segment, bool filled)
    {
        if (_page.Rules.Count >= MaxRules)
        {
            return;
        }

        var m = ctm.Multiply(_userToDisplay);
        if (segment.Rect)
        {
            var b = Bounds(ctm, segment.X1, segment.Y1, segment.X2, segment.Y2);
            _page.Rules.Add(new RuleInfo(b.LowerLeftX, b.LowerLeftY, b.UpperRightX, b.UpperRightY, true, filled));
            return;
        }

        var (x1, y1) = m.Transform(segment.X1, segment.Y1);
        var (x2, y2) = m.Transform(segment.X2, segment.Y2);
        _page.Rules.Add(new RuleInfo(x1, y1, x2, y2, false, filled));
    }

    private void DoXObject(PdfDictionary? resources, string name, GraphicsState gs, int depth, HashSet<int> forms)
    {
        var xobjectRef = _doc.ResolveDictionary(resources?["XObject"])?[name];
        if (_doc.Resolve(xobjectRef) is not PdfStream xobject)
        {
            return;
        }

        var number = (xobjectRef as PdfReference)?.Id.Number;
        switch ((_doc.Resolve(xobject.Dictionary["Subtype"]) as PdfName)?.Value)
        {
            case "Image":
                _page.Images.Add(new ImageInfo { Bounds = Bounds(gs.Ctm, 0, 0, 1, 1), ObjectNumber = number, Dictionary = xobject.Dictionary, Stream = xobject, Resources = resources, FillColor = gs.FillColor });
                break;
            case "Form" when number is int n && !forms.Contains(n):
                var data = Decode(xobject, n);
                if (data is null)
                {
                    return;
                }

                var state = gs.Clone();
                state.Ctm = Matrix.FromArray(_doc, xobject.Dictionary["Matrix"]).Multiply(gs.Ctm);
                forms.Add(n);
                Execute(data, _doc.ResolveDictionary(xobject.Dictionary["Resources"]) ?? resources, state, depth + 1, forms);
                forms.Remove(n);
                break;
        }
    }

    /// <summary>Reads an inline image (<c>BI … ID data EI</c>) and moves past its data.</summary>
    private void InlineImage(PdfLexer lexer, PdfParser parser, PdfSource source, GraphicsState gs, PdfDictionary? resources)
    {
        var dict = new PdfDictionary();
        while (true)
        {
            var token = lexer.Next();
            if (token.Type == TokenType.EndOfFile)
            {
                return;
            }

            if (token.IsKeyword("ID"))
            {
                break;
            }

            if (token.Type == TokenType.Name)
            {
                dict[PdfName.Get(token.Text!)] = parser.ParseObject();
            }
        }

        // One white-space byte follows ID; the data ends at whitespace + "EI" + whitespace (or end).
        var start = lexer.Position + 1;
        var length = InlineLength(dict);
        long end = -1;
        if (length is long known && start + known <= source.Length)
        {
            end = start + known;
        }
        else
        {
            for (var p = start; p + 2 <= source.Length; p++)
            {
                if (source.PeekAt(p) == 'E' && source.PeekAt(p + 1) == 'I' && (p == start || PdfLexer.IsWhitespace(source.PeekAt(p - 1))) && (p + 2 == source.Length || PdfLexer.IsWhitespace(source.PeekAt(p + 2))))
                {
                    end = p;
                    break;
                }
            }
        }

        if (end < 0)
        {
            lexer.Position = source.Length;
            return;
        }

        var data = source.ReadBytes(start, Math.Max(0, end - start));
        lexer.Position = end;
        var ei = lexer.Next();
        if (!ei.IsKeyword("EI"))
        {
            lexer.Position = end;
        }

        _page.Images.Add(new ImageInfo { Bounds = Bounds(gs.Ctm, 0, 0, 1, 1), Dictionary = dict, InlineData = data, Resources = resources, FillColor = gs.FillColor });
    }

    /// <summary>The exact data length of an unfiltered inline image, or null.</summary>
    private long? InlineLength(PdfDictionary dict)
    {
        if (dict["F"] is not null || dict["Filter"] is not null)
        {
            return null;
        }

        long Get(string a, string b) => (dict[a] ?? dict[b]) is PdfInteger i ? i.Value : -1;
        var w = Get("W", "Width");
        var h = Get("H", "Height");
        var bpc = (dict["IM"] ?? dict["ImageMask"]) is PdfBoolean { Value: true } ? 1 : Get("BPC", "BitsPerComponent");
        var components = ((dict["CS"] ?? dict["ColorSpace"]) as PdfName)?.Value switch
        {
            "G" or "DeviceGray" or "I" or "Indexed" => 1,
            "RGB" or "DeviceRGB" => 3,
            "CMYK" or "DeviceCMYK" => 4,
            null => 1,
            _ => -1,
        };
        if (w <= 0 || h <= 0 || bpc <= 0 || components < 0 || w > 1 << 20 || h > 1 << 20)
        {
            return null;
        }

        return ((w * components * bpc) + 7) / 8 * h;
    }
}
