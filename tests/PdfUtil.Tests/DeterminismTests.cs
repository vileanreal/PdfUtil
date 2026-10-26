using System.Security.Cryptography;
using System.Text;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

public class DeterminismTests
{
    private const string AsciiAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
    private const string LatinAlphabet = "éèàüñçøåßÉÀÜÑ¿¡©®°±¶×÷";
    private static readonly string[] UnicodeAlphabet = { "中", "文", "日", "本", "語", "ß", "ø", "ж", "λ", "😀", "🔐", "ع", "ب", "Ж", "x", "1", " " };

    /// <summary>
    /// 1,000 protections with random passwords (ASCII for RC4/AES-128; ASCII, Latin and Unicode for AES-256), random /ID bytes (including BOM-like
    /// prefixes and NULs) and every algorithm. Each protection self-verifies; every output is then verified again
    /// through the public API.
    /// </summary>
    [Fact]
    public void ThousandRandomProtections_AllVerify()
    {
        var rng = new Random(20240601);
        var structures = new[] { XrefMode.Classic, XrefMode.XrefStream, XrefMode.XrefStreamWithObjectStreams, XrefMode.Hybrid };
        for (var i = 0; i < 1000; i++)
        {
            var algorithm = TestHelpers.WritableAlgorithms[i % 3];
            var mode = structures[(i / 3) % structures.Length];
            var idLength = new[] { 1, 8, 16, 32, 64 }[rng.Next(5)];
            var id = RandomNumberGenerator.GetBytes(idLength);
            if (rng.Next(4) == 0)
            {
                id[0] = 0xFE;
                if (idLength > 1)
                {
                    id[1] = 0xFF;
                }
            }

            var input = new TestPdfBuilder { Mode = mode, Id0 = id, PageCount = 1 + (i % 3) }.Build();
            var user = RandomPassword(rng, algorithm, allowEmpty: true);
            var owner = RandomPassword(rng, algorithm, allowEmpty: false);
            if (owner == user)
            {
                owner += owner; // keeps the script (and so the SASLprep bidi rule) unchanged
            }

            var options = TestHelpers.Options(algorithm, user, owner);
            if (algorithm == PdfEncryptionAlgorithm.Aes256 && (user + owner).Any(c => LatinAlphabet.Contains(c) || char.IsSurrogate(c)))
            {
                options.PasswordPolicy = PdfPasswordPolicy.Specification; // accented Latin and emoji need the specification policy
            }

            var (output, _) = TestHelpers.Protect(input, options);
            var userCheck = Pdf.VerifyPassword(output, user);
            Assert.True(userCheck.IsValid, $"iteration {i}: user password failed ({algorithm})");
            Assert.Equal(PdfPasswordMatch.User, userCheck.MatchedAs);
            Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(output, owner).MatchedAs);
            Assert.False(Pdf.VerifyPassword(output, owner + user + "#wrong").IsValid);
            Assert.Equal(id, Pdf.GetInfo(output, owner).PermanentId);
        }
    }

    private static string RandomPassword(Random rng, PdfEncryptionAlgorithm algorithm, bool allowEmpty)
    {
        if (allowEmpty && rng.Next(10) == 0)
        {
            return "";
        }

        var length = 1 + rng.Next(algorithm == PdfEncryptionAlgorithm.Aes256 ? 40 : 20);
        var sb = new StringBuilder();
        var kind = rng.Next(3);
        for (var i = 0; i < length; i++)
        {
            if (algorithm != PdfEncryptionAlgorithm.Aes256)
            {
                sb.Append(AsciiAlphabet[rng.Next(AsciiAlphabet.Length)]); // legacy algorithms: printable ASCII only
            }
            else if (kind != 2)
            {
                var alphabet = kind == 1 ? AsciiAlphabet + LatinAlphabet : AsciiAlphabet;
                sb.Append(alphabet[rng.Next(alphabet.Length)]);
            }
            else
            {
                sb.Append(UnicodeAlphabet[rng.Next(UnicodeAlphabet.Length)]);
            }
        }

        var s = sb.ToString();
        // Avoid RandAL/L mixtures that SASLprep (correctly) rejects; they are covered by dedicated tests.
        if (s.Contains('ع') || s.Contains('ب'))
        {
            s = string.Concat(s.Where(c => c is 'ع' or 'ب'));
            s = s.Length == 0 ? "ع" : s;
        }

        return s;
    }
}
