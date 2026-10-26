using Microsoft.Extensions.DependencyInjection;
using PdfUtil.Extensions.DependencyInjection;
using PdfUtil.Signatures;
using PdfUtil.Tests.Infrastructure;

namespace PdfUtil.Tests;

/// <summary>PdfPipeline: chained operations.</summary>
public class PipelineTests
{
    private static byte[] Doc(int pages = 3) => new TestPdfBuilder { PageCount = pages }.Build();

    private static bool HasText(byte[] pdf, string text, string? password = null) =>
        Pdf.ExtractText(pdf, new PdfTextExtractionOptions { Password = password }).Pages.Any(p => p.Runs.Any(r => r.Text == text));

    [Fact]
    public async Task Watermark_Rotate_EditInfo_Protect()
    {
        var protection = new PdfProtectionOptions { UserPassword = "open-me", OwnerPassword = "owner" };
        var result = await PdfPipeline.FromBytes(Doc())
            .Watermark("CONFIDENTIAL")
            .RotatePages(90, "1-2")
            .EditInfo(i => i.Title = "Q3 report")
            .Protect(protection)
            .RunAsync();

        Assert.Equal(new[] { "Stamp", "RotatePages", "EditInfo", "Protect" }, result.Steps.Select(s => s.Name));
        Assert.Equal(new[] { 1, 2, 3 }, result.Get<PdfStampResult>("Stamp")!.StampedPages);
        Assert.NotNull(result.Get<PdfProtectionResult>("Protect"));

        var pdf = result.Pdf;
        Assert.True(Pdf.IsEncrypted(pdf));
        var info = Pdf.GetInfo(pdf, "open-me");
        Assert.Equal("Q3 report", info.Info!.Title);
        var before = Pdf.GetInfo(Doc()).Pages.Select(p => p.Rotate).ToArray();
        Assert.Equal(new[] { (before[0] + 90) % 360, (before[1] + 90) % 360, before[2] }, info.Pages.Select(p => p.Rotate));
        Assert.True(HasText(pdf, "CONFIDENTIAL", "open-me"));
        Assert.True(Pdf.Validate(pdf, new PdfValidationOptions { Password = "owner" }).IsValid);
        Assert.Null(protection.CurrentOwnerPassword);
    }

    [Fact]
    public async Task OwnerPassword_CarriesForward_WithoutChangingOptions()
    {
        var stamp = new PdfStampOptions { Text = "AFTER" };
        var rotate = new PdfRotatePagesOptions { Angle = 180 };
        var pdf = await PdfPipeline.FromBytes(Doc(2))
            .Protect("user", "owner")
            .Stamp(stamp)
            .RotatePages(rotate)
            .AddAttachment("data.txt", "hello"u8.ToArray())
            .ToBytesAsync();

        Assert.Null(stamp.Password);
        Assert.Null(rotate.Password);
        Assert.True(Pdf.IsEncrypted(pdf));
        Assert.True(HasText(pdf, "AFTER", "user"));
        var before = Pdf.GetInfo(Doc(2)).Pages.Select(p => p.Rotate);
        Assert.Equal(before.Select(r => (r + 180) % 360), Pdf.GetInfo(pdf, "user").Pages.Select(p => p.Rotate));
        Assert.Contains(Pdf.ListAttachments(pdf, "owner"), a => a.Name == "data.txt");
    }

    [Fact]
    public async Task WithPassword_OpensProtectedInput_AndDecryptRemovesIt()
    {
        var input = Pdf.Protect(Doc(1), new PdfProtectionOptions { UserPassword = "u", OwnerPassword = "o" });
        var pdf = await PdfPipeline.FromBytes(input)
            .WithPassword("o")
            .Watermark("DRAFT")
            .Decrypt()
            .EditInfo(i => i.Author = "Pipeline")
            .ToBytesAsync();

        Assert.False(Pdf.IsEncrypted(pdf));
        Assert.True(HasText(pdf, "DRAFT"));
        Assert.Equal("Pipeline", Pdf.GetInfo(pdf).Info!.Author);
    }

    [Fact]
    public async Task ChangeSecurity_NewOwnerPassword_IsUsedNext()
    {
        var pdf = await PdfPipeline.FromBytes(Doc(1))
            .Protect("u", "first")
            .ChangeSecurity(new PdfSecurityChanges { UserPassword = "u", OwnerPassword = "second" })
            .Watermark("X")
            .ToBytesAsync();

        Assert.True(HasText(pdf, "X", "second"));
        Assert.True(Pdf.Validate(pdf, new PdfValidationOptions { Password = "second" }).IsValid);
    }

