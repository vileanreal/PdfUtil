using System.Text;
using PdfUtil.Internal.Filters;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;

namespace PdfUtil.Tests;

public class LexerParserTests
{
    private static PdfObject Parse(string text)
    {
        using var source = PdfSource.FromBytes(Encoding.Latin1.GetBytes(text));
        using var lexer = new PdfLexer(source);
        return new PdfParser(lexer, 64).ParseObject();
    }

    private static byte[] Str(string text) => ((PdfString)Parse(text)).Value;

    [Theory]
    [InlineData("null", typeof(PdfNull))]
    [InlineData("true", typeof(PdfBoolean))]
    [InlineData("false", typeof(PdfBoolean))]
    [InlineData("123", typeof(PdfInteger))]
    [InlineData("-17", typeof(PdfInteger))]
    [InlineData("+4", typeof(PdfInteger))]
    [InlineData("3.14", typeof(PdfReal))]
    [InlineData("-.002", typeof(PdfReal))]
    [InlineData("4.", typeof(PdfReal))]
    [InlineData("/Name", typeof(PdfName))]
    [InlineData("(text)", typeof(PdfString))]
    [InlineData("<414243>", typeof(PdfString))]
    [InlineData("[1 2 3]", typeof(PdfArray))]
    [InlineData("<< /A 1 >>", typeof(PdfDictionary))]
    [InlineData("12 0 R", typeof(PdfReference))]
    public void ParsesAllObjectTypes(string text, Type expected) => Assert.IsType(expected, Parse(text));

    [Fact]
    public void Names_WithHexEscapes_AndPre12Form()
    {
        Assert.Equal("A B", ((PdfName)Parse("/A#20B")).Value);
        Assert.Equal("Lime Green", ((PdfName)Parse("/Lime#20Green")).Value);
        Assert.Equal("paired()parentheses", ((PdfName)Parse("/paired#28#29parentheses")).Value);
        Assert.Equal("A#B", ((PdfName)Parse("/A#B")).Value); // pre-1.2: '#' not followed by two hex digits
        Assert.Equal("", ((PdfName)Parse("/ ")).Value);
        Assert.Equal("é", ((PdfName)Parse("/#E9")).Value);
    }

    [Fact]
    public void LiteralStrings_AllEscapes()
    {
        Assert.Equal(Encoding.Latin1.GetBytes("a\nb\rc\td\be\ff(g)h\\i"), Str(@"(a\nb\rc\td\be\ff\(g\)h\\i)"));
        Assert.Equal(new byte[] { 0x53, 0x0A, 0xFF, 0x07 }, Str(@"(\123\12\377\7)"));
        Assert.Equal(new byte[] { 0x01, 0x32 }, Str(@"(\0012)")); // at most three octal digits
        Assert.Equal(Encoding.ASCII.GetBytes("balanced (nested (parens)) ok"), Str("(balanced (nested (parens)) ok)"));
        Assert.Equal(Encoding.ASCII.GetBytes("continued line"), Str("(continued \\\r\nline)"));
        Assert.Equal(Encoding.ASCII.GetBytes("continued line"), Str("(continued \\\nline)"));
        Assert.Equal(Encoding.ASCII.GetBytes("continued line"), Str("(continued \\\rline)"));
        Assert.Equal(Encoding.ASCII.GetBytes("a\nb\nc\nd"), Str("(a\r\nb\rc\nd)")); // unescaped EOLs become LF
        Assert.Equal(Encoding.ASCII.GetBytes("q"), Str(@"(\q)")); // unknown escape: backslash dropped
        Assert.Empty(Str("()"));
    }

    [Fact]
    public void HexStrings_OddLengthAndWhitespace()
    {
        Assert.Equal(new byte[] { 0x90, 0x1F, 0xA3 }, Str("<901FA3>"));
        Assert.Equal(new byte[] { 0x90, 0x1F, 0xA0 }, Str("<901FA>"));
        Assert.Equal(new byte[] { 0xAB, 0xCD }, Str("< a b\r\n c\td >"));
        Assert.Empty(Str("<>"));
    }

