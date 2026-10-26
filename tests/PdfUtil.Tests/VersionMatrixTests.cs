using System.Text;
using PdfUtil.Internal.Objects;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>Every input version and file structure, protected with every algorithm (section 4 rules).</summary>
public class VersionMatrixTests
{
    private static readonly string[] Versions = { "1.0", "1.1", "1.2", "1.3", "1.4", "1.5", "1.6", "1.7", "2.0" };

    public static IEnumerable<object[]> VersionsAndAlgorithms() =>
        from v in Versions from a in TestHelpers.WritableAlgorithms select new object[] { v, a };

    public enum Structure
    {
        Classic,
        XrefStream,
        ObjectStreams,
        Hybrid,
        Linearized,
        IncrementalUpdate,
        BrokenXrefOffsets,
        BrokenStartXref,
        WrongStreamLength,
        JunkBeforeHeader,
        Entries19Bytes,
        Entries21Bytes,
        MultipleSubsections,
        TiffPredictor,
        PngPredictor10,
        PngPredictor15,
        IndirectLength,
        CompressedXmp,
    }

    public static IEnumerable<object[]> StructuresAndAlgorithms() =>
        from s in Enum.GetValues<Structure>() from a in TestHelpers.WritableAlgorithms select new object[] { s, a };

    internal static TestPdfBuilder BuilderFor(Structure structure)
    {
        var b = new TestPdfBuilder { Version = "1.5", Xmp = TestPdfBuilder.PlainXmp() };
        switch (structure)
        {
            case Structure.XrefStream: b.Mode = XrefMode.XrefStream; break;
            case Structure.ObjectStreams: b.Mode = XrefMode.XrefStreamWithObjectStreams; break;
            case Structure.Hybrid: b.Mode = XrefMode.Hybrid; break;
            case Structure.Linearized: b.Linearized = true; break;
            case Structure.IncrementalUpdate: b.IncrementalUpdate = true; break;
            case Structure.BrokenXrefOffsets: b.BreakXrefOffsets = true; break;
            case Structure.BrokenStartXref: b.BreakStartXref = true; break;
            case Structure.WrongStreamLength: b.WrongStreamLength = true; break;
            case Structure.JunkBeforeHeader: b.JunkBeforeHeader = 100; break;
            case Structure.Entries19Bytes: b.XrefEntryEol = "\n"; break;
            case Structure.Entries21Bytes: b.XrefEntryEol = " \r\n"; break;
            case Structure.MultipleSubsections: b.MultipleSubsections = true; break;
            case Structure.TiffPredictor: b.Mode = XrefMode.XrefStream; b.Predictor = 2; break;
            case Structure.PngPredictor10: b.Mode = XrefMode.XrefStreamWithObjectStreams; b.Predictor = 10; break;
            case Structure.PngPredictor15: b.Mode = XrefMode.XrefStream; b.Predictor = 15; break;
            case Structure.IndirectLength: b.IndirectLength = true; break;
            case Structure.CompressedXmp: b.CompressXmp = true; break;
        }

        return b;
    }

