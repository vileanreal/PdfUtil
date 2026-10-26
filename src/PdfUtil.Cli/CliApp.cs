using System.Globalization;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdfUtil.Conversion;
using PdfUtil.Signatures;

namespace PdfUtil.Cli;

/// <summary>The context a command runs in.</summary>
internal sealed class CommandContext
{
    public required Arguments Args { get; init; }

    public required CliConsole Console { get; init; }

    public required Passwords Passwords { get; init; }

    public required CancellationToken Cancellation { get; init; }
}

/// <summary>A subcommand: its name, help, options, and what it does.</summary>
internal sealed record Command(string Name, string Arguments, string Summary, OptionSpec[] Options, Func<CommandContext, Task<object>> Run, bool PerFile = false, bool ReadOnly = false)
{
    public string[] Aliases { get; init; } = Array.Empty<string>();

    /// <summary>For <c>batch</c>: the output extension when it differs from the input's.</summary>
    public Func<Arguments, string?>? OutputExtension { get; init; }
}

/// <summary>
/// The <c>pdfutil</c> command-line tool. Exit codes: 0 success, 1 usage error, 2 wrong password, 3 invalid or corrupt
/// input, 4 compliance or policy refusal, 5 verification failure, 6 conversion produced an empty or unusable result.
/// </summary>
internal static class CliApp
{
    public const int Success = 0;
    public const int Usage = 1;
    public const int WrongPassword = 2;
    public const int InvalidInput = 3;
    public const int PolicyRefusal = 4;
    public const int VerificationFailed = 5;
    public const int ConversionFailed = 6;

    private static readonly OptionSpec[] Common =
    {
        new("json", null, "Write the result as JSON."),
        new("quiet", null, "Write nothing on success."),
        new("help", null, "Show help for this command."),
    };

