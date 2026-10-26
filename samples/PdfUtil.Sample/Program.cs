using System.Globalization;
using System.Text;
using PdfUtil;

// PdfUtil sample: protect, verify, inspect, and generate files for the manual compatibility checklist.
//
//   dotnet run -- protect <in.pdf> <out.pdf> <userPassword> [ownerPassword] [aes256|aes128|rc4]
//   dotnet run -- verify  <file.pdf> <password>
//   dotnet run -- info    <file.pdf> [password]
//   dotnet run -- demo    <outputDirectory>

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "protect" when args.Length >= 4:
        {
            var options = new PdfProtectionOptions
            {
                UserPassword = args[3],
                OwnerPassword = args.Length > 4 ? args[4] : null,
                Algorithm = args.Length > 5 ? ParseAlgorithm(args[5]) : PdfEncryptionAlgorithm.Aes256,
                Permissions = PdfPermissions.Print | PdfPermissions.PrintHighQuality | PdfPermissions.ExtractForAccessibility,
            };
            var result = Pdf.Protect(args[1], args[2], options);
            Console.WriteLine($"Protected with {result.Algorithm} (R{result.Revision}); PDF {result.SourceVersion} -> {result.OutputVersion}.");
            foreach (var warning in result.Warnings)
            {
                Console.WriteLine("  warning: " + warning);
            }

            return 0;
        }

        case "verify" when args.Length >= 3:
        {
            var check = Pdf.VerifyPassword(args[1], args[2]);
            Console.WriteLine(check.IsEncrypted
                ? $"Encrypted ({check.Algorithm}, R{check.Revision}). Password valid: {check.IsValid}, matched as: {check.MatchedAs}."
                : "The document is not encrypted.");
            return check.IsValid ? 0 : 2;
        }

        case "info" when args.Length >= 2:
            PrintInfo(Pdf.GetInfo(args[1], args.Length > 2 ? args[2] : null));
            return 0;

        case "demo" when args.Length >= 2:
            return Demo(args[1]);

        default:
            PrintUsage();
            return 1;
    }
}
catch (PdfInvalidPasswordException)
{
    Console.Error.WriteLine("The password is wrong.");
    return 2;
}
catch (PdfUtilException ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 3;
}

static int Demo(string directory)
{
    Directory.CreateDirectory(directory);
    var source = Path.Combine(directory, "source.pdf");
    File.WriteAllBytes(source, MinimalPdf("PdfUtil compatibility sample"));
    Console.WriteLine($"Wrote {source}");

    var samples = new (string File, PdfEncryptionAlgorithm Algorithm, string? User, string Owner)[]
    {
        ("aes256.pdf", PdfEncryptionAlgorithm.Aes256, "user", "owner"),
        ("aes128.pdf", PdfEncryptionAlgorithm.Aes128, "user", "owner"),
        ("rc4-128.pdf", PdfEncryptionAlgorithm.Rc4_128, "user", "owner"),
        ("aes256-unicode.pdf", PdfEncryptionAlgorithm.Aes256, "Пароль東京", "owner"),
        ("aes256-owner-only.pdf", PdfEncryptionAlgorithm.Aes256, null, "owner"),
    };
    foreach (var (file, algorithm, user, owner) in samples)
    {
        var path = Path.Combine(directory, file);
        var result = Pdf.Protect(source, path, new PdfProtectionOptions
        {
            UserPassword = user,
            OwnerPassword = owner,
            Algorithm = algorithm,
            Permissions = PdfPermissions.Print | PdfPermissions.ExtractForAccessibility,
        });
        Console.WriteLine($"Wrote {path}: {result.Algorithm}, PDF {result.OutputVersion}, user password '{user ?? "(none)"}', owner password '{owner}'.");
    }

    return 0;
}

static void PrintInfo(PdfDocumentInfo info)
{
    Console.WriteLine($"Version:        {info.HeaderVersion} (effective {info.EffectiveVersion})");
    foreach (var ext in info.Extensions)
    {
        Console.WriteLine($"Extension:      {ext.Prefix} base {ext.BaseVersion} level {ext.ExtensionLevel}");
    }

    Console.WriteLine($"File size:      {info.FileSize.ToString("N0", CultureInfo.InvariantCulture)} bytes");
    Console.WriteLine($"Linearized:     {info.IsLinearized}   Tagged: {info.IsTagged}   Updates: {info.IncrementalUpdateCount}   Signed: {info.IsSigned}");
    Console.WriteLine($"ID:             {info.PermanentIdHex ?? "(none)"} / {info.ChangingIdHex ?? "(none)"}");
    if (info.Encryption is { } e)
    {
        Console.WriteLine($"Encryption:     {e.Algorithm} (V{e.V} R{e.R}, {e.KeyLengthBits}-bit), permissions {e.Permissions}, opened as {e.AuthenticatedAs}");
    }

    if (info.RequiresPassword)
    {
        Console.WriteLine("A password is required to read pages and metadata.");
        return;
    }

    Console.WriteLine($"Pages:          {info.PageCount}");
    foreach (var page in info.Pages.Take(5))
    {
        Console.WriteLine($"  page {page.Number}: {page.DisplayWidth:0.#} x {page.DisplayHeight:0.#} pt, rotate {page.Rotate}");
    }

    if (info.Info is { } d)
    {
        foreach (var (key, value) in d.Entries)
        {
            Console.WriteLine($"Info /{key}: {value}");
        }
    }

    if (info.Xmp is { } x)
    {
        Console.WriteLine($"XMP:            title '{x.Title}', producer '{x.Producer}', PDF/A {x.PdfALevel ?? "no"}");
    }

    foreach (var w in info.Warnings)
    {
        Console.WriteLine("warning: " + w);
    }
}

static PdfEncryptionAlgorithm ParseAlgorithm(string value) => value.ToLowerInvariant() switch
{
    "aes256" => PdfEncryptionAlgorithm.Aes256,
    "aes128" => PdfEncryptionAlgorithm.Aes128,
    "rc4" => PdfEncryptionAlgorithm.Rc4_128,
    _ => throw new ArgumentException($"Unknown algorithm '{value}'. Use aes256, aes128 or rc4."),
};

// A one-page PDF 1.4 with text, written by hand with exact cross-reference offsets.
static byte[] MinimalPdf(string title)
{
    var content = "BT /F1 24 Tf 72 720 Td (Hello page 1 - PdfUtil sample) Tj ET";
    var objects = new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        $"<< /Title ({title}) /Producer (PdfUtil.Sample) >>",
    };
    var sb = new StringBuilder("%PDF-1.4\n");
    var offsets = new List<int>();
    for (var i = 0; i < objects.Length; i++)
    {
        offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
        sb.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
    }

    var xref = Encoding.ASCII.GetByteCount(sb.ToString());
    sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f\r\n");
    foreach (var offset in offsets)
    {
        sb.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n\r\n");
    }

    sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R /Info 6 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    return Encoding.ASCII.GetBytes(sb.ToString());
}

static void PrintUsage()
{
    Console.WriteLine("""
        PdfUtil sample
          protect <in.pdf> <out.pdf> <userPassword> [ownerPassword] [aes256|aes128|rc4]
          verify  <file.pdf> <password>
          info    <file.pdf> [password]
          demo    <outputDirectory>      (writes sample files for docs/manual-compat-checklist.md)
        """);
}
