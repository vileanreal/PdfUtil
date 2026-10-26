using System.Text;
using PdfUtil.Internal.Security;

namespace PdfUtil.Tests;

/// <summary>
/// Known-answer tests. RC4 vectors are the widely published ones; all others were produced by an independent
/// JavaScript implementation of ISO 32000-1/-2 (Node.js crypto for MD5/SHA-2/AES, BigInt arithmetic for the
/// Algorithm 2.B modulus) and are hard-coded here.
/// </summary>
public class CryptoKnownAnswerTests
{
    private static readonly byte[] Id = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
    private const int P = -3904; // 0xFFFFF0C0: only the reserved bits (7, 8, 13-32) set
    private static byte[] B(string s) => Encoding.ASCII.GetBytes(s);
    private static byte[] H(string hex) => Convert.FromHexString(hex);

    [Theory]
    [InlineData("Key", "Plaintext", "BBF316E8D940AF0AD3")]
    [InlineData("Wiki", "pedia", "1021BF0420")]
    [InlineData("Secret", "Attack at dawn", "45A01F645FC35B383552544B9BF5")]
    public void Rc4_MatchesPublishedVectors(string key, string plaintext, string expected)
    {
        var cipher = Rc4.Transform(B(key), B(plaintext));
        Assert.Equal(expected, Convert.ToHexString(cipher));
        Assert.Equal(B(plaintext), Rc4.Transform(B(key), cipher));
    }

    [Fact]
    public void ComputeP_SetsReservedBits()
    {
        Assert.Equal(P, SecurityAlgorithms.ComputeP(PdfPermissions.None));
        Assert.Equal(unchecked((int)0xFFFFF0D4), SecurityAlgorithms.ComputeP(PdfPermissions.Print | PdfPermissions.CopyContent));
        Assert.Equal(-4, SecurityAlgorithms.ComputeP(PdfPermissions.All));
    }

    [Theory]
    [InlineData(2, 5, "94E8094419662A774442FB072E3D9F19E9D130EC09A4D0061E78FE920F7AB62F", "7FCA5CFCC5", "13F520C882D052BF57B416B747C13979BDED7EA31240FE41928852ACA3894C49")]
    [InlineData(3, 16, "0BA3835F88F90388E74E54584125CE142BE0DE24C6B0D37746E075B891756671", "EBC53CF170C71152A5BA9925BD0FEFC3", "B8D04C0B647956D75DF3B1F5A437EF9700000000000000000000000000000000")]
    [InlineData(4, 16, "0BA3835F88F90388E74E54584125CE142BE0DE24C6B0D37746E075B891756671", "EBC53CF170C71152A5BA9925BD0FEFC3", "B8D04C0B647956D75DF3B1F5A437EF9700000000000000000000000000000000")]
    public void Algorithms2to5_KnownAnswers(int revision, int keyBytes, string expectedO, string expectedKey, string expectedU)
    {
        // Algorithm 3 (/O)
        var o = SecurityAlgorithms.ComputeOwnerValueR2to4(B("owner"), B("user"), revision, keyBytes);
        Assert.Equal(expectedO, Convert.ToHexString(o));

        // Algorithm 2 (file key)
        var key = SecurityAlgorithms.ComputeFileKeyR2to4(B("user"), o, P, Id, revision, keyBytes, encryptMetadata: true);
        Assert.Equal(expectedKey, Convert.ToHexString(key));

        // Algorithm 4 / 5 (/U)
        var u = SecurityAlgorithms.ComputeUserValueR2to4(key, Id, revision);
        Assert.Equal(expectedU, Convert.ToHexString(u));

        // Algorithm 6 (user authentication) and 7 (owner authentication)
        Assert.Equal(key, SecurityAlgorithms.AuthenticateUserR2to4(B("user"), o, u, P, Id, revision, keyBytes, true));
        Assert.Equal(key, SecurityAlgorithms.AuthenticateOwnerR2to4(B("owner"), o, u, P, Id, revision, keyBytes, true));
        Assert.Null(SecurityAlgorithms.AuthenticateUserR2to4(B("wrong"), o, u, P, Id, revision, keyBytes, true));
        Assert.Null(SecurityAlgorithms.AuthenticateOwnerR2to4(B("wrong"), o, u, P, Id, revision, keyBytes, true));
        Assert.Null(SecurityAlgorithms.AuthenticateOwnerR2to4(B("user"), o, u, P, Id, revision, keyBytes, true));
    }

