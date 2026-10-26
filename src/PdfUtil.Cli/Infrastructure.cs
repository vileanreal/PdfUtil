using System.Globalization;
using System.Text;

namespace PdfUtil.Cli;

/// <summary>Where the tool reads and writes: the real console, or test doubles.</summary>
internal sealed class CliConsole
{
    public required TextReader In { get; init; }

    public required TextWriter Out { get; init; }

    public required TextWriter Error { get; init; }

    /// <summary>Reads an environment variable.</summary>
    public required Func<string, string?> Environment { get; init; }

    /// <summary>Asks for a secret without echoing it, or null when nobody can be asked (no terminal).</summary>
    public required Func<string, string?>? Prompt { get; init; }

    public static CliConsole System() => new()
    {
        In = Console.In,
        Out = Console.Out,
        Error = Console.Error,
        Environment = global::System.Environment.GetEnvironmentVariable,
        Prompt = Console.IsInputRedirected ? null : ReadHidden,
    };

    private static string? ReadHidden(string prompt)
    {
        Console.Error.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return sb.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                }
            }
            else if (key.Key == ConsoleKey.Escape)
            {
                Console.Error.WriteLine();
                return null;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
            }
        }
    }
}

/// <summary>A command line that does not make sense (exit code 1).</summary>
internal sealed class CliUsageException : Exception
{
    public CliUsageException(string message) : base(message) { }
}

/// <summary>A conversion that produced nothing usable (exit code 6).</summary>
internal sealed class CliConversionException : Exception
{
    public CliConversionException(string message) : base(message) { }
}

/// <summary>A check that found problems (verification failures exit with 5, invalid documents with 3).</summary>
internal sealed class CliCheckFailedException : Exception
{
    public CliCheckFailedException(string message, int exitCode, object? result) : base(message)
    {
        ExitCode = exitCode;
        Result = result;
    }

    public int ExitCode { get; }

    public object? Result { get; }
}

/// <summary>An option a command accepts.</summary>
/// <param name="Name">Long name without dashes.</param>
/// <param name="Value">Placeholder for its value, or null for a flag.</param>
/// <param name="Help">One line of help.</param>
/// <param name="Repeatable">Whether it may be given more than once.</param>
internal sealed record OptionSpec(string Name, string? Value, string Help, bool Repeatable = false);

