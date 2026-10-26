using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfUtil.Internal;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;
using PdfUtil.Internal.Protection;
using PdfUtil.Internal.Writing;
using PdfUtil.Signatures;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>F17: digital signatures.</summary>
public class SignatureTests
{
    private static readonly PdfSigner Signer = new();

    internal static byte[] Sign(byte[] pdf, PdfSignOptions options, out PdfSignResult result)
    {
        if (options.Certificate is { HasPrivateKey: false } certificate)
        {
            options.PrivateKey ??= TestCertificates.KeyFor(certificate);
        }

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        result = Signer.SignAsync(input, output, options).GetAwaiter().GetResult();
        return output.ToArray();
    }

    internal static PdfSignatureVerificationResult Verify(byte[] pdf, PdfSignatureVerificationOptions? options = null) =>
        Signer.VerifyAsync(new MemoryStream(pdf), options).GetAwaiter().GetResult();

    [Fact]
    public void Invisible_Rsa_IsValidPades()
    {
        var input = new TestPdfBuilder().Build();
        var cert = TestCertificates.Rsa();
        var output = Sign(input, new PdfSignOptions { Certificate = cert, Reason = "Approved", Location = "Oslo" }, out var result);
        Assert.Equal("Signature1", result.FieldName);
        Assert.True(output.AsSpan(0, input.Length).SequenceEqual(input), "signing must append, not rewrite");
        Assert.Equal(output.Length, result.ByteRange[2] + result.ByteRange[3]);

        var v = Assert.Single(Verify(output).Signatures);
        Assert.True(v.IsValid, string.Join("; ", v.Problems));
        Assert.True(v.DigestMatches);
        Assert.True(v.SignatureIsValid);
        Assert.True(v.CoversWholeFile);
        Assert.False(v.ModifiedAfterSigning);
        Assert.Equal(2, v.Revision);
        Assert.Equal("ETSI.CAdES.detached", v.SubFilter);
        Assert.Equal("Jane Signer", v.SignerName);
        Assert.Equal("SHA256", v.DigestAlgorithm);
        Assert.Equal(cert.Thumbprint, v.Signer!.Thumbprint);
        Assert.Null(v.SigningTime); // PAdES: the claimed time is /M, not a signing-time attribute
        Assert.NotNull(v.ClaimedTime);

        // The CMS carries the ESS signing-certificate-v2 attribute PAdES requires.
        using var doc = TestHelpers.Open(output);
        var sig = (PdfDictionary)doc.Resolve(((PdfDictionary)doc.Resolve(((PdfArray)doc.Resolve(doc.Catalog!.Resolve("AcroForm", doc)!["Fields"])!)[0])!)["V"])!;
        var cms = new SignedCms();
        cms.Decode(Signatures.Internal.SignatureVerifier.TrimDer(((PdfString)sig["Contents"]!).Value));
        Assert.Contains(cms.SignerInfos[0].SignedAttributes.Cast<CryptographicAttributeObject>(), a => a.Oid.Value == "1.2.840.113549.1.9.16.2.47");
        Assert.Equal("Approved", Internal.Text.TextString.Decode(((PdfString)sig["Reason"]!).Value));

        // Inspection sees it too.
        var signature = Assert.Single(Pdf.Inspect(output).Signatures);
        Assert.True(signature.CoversWholeFile);
        Assert.Equal("ETSI.CAdES.detached", signature.SubFilter);
    }

    [Theory]
    [InlineData("SHA256")]
    [InlineData("SHA384")]
    [InlineData("SHA512")]
    public void Ecdsa_AndEveryDigest(string digest)
    {
        var output = Sign(new TestPdfBuilder { Mode = XrefMode.XrefStreamWithObjectStreams }.Build(), new PdfSignOptions { Certificate = TestCertificates.Ecdsa(), DigestAlgorithm = new HashAlgorithmName(digest) }, out _);
        var v = Assert.Single(Verify(output).Signatures);
        Assert.True(v.IsValid, string.Join("; ", v.Problems));
        Assert.Equal(digest, v.DigestAlgorithm);
    }