    private static readonly OptionSpec ObjectStreams = new("object-streams", null, "Write compressed object streams (smaller files).");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(), new CertificateConverter(), new ExceptionConverter() },
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static readonly Lazy<IReadOnlyList<Command>> CommandList = new(() => Commands_());

    /// <summary>The commands (built lazily, after the shared option tables).</summary>
    public static IReadOnlyList<Command> Commands => CommandList.Value;

    public static async Task<int> RunAsync(string[] args, CliConsole console, CancellationToken ct = default)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            if (args.Length > 1 && Find(args[1]) is { } helpFor)
            {
                console.Out.Write(Help(helpFor));
                return Success;
            }

            console.Out.Write(Overview());
            return args.Length == 0 ? Usage : Success;
        }

        if (args[0] is "--version" or "version")
        {
            console.Out.WriteLine("pdfutil " + (typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0"));
            return Success;
        }

        var command = Find(args[0]);
        if (command is null)
        {
            console.Error.WriteLine($"pdfutil: unknown command '{args[0]}'. Run 'pdfutil --help' for the list.");
            return Usage;
        }

        var json = args.Contains("--json");
        try
        {
            if (args.Skip(1).Contains("--help"))
            {
                console.Out.Write(Help(command));
                return Success;
            }

            object result;
            Arguments parsed;
            if (command.Name == "batch")
            {
                (result, parsed) = await BatchAsync(args.Skip(1).ToList(), console, ct).ConfigureAwait(false);
            }
            else
            {
                parsed = Arguments.Parse(args.Skip(1).ToList(), command.Options.Concat(Common).ToList());
                var context = new CommandContext { Args = parsed, Console = console, Passwords = new Passwords(parsed, console), Cancellation = ct };
                result = await command.Run(context).ConfigureAwait(false);
            }
            if (json)
            {
                console.Out.WriteLine(JsonSerializer.Serialize(new { command = command.Name, ok = true, result }, Json));
            }
            else if (!parsed.Has("quiet"))
            {
                Human(console.Out, result);
            }

            return result is BatchSummary batch ? batch.ExitCode : Success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var code = ExitCode(ex);
            var result = (ex as CliCheckFailedException)?.Result;
            if (json)
            {
                console.Out.WriteLine(JsonSerializer.Serialize(new { command = command.Name, ok = false, exitCode = code, error = ex.GetType().Name, message = ex.Message, result }, Json));
            }
            else
            {
                if (result is not null)
                {
                    Human(console.Out, result);
                }

                console.Error.WriteLine($"pdfutil {command.Name}: {ex.Message}");
                if (code == Usage && ex is CliUsageException)
                {
                    console.Error.WriteLine($"Run 'pdfutil {command.Name} --help' for usage.");
                }
            }

            return code;
        }
    }

    public static int ExitCode(Exception ex) => ex switch
    {
        CliUsageException or ArgumentException => Usage,
        CliCheckFailedException check => check.ExitCode,
        CliConversionException => ConversionFailed,
        PdfInvalidPasswordException or PdfPasswordEncodingException => WrongPassword,
        PdfComplianceException or PdfAlreadyEncryptedException or PdfNotEncryptedException => PolicyRefusal,
        PdfVerificationException => VerificationFailed,
        PdfUtilException or FileNotFoundException or DirectoryNotFoundException or InvalidDataException or IOException or UnauthorizedAccessException => InvalidInput,
        _ => Usage,
    };

    private static Command? Find(string name) => Commands.FirstOrDefault(c => c.Name == name || c.Aliases.Contains(name));

    // ------------------------------------------------------------------ help and output

    private static string Overview()
    {
        var sb = new System.Text.StringBuilder("pdfutil: protect, inspect, repair, edit, merge, split, sign and convert PDF files.\n\nUsage: pdfutil <command> [arguments] [options]\n\nCommands:\n");
        foreach (var c in Commands)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  {(c.Name + (c.Aliases.Length > 0 ? ", " + string.Join(", ", c.Aliases) : "")),-22}{c.Summary}\n");
        }

        sb.Append("\nRun 'pdfutil <command> --help' for a command's options. Every command takes --json and --quiet.\n");
        sb.Append("Passwords are never given as plain arguments: use --password-stdin, --<name>-env VARIABLE, or the prompt.\n");
        sb.Append("Exit codes: 0 success, 1 usage, 2 wrong password, 3 invalid or corrupt input, 4 policy refusal, 5 verification failure, 6 unusable conversion.\n");
        return sb.ToString();
    }

    private static string Help(Command c)
    {
        var sb = new System.Text.StringBuilder($"pdfutil {c.Name} {c.Arguments}\n\n{c.Summary}\n\nOptions:\n");
        foreach (var o in c.Options.Concat(Common))
        {
            sb.Append(CultureInfo.InvariantCulture, $"  --{o.Name + (o.Value is null ? "" : " " + o.Value),-34}{o.Help}\n");
        }

        return sb.ToString();
    }

    private static void Human(TextWriter output, object result)
    {
        switch (result)
        {
            case string text:
                output.WriteLine(text);
                break;
            case HumanResult h:
                foreach (var line in h.Lines)
                {
                    output.WriteLine(line);
                }

                break;
            case BatchSummary batch:
                foreach (var item in batch.Items)
                {
                    output.WriteLine($"{(item.Ok ? "ok    " : "FAILED")} {item.Input}{(item.Output is null ? "" : " -> " + item.Output)}{(item.Message is null ? "" : ": " + item.Message)}");
                }

                output.WriteLine($"{batch.Items.Count(i => i.Ok)} of {batch.Items.Count} succeeded.");
                break;
            default:
                output.WriteLine(result.ToString());
                if (result.GetType().GetProperty("Warnings")?.GetValue(result) is IEnumerable<string> warnings)
                {
                    foreach (var warning in warnings)
                    {
                        output.WriteLine("warning: " + warning);
                    }
                }

                break;
        }
    }

    /// <summary>A result with its own human-readable form; JSON still shows <see cref="Value"/>.</summary>
    internal sealed class HumanResult
    {
        public HumanResult(object value, IEnumerable<string> lines)
        {
            Value = value;
            Lines = lines.ToList();
        }

        [JsonIgnore]
        public List<string> Lines { get; }

        public object Value { get; }
    }

    internal sealed record BatchItem(string Input, string? Output, bool Ok, int ExitCode, string? Message, object? Result);

    internal sealed record BatchSummary(IReadOnlyList<BatchItem> Items)
    {
        public int ExitCode => Items.Select(i => i.ExitCode).DefaultIfEmpty(0).Max();
    }

    private sealed class CertificateConverter : JsonConverter<X509Certificate2>
    {
        public override X509Certificate2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, X509Certificate2 value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("subject", value.Subject);
            writer.WriteString("issuer", value.Issuer);
            writer.WriteString("serialNumber", value.SerialNumber);
            writer.WriteString("thumbprint", value.Thumbprint);
            writer.WriteString("notBefore", value.NotBefore.ToUniversalTime());
            writer.WriteString("notAfter", value.NotAfter.ToUniversalTime());
            writer.WriteEndObject();
        }
    }

    private sealed class ExceptionConverter : JsonConverter<Exception>
    {
        public override bool CanConvert(Type typeToConvert) => typeof(Exception).IsAssignableFrom(typeToConvert);

        public override Exception Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, Exception value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("type", value.GetType().Name);
            writer.WriteString("message", value.Message);
            writer.WriteEndObject();
        }
    }

    // ------------------------------------------------------------------ helpers

    private static PdfWriteOptions? Write(Arguments a) => a.Has("object-streams") ? new PdfWriteOptions { UseObjectStreams = true } : null;

    private static PdfPageRange? Pages(Arguments a, string name = "pages")
    {
        var text = a.Get(name);
        if (text is null)
        {
            return null;
        }

        return PdfPageRange.TryParse(text, out var range, out var error) ? range! : throw new CliUsageException($"--{name}: {error} (examples: 1-3,5  odd  even  last  2-).");
    }

    /// <summary>The password that opens an input: given explicitly, or asked for when the file is encrypted.</summary>
    private static string? OpenPassword(CommandContext c, string path, string role = "password")
    {
        if (c.Args.Has(role + "-env") || c.Args.Has("password-stdin"))
        {
            return c.Passwords.Get(role, "", ask: false);
        }

        bool encrypted;
        try
        {
            encrypted = File.Exists(path) && Pdf.IsEncrypted(path);
        }
        catch (PdfUtilException)
        {
            encrypted = false;
        }

        return encrypted ? c.Passwords.Get(role, $"Password for {Path.GetFileName(path)}: ", ask: true) : null;
    }

    private static PdfInfoChanges InfoChanges(Arguments a)
    {
        var changes = new PdfInfoChanges
        {
            Title = a.Get("title"),
            Author = a.Get("author"),
            Subject = a.Get("subject"),
            Keywords = a.Get("keywords"),
            Creator = a.Get("creator"),
            Producer = a.Get("producer"),
        };
        foreach (var pair in a.All("set"))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                throw new CliUsageException($"--set needs KEY=VALUE, not '{pair}'.");
            }

            changes.CustomProperties[pair[..eq]] = pair[(eq + 1)..];
        }

        foreach (var key in a.All("remove"))
        {
            changes.Remove.Add(key);
        }

        return changes;
    }

    private static readonly OptionSpec[] InfoOptions =
    {
        new("title", "TEXT", "Set the title."),
        new("author", "TEXT", "Set the author."),
        new("subject", "TEXT", "Set the subject."),
        new("keywords", "TEXT", "Set the keywords."),
        new("creator", "TEXT", "Set the creator."),
        new("producer", "TEXT", "Set the producer."),
        new("set", "KEY=VALUE", "Set a custom property.", Repeatable: true),
        new("remove", "KEY", "Remove an entry.", Repeatable: true),
    };

    private static X509Certificate2 LoadCertificate(CommandContext c, string path, bool needsKey)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The certificate file does not exist.", path);
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".pfx" or ".p12")
        {
            var password = c.Passwords.Get("certificate-password", $"Password for {Path.GetFileName(path)}: ", ask: true) ?? "";
#pragma warning disable SYSLIB0057 // X509CertificateLoader is .NET 9+; this tool targets .NET 8.
            // Ephemeral key sets keep the key off disk; macOS does not support them.
            return new X509Certificate2(path, password, OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
#pragma warning restore SYSLIB0057
        }

        if (needsKey)
        {
            throw new CliUsageException("A certificate with its private key is needed: give a .pfx or .p12 file.");
        }

#pragma warning disable SYSLIB0057
        return new X509Certificate2(path);
#pragma warning restore SYSLIB0057
    }

    // ------------------------------------------------------------------ commands

    private static List<Command> Commands_()
    {
        var algorithm = new OptionSpec("algorithm", "NAME", "aes256 (default), aes128, rc4-128 or rc4-40.");
        var permissions = new OptionSpec("permissions", "LIST", "Allowed actions: all (default), none, or a list such as print,copy-content,fill-forms.");
        var pages = new OptionSpec("pages", "RANGE", "Pages, for example 1-3,5 or odd or 2- (default: all).");
        var list = new List<Command>();

        list.Add(new Command("protect", "<input.pdf> <output.pdf>", "Encrypt with passwords, or for recipients' certificates.",
            Passwords.Options("user-password", "owner-password").Concat(new[]
            {
                algorithm, permissions,
                new OptionSpec("no-user-password", null, "Anyone may open the file (only the owner password is set)."),
                new OptionSpec("no-encrypt-metadata", null, "Leave the XMP metadata readable."),
                new OptionSpec("recipient", "CERT", "Encrypt for this certificate (.cer, .pem, .pfx) instead of passwords.", Repeatable: true),
                ObjectStreams,
            }).ToArray(),
            async c =>
            {
                var input = c.Args.Positional(0, "input file");
                var output = c.Args.Positional(1, "output file");
                var perms = c.Args.Flags<PdfPermissions>("permissions") ?? PdfPermissions.All;
                var alg = c.Args.Enum<PdfEncryptionAlgorithm>("algorithm") ?? PdfEncryptionAlgorithm.Aes256;
                if (c.Args.All("recipient") is { Count: > 0 } recipients)
                {
                    var options = new PdfCertificateProtectionOptions
                    {
                        Recipients = recipients.Select(r => new PdfCertificateRecipient(LoadCertificate(c, r, needsKey: false), perms)).ToList(),
                        Algorithm = alg,
                        EncryptMetadata = !c.Args.Has("no-encrypt-metadata"),
                        Write = Write(c.Args),
                    };
                    return await new PdfCertificateSecurity().ProtectFileAsync(input, output, options, c.Cancellation).ConfigureAwait(false);
                }

                var user = c.Args.Has("no-user-password") ? "" : c.Passwords.Required("user-password", "User password (to open): ");
                var owner = c.Passwords.Required("owner-password", "Owner password: ");
                return Pdf.Protect(input, output, new PdfProtectionOptions { UserPassword = user, OwnerPassword = owner, Algorithm = alg, Permissions = perms, EncryptMetadata = !c.Args.Has("no-encrypt-metadata"), Write = Write(c.Args) });
            }, PerFile: true));

        list.Add(new Command("decrypt", "<input.pdf> <output.pdf>", "Remove protection (owner password, or a recipient certificate).",
            Passwords.Options("password", "certificate-password").Concat(new[]
            {
                new OptionSpec("allow-user-password", null, "Also accept the user password."),
                new OptionSpec("certificate", "PFX", "Decrypt a certificate-encrypted file with this certificate and key."),
                ObjectStreams,
            }).ToArray(),
            async c =>
            {
                var input = c.Args.Positional(0, "input file");
                var output = c.Args.Positional(1, "output file");
                if (c.Args.Get("certificate") is { } certificate)
                {
                    var cert = LoadCertificate(c, certificate, needsKey: true);
                    return await new PdfCertificateSecurity().DecryptFileAsync(input, output, new PdfCertificateDecryptOptions { Certificate = cert, Write = Write(c.Args) }, c.Cancellation).ConfigureAwait(false);
                }

                var password = c.Passwords.Required("password", "Owner password: ");
                return Pdf.Decrypt(input, output, new PdfDecryptOptions { Password = password, AllowUserPassword = c.Args.Has("allow-user-password"), Write = Write(c.Args) });
            }, PerFile: true));

        list.Add(new Command("security", "<input.pdf> <output.pdf>", "Change the passwords, permissions or algorithm of a protected file.",
            Passwords.Options("password", "new-user-password", "new-owner-password").Concat(new[]
            {
                algorithm, permissions,
                new OptionSpec("change-passwords", null, "Set new user and owner passwords (read or prompted after the current one)."),
                ObjectStreams,
            }).ToArray(),
            c =>
            {
                var input = c.Args.Positional(0, "input file");
                var output = c.Args.Positional(1, "output file");
                var changes = new PdfSecurityChanges
                {
                    CurrentOwnerPassword = c.Passwords.Required("password", "Current owner password: "),
                    Permissions = c.Args.Flags<PdfPermissions>("permissions"),
                    Algorithm = c.Args.Enum<PdfEncryptionAlgorithm>("algorithm"),
                    Write = Write(c.Args),
                };
                if (c.Args.Has("change-passwords") || c.Args.Has("new-user-password-env") || c.Args.Has("new-owner-password-env"))
                {
                    changes.UserPassword = c.Passwords.Required("new-user-password", "New user password: ");
                    changes.OwnerPassword = c.Passwords.Required("new-owner-password", "New owner password: ");
                }

                return Task.FromResult<object>(Pdf.ChangeSecurity(input, output, changes));
            }, PerFile: true));

        list.Add(new Command("info", "<input.pdf>", "Show the version, page count, encryption and document information.",
            Passwords.Options("password").ToArray(),
            c =>
            {
                var input = c.Args.Positional(0, "input file");
                var info = Pdf.GetInfo(input, OpenPassword(c, input));
                return Task.FromResult<object>(new HumanResult(info, InfoLines(input, info)));
            }, ReadOnly: true));

        list.Add(new Command("inspect", "<input.pdf>", "Show everything: fonts, images, attachments, bookmarks, form fields, signatures, page labels, actions.",
            Passwords.Options("password").Append(new OptionSpec("details", "LIST", "What to read: all (default) or a list such as fonts,images,signatures.")).ToArray(),
            c =>
            {
                var input = c.Args.Positional(0, "input file");
                var info = Pdf.Inspect(input, new PdfInspectionOptions { Password = OpenPassword(c, input), Details = c.Args.Flags<PdfInspectionDetails>("details") ?? PdfInspectionDetails.All });
                var lines = InfoLines(input, info).ToList();
                lines.AddRange(info.Fonts.Select(f => $"font       {f}"));
                lines.AddRange(info.Images.Select(i => $"image      {i}"));
                lines.AddRange(info.Attachments.Select(a => $"attachment {a}"));
                lines.AddRange(Flatten(info.Bookmarks, 0).Select(b => $"bookmark   {b}"));
                lines.AddRange(info.FormFields.Select(f => $"field      {f}"));
                lines.AddRange(info.Signatures.Select(s => $"signature  {s}"));
                lines.AddRange(info.Actions.Select(a => $"action     {a}"));
                return Task.FromResult<object>(new HumanResult(info, lines));
            }, ReadOnly: true));

        list.Add(new Command("edit-info", "<input.pdf> <output.pdf>", "Change the title, author and other document information (XMP kept in sync).",
            Passwords.Options("password").Concat(InfoOptions).Concat(new[] { new OptionSpec("incremental", null, "Append the change, keeping every original byte (signatures stay valid)."), ObjectStreams }).ToArray(),
            c =>
            {
                var input = c.Args.Positional(0, "input file");
                var output = c.Args.Positional(1, "output file");
                return Task.FromResult<object>(Pdf.EditInfo(input, output, new PdfEditInfoOptions
                {
                    Changes = InfoChanges(c.Args),
                    Password = OpenPassword(c, input),
                    SaveMode = c.Args.Has("incremental") ? PdfSaveMode.Incremental : PdfSaveMode.Rewrite,
                    Write = Write(c.Args),
                }));
            }, PerFile: true));

        list.Add(new Command("validate", "<input.pdf>", "Check the structure; exits with 3 when errors are found.",
            Passwords.Options("password").Append(new OptionSpec("no-streams", null, "Skip decoding every stream (faster).")).ToArray(),
            c =>
            {
                var input = c.Args.Positional(0, "input file");
                var report = Pdf.Validate(input, new PdfValidationOptions { Password = OpenPassword(c, input), CheckStreams = !c.Args.Has("no-streams") });
                var lines = report.Issues.Select(i => i.ToString()).Append(report.ToString()).ToList();
                if (!report.IsValid)
                {
                    throw new CliCheckFailedException($"{input} has {report.Issues.Count(i => i.Severity == PdfValidationSeverity.Error)} error(s).", InvalidInput, new HumanResult(report, lines));
                }

                return Task.FromResult<object>(new HumanResult(report, lines));
            }, ReadOnly: true));

        list.Add(new Command("repair", "<input.pdf> <output.pdf>", "Rebuild a damaged file (cross-reference table, page tree, stream lengths).",
            Passwords.Options("password").Concat(new[] { new OptionSpec("remove-protection", null, "Write the repaired file without protection (needs the owner password)."), ObjectStreams }).ToArray(),
            c =>
            {
                var input = c.Args.Positional(0, "input file");
                var output = c.Args.Positional(1, "output file");
                return Task.FromResult<object>(Pdf.Repair(input, output, new PdfRepairOptions { Password = OpenPassword(c, input), RemoveProtection = c.Args.Has("remove-protection"), Write = Write(c.Args) }));
            }, PerFile: true));

        list.Add(new Command("sanitize", "<input.pdf> <output.pdf>", "Remove JavaScript (default) and, on request, metadata, attachments, actions and annotations.",
            Passwords.Options("password").Concat(new[]
            {
                new OptionSpec("all", null, "Remove everything below."),
                new OptionSpec("info", null, "Remove the document information."),
                new OptionSpec("xmp", null, "Remove the XMP metadata."),
                new OptionSpec("attachments", null, "Remove attachments."),
                new OptionSpec("actions", null, "Remove actions."),
                new OptionSpec("annotations", null, "Remove annotations."),
                new OptionSpec("remove-links", null, "Also remove link annotations."),
                new OptionSpec("keep-javascript", null, "Keep JavaScript."),
                ObjectStreams,
            }).ToArray(),
            c =>
            {
                var input = c.Args.Positional(0, "input file");
                var output = c.Args.Positional(1, "output file");
                var options = c.Args.Has("all") ? PdfSanitizeOptions.All() : new PdfSanitizeOptions
                {
                    RemoveDocumentInfo = c.Args.Has("info"),
                    RemoveXmp = c.Args.Has("xmp"),
                    RemoveAttachments = c.Args.Has("attachments"),
                    RemoveActions = c.Args.Has("actions"),
                    RemoveAnnotations = c.Args.Has("annotations"),
                };
                options.RemoveJavaScript = !c.Args.Has("keep-javascript");
                options.KeepLinks = !c.Args.Has("remove-links");
                options.Password = OpenPassword(c, input);
                options.Write = Write(c.Args);
                return Task.FromResult<object>(Pdf.Sanitize(input, output, options));
            }, PerFile: true));

        list.Add(new Command("attach", "<input.pdf> <output.pdf> <file>", "Embed a file.",
            Passwords.Options("password").Concat(new[]
            {
                new OptionSpec("name", "NAME", "The attachment's name (default: the file name)."),
                new OptionSpec("description", "TEXT", "A description."),
                new OptionSpec("mime-type", "TYPE", "Its media type."),
                new OptionSpec("replace", null, "Replace an attachment with the same name."),
                ObjectStreams,
            }).ToArray(),
            c =>
            {
                var input = c.Args.Positional(0, "input file");
                var output = c.Args.Positional(1, "output file");
                var file = c.Args.Positional(2, "file to attach");
                var info = new FileInfo(file);
                return Task.FromResult<object>(Pdf.AddAttachment(input, output, new PdfAddAttachmentOptions
                {
                    Name = c.Args.Get("name") ?? info.Name,
                    Content = File.ReadAllBytes(file),
                    Description = c.Args.Get("description"),
                    MimeType = c.Args.Get("mime-type"),
                    ModificationDate = info.LastWriteTimeUtc,
                    ReplaceExisting = c.Args.Has("replace"),
                    Password = OpenPassword(c, input),
                    Write = Write(c.Args),
                }));
            }));

        list.Add(new Command("detach", "<input.pdf> <output.pdf> <name>", "Remove an attachment.",
            Passwords.Options("password").Append(ObjectStreams).ToArray(),
            c =>
            {
                var input = c.Args.Positional(0, "input file");
                return Task.FromResult<object>(Pdf.RemoveAttachment(input, c.Args.Positional(1, "output file"), new PdfRemoveAttachmentOptions { Name = c.Args.Positional(2, "attachment name"), Password = OpenPassword(c, input), Write = Write(c.Args) }));
            }));

        list.Add(new Command("extract-attachment", "<input.pdf> <name> <output-file>", "Save an attachment's content (or list attachments when no name is given).",
            Passwords.Options("password").ToArray(),
            c =>
            {
                var input = c.Args.Positional(0, "input file");
                var password = OpenPassword(c, input);
                if (c.Args.Positionals.Count < 2)
                {
                    var attachments = Pdf.ListAttachments(input, password);
                    return Task.FromResult<object>(new HumanResult(attachments, attachments.Select(a => a.ToString())));
                }

                var name = c.Args.Positional(1, "attachment name");
                var output = c.Args.Positional(2, "output file");
                var data = Pdf.ExtractAttachment(input, name, password);
                File.WriteAllBytes(output, data);
                return Task.FromResult<object>(new HumanResult(new { name, output, bytes = data.Length }, new[] { $"Saved {name} ({data.Length} bytes) to {output}." }));
            }, ReadOnly: true));

        list.Add(new Command("rotate", "<input.pdf> <output.pdf>", "Rotate pages.",
            Passwords.Options("password").Concat(new[] { new OptionSpec("angle", "DEGREES", "90 (default), 180, 270 or -90."), pages, ObjectStreams }).ToArray(),
            c =>
            {
                var input = c.Args.Positional(0, "input file");
                return Task.FromResult<object>(Pdf.RotatePages(input, c.Args.Positional(1, "output file"), new PdfRotatePagesOptions { Angle = c.Args.Integer("angle") ?? 90, Pages = Pages(c.Args), Password = OpenPassword(c, input), Write = Write(c.Args) }));
            }, PerFile: true));

        list.Add(new Command("delete-pages", "<input.pdf> <output.pdf>", "Delete pages.",
            Passwords.Options("password").Concat(new[] { pages, ObjectStreams }).ToArray(),
            c =>
            {
                var input = c.Args.Positional(0, "input file");
                var range = Pages(c.Args) ?? throw new CliUsageException("Give the pages to delete with --pages.");
                return Task.FromResult<object>(Pdf.DeletePages(input, c.Args.Positional(1, "output file"), new PdfDeletePagesOptions { Pages = range, Password = OpenPassword(c, input), Write = Write(c.Args) }));
            }, PerFile: true));

        list.Add(new Command("merge", "<output.pdf> <input.pdf>...", "Combine files (an input may be 'file.pdf:RANGE', for example 'a.pdf:1-3').",
            Passwords.Options("password").Concat(new[]
            {
                new OptionSpec("outlines", "MODE", "per-document (default), flat or none."),
                new OptionSpec("deduplicate", null, "Share identical fonts and images."),
                ObjectStreams,
            }).ToArray(),
            c =>
            {
                var output = c.Args.Positional(0, "output file");
                if (c.Args.Positionals.Count < 3)
                {
                    throw new CliUsageException("Give at least two input files.");
                }

                var inputs = c.Args.Positionals.Skip(1).Select(spec =>
                {
                    var colon = spec.LastIndexOf(':');
                    var path = colon > 1 && !File.Exists(spec) ? spec[..colon] : spec;
                    var range = colon > 1 && !File.Exists(spec) ? (PdfPageRange.TryParse(spec[(colon + 1)..], out var r, out var error) ? r : throw new CliUsageException($"'{spec}': {error}")) : null;
                    return PdfMergeInput.FromFile(path, OpenPassword(c, path), range);
                }).ToList();
                return Task.FromResult<object>(Pdf.Merge(inputs, output, new PdfMergeOptions { Outlines = c.Args.Enum<PdfMergeOutlineMode>("outlines") ?? PdfMergeOutlineMode.PerDocument, DeduplicateResources = c.Args.Has("deduplicate"), Write = Write(c.Args) }));
            }));

        list.Add(new Command("split", "<input.pdf> <output-pattern>", "Split into files; {0} in the pattern is the part number (default: one file per page).",
            Passwords.Options("password").Concat(new[]
            {
                new OptionSpec("range", "RANGE", "A part: pages for one output file.", Repeatable: true),
                new OptionSpec("every", "N", "Parts of N pages."),
                ObjectStreams,
            }).ToArray(),
            c =>
            {
                var input = c.Args.Positional(0, "input file");
                var pattern = c.Args.Positional(1, "output pattern");
                if (!pattern.Contains("{0}", StringComparison.Ordinal))
                {
                    pattern = Path.Combine(Path.GetDirectoryName(pattern) ?? "", Path.GetFileNameWithoutExtension(pattern) + "-{0}" + Path.GetExtension(pattern));
                }

                var ranges = c.Args.All("range").Select(r => PdfPageRange.TryParse(r, out var range, out var error) ? range! : throw new CliUsageException($"--range: {error}")).ToList();
                return Task.FromResult<object>(Pdf.Split(input, pattern, new PdfSplitOptions { Ranges = ranges.Count > 0 ? ranges : null, EveryNPages = c.Args.Integer("every") ?? (ranges.Count == 0 ? 1 : null), Password = OpenPassword(c, input), Write = Write(c.Args) }));
            }));

        list.Add(new Command("text", "<input.pdf>", "Extract the text.",
            Passwords.Options("password").Concat(new[]
            {
                pages,
                new OptionSpec("layout", null, "Order text by position (lines top to bottom) instead of drawing order."),
                new OptionSpec("output", "FILE", "Write the text to a file."),
            }).ToArray(),
            c =>
            {
                var input = c.Args.Positional(0, "input file");
                var result = Pdf.ExtractText(input, new PdfTextExtractionOptions { Pages = Pages(c.Args), Order = c.Args.Has("layout") ? PdfTextOrder.Layout : PdfTextOrder.Content, Password = OpenPassword(c, input) });
                if (c.Args.Get("output") is { } output)
                {
                    File.WriteAllText(output, result.Text);
                    return Task.FromResult<object>(new HumanResult(result, new[] { $"Wrote {result.Text.Length} characters from {result.Pages.Count} pages to {output}." }));
                }

                return Task.FromResult<object>(new HumanResult(result, new[] { result.Text }));
            }, ReadOnly: true));

        list.Add(new Command("watermark", "<input.pdf> <output.pdf>", "Stamp text or an image on pages.",
            Passwords.Options("password").Concat(new[]
            {
                new OptionSpec("text", "TEXT", "The text (\\n for a new line)."),
                new OptionSpec("image", "FILE", "A PNG or JPEG image instead of text."),
                new OptionSpec("font", "TTF", "A TrueType font for the text (any script)."),
                new OptionSpec("font-size", "PT", "Font size (default 48)."),
                new OptionSpec("color", "#RRGGBB", "Text colour (default gray)."),
                new OptionSpec("opacity", "0-1", "Opacity (default 0.3)."),
                new OptionSpec("rotation", "DEGREES", "Rotation (default 45)."),
                new OptionSpec("position", "NAME", "center (default), top-left, top-center, top-right, middle-left, middle-right, bottom-left, bottom-center, bottom-right."),
                new OptionSpec("under", null, "Put the stamp under the page content."),
                pages, ObjectStreams,
            }).ToArray(),
            c =>
            {
                var input = c.Args.Positional(0, "input file");
                var options = new PdfStampOptions
                {
                    Text = c.Args.Get("text")?.Replace("\\n", "\n", StringComparison.Ordinal),
                    Image = c.Args.Get("image") is { } image ? File.ReadAllBytes(image) : null,
                    FontData = c.Args.Get("font") is { } font ? File.ReadAllBytes(font) : null,
                    FontSize = c.Args.Number("font-size") ?? 48,
                    Opacity = c.Args.Number("opacity") ?? 0.3,
                    Rotation = c.Args.Number("rotation") ?? 45,
                    Position = c.Args.Enum<PdfStampPosition>("position") ?? PdfStampPosition.Center,
                    Layer = c.Args.Has("under") ? PdfStampLayer.Under : PdfStampLayer.Over,
                    Pages = Pages(c.Args),
                    Password = OpenPassword(c, input),
                    Write = Write(c.Args),
                };
                if (c.Args.Get("color") is { } color)
                {
                    options.Color = PdfColor.FromHex(color);
                }

                return Task.FromResult<object>(Pdf.Stamp(input, c.Args.Positional(1, "output file"), options));
            }, PerFile: true) { Aliases = new[] { "stamp" } });

        list.Add(new Command("sign", "<input.pdf> <output.pdf>", "Sign (PAdES) with a certificate and its private key from a .pfx file.",
            Passwords.Options("certificate-password").Concat(new[]
            {
                new OptionSpec("certificate", "PFX", "The signer's certificate with its private key (.pfx or .p12)."),
                new OptionSpec("reason", "TEXT", "Why the document is signed."),
                new OptionSpec("location", "TEXT", "Where it is signed."),
                new OptionSpec("contact", "TEXT", "How to reach the signer."),
                new OptionSpec("field", "NAME", "Sign this existing (or new) signature field."),
                new OptionSpec("visible", "PAGE:X1,Y1,X2,Y2", "Show the signature on a page, for example 1:36,36,236,96."),
                new OptionSpec("timestamp", "URL", "Add an RFC 3161 timestamp from this server."),
            }).ToArray(),
            async c =>
            {
                var certificatePath = c.Args.Get("certificate") ?? throw new CliUsageException("Give the signer's certificate with --certificate file.pfx.");
                var options = new PdfSignOptions
                {
                    Certificate = LoadCertificate(c, certificatePath, needsKey: true),
                    Reason = c.Args.Get("reason"),
                    Location = c.Args.Get("location"),
                    ContactInfo = c.Args.Get("contact"),
                    FieldName = c.Args.Get("field"),
                    TimestampServer = c.Args.Get("timestamp") is { } url ? (Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : throw new CliUsageException("--timestamp must be an absolute URL.")) : null,
                };
                if (c.Args.Get("visible") is { } visible)
                {
                    var colon = visible.IndexOf(':', StringComparison.Ordinal);
                    var numbers = (colon < 0 ? "" : visible[(colon + 1)..]).Split(',').Select(n => double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN).ToArray();
                    if (colon <= 0 || !int.TryParse(visible[..colon], out var page) || numbers.Length != 4 || numbers.Any(double.IsNaN))
                    {
                        throw new CliUsageException("--visible must look like PAGE:X1,Y1,X2,Y2, for example 1:36,36,236,96.");
                    }

                    options.Appearance = new PdfSignatureAppearance { Page = page, Bounds = new PdfRectangle(numbers[0], numbers[1], numbers[2], numbers[3]) };
                }

                return await new PdfSigner().SignFileAsync(c.Args.Positional(0, "input file"), c.Args.Positional(1, "output file"), options, c.Cancellation).ConfigureAwait(false);
            }, PerFile: true));

        list.Add(new Command("verify-signatures", "<input.pdf>", "Check every signature; exits with 5 when one is not valid.",
            Passwords.Options("password").Append(new OptionSpec("check-chain", null, "Also validate the certificate chains against the system's trust store.")).ToArray(),
            async c =>
            {
                var input = c.Args.Positional(0, "input file");
                await using var stream = File.OpenRead(input);
                var options = new PdfSignatureVerificationOptions { Password = OpenPassword(c, input), ChainPolicy = c.Args.Has("check-chain") ? new X509ChainPolicy() : null };
                var result = await new PdfSigner().VerifyAsync(stream, options, c.Cancellation).ConfigureAwait(false);
                var lines = result.Signatures.Select(s => $"{(s.IsValid ? "valid  " : "INVALID")} {s.FieldName}: {s.SignerName ?? "unknown signer"}, {s.SubFilter}, {(s.CoversWholeFile ? "covers the whole file" : "changed after signing: " + string.Join(", ", s.ChangesAfterSigning))}{(s.Problems.Count > 0 ? " (" + string.Join("; ", s.Problems) + ")" : "")}").ToList();
                if (result.Signatures.Count == 0)
                {
                    lines.Add("The document has no signatures.");
                }

                var human = new HumanResult(result, lines);
                if (result.Signatures.Count > 0 && !result.AllValid)
                {
                    throw new CliCheckFailedException("At least one signature is not valid.", VerificationFailed, human);
                }

                return human;
            }, ReadOnly: true));

        list.Add(new Command("convert", "<input> <output>", "Convert PDF to HTML or Word, or HTML or Word to PDF (direction from the extensions, or --to).",
            Passwords.Options("password").Concat(new[]
            {
                new OptionSpec("to", "FORMAT", "html, docx or pdf (default: from the output's extension)."),
                pages,
                new OptionSpec("layout", "MODE", "PDF to HTML: reflow (default) or fixed."),
                new OptionSpec("images", "MODE", "PDF to HTML: embedded (default), files or none."),
                new OptionSpec("page-breaks", null, "PDF to Word: start each PDF page on a new page."),
                new OptionSpec("page-size", "NAME", "To PDF: a4 (default), letter, legal, a3 or a5."),
                new OptionSpec("landscape", null, "To PDF: landscape pages."),
                new OptionSpec("margin", "PT", "HTML to PDF: page margins in points (default 54)."),
                new OptionSpec("font", "TTF", "To PDF: a TrueType font to use.", Repeatable: true),
                new OptionSpec("font-dir", "DIR", "To PDF: a folder of fonts.", Repeatable: true),
                new OptionSpec("system-fonts", null, "To PDF: also use the system's fonts."),
                new OptionSpec("resources", "DIR", "HTML to PDF: load images and style sheets from this folder (default: the HTML file's folder)."),
                new OptionSpec("no-resources", null, "HTML to PDF: load no external resources."),
                new OptionSpec("footer", "HTML", "HTML to PDF: footer template ({page} and {pages} are page numbers)."),
                new OptionSpec("header", "HTML", "HTML to PDF: header template."),
            }).ToArray(),
            Convert, PerFile: true) { OutputExtension = a => a.Get("to") is { } to ? "." + to.ToLowerInvariant().Replace("htm", "html", StringComparison.Ordinal).Replace("htmll", "html", StringComparison.Ordinal) : null });

        list.Add(new Command("batch", "<command> --out-dir DIR [command options] <input>...", "Run a command on many files in parallel (inputs may use * and ? wildcards).",
            new[]
            {
                new OptionSpec("out-dir", "DIR", "Where output files go (same names; convert uses --to's extension)."),
                new OptionSpec("parallel", "N", "How many files at once (default: the number of processors)."),
                new OptionSpec("stop-on-error", null, "Stop at the first failure."),
            },
            _ => throw new InvalidOperationException("batch is run by CliApp.BatchAsync.")));

        return list;
    }

    private static IEnumerable<PdfBookmark> Flatten(IReadOnlyList<PdfBookmark> bookmarks, int depth) =>
        depth > 32 ? Enumerable.Empty<PdfBookmark>() : bookmarks.SelectMany(b => new[] { b }.Concat(Flatten(b.Children, depth + 1)));

    private static IEnumerable<string> InfoLines(string path, PdfDocumentInfo info)
    {
        yield return $"File:       {path} ({info.FileSize.ToString("N0", CultureInfo.InvariantCulture)} bytes)";
        yield return $"Version:    PDF {info.EffectiveVersion}";
        yield return $"Pages:      {(info.PageCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown (encrypted)")}";
        yield return $"Encrypted:  {(info.Encryption is { } e ? $"{e.Algorithm} ({e.Filter}{(info.RequiresCertificate ? ", needs a certificate" : info.RequiresPassword ? ", needs a password" : "")})" : "no")}";
        if (info.Info?.Title is { } title)
        {
            yield return $"Title:      {title}";
        }

        if (info.Info?.Author is { } author)
        {
            yield return $"Author:     {author}";
        }

        if (info.IsPdfA)
        {
            yield return $"Conformance: {info.Xmp!.PdfALevel}";
        }

        foreach (var warning in info.Warnings)
        {
            yield return "warning: " + warning;
        }
    }

    // ------------------------------------------------------------------ convert

    private static async Task<object> Convert(CommandContext c)
    {
        var input = c.Args.Positional(0, "input file");
        var output = c.Args.Positional(1, "output file");
        var from = Path.GetExtension(input).ToLowerInvariant();
        var to = (c.Args.Get("to") ?? Path.GetExtension(output).TrimStart('.')).ToLowerInvariant();
        to = to is "htm" or "xhtml" ? "html" : to;
        var converter = new PdfConverter();
        var pageSize = (c.Args.Get("page-size")?.ToLowerInvariant() ?? "a4") switch
        {
            "a4" => PdfPageSize.A4,
            "a3" => PdfPageSize.A3,
            "a5" => PdfPageSize.A5,
            "letter" => PdfPageSize.Letter,
            "legal" => PdfPageSize.Legal,
            var other => throw new CliUsageException($"--page-size '{other}' is not a4, a3, a5, letter or legal."),
        };
        if (c.Args.Has("landscape"))
        {
            pageSize = pageSize.Landscape;
        }

        IEnumerable<PdfFontSource> Fonts() => c.Args.All("font").Select(f => new PdfFontSource(File.ReadAllBytes(f)));
        switch (from, to)
        {
            case (".pdf", "html"):
            {
                var result = await converter.ConvertToHtmlFileAsync(input, output, new PdfToHtmlOptions
                {
                    Password = OpenPassword(c, input),
                    Pages = Pages(c.Args),
                    Layout = c.Args.Enum<PdfHtmlLayout>("layout") ?? PdfHtmlLayout.Reflow,
                    Images = c.Args.Get("images")?.ToLowerInvariant() switch { null or "embedded" => PdfHtmlImageMode.Embedded, "files" => PdfHtmlImageMode.SeparateFiles, "none" => PdfHtmlImageMode.None, var other => throw new CliUsageException($"--images '{other}' is not embedded, files or none.") },
                }, c.Cancellation).ConfigureAwait(false);
                return result.PageCount == 0 || (result.ImageCount == 0 && !HasText(output)) ? throw new CliConversionException("The conversion produced an empty document (the PDF may contain only scanned images).") : result;
            }

            case (".pdf", "docx"):
            {
                var result = await converter.ConvertToDocxFileAsync(input, output, new PdfToDocxOptions { Password = OpenPassword(c, input), Pages = Pages(c.Args), PageBreaks = c.Args.Has("page-breaks") }, c.Cancellation).ConfigureAwait(false);
                return result.ParagraphCount == 0 && result.ImageCount == 0 ? throw new CliConversionException("The conversion produced an empty document (the PDF may contain only scanned images).") : result;
            }

            case (".html" or ".htm" or ".xhtml", "pdf"):
            {
                var options = new HtmlToPdfOptions
                {
                    PageSize = pageSize,
                    Margins = PdfMargins.All(c.Args.Number("margin") ?? 54),
                    HeaderHtml = c.Args.Get("header"),
                    FooterHtml = c.Args.Get("footer"),
                    UseSystemFonts = c.Args.Has("system-fonts"),
                    ResourceResolver = c.Args.Has("no-resources") ? null : HtmlResources.FromDirectory(c.Args.Get("resources") ?? Path.GetDirectoryName(Path.GetFullPath(input))!),
                };
                foreach (var font in Fonts())
                {
                    options.Fonts.Add(font);
                }

                foreach (var dir in c.Args.All("font-dir"))
                {
                    options.FontDirectories.Add(dir);
                }

                return await converter.ConvertFromHtmlFileAsync(input, output, options, c.Cancellation).ConfigureAwait(false);
            }

            case (".docx", "pdf"):
            {
                var options = new DocxToPdfOptions { UseSystemFonts = c.Args.Has("system-fonts") };
                foreach (var font in Fonts())
                {
                    options.Fonts.Add(font);
                }

                foreach (var dir in c.Args.All("font-dir"))
                {
                    options.FontDirectories.Add(dir);
                }

                return await converter.ConvertFromDocxFileAsync(input, output, options, c.Cancellation).ConfigureAwait(false);
            }

            default:
                throw new CliUsageException($"Cannot convert {(from.Length == 0 ? "this file" : from)} to {to}: supported are .pdf to html or docx, and .html or .docx to pdf.");
        }
    }

    /// <summary>Whether a converted HTML file's main content has any visible text.</summary>
    private static bool HasText(string htmlPath)
    {
        var html = File.ReadAllText(htmlPath);
        var start = html.IndexOf("<main", StringComparison.Ordinal);
        var end = html.LastIndexOf("</main>", StringComparison.Ordinal);
        var body = start >= 0 && end > start ? html[start..end] : html;
        return System.Text.RegularExpressions.Regex.Replace(body, "<[^>]*>", "").Any(char.IsLetterOrDigit);
    }

    // ------------------------------------------------------------------ batch

    private static readonly OptionSpec[] BatchOptions =
    {
        new("out-dir", "DIR", ""),
        new("parallel", "N", ""),
        new("stop-on-error", null, ""),
        new("json", null, ""),
        new("quiet", null, ""),
    };

    /// <summary>
    /// <c>batch &lt;command&gt; [options] &lt;inputs&gt;</c>: the batch's own options are taken out, everything else is
    /// parsed with the command's options; each input runs the command once (output in <c>--out-dir</c>).
    /// </summary>
    private static async Task<(object Result, Arguments Parsed)> BatchAsync(List<string> args, CliConsole console, CancellationToken ct)
    {
        if (args.Count == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            throw new CliUsageException("Give the command to run, for example: pdfutil batch protect --out-dir out --owner-password-env OWNER *.pdf");
        }

        var command = Find(args[0]) ?? throw new CliUsageException($"Unknown command '{args[0]}'.");
        if (!command.PerFile && !command.ReadOnly)
        {
            throw new CliUsageException($"'{command.Name}' cannot run in a batch.");
        }

        // Split the batch's options from the command's options and the inputs.
        var batchTokens = new List<string>();
        var commandTokens = new List<string>();
        var inputs = new List<string>();
        var specs = command.Options.Concat(Common).ToDictionary(o => o.Name, StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i++)
        {
            var token = args[i];
            var name = token.StartsWith("--", StringComparison.Ordinal) ? token[2..].Split('=')[0] : null;
            if (name is null)
            {
                inputs.Add(token);
                continue;
            }

            var batchSpec = BatchOptions.FirstOrDefault(o => o.Name == name);
            var target = batchSpec is not null && (name is not ("json" or "quiet")) ? batchTokens : commandTokens;
            if (name is "json" or "quiet")
            {
                batchTokens.Add(token);
            }

            target.Add(token);
            var spec = batchSpec ?? (specs.TryGetValue(name, out var s) ? s : throw new CliUsageException($"Unknown option --{name} for {command.Name}."));
            if (spec.Value is not null && !token.Contains('=', StringComparison.Ordinal))
            {
                if (i + 1 >= args.Count)
                {
                    throw new CliUsageException($"Option --{name} needs a value ({spec.Value}).");
                }

                target.Add(args[++i]);
            }
        }

        var batchArgs = Arguments.Parse(batchTokens, BatchOptions);
        var commandArgs = Arguments.Parse(commandTokens, command.Options.Concat(Common).ToList());
        var outDir = batchArgs.Get("out-dir");
        if (command.PerFile && outDir is null)
        {
            throw new CliUsageException("Give the output folder with --out-dir.");
        }

        if (outDir is not null)
        {
            Directory.CreateDirectory(outDir);
        }

        var files = new List<string>();
        foreach (var pattern in inputs)
        {
            if (pattern.Contains('*', StringComparison.Ordinal) || pattern.Contains('?', StringComparison.Ordinal))
            {
                var directory = Path.GetDirectoryName(pattern) is { Length: > 0 } d ? d : ".";
                files.AddRange(Directory.EnumerateFiles(directory, Path.GetFileName(pattern)).Order(StringComparer.Ordinal));
            }
            else
            {
                files.Add(pattern);
            }
        }

        if (files.Count == 0)
        {
            throw new CliUsageException("No input files.");
        }

        var shared = new Passwords(commandArgs, console);
        var results = new BatchItem[files.Count];
        var gate = new SemaphoreSlim(1, 1);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, batchArgs.Integer("parallel") ?? Environment.ProcessorCount), CancellationToken = stop.Token }, async (i, token) =>
            {
                var input = files[i];
                string? output = null;
                if (command.PerFile)
                {
                    var extension = command.OutputExtension?.Invoke(commandArgs) ?? Path.GetExtension(input);
                    output = Path.Combine(outDir!, Path.GetFileNameWithoutExtension(input) + extension);
                }

                try
                {
                    var fileTokens = new List<string> { input };
                    if (output is not null)
                    {
                        fileTokens.Add(output);
                    }

                    var fileArgs = Arguments.Parse(fileTokens.Concat(commandTokens).ToList(), command.Options.Concat(Common).ToList());

                    // Passwords are read once and shared; reading and prompting are serialised.
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    object result;
                    try
                    {
                        result = await command.Run(new CommandContext { Args = fileArgs, Console = console, Passwords = shared, Cancellation = token }).ConfigureAwait(false);
                    }
                    finally
                    {
                        gate.Release();
                    }

                    results[i] = new BatchItem(input, output, true, Success, null, result is HumanResult h ? h.Value : result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    results[i] = new BatchItem(input, output, false, ExitCode(ex), ex.Message, (ex as CliCheckFailedException)?.Result is HumanResult hr ? hr.Value : null);
                    if (batchArgs.Has("stop-on-error"))
                    {
                        await stop.CancelAsync().ConfigureAwait(false);
                    }
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Stopped at the first error.
        }

        return (new BatchSummary(results.Where(r => r is not null).ToList()), batchArgs);
    }
}