    [Fact]
    public async Task Append_DeletePages_Repair_Sanitize()
    {
        var dir = Directory.CreateTempSubdirectory("pipeline");
        try
        {
            var other = Path.Combine(dir.FullName, "other.pdf");
            await File.WriteAllBytesAsync(other, Doc(2));
            var result = await PdfPipeline.FromBytes(Doc(3))
                .Append(other)
                .DeletePages("1")
                .Repair()
                .Sanitize()
                .RunAsync();

            Assert.Equal(4, Pdf.GetInfo(result.Pdf).PageCount);
            Assert.Equal(5, result.Get<PdfMergeResult>("Append")!.PageCount);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FailingStep_KeepsExceptionType_NamesStep_AndWritesNothing()
    {
        var dir = Directory.CreateTempSubdirectory("pipeline");
        try
        {
            var path = Path.Combine(dir.FullName, "out.pdf");
            var pipeline = PdfPipeline.FromBytes(Doc(2)).Watermark("A").DeletePages("1-2");
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => pipeline.SaveAsync(path));
            Assert.Equal("2: DeletePages", ex.Data["PdfPipelineStep"]);
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFiles(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WrongPassword_FailsInFirstStep()
    {
        var input = Pdf.Protect(Doc(1), new PdfProtectionOptions { UserPassword = "u", OwnerPassword = "o" });
        var ex = await Assert.ThrowsAsync<PdfInvalidPasswordException>(() => PdfPipeline.FromBytes(input).WithPassword("nope").Watermark("A").ToBytesAsync());
        Assert.Equal("1: Stamp", ex.Data["PdfPipelineStep"]);
    }

    [Fact]
    public async Task Save_File_Stream_AndRunsTwice()
    {
        var dir = Directory.CreateTempSubdirectory("pipeline");
        try
        {
            var source = Path.Combine(dir.FullName, "in.pdf");
            await File.WriteAllBytesAsync(source, Doc(1));
            var pipeline = PdfPipeline.FromFile(source).Watermark("S");
            Assert.Equal(new[] { "Stamp" }, pipeline.Steps);

            // Same file in and out: the input is read before anything is written.
            await pipeline.SaveAsync(source);
            Assert.True(HasText(await File.ReadAllBytesAsync(source), "S"));

            using var input = new MemoryStream(Doc(1));
            var fromStream = PdfPipeline.FromStream(input).Watermark("T");
            using var first = new MemoryStream();
            await fromStream.ToStreamAsync(first);
            var second = await fromStream.ToBytesAsync();
            Assert.Equal(first.ToArray().Length, second.Length);
            Assert.True(HasText(second, "T"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Then_CustomStep_GetsCurrentPassword()
    {
        string? seen = null;
        var result = await PdfPipeline.FromBytes(Doc(1))
            .Protect(null, "owner")
            .Then("Copy", async (input, output, password, ct) =>
            {
                seen = password;
                await input.CopyToAsync(output, ct);
                return "copied";
            })
            .RunAsync();

        Assert.Equal("owner", seen);
        Assert.Equal("copied", result.Steps[1].Result);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Cancellation_StopsBeforeRunning()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfPipeline.FromBytes(Doc(1)).Watermark("A").ToBytesAsync(cts.Token));
    }

#if !NETFRAMEWORK
    // The test certificates need .NET 8 APIs.
    [Fact]
    public async Task Sign_AsLastStep_IsValid()
    {
        var cert = TestCertificates.Rsa();
        var pdf = await PdfPipeline.FromBytes(Doc(1))
            .Watermark("SIGNED COPY")
            .EditInfo(i => i.Title = "Contract")
            .Sign(new PdfSignOptions { Certificate = cert, PrivateKey = cert.HasPrivateKey ? null : TestCertificates.KeyFor(cert), Reason = "Approved" })
            .ToBytesAsync();

        var signature = Assert.Single(SignatureTests.Verify(pdf).Signatures);
        Assert.True(signature.IsValid, string.Join("; ", signature.Problems));
        Assert.True(signature.CoversWholeFile);
        Assert.True(HasText(pdf, "SIGNED COPY"));
    }
#endif

    [Fact]
    public async Task Factory_FromDependencyInjection_UsesApplicationOptions()
    {
        var services = new ServiceCollection();
        services.AddPdfUtil(o => o.MaxFileSizeBytes = 100);
        using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IPdfPipelineFactory>();
        Assert.Same(factory, sp.GetRequiredService<IPdfPipelineFactory>());
        await Assert.ThrowsAsync<PdfLimitExceededException>(() => factory.FromBytes(Doc(1)).Watermark("A").ToBytesAsync());
    }
}