    [Fact]
    public void Dictionaries_AndArrays_Nested()
    {
        var d = (PdfDictionary)Parse("<< /Type /Page /Kids [1 0 R 2 0 R] /Inner << /X [1 [2 [3]]] >> /S (x) /N null >>");
        Assert.Equal("Page", ((PdfName)d["Type"]!).Value);
        Assert.Equal(2, ((PdfArray)d["Kids"]!).Count);
        Assert.IsType<PdfReference>(((PdfArray)d["Kids"]!)[0]);
        Assert.IsType<PdfNull>(d["N"]);
        Assert.Equal(new[] { "Type", "Kids", "Inner", "S", "N" }, d.Keys.Select(k => k.Value));
    }

    [Fact]
    public void LineEndings_CrLfAndCrlf_AndComments()
    {
        var d = (PdfDictionary)Parse("<<\r/A 1\n/B 2\r\n/C % comment\r\n3 >>");
        Assert.Equal(3, ((PdfInteger)d["C"]!).Value);
    }

    [Fact]
    public void NestingDepthLimit_IsEnforced()
    {
        Assert.Throws<PdfLimitExceededException>(() => Parse(new string('[', 100) + new string(']', 100)));
    }

    [Fact]
    public void JunkBeforeHeader_IsTolerated()
    {
        var pdf = Encoding.Latin1.GetBytes(new string('x', 500) + "\n%PDF-1.3\n1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n2 0 obj\n<</Type/Pages/Kids[]/Count 0>>\nendobj\ntrailer\n<</Root 1 0 R>>\n%%EOF");
        var info = Pdf.GetInfo(pdf);
        Assert.Equal("1.3", info.HeaderVersion);
        Assert.Equal(0, info.PageCount);

        var tooFar = Encoding.Latin1.GetBytes(new string('x', 2000) + "%PDF-1.3\n");
        Assert.Throws<PdfCorruptException>(() => Pdf.GetInfo(tooFar));
    }

    [Fact]
    public void Predictors_PngAllTypes_AndTiff()
    {
        // Rows of 3 bytes with each PNG filter type.
        var raw = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
        var encoded = new List<byte>();
        var prior = new byte[3];
        for (var r = 0; r < 5; r++)
        {
            var type = (byte)r;
            encoded.Add(type);
            for (var c = 0; c < 3; c++)
            {
                var x = raw[(r * 3) + c];
                var left = c > 0 ? raw[(r * 3) + c - 1] : 0;
                var up = prior[c];
                var upLeft = c > 0 ? prior[c - 1] : 0;
                encoded.Add(type switch
                {
                    1 => (byte)(x - left),
                    2 => (byte)(x - up),
                    3 => (byte)(x - ((left + up) >> 1)),
                    4 => (byte)(x - Paeth(left, up, upLeft)),
                    _ => x,
                });
            }

            Array.Copy(raw, r * 3, prior, 0, 3);
        }

        var parms = new PdfDictionary { ["Predictor"] = new PdfInteger(15), ["Columns"] = new PdfInteger(3) };
        Assert.Equal(raw, StreamFilters.ApplyPredictor(encoded.ToArray(), parms, 1 << 20));

        var tiff = new byte[] { 10, 1, 1, 20, 2, 2 };
        var tiffParms = new PdfDictionary { ["Predictor"] = new PdfInteger(2), ["Columns"] = new PdfInteger(3) };
        Assert.Equal(new byte[] { 10, 11, 12, 20, 22, 24 }, StreamFilters.ApplyPredictor(tiff, tiffParms, 1 << 20));

        static int Paeth(int a, int b, int c)
        {
            var p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }
    }

    [Fact]
    public void Flate_RoundTrip_RawDeflate_AndTruncation()
    {
        var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("hello pdf ", 500)));
        var z = StreamFilters.FlateEncode(data);
        Assert.Equal(data, StreamFilters.FlateDecode(z, 1 << 20));
        Assert.Equal(data, StreamFilters.FlateDecode(z.AsSpan(2).ToArray(), 1 << 20)); // no zlib header
        var random = new byte[300_000];
        new Random(7).NextBytes(random);
        var zr = StreamFilters.FlateEncode(random);
        var truncated = StreamFilters.FlateDecode(zr.AsSpan(0, zr.Length / 2).ToArray(), 1 << 20);
        Assert.True(truncated.Length > 0 && random.AsSpan().StartsWith(truncated));
        Assert.Throws<PdfLimitExceededException>(() => StreamFilters.FlateDecode(z, 100));
    }
}
