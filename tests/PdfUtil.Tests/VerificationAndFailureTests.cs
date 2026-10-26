using System.IO.Compression;
using System.Text;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

public class VerificationAndFailureTests
{
    private static readonly byte[] Plain = new TestPdfBuilder().Build();

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256)]
    [InlineData(PdfEncryptionAlgorithm.Aes128)]
    [InlineData(PdfEncryptionAlgorithm.Rc4_128)]
    public void PasswordChecks_ReportUserOwnerWrongNullAndEmpty(PdfEncryptionAlgorithm algorithm)
    {
        var (output, _) = TestHelpers.Protect(Plain, TestHelpers.Options(algorithm));
        var revision = algorithm switch { PdfEncryptionAlgorithm.Aes256 => 6, PdfEncryptionAlgorithm.Aes128 => 4, _ => 3 };

        var user = Pdf.VerifyPassword(output, "user");
        Assert.Equal((true, true, PdfPasswordMatch.User, algorithm, revision, false), (user.IsEncrypted, user.IsValid, user.MatchedAs, user.Algorithm, user.Revision, user.OpensWithoutPassword));
        Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(output, "owner").MatchedAs);
        foreach (var bad in new[] { "wrong", null, "", "User", "user ", "owne" })
        {
            var r = Pdf.VerifyPassword(output, bad);
            Assert.True(r.IsEncrypted);
            Assert.False(r.IsValid);
            Assert.Equal(PdfPasswordMatch.None, r.MatchedAs);
        }

        Assert.True(Pdf.IsEncrypted(output));
        Assert.False(Pdf.IsEncrypted(Plain));
    }

    [Fact]
    public void UnencryptedDocument_IsValidForAnyPassword()
    {
        var r = Pdf.VerifyPassword(Plain, "anything");
        Assert.False(r.IsEncrypted);
        Assert.True(r.IsValid);
        Assert.Equal(PdfPasswordMatch.None, r.MatchedAs);
        Assert.Equal(PdfEncryptionAlgorithm.None, r.Algorithm);
    }

    [Fact]
    public void Inspector_WrongPasswordThrows_NullPasswordGivesPartialResult()
    {
        var (output, _) = TestHelpers.Protect(Plain, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256));
        Assert.Throws<PdfInvalidPasswordException>(() => Pdf.GetInfo(output, "wrong"));
        var partial = Pdf.GetInfo(output);
        Assert.True(partial.RequiresPassword);
        Assert.Null(partial.PageCount);
        Assert.Null(partial.Info);
        Assert.NotNull(partial.PermanentId);
        Assert.Equal(6, partial.Encryption!.R);
        Assert.Equal(5, partial.Encryption.V);
        Assert.Equal(256, partial.Encryption.KeyLengthBits);
        Assert.Equal(PdfPermissions.Print | PdfPermissions.CopyContent, partial.Encryption.Permissions);
        Assert.Equal(PdfPasswordMatch.None, partial.Encryption.AuthenticatedAs);
        var full = Pdf.GetInfo(output, "owner");
        Assert.Equal(PdfPasswordMatch.Owner, full.Encryption!.AuthenticatedAs);
        Assert.Equal("StdCF", full.Encryption.StreamFilter);
    }

    [Fact]
    public void AlreadyEncrypted_DefaultPolicy_Throws()
    {
        var (output, _) = TestHelpers.Protect(Plain, TestHelpers.Options(PdfEncryptionAlgorithm.Aes128));
        Assert.Throws<PdfAlreadyEncryptedException>(() => Pdf.Protect(output, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256, "new", "newowner")));
    }

    [Fact]
    public void AlreadyEncrypted_ReEncrypt_RequiresOwnerPassword()
    {
        var (output, _) = TestHelpers.Protect(Plain, TestHelpers.Options(PdfEncryptionAlgorithm.Aes128));
        var options = TestHelpers.Options(PdfEncryptionAlgorithm.Aes256, "new", "newowner");
        options.AlreadyEncryptedPolicy = AlreadyEncryptedPolicy.ReEncryptWithOwnerPassword;

        options.CurrentOwnerPassword = "user"; // the user password is not enough
        Assert.Throws<PdfInvalidPasswordException>(() => Pdf.Protect(output, options));
        options.CurrentOwnerPassword = "wrong";
        Assert.Throws<PdfInvalidPasswordException>(() => Pdf.Protect(output, options));
        options.CurrentOwnerPassword = null;
        Assert.Throws<ArgumentException>(() => Pdf.Protect(output, options));

        options.CurrentOwnerPassword = "owner";
        var (reencrypted, result) = TestHelpers.Protect(output, options);
        Assert.Equal(PdfEncryptionAlgorithm.Aes256, result.Algorithm);
        Assert.Equal(PdfPasswordMatch.User, Pdf.VerifyPassword(reencrypted, "new").MatchedAs);
        Assert.False(Pdf.VerifyPassword(reencrypted, "user").IsValid);
        ContentPreservationTests.AssertStreamsPreserved(output, "owner", reencrypted, "newowner");
    }

    [Fact]
    public void PublicKeySecurity_IsReported_NotACrash()
    {
        // Phase 6: certificate encryption is reported (details, RequiresCertificate) instead of throwing.
        var input = new TestPdfBuilder { PubSecEncryption = true }.Build();
        Assert.True(Pdf.IsEncrypted(input));
        var check = Pdf.VerifyPassword(input, "x");
        Assert.True(check.RequiresCertificate);
        Assert.False(check.IsValid);
        var info = Pdf.GetInfo(input);
        Assert.True(info.RequiresCertificate);
        Assert.Equal("Adobe.PubSec", info.Encryption!.Filter);
        Assert.Equal(1, info.Encryption.RecipientCount);
        var options = TestHelpers.Options(PdfEncryptionAlgorithm.Aes256);
        options.AlreadyEncryptedPolicy = AlreadyEncryptedPolicy.ReEncryptWithOwnerPassword;
        options.CurrentOwnerPassword = "x";
        Assert.Throws<PdfUnsupportedEncryptionException>(() => Pdf.Protect(input, options));
    }

    [Fact]
    public void ZeroByteInput_IsCorrupt()
    {
        Assert.Throws<PdfCorruptException>(() => Pdf.GetInfo(Array.Empty<byte>()));
        Assert.Throws<PdfCorruptException>(() => Pdf.Protect(Array.Empty<byte>(), TestHelpers.Options(PdfEncryptionAlgorithm.Aes256)));
    }

    [Theory]
    [InlineData("This is not a PDF at all.")]
    [InlineData("PK\u0003\u0004 zip file")]
    [InlineData("%PDF-1.4\n")]
    [InlineData("%PDF-1.7\nxref\ngarbage\ntrailer\n<<>>\nstartxref\n9\n%%EOF")]
    public void NonPdfOrCorruptInput_ThrowsCorrupt(string content)
    {
        var bytes = Encoding.Latin1.GetBytes(content);
        Assert.ThrowsAny<PdfCorruptException>(() => Pdf.GetInfo(bytes));
        Assert.ThrowsAny<PdfUtilException>(() => Pdf.Protect(bytes, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256)));
    }

    [Fact]
    public void CatalogWithoutPagesOrXref_IsRecoveredWithZeroPages()
    {
        var info = Pdf.GetInfo(Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\n"));
        Assert.True(info.WasRepaired);
        Assert.Equal(0, info.PageCount);
    }

    [Fact]
    public void RandomGarbage_NeverCrashesWithUnexpectedExceptions()
    {
        var rng = new Random(1234);
        var valid = new TestPdfBuilder().Build();
        for (var i = 0; i < 200; i++)
        {
            var mutated = (byte[])valid.Clone();
            for (var k = 0; k < 20; k++)
            {
                mutated[rng.Next(mutated.Length)] = (byte)rng.Next(256);
            }

            try
            {
                Pdf.GetInfo(mutated);
                Pdf.VerifyPassword(mutated, "x");
            }
            catch (PdfUtilException)
            {
                // Expected for damaged input.
            }
        }
    }

    [Fact]
    public async Task ZipBomb_IsStoppedByDecompressedSizeLimit()
    {
        // An object stream that inflates to 64 MiB of zeros.
        var bomb = Deflate(new byte[64 * 1024 * 1024]);
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.5\n");
        var off1 = ms.Position;
        W($"1 0 obj\n<< /Type /ObjStm /N 1 /First 4 /Filter /FlateDecode /Length {bomb.Length} >>\nstream\n");
        ms.Write(bomb);
        W("\nendstream\nendobj\n");
        var xref = ms.Position;
        var rows = new byte[] { 0, 0, 0, 0, 0, 255, 255, 1, 0, 0, 0, (byte)off1, 0, 0, 2, 0, 0, 0, 1, 0, 0 };
        var data = Deflate(rows);
        W($"2 0 obj\n<< /Type /XRef /Size 3 /W [1 4 2] /Root 3 0 R /Filter /FlateDecode /Length {data.Length} >>\nstream\n");
        ms.Write(data);
        W($"\nendstream\nendobj\nstartxref\n{xref}\n%%EOF\n");
        var pdf = ms.ToArray();

        var inspector = new PdfInspector(new PdfUtilOptions { MaxDecompressedStreamBytes = 1024 * 1024 });
        await Assert.ThrowsAnyAsync<PdfCorruptException>(() => inspector.GetInfoAsync(new MemoryStream(pdf)));
    }

    [Fact]
    public async Task Limits_MaxFileSize_AndNesting()
    {
        var small = new PdfInspector(new PdfUtilOptions { MaxFileSizeBytes = 100 });
        await Assert.ThrowsAsync<PdfLimitExceededException>(() => small.GetInfoAsync(new MemoryStream(Plain)));

        var deep = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R /X " + new string('[', 5000) + new string(']', 5000) + " >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");
        Assert.ThrowsAny<PdfCorruptException>(() => Pdf.GetInfo(deep));
    }

    [Fact]
    public void PrevLoop_IsDetected()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.4\n");
        var o1 = ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        var x = ms.Position;
        W($"xref\n0 3\n0000000000 65535 f\r\n{o1:D10} 00000 n\r\n{o2:D10} 00000 n\r\ntrailer\n<< /Size 3 /Root 1 0 R /Prev {x} >>\nstartxref\n{x}\n%%EOF\n");
        var info = Pdf.GetInfo(ms.ToArray());
        Assert.Equal(0, info.PageCount);
        Assert.Contains(info.Warnings, w => w.Contains("loop", StringComparison.Ordinal));
    }

    [Fact]
    public void PageTreeCycle_IsDetected()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [2 0 R 3 0 R] /Count 1 >>\nendobj\n3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");
        var info = Pdf.GetInfo(pdf);
        Assert.Equal(1, info.PageCount);
        Assert.Contains(info.Warnings, w => w.Contains("cycle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_IsHonoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var protector = new PdfProtector();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => protector.ProtectAsync(new MemoryStream(Plain), new MemoryStream(), TestHelpers.Options(PdfEncryptionAlgorithm.Aes256), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PdfInspector().GetInfoAsync(new MemoryStream(Plain), null, cts.Token));
    }

    [Fact]
    public async Task NonSeekableInput_IsBuffered()
    {
        var protector = new PdfProtector();
        var output = new MemoryStream();
        await protector.ProtectAsync(new NonSeekableStream(Plain), output, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256));
        Assert.True(Pdf.VerifyPassword(output.ToArray(), "user").IsValid);

        // Force the temporary-file path.
        var tiny = new PdfProtector(new PdfUtilOptions { InMemoryBufferThresholdBytes = 16 });
        var output2 = new MemoryStream();
        await tiny.ProtectAsync(new NonSeekableStream(Plain), output2, TestHelpers.Options(PdfEncryptionAlgorithm.Aes128));
        Assert.True(Pdf.VerifyPassword(output2.ToArray(), "owner").IsValid);
        var check = await new PdfOpener().VerifyPasswordAsync(new NonSeekableStream(output2.ToArray()), "user");
        Assert.True(check.IsValid);
    }

    [Fact]
    public async Task ProtectFile_IsAtomic_AndLeavesNothingBehindOnFailure()
    {
        var dir = Directory.CreateTempSubdirectory("pdfutil-test-").FullName;
        try
        {
            var input = Path.Combine(dir, "in.pdf");
            var output = Path.Combine(dir, "out.pdf");
            await File.WriteAllBytesAsync(input, Plain);
            await File.WriteAllBytesAsync(output, Encoding.ASCII.GetBytes("previous content"));

            // Failure (PDF/A with Throw policy): the existing output is untouched, no temp files remain.
            var pdfa = Path.Combine(dir, "pdfa.pdf");
            await File.WriteAllBytesAsync(pdfa, new TestPdfBuilder { Xmp = TestPdfBuilder.PdfAXmp(1, "B") }.Build());
            await Assert.ThrowsAsync<PdfComplianceException>(() => new PdfProtector().ProtectFileAsync(pdfa, output, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256)));
            Assert.Equal("previous content", await File.ReadAllTextAsync(output));
            Assert.Equal(3, Directory.GetFiles(dir).Length);

            // Success replaces the output.
            var result = await new PdfProtector().ProtectFileAsync(input, output, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256));
            Assert.Equal("1.7", result.OutputVersion);
            Assert.True(Pdf.VerifyPassword(output, "user").IsValid);
            Assert.Equal(3, Directory.GetFiles(dir).Length);

            // In-place protection (input == output).
            var inPlace = Path.Combine(dir, "inplace.pdf");
            await File.WriteAllBytesAsync(inPlace, Plain);
            Pdf.Protect(inPlace, inPlace, TestHelpers.Options(PdfEncryptionAlgorithm.Rc4_128));
            Assert.True(Pdf.IsEncrypted(inPlace));
            Assert.Equal(PdfPasswordMatch.Owner, Pdf.VerifyPassword(inPlace, "owner").MatchedAs);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task StreamOutput_IsUntouchedOnFailure()
    {
        var output = new MemoryStream();
        var pdfa = new TestPdfBuilder { Xmp = TestPdfBuilder.PdfAXmp(1, "B") }.Build();
        await Assert.ThrowsAsync<PdfComplianceException>(() => new PdfProtector().ProtectAsync(new MemoryStream(pdfa), output, TestHelpers.Options(PdfEncryptionAlgorithm.Aes256)));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task Secrets_NeverAppearInMessagesOrToString()
    {
        const string secret = "S3cr3t-密码";
        var options = TestHelpers.Options(PdfEncryptionAlgorithm.Aes128, secret, secret + "-owner");
        Assert.DoesNotContain(secret, options.ToString(), StringComparison.Ordinal);
        var logs = new List<string>();
        var protector = new PdfProtector(new PdfUtilOptions { Logger = (_, m) => { lock (logs) { logs.Add(m); } } });
        var ex = await Assert.ThrowsAsync<PdfPasswordEncodingException>(() => protector.ProtectAsync(new MemoryStream(Plain), new MemoryStream(), options));
        Assert.DoesNotContain(secret, ex.Message, StringComparison.Ordinal);

        options.Algorithm = PdfEncryptionAlgorithm.Aes256;
        var output = new MemoryStream();
        var result = await protector.ProtectAsync(new MemoryStream(Plain), output, options);
        Assert.DoesNotContain(secret, result.ToString(), StringComparison.Ordinal);
        Assert.All(logs, m => Assert.DoesNotContain(secret, m, StringComparison.Ordinal));
        Assert.NotEmpty(logs);
        Assert.DoesNotContain(Encoding.UTF8.GetBytes(secret).Select(b => b.ToString("X2")).Aggregate(string.Concat), Convert.ToHexString(output.ToArray()));
    }

    [Fact]
    public void InvalidOptions_AreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => Pdf.Protect(Plain, null!));
        Assert.Throws<ArgumentException>(() => Pdf.Protect(Plain, new PdfProtectionOptions { UserPassword = "a", Algorithm = PdfEncryptionAlgorithm.Aes256R5 }));
        Assert.Throws<ArgumentException>(() => Pdf.Protect(Plain, new PdfProtectionOptions { UserPassword = "a", Algorithm = PdfEncryptionAlgorithm.Rc4_40 }));
        Assert.Throws<ArgumentException>(() => Pdf.Protect(Plain, new PdfProtectionOptions { UserPassword = "a", Permissions = (PdfPermissions)1 }));
    }

    [Theory]
    [InlineData(PdfPermissions.None)]
    [InlineData(PdfPermissions.Print)]
    [InlineData(PdfPermissions.All)]
    [InlineData(PdfPermissions.FillForms | PdfPermissions.ExtractForAccessibility | PdfPermissions.PrintHighQuality)]
    public void Permissions_RoundTrip(PdfPermissions permissions)
    {
        foreach (var algorithm in TestHelpers.WritableAlgorithms)
        {
            var options = TestHelpers.Options(algorithm);
            options.Permissions = permissions;
            var (output, _) = TestHelpers.Protect(Plain, options);
            var info = Pdf.GetInfo(output, "user");
            Assert.Equal(permissions, info.Encryption!.Permissions);
            Assert.Equal(unchecked((int)(0xFFFFF0C0u | (uint)permissions)), info.Encryption.RawPermissions);
        }
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }

    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableStream(byte[] data) => _inner = new MemoryStream(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 1000));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