    [Fact]
    public void Visible_OnARotatedPage()
    {
        var input = new TestPdfBuilder().Build(); // page 2 is rotated 90 degrees
        var output = Sign(input, new PdfSignOptions
        {
            Certificate = TestCertificates.Rsa(),
            Appearance = new PdfSignatureAppearance { Page = 2, Bounds = new PdfRectangle(50, 50, 250, 110) },
        }, out _);
        Assert.True(Assert.Single(Verify(output).Signatures).IsValid);

        using var doc = TestHelpers.Open(output);
        var page = PageTreeReader.ReadPageNodes(doc, 64, CancellationToken.None)[1];
        var widget = ((PdfArray)doc.Resolve(page.Page["Annots"])!).Items.Select(doc.ResolveDictionary).Single(a => a!["FT"] is PdfName { Value: "Sig" })!;
        var rect = ((PdfArray)widget["Rect"]!).Items.Select(i => i is PdfInteger n ? n.Value : ((PdfReal)i).Value).ToArray();
        Assert.Equal(new[] { 485.0, 50, 545, 250 }, rect.Select(x => Math.Round(x)));
        var appearance = (PdfStream)doc.Resolve(doc.ResolveDictionary(widget["AP"])!["N"])!;
        Assert.Contains("Digitally signed by Jane Signer", Encoding.Latin1.GetString(doc.GetDecodedStreamData(appearance, new ObjectId(0, 0))!), StringComparison.Ordinal);
        Assert.Equal(4, ((PdfArray)appearance.Dictionary["BBox"]!).Count);
        Assert.Equal(new[] { 2 }, Pdf.Inspect(output).Signatures.Select(s => s.Page!.Value));
    }

    [Fact]
    public void ExistingUnsignedField_IsSigned()
    {
        var input = new TestPdfBuilder { Rich = true }.Build();
        var output = Sign(input, new PdfSignOptions { Certificate = TestCertificates.Rsa(), FieldName = "approval" }, out var result);
        Assert.Equal("approval", result.FieldName);
        Assert.Equal("approval", Assert.Single(Verify(output).Signatures).FieldName);
        Assert.Throws<ArgumentException>(() => Sign(output, new PdfSignOptions { Certificate = TestCertificates.Rsa(), FieldName = "approval" }, out _));
        Assert.Throws<ArgumentException>(() => Sign(input, new PdfSignOptions { Certificate = TestCertificates.Rsa(), FieldName = "name" }, out _));
    }

    [Fact]
    public void Tampering_IsDetected()
    {
        var output = Sign(new TestPdfBuilder().Build(), new PdfSignOptions { Certificate = TestCertificates.Rsa() }, out var result);

        // One byte of the signed content.
        var text = Encoding.Latin1.GetString(output);
        var at = text.IndexOf("/Producer", StringComparison.Ordinal) + 12;
        var tampered = (byte[])output.Clone();
        tampered[at] ^= 0x20;
        var v = Assert.Single(Verify(tampered).Signatures);
        Assert.False(v.IsValid);
        Assert.False(v.DigestMatches);
        Assert.Contains(v.Problems, p => p.Contains("changed after signing", StringComparison.Ordinal));

        // One byte of the signature value.
        var broken = (byte[])output.Clone();
        var sigAt = (int)result.ByteRange[1] + 200;
        broken[sigAt] = broken[sigAt] == (byte)'A' ? (byte)'B' : (byte)'A';
        Assert.False(Assert.Single(Verify(broken).Signatures).IsValid);
    }

    [Fact]
    public void SecondSignature_KeepsTheFirstValid()
    {
        var once = Sign(new TestPdfBuilder().Build(), new PdfSignOptions { Certificate = TestCertificates.Rsa("First") }, out _);
        var twice = Sign(once, new PdfSignOptions { Certificate = TestCertificates.Ecdsa("Second") }, out var result);
        Assert.Equal("Signature2", result.FieldName);
        var all = Verify(twice);
        Assert.True(all.AllValid, string.Join("; ", all.Signatures.SelectMany(s => s.Problems)));
        var first = all.Signatures.Single(s => s.FieldName == "Signature1");
        var second = all.Signatures.Single(s => s.FieldName == "Signature2");
        Assert.True(first.ModifiedAfterSigning);
        Assert.False(first.CoversWholeFile);
        Assert.Contains("signatures added", first.ChangesAfterSigning);
        Assert.Equal(2, first.Revision);
        Assert.Equal(3, second.Revision);
        Assert.True(second.CoversWholeFile);
    }

    [Fact]
    public void ChangesAfterSigning_AreClassified()
    {
        var signed = Sign(new TestPdfBuilder().Build(), new PdfSignOptions { Certificate = TestCertificates.Rsa() }, out _);
        var edited = Pdf.EditInfo(signed, new PdfEditInfoOptions { Changes = new PdfInfoChanges { Title = "Changed" }, SaveMode = PdfSaveMode.Incremental }, out _);
        var v = Assert.Single(Verify(edited).Signatures);
        Assert.True(v.IsValid);
        Assert.True(v.ModifiedAfterSigning);
        Assert.Contains("document information or metadata changed", v.ChangesAfterSigning);

        // A full rewrite breaks the signature, as the warning says.
        var rewritten = Pdf.EditInfo(signed, new PdfInfoChanges { Title = "Rewritten" }, null, out var rewrite);
        Assert.Contains(rewrite.Warnings, w => w.Contains("invalidates the existing signatures", StringComparison.Ordinal));
        Assert.False(Assert.Single(Verify(rewritten).Signatures).IsValid);
    }

