using System.Text;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>Section 7.1-7.4: the trailer /ID is raw bytes; /ID[0] is preserved and is the key-derivation input.</summary>
public class IdHandlingTests
{
    public static IEnumerable<object[]> TrickyIds()
    {
        var cases = new Dictionary<string, byte[]>
        {
            ["FE FF prefix"] = new byte[] { 0xFE, 0xFF, 0x00, 0x41, 0x00, 0x42, 0xD8, 0x00 },
            ["EF BB BF prefix"] = new byte[] { 0xEF, 0xBB, 0xBF, 0x68, 0x69, 0x80, 0xFF },
            ["NUL bytes"] = new byte[] { 0, 0, 0, 1, 0, 0 },
            ["backslash and parens"] = Encoding.ASCII.GetBytes("\\()\\\\((("),
            ["CR LF"] = new byte[] { 13, 10, 13, 13, 10, 10, 0x20 },
            ["length 1"] = new byte[] { 0x7F },
            ["length 16"] = Enumerable.Range(0, 16).Select(i => (byte)(i * 17)).ToArray(),
            ["length 32"] = Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray(),
            ["length 64"] = Enumerable.Range(0, 64).Select(i => (byte)(i * 31)).ToArray(),
        };
        return from c in cases from a in TestHelpers.WritableAlgorithms select new object[] { c.Key, c.Value, a };
    }

    [Theory]
    [MemberData(nameof(TrickyIds))]
    public void PermanentId_IsPreservedByteForByte_AndPasswordsWork(string label, byte[] id0, PdfEncryptionAlgorithm algorithm)
    {
        Assert.NotNull(label);
        foreach (var mode in new[] { XrefMode.Classic, XrefMode.XrefStream })
        {
            var input = new TestPdfBuilder { Id0 = id0, Mode = mode }.Build();
            Assert.Equal(id0, Pdf.GetInfo(input).PermanentId);

            var (output, _) = TestHelpers.Protect(input, TestHelpers.Options(algorithm));
            var info = Pdf.GetInfo(output, "user");
            Assert.Equal(id0, info.PermanentId);
            Assert.Equal(Convert.ToHexString(id0), info.PermanentIdHex);
            Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(output, "user").MatchedAs);
            Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(output, "owner").MatchedAs);

            // Written as a hex string, never as a literal string.
            var text = Encoding.Latin1.GetString(output);
            Assert.Contains("/ID [<" + Convert.ToHexString(id0) + "> <", text);

