using System.Text;
using PdfUtil.Internal.Text;

namespace PdfUtil.Internal.Security;

/// <summary>
/// The single place where passwords become bytes, shared by protection and verification so that a password that
/// protects a file always verifies against it. Passwords are never trimmed or case-folded.
/// </summary>
internal static class PasswordEncoder
{
    /// <summary>R5/R6: UTF-8 passwords are truncated to 127 bytes (ISO 32000-2 §7.6.4.3.3).</summary>
    public const int MaxUnicodePasswordBytes = 127;

    /// <summary>R2-R4: passwords are padded or truncated to 32 bytes (Algorithm 2, step a).</summary>
    public const int MaxLegacyPasswordBytes = 32;

    public static bool IsUnicodeRevision(int revision) => revision >= 5;

    /// <summary>
    /// Encodes a password for writing a new security handler. Throws <see cref="PdfPasswordEncodingException"/>
    /// rather than silently altering a password in a way the user would not reproduce when typing it.
    /// </summary>
    /// <param name="password">The password; null or empty means the empty password.</param>
    /// <param name="revision">Security handler revision.</param>
    /// <param name="allowTruncation">R2-R4 only: allow passwords longer than 32 bytes.</param>
    /// <param name="role">"user" or "owner", for error messages (never the password itself).</param>
    /// <param name="policy">Whether to accept only passwords that every major reader encodes identically.</param>
    /// <param name="changedBySaslPrep">Set when SASLprep altered the password (R5/R6).</param>
    public static byte[] EncodeForProtection(string? password, int revision, bool allowTruncation, string role, PdfPasswordPolicy policy, out bool changedBySaslPrep)
    {
        changedBySaslPrep = false;
        if (string.IsNullOrEmpty(password))
        {
            return Array.Empty<byte>();
        }

        if (IsUnicodeRevision(revision))
        {
            if (!SaslPrep.TryPrepare(password, out var prepared))
            {
                throw new PdfPasswordEncodingException(
                    $"The {role} password contains characters that SASLprep (RFC 4013) prohibits, such as control characters, private-use or non-character code points, or invalid bidirectional text. Choose a different password.");
            }

            if (prepared.Length == 0)
            {
                throw new PdfPasswordEncodingException($"The {role} password consists only of characters that SASLprep removes; it would be equivalent to an empty password.");
            }

            changedBySaslPrep = !string.Equals(prepared, password, StringComparison.Ordinal);
            if (policy == PdfPasswordPolicy.Interoperable && (changedBySaslPrep || !IsNormalizationStable(prepared) || prepared.Any(char.IsSurrogate)))
            {
                // Readers derive AES-256 password bytes differently: the specification applies SASLprep (NFKC),
                // pdf.js uses the plain UTF-8 of what was typed, and Apple PDFKit decomposes characters (NFD-like)
                // and mishandles characters outside the Basic Multilingual Plane. These agree only when SASLprep
                // changes nothing, no character has a canonical decomposition and no character is a surrogate pair.
                throw new PdfPasswordEncodingException(
                    $"The {role} password contains characters that PDF readers encode differently (for example accented letters such as '\u00E9', Hangul syllables, emoji and other characters outside the Basic Multilingual Plane, non-ASCII spaces, ligatures or soft hyphens), so it would be rejected by some viewers. Choose a password without such characters, or set PasswordPolicy = Specification to apply the ISO 32000-2 rules regardless of reader behaviour.");
            }

            return TruncateUtf8(Encoding.UTF8.GetBytes(prepared));
        }

        if (!PdfDocEncoding.TryEncode(password, out var bytes))
        {
            throw new PdfPasswordEncodingException(
                $"The {role} password contains characters that cannot be represented in PDFDocEncoding, which RC4 and AES-128 encryption require. Use only Latin characters supported by PDFDocEncoding, or use AES-256, which supports any Unicode password.");
        }

        if (policy == PdfPasswordPolicy.Interoperable && !password.All(IsInteroperableLegacyChar))
        {
            // Readers disagree on how to turn a legacy (R2-R4) password into bytes: the specification says
            // PDFDocEncoding, pdf.js uses the low byte of each code point, Apple PDFKit uses UTF-8, and Acrobat has
            // historically used the platform code page. They agree only on printable ASCII, so any other character
            // would produce a file that opens in some viewers and rejects the correct password in others.
            throw new PdfPasswordEncodingException(
                $"The {role} password contains non-ASCII or control characters. PDF readers encode such characters differently for RC4 and AES-128 encryption, so the password would be rejected by some viewers. Use printable ASCII characters only, use AES-256 (which supports Unicode passwords), or set PasswordPolicy = Specification to apply the PDFDocEncoding rule regardless of reader behaviour.");
        }

        if (bytes.Length > MaxLegacyPasswordBytes)
        {
            if (!allowTruncation)
            {
                throw new PdfPasswordEncodingException(
                    $"The {role} password is longer than 32 bytes, the maximum for RC4 and AES-128 encryption; only the first 32 bytes would matter. Use a shorter password, AES-256, or set AllowPasswordTruncation.");
            }

            bytes = bytes.AsSpan(0, MaxLegacyPasswordBytes).ToArray();
        }

        return bytes;
    }