    [Theory]
    [MemberData(nameof(VersionsAndAlgorithms))]
    public void OutputVersion_Extensions_AndEncryptDictionary_FollowTheRules(string version, PdfEncryptionAlgorithm algorithm)
    {
        var input = new TestPdfBuilder { Version = version }.Build();
        var (output, result) = TestHelpers.Protect(input, TestHelpers.Options(algorithm));

        var required = algorithm switch { PdfEncryptionAlgorithm.Rc4_128 => "1.4", PdfEncryptionAlgorithm.Aes128 => "1.6", _ => "1.7" };
        var expected = string.CompareOrdinal(version, required) >= 0 ? version : required;
        Assert.Equal(expected, result.OutputVersion);
        Assert.Equal(version, result.SourceVersion);
        Assert.Equal("%PDF-" + expected, Encoding.ASCII.GetString(output, 0, 8));
        Assert.Equal(new byte[] { (byte)'\n', (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }, output.AsSpan(8, 7).ToArray());

        var info = Pdf.GetInfo(output, "user");
        Assert.Equal(expected, info.HeaderVersion);
        var adbe = info.Extensions.SingleOrDefault(e => e.Prefix == "ADBE");
        if (algorithm == PdfEncryptionAlgorithm.Aes256 && version != "2.0")
        {
            Assert.NotNull(adbe);
            Assert.Equal("1.7", adbe!.BaseVersion);
            Assert.Equal(8, adbe.ExtensionLevel);
        }
        else
        {
            Assert.Null(adbe);
        }

        AssertEncryptDictionary(output, algorithm);
        AssertSingleTrailerStructure(output);
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, "user").MatchedAs);
        Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(output, "owner").MatchedAs);
        Assert.False(Pdf.VerifyPassword(output, "bad").IsValid);
    }

    [Theory]
    [InlineData("1.3", PdfEncryptionAlgorithm.Aes256, "1.7")]
    [InlineData("1.5", PdfEncryptionAlgorithm.Rc4_128, "1.5")]
    [InlineData("1.7", PdfEncryptionAlgorithm.Aes128, "1.7")]
    [InlineData("2.0", PdfEncryptionAlgorithm.Aes256, "2.0")]
    public void CatalogVersion_IsKeptConsistentWithHeader(string catalogVersion, PdfEncryptionAlgorithm algorithm, string expected)
    {
        var input = new TestPdfBuilder { Version = "1.3", CatalogVersion = catalogVersion }.Build();
        var (output, result) = TestHelpers.Protect(input, TestHelpers.Options(algorithm));
        Assert.Equal(expected, result.OutputVersion);
        var info = Pdf.GetInfo(output, "owner");
        Assert.Equal(expected, info.HeaderVersion);
        Assert.Equal(expected, info.CatalogVersion);
        Assert.Equal(expected, info.EffectiveVersion);
    }

    [Theory]
    [MemberData(nameof(StructuresAndAlgorithms))]
    public void EveryStructure_IsReadProtectedAndVerified(Structure structure, PdfEncryptionAlgorithm algorithm)
    {
        var builder = BuilderFor(structure);
        var input = builder.Build();
        var before = Pdf.GetInfo(input);
        Assert.Equal(2, before.PageCount);
        Assert.Equal(structure == Structure.IncrementalUpdate ? "Updated title" : "Test document", before.Info!.Title);
        Assert.Equal("Plain XMP", before.Xmp!.Title);

        var (output, result) = TestHelpers.Protect(input, TestHelpers.Options(algorithm));
        Assert.Equal(algorithm, result.Algorithm);

        var after = Pdf.GetInfo(output, "user");
        Assert.Equal(2, after.PageCount);
        Assert.Equal(before.Info.Title, after.Info!.Title);
        Assert.Equal(before.Xmp.RawPacket, after.Xmp!.RawPacket);
        Assert.False(after.IsLinearized);
        Assert.False(after.HasObjectStreams);
        Assert.False(after.HasXrefStreams);
        Assert.False(after.WasRepaired);
        Assert.Equal(0, after.IncrementalUpdateCount);
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, "user").MatchedAs);
        Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(output, "owner").MatchedAs);
        AssertSingleTrailerStructure(output);
        ContentPreservationTests.AssertStreamsPreserved(input, null, output, "owner");

        switch (structure)
        {
            case Structure.Linearized:
                Assert.True(before.IsLinearized);
                Assert.Equal(0, before.IncrementalUpdateCount);
                Assert.Contains(result.Warnings, w => w.Contains("linearized", StringComparison.OrdinalIgnoreCase));
                break;
            case Structure.IncrementalUpdate:
                Assert.Equal(1, before.IncrementalUpdateCount);
                break;
            case Structure.BrokenXrefOffsets:
            case Structure.BrokenStartXref:
            case Structure.JunkBeforeHeader:
                Assert.True(before.WasRepaired || before.Warnings.Count > 0);
                break;
            case Structure.WrongStreamLength:
                Assert.Contains(result.Warnings, w => w.Contains("/Length", StringComparison.Ordinal));
                break;
            case Structure.ObjectStreams:
            case Structure.Hybrid:
            case Structure.PngPredictor10:
                Assert.True(before.HasObjectStreams);
                Assert.True(before.HasXrefStreams);
                break;
        }
    }

    [Fact]
    public void PreviouslyLinearizedOutput_ContainsNoLinearizationOrHintObjects()
    {
        var input = new TestPdfBuilder { Linearized = true }.Build();
        var (output, _) = TestHelpers.Protect(input, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256));
        var text = Encoding.Latin1.GetString(output);
        Assert.DoesNotContain("/Linearized", text);
        using var doc = TestHelpers.Open(output, strict: true);
        Assert.False(doc.IsLinearized);
    }

    internal static void AssertEncryptDictionary(byte[] output, PdfEncryptionAlgorithm algorithm)
    {
        var d = TestHelpers.EncryptDictionary(output);
        long Int(string k) => ((PdfInteger)d[k]!).Value;
        string Name(PdfDictionary dict, string k) => ((PdfName)dict[k]!).Value;
        int Len(string k) => ((PdfString)d[k]!).Value.Length;

        Assert.Equal("Standard", Name(d, "Filter"));
        Assert.True(Int("P") < 0, "/P must be written as a signed 32-bit integer.");
        foreach (var key in new[] { "O", "U", "OE", "UE", "Perms" })
        {
            if (d[key] is PdfString s)
            {
                Assert.True(s.IsHex, $"/{key} must be a hex string");
            }
        }

        Assert.False(d.ContainsKey("EncryptMetadata"));
        switch (algorithm)
        {
            case PdfEncryptionAlgorithm.Aes256:
                Assert.Equal(5, Int("V"));
                Assert.Equal(6, Int("R"));
                Assert.Equal(256, Int("Length"));
                Assert.Equal(48, Len("O"));
                Assert.Equal(48, Len("U"));
                Assert.Equal(32, Len("OE"));
                Assert.Equal(32, Len("UE"));
                Assert.Equal(16, Len("Perms"));
                AssertStdCF(d, "AESV3", 32);
                break;
            case PdfEncryptionAlgorithm.Aes128:
                Assert.Equal(4, Int("V"));
                Assert.Equal(4, Int("R"));
                Assert.Equal(128, Int("Length"));
                Assert.Equal(32, Len("O"));
                Assert.Equal(32, Len("U"));
                AssertStdCF(d, "AESV2", 16);
                break;
            case PdfEncryptionAlgorithm.Rc4_128:
                Assert.Equal(2, Int("V"));
                Assert.Equal(3, Int("R"));
                Assert.Equal(128, Int("Length"));
                Assert.False(d.ContainsKey("CF"));
                break;
        }

        static void AssertStdCF(PdfDictionary d, string cfm, int length)
        {
            Assert.Equal("StdCF", ((PdfName)d["StmF"]!).Value);
            Assert.Equal("StdCF", ((PdfName)d["StrF"]!).Value);
            var std = (PdfDictionary)((PdfDictionary)d["CF"]!)["StdCF"]!;
            Assert.Equal(cfm, ((PdfName)std["CFM"]!).Value);
            Assert.Equal("DocOpen", ((PdfName)std["AuthEvent"]!).Value);
            Assert.Equal(length, ((PdfInteger)std["Length"]!).Value);
        }
    }

    internal static void AssertSingleTrailerStructure(byte[] output)
    {
        var text = Encoding.Latin1.GetString(output);
        Assert.Equal(1, Count(text, "\ntrailer\n"));
        Assert.Equal(1, Count(text, "startxref"));
        Assert.Equal(1, Count(text, "%%EOF"));
        Assert.Equal(1, Count(text, "\nxref\n"));
        Assert.DoesNotContain("/ObjStm", text);
        Assert.DoesNotContain("/XRef", text);
        Assert.EndsWith("%%EOF\n", text);

        // Every xref entry is exactly 20 bytes.
        var xref = text.IndexOf("\nxref\n", StringComparison.Ordinal) + 6;
        var headerEnd = text.IndexOf('\n', xref);
        var count = int.Parse(text[xref..headerEnd].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        var entries = text.Substring(headerEnd + 1, count * 20);
        for (var i = 0; i < count; i++)
        {
            var entry = entries.Substring(i * 20, 20);
            Assert.Matches(@"^\d{10} \d{5} [nf]\r\n$", entry);
        }

        Assert.StartsWith("trailer", text[(headerEnd + 1 + (count * 20))..]);

        using var doc = TestHelpers.Open(output, strict: true);
        doc.VerifyXrefOffsetsExact();
    }

    private static int Count(string text, string pattern)
    {
        var n = 0;
        for (var i = text.IndexOf(pattern, StringComparison.Ordinal); i >= 0; i = text.IndexOf(pattern, i + 1, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }
}