    [Fact]
    public void Algorithm2_R4WithoutMetadataEncryption()
    {
        var o = SecurityAlgorithms.ComputeOwnerValueR2to4(B("owner"), B("user"), 4, 16);
        var key = SecurityAlgorithms.ComputeFileKeyR2to4(B("user"), o, P, Id, 4, 16, encryptMetadata: false);
        Assert.Equal("B7F28EE1B51D773508063D025D186024", Convert.ToHexString(key));
    }

    [Fact]
    public void Algorithms2to5_EmptyUserPassword()
    {
        var o = SecurityAlgorithms.ComputeOwnerValueR2to4(B("owner"), Array.Empty<byte>(), 3, 16);
        Assert.Equal("566FA873EE33C797CD3B904FDADF814AFA34DF9A38F6ED41B984E2C6DA2AA6F5", Convert.ToHexString(o));
        var key = SecurityAlgorithms.ComputeFileKeyR2to4(Array.Empty<byte>(), o, P, Id, 3, 16, true);
        Assert.Equal("B12BD8E5CA6152AE54C1256F8EB217D7", Convert.ToHexString(key));
        var u = SecurityAlgorithms.ComputeUserValueR2to4(key, Id, 3);
        Assert.Equal("6A377F14504BE9F1ABA99E13933760AC00000000000000000000000000000000", Convert.ToHexString(u));
    }

    [Fact]
    public void Algorithm1_ObjectKeys()
    {
        Assert.Equal("C04F7FCAF7C597813958964926B93471", Convert.ToHexString(SecurityAlgorithms.ComputeObjectKey(H("EBC53CF170C71152A5BA9925BD0FEFC3"), 12, 0, aes: false)));
        Assert.Equal("C9C563681CC721EEA7142A8E3997D65A", Convert.ToHexString(SecurityAlgorithms.ComputeObjectKey(H("EBC53CF170C71152A5BA9925BD0FEFC3"), 0x123456, 7, aes: true)));
        Assert.Equal("6CD25D5836E2FBD3FC86", Convert.ToHexString(SecurityAlgorithms.ComputeObjectKey(H("7FCA5CFCC5"), 1, 0, aes: false)));
    }

