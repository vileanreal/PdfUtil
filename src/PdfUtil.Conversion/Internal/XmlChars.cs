using System.Text;

namespace PdfUtil.Conversion.Internal;

/// <summary>Characters XML 1.0 allows: everything else (C0 and C1 controls, lone surrogates, U+FFFE/FFFF) becomes U+FFFD.</summary>
internal static class XmlChars
{
    public static bool IsAllowed(char c) =>
        c is '\t' or '\n' or '\r'
        || (c >= 0x20 && c < 0x7F)
        || (c >= 0xA0 && c <= 0xD7FF)
        || (c >= 0xE000 && c <= 0xFFFD);

    public static string Clean(string text)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var pair = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            if (pair)
            {
                sb?.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }

            if (IsAllowed(c))
            {
                sb?.Append(c);
                continue;
            }

            sb ??= new StringBuilder(text, 0, i, text.Length);
            sb.Append('�');
        }

        return sb?.ToString() ?? text;
    }
}