    [Fact]
    public void Timestamp_FromAnInjectedTsa()
    {
        var tsa = new TestCertificates.FakeTsa();
        using var http = new HttpClient(tsa);
        var output = Sign(new TestPdfBuilder().Build(), new PdfSignOptions { Certificate = TestCertificates.Rsa(), TimestampServer = new Uri("https://tsa.example/"), TimestampHttpClient = http }, out var result);
        Assert.Equal(1, tsa.Requests);
        Assert.Equal(tsa.Time, result.TimestampTime);
        var v = Assert.Single(Verify(output).Signatures);
        Assert.True(v.IsValid, string.Join("; ", v.Problems));
        Assert.True(v.TimestampIsValid);
        Assert.Equal(tsa.Time, v.TimestampTime);
    }

    [Fact]
    public void ChainPolicy_TrustAnchors()
    {
        var output = Sign(new TestPdfBuilder().Build(), new PdfSignOptions { Certificate = TestCertificates.Rsa() }, out _);
        var trusted = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        trusted.CustomTrustStore.Add(TestCertificates.Root);
        var ok = Assert.Single(Verify(output, new PdfSignatureVerificationOptions { ChainPolicy = trusted }).Signatures);
        Assert.True(ok.ChainIsValid);
        Assert.True(ok.IsValid);

        var untrusted = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        var bad = Assert.Single(Verify(output, new PdfSignatureVerificationOptions { ChainPolicy = untrusted }).Signatures);
        Assert.False(bad.ChainIsValid);
        Assert.False(bad.IsValid);
        Assert.NotEmpty(bad.ChainStatus);
    }

    [Fact]
    public void ReservedSpace_GrowsOnceWhenTooSmall()
    {
        var output = Sign(new TestPdfBuilder().Build(), new PdfSignOptions { Certificate = TestCertificates.Rsa(), ReservedSize = 1024 }, out var result);
        Assert.True(result.ReservedSize > 1024);
        Assert.True(result.SignatureSize <= result.ReservedSize);
        Assert.True(Assert.Single(Verify(output).Signatures).IsValid);
    }

    [Fact]
    public void InvalidInputs()
    {
        using var publicOnly = new X509Certificate2(TestCertificates.Ecdsa().RawData);
        Assert.Throws<ArgumentException>(() => Sign(new TestPdfBuilder().Build(), new PdfSignOptions { Certificate = publicOnly }, out _));
        Assert.Throws<ArgumentException>(() => Sign(new TestPdfBuilder().Build(), new PdfSignOptions(), out _));
        Assert.Throws<ArgumentException>(() => Sign(new TestPdfBuilder().Build(), new PdfSignOptions { Certificate = TestCertificates.Rsa(), DigestAlgorithm = HashAlgorithmName.MD5 }, out _));
        Assert.Throws<NotSupportedException>(() => Sign(DecryptTests.Encrypted(6), new PdfSignOptions { Certificate = TestCertificates.Rsa() }, out _));
        Assert.Empty(Verify(new TestPdfBuilder().Build()).Signatures);
        Assert.False(Verify(new TestPdfBuilder().Build()).AllValid);
    }