    private static readonly byte[] FileKey = H("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
    private static readonly byte[] Uvs = H("1111111111111111");
    private static readonly byte[] Uks = H("2222222222222222");
    private static readonly byte[] Ovs = H("3333333333333333");
    private static readonly byte[] Oks = H("4444444444444444");
    private const string R6U = "0396260D544D947616A79D025B32F1365507BDBA71CE0350EF96F8F55AF4564A11111111111111112222222222222222";
    private const string R6UE = "F56815BA98D2731E41FC45077591152568022DF17D1F14B7D4409A7E44380D09";
    private const string R6O = "53D99D8C5B202732CF0C0B01CE3EECDEED87709C7B55A26B5B206CC9B318682F33333333333333334444444444444444";
    private const string R6OE = "6FE861021DB087D7897284A921CBFBBFBD4D9EC49ADBF563346D522BA97FD4B1";

    [Fact]
    public void Algorithms8and9_R6_KnownAnswers()
    {
        var (u, ue) = SecurityAlgorithms.ComputeUserValuesR6(6, B("user"), FileKey, Uvs, Uks);
        Assert.Equal(R6U, Convert.ToHexString(u));
        Assert.Equal(R6UE, Convert.ToHexString(ue));
        var (o, oe) = SecurityAlgorithms.ComputeOwnerValuesR6(6, B("owner"), FileKey, u, Ovs, Oks);
        Assert.Equal(R6O, Convert.ToHexString(o));
        Assert.Equal(R6OE, Convert.ToHexString(oe));
    }

    [Fact]
    public void Algorithm2A_R6_RetrievesFileKey()
    {
        var (userKey, userMatch) = SecurityAlgorithms.AuthenticateR6(6, B("user"), H(R6O), H(R6U), H(R6OE), H(R6UE));
        Assert.Equal(PdfPasswordMatch.User, userMatch);
        Assert.Equal(FileKey, userKey);
        var (ownerKey, ownerMatch) = SecurityAlgorithms.AuthenticateR6(6, B("owner"), H(R6O), H(R6U), H(R6OE), H(R6UE));
        Assert.Equal(PdfPasswordMatch.Owner, ownerMatch);
        Assert.Equal(FileKey, ownerKey);
        Assert.Equal(PdfPasswordMatch.None, SecurityAlgorithms.AuthenticateR6(6, B("nope"), H(R6O), H(R6U), H(R6OE), H(R6UE)).Match);
    }

    [Fact]
    public void Algorithm2B_KnownAnswers()
    {
        Assert.Equal("20F5C367311B95DCCA918CB8CC98D654CAA337E00D59F34299A39023D78206EF", Convert.ToHexString(SecurityAlgorithms.HashR6(Encoding.UTF8.GetBytes("pässwörd😀"), Uvs, ReadOnlySpan<byte>.Empty)));
        Assert.Equal("5CE96B4C1189575B48F8769CD522AFB1D210241435ABA4BF5830F644E9A8BB20", Convert.ToHexString(SecurityAlgorithms.HashR6(Enumerable.Repeat((byte)'a', 127).ToArray(), Uvs, H(R6U))));
    }

    [Fact]
    public void Algorithm10and13_Perms()
    {
        var perms = SecurityAlgorithms.ComputePerms(FileKey, P, encryptMetadata: true, B("ABCD"));
        Assert.Equal("1EF26B7E8E83F6CE6F184A209571CACA", Convert.ToHexString(perms));
        Assert.True(SecurityAlgorithms.ValidatePerms(FileKey, perms, P, true));
        Assert.False(SecurityAlgorithms.ValidatePerms(FileKey, perms, P, false));
        Assert.False(SecurityAlgorithms.ValidatePerms(FileKey, perms, -4, true));
    }

    [Fact]
    public void R5_KnownAnswers()
    {
        var (u, ue) = SecurityAlgorithms.ComputeUserValuesR6(5, B("user"), FileKey, Uvs, Uks);
        Assert.Equal("F0FE5A226C44631F1BCC64E5BD4AFEFEBAA6FE0EBA174AFB9429F2445879AF0811111111111111112222222222222222", Convert.ToHexString(u));
        Assert.Equal("4860CFD629B410C3429B70FD37A251DF84DAB04E08E047938FC0A3FD046EEB09", Convert.ToHexString(ue));
    }

    [Fact]
    public void Aes_KnownAnswerAndRoundTrip()
    {
        var key = H("C9C563681CC721EEA7142A8E3997D65A");
        var iv = H("A0A1A2A3A4A5A6A7A8A9AAABACADAEAF");
        Assert.Equal("A0A1A2A3A4A5A6A7A8A9AAABACADAEAFE364AAF6FB95EBCD369A9D50746A9CD8", Convert.ToHexString(AesCipher.EncryptWithIv(key, iv, B("Hello, PDF!"))));

        // Empty input still gets an IV and one padding block.
        var empty = AesCipher.EncryptWithIv(key, iv, Array.Empty<byte>());
        Assert.Equal("A0A1A2A3A4A5A6A7A8A9AAABACADAEAF5D16997DFEF03939EB574698917281CC", Convert.ToHexString(empty));
        Assert.Empty(AesCipher.DecryptWithIv(key, empty));

        foreach (var len in new[] { 0, 1, 15, 16, 17, 1000 })
        {
            var data = Enumerable.Range(0, len).Select(i => (byte)i).ToArray();
            var a = AesCipher.EncryptWithRandomIv(key, data);
            var b = AesCipher.EncryptWithRandomIv(key, data);
            Assert.NotEqual(a.AsSpan(0, 16).ToArray(), b.AsSpan(0, 16).ToArray()); // fresh IVs
            Assert.Equal(16 + ((len / 16) + 1) * 16, a.Length);
            Assert.Equal(data, AesCipher.DecryptWithIv(key, a));
            Assert.Equal(data, AesCipher.DecryptWithIv(FileKey, AesCipher.EncryptWithRandomIv(FileKey, data)));
        }
    }

    [Fact]
    public void PasswordPadding_MatchesSpecification()
    {
        Assert.Equal("28BF4E5E4E758A4164004E56FFFA01082E2E00B6D0683E802F0CA9FE6453697A", Convert.ToHexString(SecurityAlgorithms.PasswordPadding));
        Assert.Equal(SecurityAlgorithms.PasswordPadding.ToArray(), SecurityAlgorithms.PadPassword(Array.Empty<byte>()));
        Assert.Equal(B(new string('x', 32)), SecurityAlgorithms.PadPassword(B(new string('x', 40))));
    }
}
