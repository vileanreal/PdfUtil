using System.Globalization;
using System.Text;

namespace PdfUtil.Internal.Text;

/// <summary>
/// Text strings (ISO 32000-2 §7.9.2.2): PDFDocEncoding, UTF-16BE with a FE FF byte order mark, or UTF-8 with an
/// EF BB BF byte order mark (PDF 2.0). Only for values the specification defines as text strings; byte strings such
/// as the trailer <c>/ID</c> never go through this class.
/// </summary>
internal static class TextString
{
    private static readonly Encoding Utf16BeStrict = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: false);
    private static readonly Encoding Utf8Lenient = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public static bool HasUtf16Bom(ReadOnlySpan<byte> b) => b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF;

    public static bool HasUtf8Bom(ReadOnlySpan<byte> b) => b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;

    /// <summary>Decodes already-decrypted text string bytes. Language escape sequences (ESC lang ESC) are removed.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (HasUtf16Bom(bytes))
        {
            var body = bytes[2..];
            if ((body.Length & 1) == 1)
            {
                body = body[..^1]; // dangling odd byte
            }

            return StripLanguageEscapes(Utf16BeStrict.GetString(body));
        }

        if (HasUtf8Bom(bytes))
        {
            return StripLanguageEscapes(Utf8Lenient.GetString(bytes[3..]));
        }

        return PdfDocEncoding.Decode(bytes);
    }

    /// <summary>
    /// Encodes text: PDFDocEncoding when possible, otherwise UTF-16BE with BOM. UTF-8 is never produced, so the
    /// result is readable by readers of every PDF version.
    /// </summary>
    public static byte[] Encode(string text)
    {
        if (PdfDocEncoding.TryEncode(text, out var pdfDoc))
        {
            return pdfDoc;
        }

        var body = Utf16BeStrict.GetBytes(text);
        var result = new byte[body.Length + 2];
        result[0] = 0xFE;
        result[1] = 0xFF;
        body.CopyTo(result, 2);
        return result;
    }

    /// <summary>Converts a UTF-8 (BOM) text string to UTF-16BE with BOM; other forms are returned unchanged.</summary>
    public static byte[] DowngradeUtf8(byte[] bytes) => HasUtf8Bom(bytes) ? Encode(Decode(bytes)) : bytes;

    private static string StripLanguageEscapes(string s)
    {
        var esc = s.IndexOf('\u001B');
        if (esc < 0)
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '\u001B')
            {
                var end = s.IndexOf('\u001B', i + 1);
                if (end < 0)
                {
                    break; // unterminated escape: drop the remainder of the marker
                }

                i = end + 1;
                continue;
            }

            sb.Append(s[i]);
            i++;
        }

        return sb.ToString();
    }
}

/// <summary>PDF date strings (ISO 32000-1 §7.9.4): <c>D:YYYYMMDDHHmmSSOHH'mm'</c>, parsed leniently.</summary>
internal static class PdfDate
{
    /// <summary>Formats a date as <c>D:YYYYMMDDHHmmSS+HH'mm'</c>, the form every reader accepts.</summary>
    public static string Format(DateTimeOffset value)
    {
        var offset = value.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return "D:" + value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
            + sign + offset.Hours.ToString("D2", CultureInfo.InvariantCulture) + "'" + offset.Minutes.ToString("D2", CultureInfo.InvariantCulture) + "'";
    }

    private static readonly System.Text.RegularExpressions.Regex StrictFormat = new(
        @"^D:(?<y>\d{4})(?<mo>\d{2})?(?<d>\d{2})?(?<h>\d{2})?(?<mi>\d{2})?(?<s>\d{2})?(?:Z(?:00'?(?:00'?)?)?|(?<sign>[+\-])(?<oh>\d{2})(?:'(?:(?<om>\d{2})'?)?)?)?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Checks a date string against the format of ISO 32000 §7.9.4: <c>D:</c>, then fields in order (each optional only
    /// if the following ones are absent), valid ranges, and an optional <c>Z</c> or <c>+HH'mm'</c> offset.
    /// Returns null when valid, otherwise the reason.
    /// </summary>
    public static string? Check(string text)
    {
        if (!text.StartsWith("D:", StringComparison.Ordinal))
        {
            return "it does not start with 'D:'";
        }

        var m = StrictFormat.Match(text);
        if (!m.Success)
        {
            return "it does not match D:YYYYMMDDHHmmSSOHH'mm'";
        }

        // A field may only be present when all earlier ones are.
        string[] order = { "mo", "d", "h", "mi", "s" };
        for (var i = 1; i < order.Length; i++)
        {
            if (m.Groups[order[i]].Success && !m.Groups[order[i - 1]].Success)
            {
                return "its fields are out of order";
            }
        }

        int Field(string name, int fallback) => m.Groups[name].Success ? int.Parse(m.Groups[name].Value, NumberStyles.None, CultureInfo.InvariantCulture) : fallback;
        var year = Field("y", 0);
        var month = Field("mo", 1);
        if (month is < 1 or > 12)
        {
            return "the month is out of range";
        }

        var day = Field("d", 1);
        if (day < 1 || day > DateTime.DaysInMonth(Math.Max(1, year), month))
        {
            return "the day is out of range";
        }

        if (Field("h", 0) > 23 || Field("mi", 0) > 59 || Field("s", 0) > 59)
        {
            return "the time is out of range";
        }

        if (Field("oh", 0) > 23 || Field("om", 0) > 59)
        {
            return "the time zone offset is out of range";
        }

        return null;
    }

    public static DateTimeOffset? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var s = text.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        var pos = 0;
        int Read(int digits, int fallback, int min, int max)
        {
            if (pos + digits > s.Length)
            {
                return fallback;
            }

            for (var k = 0; k < digits; k++)
            {
                if (!char.IsAsciiDigit(s[pos + k]))
                {
                    return fallback;
                }
            }

            var v = int.Parse(s.AsSpan(pos, digits), NumberStyles.None, CultureInfo.InvariantCulture);
            pos += digits;
            return v < min || v > max ? fallback : v;
        }

        var year = Read(4, -1, 0, 9999);
        if (year < 0)
        {
            return null;
        }

        var month = Read(2, 1, 1, 12);
        var day = Read(2, 1, 1, 31);
        var hour = Read(2, 0, 0, 23);
        var minute = Read(2, 0, 0, 59);
        var second = Read(2, 0, 0, 59);

        var offset = TimeSpan.Zero;
        if (pos < s.Length)
        {
            var sign = s[pos];
            if (sign is '+' or '-')
            {
                pos++;
                var oh = Read(2, 0, 0, 23);
                if (pos < s.Length && s[pos] == '\'')
                {
                    pos++;
                }

                var om = Read(2, 0, 0, 59);
                offset = new TimeSpan(oh, om, 0);
                if (sign == '-')
                {
                    offset = -offset;
                }
            }
        }

        day = Math.Min(day, DateTime.DaysInMonth(Math.Max(1, year), month));
        try
        {
            return new DateTimeOffset(Math.Max(1, year), month, day, hour, minute, second, offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