    /// <summary>Builds a document signed by hand with another format, through the same incremental writer and reserved space.</summary>
    private static byte[] HandSigned(string subFilter, Func<byte[], byte[]> sign)
    {
        var input = new TestPdfBuilder().Build();
        using var doc = PdfDocument.Open(PdfSource.FromBytes(input), new PdfUtilOptions(), PdfLog.None, strict: false, CancellationToken.None);
        var edits = new ObjectEdits(doc);
        var contents = ReservedSpace.HexString(8192);
        var range = ReservedSpace.Text("[0 0 0 0]", 64);
        var sig = edits.Add(new PdfDictionary
        {
            ["Type"] = PdfName.Get(subFilter == "ETSI.RFC3161" ? "DocTimeStamp" : "Sig"),
            ["Filter"] = PdfName.Get("Adobe.PPKLite"),
            ["SubFilter"] = PdfName.Get(subFilter),
            ["ByteRange"] = range,
            ["Contents"] = contents,
        });
        var widget = edits.Add(new PdfDictionary
        {
            ["Type"] = PdfName.Get("Annot"),
            ["Subtype"] = PdfName.Get("Widget"),
            ["FT"] = PdfName.Get("Sig"),
            ["T"] = new PdfString(Encoding.ASCII.GetBytes("Hand")),
            ["V"] = new PdfReference(sig, 0),
            ["Rect"] = new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0) }),
        });
        var catalogNumber = ((PdfReference)doc.Trailer[PdfName.Root]!).Id.Number;
        var catalog = ((PdfDictionary)doc.GetObject(catalogNumber)!).ShallowClone();
        catalog["AcroForm"] = new PdfDictionary { ["Fields"] = new PdfArray(new PdfObject[] { new PdfReference(widget, 0) }), ["SigFlags"] = new PdfInteger(3) };
        edits.Replace(catalogNumber, catalog);
        using var buffer = new MemoryStream();
        new IncrementalEngine(new PdfUtilOptions(), PdfLog.None).Append(doc, new IncrementalSettings
        {
            Progress = ProgressReporter.None,
            Edits = edits,
            Fill = output =>
            {
                var start = contents.Offset;
                var end = start + contents.Width;
                output.Fill(range, Encoding.ASCII.GetBytes($"[0 {start} {end} {output.Length - end}]"));
                var data = output.Read(0, (int)start).Concat(output.Read(end, (int)(output.Length - end))).ToArray();
                output.Fill(contents, sign(data));
            },
        }, buffer, CancellationToken.None);
        return buffer.ToArray();
    }

    [Fact]
    public void Verify_AdbePkcs7Detached_AndSha1()
    {
        var cert = TestCertificates.Rsa("Legacy");
        var detached = HandSigned("adbe.pkcs7.detached", data =>
        {
            var cms = new SignedCms(new ContentInfo(data), detached: true);
            cms.ComputeSignature(new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert, TestCertificates.KeyFor(cert)) { IncludeOption = X509IncludeOption.EndCertOnly }, silent: true);
            return cms.Encode();
        });
        var v1 = Assert.Single(Verify(detached).Signatures);
        Assert.True(v1.IsValid, string.Join("; ", v1.Problems));
        Assert.Equal("adbe.pkcs7.detached", v1.SubFilter);

        var sha1 = HandSigned("adbe.pkcs7.sha1", data =>
        {
            var digest = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
            digest.WriteOctetString(SHA1.HashData(data));
            var cms = new SignedCms(new ContentInfo(digest.Encode()), detached: false);
            cms.ComputeSignature(new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert, TestCertificates.KeyFor(cert)) { IncludeOption = X509IncludeOption.EndCertOnly }, silent: true);
            return cms.Encode();
        });
        var v2 = Assert.Single(Verify(sha1).Signatures);
        Assert.True(v2.IsValid, string.Join("; ", v2.Problems));
        Assert.Equal("adbe.pkcs7.sha1", v2.SubFilter);
    }

    [Fact]
    public void Verify_DocumentTimestamp()
    {
        var time = new DateTimeOffset(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var output = HandSigned("ETSI.RFC3161", data => TestCertificates.TimestampToken(new Oid("2.16.840.1.101.3.4.2.1"), SHA256.HashData(data), null, time));
        var v = Assert.Single(Verify(output).Signatures);
        Assert.True(v.IsDocumentTimestamp);
        Assert.True(v.IsValid, string.Join("; ", v.Problems));
        Assert.Equal(time, v.TimestampTime);
        Assert.Equal("PdfUtil Test TSA", v.SignerName);
    }

    [Fact]
    public async Task FileApi_AndDi()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        PdfUtil.Extensions.DependencyInjection.PdfUtilSignaturesServiceCollectionExtensions.AddPdfUtilSignatures(services);
        using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var signer = (IPdfSigner)sp.GetService(typeof(IPdfSigner))!;
        Assert.NotNull(sp.GetService(typeof(IPdfCertificateSecurity)));
        Assert.NotNull(sp.GetService(typeof(IPdfAnalyzer)));

        var path = Path.Combine(Path.GetTempPath(), "pdfutil-sign-" + Guid.NewGuid().ToString("N") + ".pdf");
        await File.WriteAllBytesAsync(path, new TestPdfBuilder().Build());
        try
        {
            var cert = TestCertificates.Rsa();
            await signer.SignFileAsync(path, path, new PdfSignOptions { Certificate = cert, PrivateKey = TestCertificates.KeyFor(cert) });
            await using var stream = File.OpenRead(path);
            Assert.True((await signer.VerifyAsync(stream)).AllValid);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

internal static class SignatureTestExtensions
{
    public static PdfDictionary? Resolve(this PdfDictionary dict, string key, PdfDocument doc) => doc.ResolveDictionary(dict[key]);
}
