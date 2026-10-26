using System.Globalization;
using System.Text;

namespace PdfUtil.Conversion.Internal.Html;

/// <summary>
/// Writes markup that is valid HTML5 and well-formed XML at the same time: every element is closed (void elements
/// as <c>&lt;br/&gt;</c>), text and attributes are escaped, and characters XML does not allow are replaced by U+FFFD.
/// Counts elements against the output limit.
/// </summary>
internal sealed class HtmlBuilder
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.Ordinal) { "br", "hr", "img", "meta", "link", "col", "wbr" };

    private readonly StringBuilder _sb = new();
    private readonly Stack<string> _open = new();
    private readonly int _maxElements;
    private int _elements;

    public HtmlBuilder(int maxElements) => _maxElements = maxElements;

    public int Depth => _open.Count;

    public void Raw(string text) => _sb.Append(text);

    public void NewLine() => _sb.Append('\n');

    public void Open(string tag, params (string Name, string? Value)[] attributes)
    {
        Start(tag, attributes);
        _sb.Append('>');
        _open.Push(tag);
    }

    public void Close(string tag)
    {
        if (_open.Count == 0 || _open.Peek() != tag)
        {
            throw new InvalidOperationException($"Closing <{tag}> but <{(_open.Count > 0 ? _open.Peek() : "nothing")}> is open.");
        }

        _open.Pop();
        _sb.Append("</").Append(tag).Append('>');
    }

    public void Void(string tag, params (string Name, string? Value)[] attributes)
    {
        if (!VoidElements.Contains(tag))
        {
            throw new InvalidOperationException($"<{tag}> is not a void element.");
        }

        Start(tag, attributes);
        _sb.Append("/>");
    }

    /// <summary>An element with text content (or none: it is still written with a closing tag).</summary>
    public void Element(string tag, string? text, params (string Name, string? Value)[] attributes)
    {
        Open(tag, attributes);
        if (text is not null)
        {
            Text(text);
        }

        Close(tag);
    }

    public void Text(string text) => Escape(_sb, text, attribute: false);

    public override string ToString()
    {
        if (_open.Count > 0)
        {
            throw new InvalidOperationException($"<{_open.Peek()}> is not closed.");
        }

        return _sb.ToString();
    }

    private void Start(string tag, (string Name, string? Value)[] attributes)
    {
        if (++_elements > _maxElements)
        {
            throw new PdfLimitExceededException($"The output has more elements than the configured maximum of {_maxElements}.");
        }

        _sb.Append('<').Append(tag);
        foreach (var (name, value) in attributes)
        {
            if (value is null)
            {
                continue;
            }

            _sb.Append(' ').Append(name).Append("=\"");
            Escape(_sb, value, attribute: true);
            _sb.Append('"');
        }
    }

    public static string Escape(string text, bool attribute)
    {
        var sb = new StringBuilder(text.Length + 16);
        Escape(sb, text, attribute);
        return sb.ToString();
    }

    private static void Escape(StringBuilder sb, string text, bool attribute)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '&':
                    sb.Append("&amp;");
                    break;
                case '<':
                    sb.Append("&lt;");
                    break;
                case '>':
                    sb.Append("&gt;");
                    break;
                case '"' when attribute:
                    sb.Append("&quot;");
                    break;
                case '\n' or '\r' or '\t' when attribute:
                    sb.Append("&#").Append(((int)c).ToString(CultureInfo.InvariantCulture)).Append(';');
                    break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        sb.Append(c).Append(text[++i]);
                    }
                    else if (XmlChars.IsAllowed(c))
                    {
                        sb.Append(c);
                    }
                    else
                    {
                        sb.Append('�');
                    }

                    break;
            }
        }
    }

    /// <summary>A CSS number with up to two decimals.</summary>
    public static string Num(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>A CSS string for a font family name.</summary>
    public static string CssString(string value)
    {
        var sb = new StringBuilder("'");
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c) || c == ' ' || c == '-')
            {
                sb.Append(c);
            }
        }

        return sb.Append('\'').ToString();
    }
}