            // The key derivation input is exactly these bytes.
            using var doc = TestHelpers.Open(output, strict: true);
            var handler = doc.InitializeSecurity();
            Assert.Equal(id0, handler.PermanentId);
        }
    }

    [Theory]
    [InlineData(IdMode.Missing)]
    [InlineData(IdMode.Malformed)]
    public void MissingOrMalformedId_GeneratesSixteenRandomBytes(IdMode mode)
    {
        foreach (var algorithm in TestHelpers.WritableAlgorithms)
        {
            var input = new TestPdfBuilder { IdMode = mode, Version = "1.2" }.Build();
            Assert.Null(Pdf.GetInfo(input).PermanentId);
            var (out1, _) = TestHelpers.Protect(input, TestHelpers.Options(algorithm));
            var (out2, _) = TestHelpers.Protect(input, TestHelpers.Options(algorithm));
            var id1 = Pdf.GetInfo(out1, "user").PermanentId!;
            var id2 = Pdf.GetInfo(out2, "user").PermanentId!;
            Assert.Equal(16, id1.Length);
            Assert.NotEqual(id1, id2);
            Assert.True(Pdf.VerifyPassword(out1, "user").IsValid);
        }
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256)]
    [InlineData(PdfEncryptionAlgorithm.Aes128)]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128)]
    public void ChangingId_IsRegenerated_PermanentIdIsNot(PdfEncryptionAlgorithm algorithm)
    {
        var id0 = Enumerable.Repeat((byte)0xAB, 16).ToArray();
        var id1 = Enumerable.Repeat((byte)0xCD, 16).ToArray();
        var input = new TestPdfBuilder { Id0 = id0, Id1 = id1 }.Build();
        var (output, _) = TestHelpers.Protect(input, TestHelpers.Options(algorithm));
        var info = Pdf.GetInfo(output, "user");
        Assert.Equal(id0, info.PermanentId);
        Assert.NotEqual(id1, info.ChangingId);
        Assert.Equal(16, info.ChangingId!.Length);

        // Protecting the protected output's source again changes /ID[1] again but never /ID[0].
        var (output2, _) = TestHelpers.Protect(input, TestHelpers.Options(algorithm));
        var info2 = Pdf.GetInfo(output2, "user");
        Assert.Equal(id0, info2.PermanentId);
        Assert.NotEqual(info.ChangingId, info2.ChangingId);
        Assert.True(Pdf.VerifyPassword(output2, "user").IsValid);
    }

    [Fact]
    public void ChangingId_IsNotPartOfKeyDerivation()
    {
        // Rewrite /ID[1] in a protected R4 file: both passwords must keep working.
        var input = new TestPdfBuilder().Build();
        var (output, _) = TestHelpers.Protect(input, TestHelpers.Options(PdfEncryptionAlgorithm.Aes128));
        var text = Encoding.Latin1.GetString(output);
        var start = text.IndexOf("/ID [<", StringComparison.Ordinal);
        var second = text.IndexOf("> <", start, StringComparison.Ordinal) + 3;
        var tampered = (byte[])output.Clone();
        for (var i = 0; i < 32; i++)
        {
            tampered[second + i] = (byte)'0';
        }

        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(tampered, "user").MatchedAs);
        Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(tampered, "owner").MatchedAs);
    }

    [Fact]
    public void ChangingPermanentId_BreaksLegacyKeys_ProvingItIsTheKeyInput()
    {
        var input = new TestPdfBuilder().Build();
        var (output, _) = TestHelpers.Protect(input, TestHelpers.Options(PdfEncryptionAlgorithm.Rc4_128));
        var text = Encoding.Latin1.GetString(output);
        var first = text.IndexOf("/ID [<", StringComparison.Ordinal) + 6;
        var tampered = (byte[])output.Clone();
        tampered[first] = tampered[first] == (byte)'0' ? (byte)'1' : (byte)'0';
        Assert.False(Pdf.VerifyPassword(tampered, "user").IsValid);
    }

    [Fact]
    public void IncrementalUpdate_LastTrailerIdWins()
    {
        var originalId = Enumerable.Repeat((byte)0x11, 16).ToArray();
        var updatedId = new byte[] { 0xFE, 0xFF, 0x22, 0x22, 0x5C, 0x28, 0x29, 0x00 };
        var input = new TestPdfBuilder { Id0 = originalId, IncrementalUpdate = true, IncrementalId0 = updatedId }.Build();
        Assert.Equal(updatedId, Pdf.GetInfo(input).PermanentId);
        foreach (var algorithm in TestHelpers.WritableAlgorithms)
        {
            var (output, _) = TestHelpers.Protect(input, TestHelpers.Options(algorithm));
            Assert.Equal(updatedId, Pdf.GetInfo(output, "user").PermanentId);
            Assert.True(Pdf.VerifyPassword(output, "owner").IsValid);
        }
    }

    [Fact]
    public void ReEncryption_PreservesPermanentIdOfEncryptedSource()
    {
        var id0 = new byte[] { 0xEF, 0xBB, 0xBF, 1, 2, 3 };
        var input = new TestPdfBuilder { Id0 = id0, Encryption = (3, "old", "oldowner", true) }.Build();
        var options = TestHelpers.Options(PdfEncryptionAlgorithm.Aes256);
        options.AlreadyEncryptedPolicy = AlreadyEncryptedPolicy.ReEncryptWithOwnerPassword;
        options.CurrentOwnerPassword = "oldowner";
        var (output, _) = TestHelpers.Protect(input, options);
        Assert.Equal(id0, Pdf.GetInfo(output, "user").PermanentId);
    }

    [Fact]
    public void IdBytes_AreNeverTextDecoded_ByTheReader()
    {
        // A literal-string /ID with escapes is read back as exact bytes.
        var pdf = Encoding.Latin1.GetBytes(
            "%PDF-1.4\n1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n2 0 obj\n<</Type/Pages/Kids[]/Count 0>>\nendobj\n" +
            "trailer\n<</Root 1 0 R/ID[(\xFE\xFF\\000\\\\\\(\\)\r\n) <0102>]>>\n%%EOF\n");
        var info = Pdf.GetInfo(pdf);
        Assert.Equal(new byte[] { 0xFE, 0xFF, 0x00, (byte)'\\', (byte)'(', (byte)')', (byte)'\n' }, info.PermanentId);
        Assert.Equal(new byte[] { 1, 2 }, info.ChangingId);
    }
}