    /// <summary>
    /// Returns the candidate byte encodings to try when verifying a password, most specific first.
    /// Never throws: an unencodable password simply has fewer (or no) candidates.
    /// </summary>
    public static List<byte[]> EncodeForVerification(string? password, int revision)
    {
        var candidates = new List<byte[]>();
        if (string.IsNullOrEmpty(password))
        {
            candidates.Add(Array.Empty<byte>());
            return candidates;
        }

        if (IsUnicodeRevision(revision))
        {
            if (SaslPrep.TryPrepare(password, out var prepared))
            {
                candidates.Add(TruncateUtf8(Encoding.UTF8.GetBytes(prepared)));
            }

            // Producers that skip SASLprep store the plain UTF-8 bytes.
            AddDistinct(candidates, TruncateUtf8(Encoding.UTF8.GetBytes(password)));
            return candidates;
        }

        if (PdfDocEncoding.TryEncode(password, out var pdfDoc))
        {
            candidates.Add(Truncate(pdfDoc, MaxLegacyPasswordBytes));
        }

        // Producers also encode legacy passwords as ISO 8859-1 (pdf.js and most Windows tools) or UTF-8 (Apple).
        if (password.All(c => c <= 0xFF))
        {
            AddDistinct(candidates, Truncate(Encoding.Latin1.GetBytes(password), MaxLegacyPasswordBytes));
        }

        AddDistinct(candidates, Truncate(Encoding.UTF8.GetBytes(password), MaxLegacyPasswordBytes));

        return candidates;
    }

    /// <summary>True when no character changes under canonical decomposition (NFD), e.g. Cyrillic, CJK ideographs, emoji.</summary>
    public static bool IsNormalizationStable(string s)
    {
        try
        {
            return string.Equals(s.Normalize(NormalizationForm.FormD), s, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Printable ASCII: the only characters every reader encodes identically for legacy passwords.</summary>
    public static bool IsInteroperableLegacyChar(char c) => c is >= '\u0020' and <= '\u007E';

    private static byte[] TruncateUtf8(byte[] utf8) => Truncate(utf8, MaxUnicodePasswordBytes);

    private static byte[] Truncate(byte[] bytes, int max) => bytes.Length <= max ? bytes : bytes.AsSpan(0, max).ToArray();

    private static void AddDistinct(List<byte[]> list, byte[] candidate)
    {
        foreach (var existing in list)
        {
            if (existing.AsSpan().SequenceEqual(candidate))
            {
                return;
            }
        }

        list.Add(candidate);
    }
}