/// <summary>
/// Parsed arguments: positionals in order, and options as <c>--name value</c>, <c>--name=value</c> or flags. Unknown
/// options, missing values and repeated single options are usage errors.
/// </summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);

    public List<string> Positionals { get; } = new();

    public static Arguments Parse(IReadOnlyList<string> args, IReadOnlyCollection<OptionSpec> specs)
    {
        var result = new Arguments();
        var byName = specs.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var onlyPositionals = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (onlyPositionals || !arg.StartsWith("--", StringComparison.Ordinal) || arg == "--")
            {
                if (arg == "--" && !onlyPositionals)
                {
                    onlyPositionals = true;
                    continue;
                }

                result.Positionals.Add(arg);
                continue;
            }

            var name = arg[2..];
            string? value = null;
            var eq = name.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
            {
                value = name[(eq + 1)..];
                name = name[..eq];
            }

            if (!byName.TryGetValue(name, out var spec))
            {
                throw new CliUsageException($"Unknown option --{name}.");
            }

            if (spec.Value is null)
            {
                if (value is not null)
                {
                    throw new CliUsageException($"Option --{name} does not take a value.");
                }

                value = "true";
            }
            else if (value is null)
            {
                if (i + 1 >= args.Count)
                {
                    throw new CliUsageException($"Option --{name} needs a value ({spec.Value}).");
                }

                value = args[++i];
            }

            if (!result._options.TryGetValue(name, out var list))
            {
                list = new List<string>();
                result._options[name] = list;
            }
            else if (!spec.Repeatable)
            {
                throw new CliUsageException($"Option --{name} is given more than once.");
            }

            list.Add(value);
        }

        return result;
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string? Get(string name) => _options.TryGetValue(name, out var list) ? list[^1] : null;

    public IReadOnlyList<string> All(string name) => _options.TryGetValue(name, out var list) ? list : Array.Empty<string>();

    public double? Number(string name)
    {
        var value = Get(name);
        if (value is null)
        {
            return null;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : throw new CliUsageException($"--{name} must be a number.");
    }

    public int? Integer(string name)
    {
        var value = Get(name);
        if (value is null)
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : throw new CliUsageException($"--{name} must be a whole number.");
    }

    public T? Enum<T>(string name)
        where T : struct, System.Enum
    {
        var value = Get(name);
        if (value is null)
        {
            return null;
        }

        return Parse<T>(value) ?? throw new CliUsageException($"--{name} must be one of: {string.Join(", ", System.Enum.GetNames<T>().Select(Display))}.");
    }

    /// <summary>A comma-separated list of flag names, combined.</summary>
    public T? Flags<T>(string name)
        where T : struct, System.Enum
    {
        var value = Get(name);
        if (value is null)
        {
            return null;
        }

        ulong combined = 0;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parsed = Parse<T>(part) ?? throw new CliUsageException($"--{name}: '{part}' is not one of: {string.Join(", ", System.Enum.GetNames<T>().Select(Display))}.");
            combined |= Convert.ToUInt64(parsed, CultureInfo.InvariantCulture);
        }

        return (T)System.Enum.ToObject(typeof(T), combined);
    }

    /// <summary>An enum name as typed on the command line: lower case, words joined by hyphens.</summary>
    public static string Display(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '_')
            {
                sb.Append('-');
                continue;
            }

            if (i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
            {
                sb.Append('-');
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    private static T? Parse<T>(string value)
        where T : struct, System.Enum
    {
        static string Key(string s) => s.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        var key = Key(value);
        foreach (var name in System.Enum.GetNames<T>())
        {
            if (Key(name) == key)
            {
                return System.Enum.Parse<T>(name);
            }
        }

        return null;
    }

    public string Positional(int index, string what) =>
        index < Positionals.Count ? Positionals[index] : throw new CliUsageException($"Missing {what}.");
}

/// <summary>
/// Passwords never come from plain arguments. Each one is read, in this order, from an environment variable named on
/// the command line (<c>--password-env NAME</c>, <c>--owner-password-env NAME</c>, …), from standard input with
/// <c>--password-stdin</c> (one line per password, in the order the command documents), or from a hidden prompt when
/// a terminal is attached.
/// </summary>
internal sealed class Passwords
{
    private readonly Arguments _args;
    private readonly CliConsole _console;
    private readonly Dictionary<string, string?> _cache = new(StringComparer.Ordinal);

    public Passwords(Arguments args, CliConsole console)
    {
        _args = args;
        _console = console;
    }

    public static IEnumerable<OptionSpec> Options(params string[] roles)
    {
        yield return new OptionSpec("password-stdin", null, "Read passwords from standard input, one per line" + (roles.Length > 1 ? $" (order: {string.Join(", ", roles)})" : "") + ".");
        foreach (var role in roles)
        {
            yield return new OptionSpec(role + "-env", "NAME", $"Read the {role.Replace('-', ' ')} from environment variable NAME.");
        }
    }

    /// <summary>
    /// The password for a role, or null when none was given and nobody can be asked. <paramref name="ask"/> decides
    /// whether to prompt (for example only when the input is encrypted).
    /// </summary>
    public string? Get(string role, string prompt, bool ask)
    {
        if (_cache.TryGetValue(role, out var cached))
        {
            return cached;
        }

        string? value = null;
        if (_args.Get(role + "-env") is { } variable)
        {
            value = _console.Environment(variable) ?? throw new CliUsageException($"Environment variable {variable} is not set.");
        }
        else if (_args.Has("password-stdin"))
        {
            value = _console.In.ReadLine() ?? throw new CliUsageException("Standard input ended before all passwords were read.");
        }
        else if (ask && _console.Prompt is { } promptFunction)
        {
            value = promptFunction(prompt);
        }

        _cache[role] = value;
        return value;
    }

    public string Required(string role, string prompt) =>
        Get(role, prompt, ask: true) ?? throw new CliUsageException($"The {role.Replace('-', ' ')} is required: use --{role}-env NAME or --password-stdin (or run in a terminal to be prompted).");
}
