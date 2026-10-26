using System.Globalization;
using System.Text;

namespace PdfUtil.Conversion.Internal.Html;

internal abstract class Node
{
    public Element? Parent { get; set; }
}

internal sealed class TextNode : Node
{
    public TextNode(string text) => Text = text;

    public string Text { get; set; }
}

internal sealed class Element : Node
{
    public Element(string name) => Name = name;

    /// <summary>Lower-case tag name.</summary>
    public string Name { get; }

    public List<KeyValuePair<string, string>> Attributes { get; } = new();

    public List<Node> Children { get; } = new();

    public string? Attr(string name)
    {
        foreach (var (key, value) in Attributes)
        {
            if (key == name)
            {
                return value;
            }
        }

        return null;
    }

    public IEnumerable<string> Classes => (Attr("class") ?? "").Split(new[] { ' ', '\t', '\n', '\r', '\f' }, StringSplitOptions.RemoveEmptyEntries);

    public void Append(Node node)
    {
        node.Parent = this;
        Children.Add(node);
    }

    public IEnumerable<Element> Descendants()
    {
        foreach (var child in Children.OfType<Element>())
        {
            yield return child;
            foreach (var d in child.Descendants())
            {
                yield return d;
            }
        }
    }

    public string TextContent => string.Concat(Children.Select(c => c is TextNode t ? t.Text : c is Element e ? e.TextContent : ""));

    public override string ToString() => "<" + Name + ">";
}

/// <summary>A parsed document: the <c>html</c> element with its implied <c>head</c> and <c>body</c>.</summary>
internal sealed class HtmlDom
{
    public required Element Root { get; init; }

    public required Element Head { get; init; }

    public required Element Body { get; init; }
}

/// <summary>
/// A tolerant HTML parser following the shape of the HTML5 algorithm: a tokenizer (character references with the
/// full named table, raw-text and RCDATA elements, comments, doctype) and a tree builder with the common recovery
/// rules: implied <c>html</c>, <c>head</c> and <c>body</c>; <c>p</c>, <c>li</c>, <c>dt</c>/<c>dd</c>, cells, rows
/// and table sections closed implicitly; implied <c>tbody</c> and <c>tr</c>; stray text in tables placed before the
/// table; and formatting elements (<c>b</c>, <c>i</c>, …) reopened after misnesting. Limits: element count and depth.
/// </summary>
internal sealed class HtmlParser
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.Ordinal) { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr", "keygen" };
    private static readonly HashSet<string> RawText = new(StringComparer.Ordinal) { "script", "style", "xmp", "iframe", "noembed", "noframes", "noscript" };
    private static readonly HashSet<string> RcData = new(StringComparer.Ordinal) { "title", "textarea" };
    private static readonly HashSet<string> HeadElements = new(StringComparer.Ordinal) { "title", "meta", "link", "style", "base", "script", "noscript", "template" };
    private static readonly HashSet<string> ClosesP = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "center", "details", "dialog", "dir", "div", "dl", "fieldset", "figcaption", "figure", "footer", "form",
        "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup", "hr", "li", "main", "menu", "nav", "ol", "p", "pre", "section", "summary", "table", "ul", "dd", "dt",
    };

    private static readonly HashSet<string> Formatting = new(StringComparer.Ordinal) { "a", "b", "big", "code", "em", "font", "i", "nobr", "s", "small", "strike", "strong", "tt", "u" };
    private static readonly HashSet<string> Headings = new(StringComparer.Ordinal) { "h1", "h2", "h3", "h4", "h5", "h6" };
    private static readonly HashSet<string> TableSections = new(StringComparer.Ordinal) { "thead", "tbody", "tfoot" };
    private static readonly HashSet<string> Scoping = new(StringComparer.Ordinal) { "html", "table", "td", "th", "caption", "marquee", "object", "template", "applet" };

    private readonly string _text;
    private readonly int _maxElements;
    private readonly int _maxDepth;
    private readonly List<Element> _open = new();
    private readonly List<Element> _formatting = new();
    private Element _html = null!;
    private Element _head = null!;
    private Element _body = null!;
    private bool _inBody;
    private int _elements;
    private int _pos;

    private HtmlParser(string text, int maxElements, int maxDepth)
    {
        _text = text;
        _maxElements = maxElements;
        _maxDepth = maxDepth;
    }

    public static HtmlDom Parse(string html, int maxElements, int maxDepth)
    {
        var parser = new HtmlParser(html, maxElements, maxDepth);
        parser.Run();
        return new HtmlDom { Root = parser._html, Head = parser._head, Body = parser._body };
    }

    /// <summary>Decodes HTML bytes: a byte order mark, then a <c>meta charset</c> in the first 1024 bytes, else UTF-8.</summary>
    public static string Decode(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        }

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        }

        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        }

        var head = Encoding.Latin1.GetString(data, 0, Math.Min(data.Length, 1024)).ToLowerInvariant();
        var at = head.IndexOf("charset", StringComparison.Ordinal);
        if (at >= 0)
        {
            var rest = head[(at + 7)..].TrimStart(' ', '=', '"', '\'');
            var name = new string(rest.TakeWhile(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
            switch (name)
            {
                case "iso-8859-1" or "latin1" or "windows-1252" or "cp1252" or "us-ascii" or "ascii":
                    return Windows1252(data);
                case "utf-16" or "utf-16le":
                    return Encoding.Unicode.GetString(data);
            }
        }

        return Encoding.UTF8.GetString(data);
    }

    private static readonly char[] C1 =
    {
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
        '\u0090', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ',
    };

    private static string Windows1252(byte[] data)
    {
        var chars = new char[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            chars[i] = data[i] is >= 0x80 and < 0xA0 ? C1[data[i] - 0x80] : (char)data[i];
        }

        return new string(chars);
    }

    // ------------------------------------------------------------------ tree building

    private Element Current => _open[^1];

    private void Run()
    {
        _html = new Element("html");
        _head = new Element("head");
        _body = new Element("body");
        _html.Append(_head);
        _html.Append(_body);
        _open.Add(_html);
        _open.Add(_head);
        while (_pos < _text.Length)
        {
            if (_text[_pos] == '<' && TryTag())
            {
                continue;
            }

            var next = _text.IndexOf('<', _pos + 1);
            var end = next < 0 ? _text.Length : next;
            AddText(DecodeText(_text[_pos..end], attribute: false));
            _pos = end;
        }
    }

    private void EnterBody()
    {
        if (!_inBody)
        {
            _inBody = true;
            _open.RemoveRange(1, _open.Count - 1);
            _open.Add(_body);
        }
    }

    private void AddText(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (!_inBody)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            EnterBody();
        }

        var parent = Current;
        if (parent.Name is "table" or "tbody" or "thead" or "tfoot" or "tr")
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            // Foster parenting: text that is not in a cell goes before the table.
            var table = _open.LastOrDefault(e => e.Name == "table");
            if (table?.Parent is { } host)
            {
                var node = new TextNode(text) { Parent = host };
                host.Children.Insert(host.Children.IndexOf(table), node);
                return;
            }
        }

        ReconstructFormatting();
        if (Current.Children.LastOrDefault() is TextNode last)
        {
            last.Text += text;
        }
        else
        {
            Current.Append(new TextNode(text));
        }
    }

    private Element Insert(string name, List<KeyValuePair<string, string>> attributes)
    {
        if (++_elements > _maxElements)
        {
            throw new PdfLimitExceededException($"The HTML has more elements than the configured maximum of {_maxElements}.");
        }

        if (_open.Count > _maxDepth)
        {
            throw new PdfLimitExceededException($"The HTML is nested deeper than the configured maximum of {_maxDepth}.");
        }

        var element = new Element(name);
        element.Attributes.AddRange(attributes);
        Current.Append(element);
        if (!VoidElements.Contains(name))
        {
            _open.Add(element);
        }

        return element;
    }

    private bool InScope(string name, HashSet<string>? extraScope = null)
    {
        for (var i = _open.Count - 1; i >= 0; i--)
        {
            if (_open[i].Name == name)
            {
                return true;
            }

            if (Scoping.Contains(_open[i].Name) || (extraScope?.Contains(_open[i].Name) ?? false))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Pops elements up to and including the nearest <paramref name="name"/>.</summary>
    private void PopTo(string name)
    {
        for (var i = _open.Count - 1; i >= 2; i--)
        {
            var element = _open[i];
            _open.RemoveAt(i);
            if (element.Name == name)
            {
                // Formatting does not leak out of table cells and rows.
                if (name is "td" or "th" or "tr" or "table" or "caption" or "tbody" or "thead" or "tfoot")
                {
                    _formatting.Clear();
                }

                return;
            }

            // A formatting element closed implicitly is reopened for the content that follows.
            if (Formatting.Contains(element.Name) && element.Name != "a" && !_formatting.Contains(element))
            {
                _formatting.Insert(0, element);
            }
        }
    }

    private void ReconstructFormatting()
    {
        if (_formatting.Count == 0)
        {
            return;
        }

        var pending = _formatting.ToList();
        _formatting.Clear();
        foreach (var element in pending)
        {
            if (_open.Count <= _maxDepth)
            {
                Insert(element.Name, element.Attributes);
            }
        }
    }

    private void CloseP()
    {
        if (InScope("p", new HashSet<string> { "button" }))
        {
            PopTo("p");
        }
    }

    private void StartTag(string name, List<KeyValuePair<string, string>> attributes, bool selfClosing)
    {
        if (name == "html")
        {
            foreach (var attribute in attributes.Where(a => _html.Attr(a.Key) is null))
            {
                _html.Attributes.Add(attribute);
            }

            return;
        }

        if (name == "head")
        {
            return;
        }

        if (!_inBody && HeadElements.Contains(name))
        {
            var element = new Element(name);
            element.Attributes.AddRange(attributes);
            _head.Append(element);
            if (RawText.Contains(name) || RcData.Contains(name))
            {
                element.Append(new TextNode(ReadRawText(name, RcData.Contains(name))));
            }

            return;
        }

        if (name == "body")
        {
            EnterBody();
            foreach (var attribute in attributes.Where(a => _body.Attr(a.Key) is null))
            {
                _body.Attributes.Add(attribute);
            }

            return;
        }

        EnterBody();
        if (Current.Name == "colgroup" && name != "col")
        {
            PopTo("colgroup");
        }

        if (ClosesP.Contains(name))
        {
            CloseP();
        }

        switch (name)
        {
            case "li":
                CloseListItem("li", new[] { "li" });
                break;
            case "dt" or "dd":
                CloseListItem(name, new[] { "dt", "dd" });
                break;
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6" when Headings.Contains(Current.Name):
                PopTo(Current.Name);
                break;
            case "td" or "th":
                CloseCell();
                if (Current.Name != "tr")
                {
                    EnsureTableSection();
                    if (Current.Name != "tr")
                    {
                        Insert("tr", new());
                    }
                }

                break;
            case "tr":
                CloseCell();
                if (InScope("tr", new HashSet<string> { "tbody", "thead", "tfoot" }))
                {
                    PopTo("tr");
                }

                EnsureTableSection();
                break;
            case "thead" or "tbody" or "tfoot":
                CloseCell();
                if (InScope("tr", TableSections))
                {
                    PopTo("tr");
                }

                foreach (var section in TableSections)
                {
                    if (Current.Name == section)
                    {
                        PopTo(section);
                    }
                }

                break;
            case "col" when Current.Name == "table":
                Insert("colgroup", new());
                break;
            case "caption" or "colgroup":
                break;
            case "a" when _open.Any(e => e.Name == "a"):
                PopTo("a");
                break;
            case "option" when Current.Name == "option":
                PopTo("option");
                break;
        }

        if (Current.Name is "table" or "tbody" or "thead" or "tfoot" or "tr" && name is not ("tr" or "td" or "th" or "thead" or "tbody" or "tfoot" or "caption" or "colgroup" or "col" or "style" or "script" or "template"))
        {
            // Content that is not table structure goes before the table.
            var table = _open.Last(e => e.Name == "table");
            if (table.Parent is { } host && !VoidElements.Contains(name))
            {
                var fostered = new Element(name) { Parent = host };
                fostered.Attributes.AddRange(attributes);
                host.Children.Insert(host.Children.IndexOf(table), fostered);
                if (++_elements > _maxElements)
                {
                    throw new PdfLimitExceededException($"The HTML has more elements than the configured maximum of {_maxElements}.");
                }

                _open.Add(fostered);
                return;
            }
        }

        if (Formatting.Contains(name) || name is "img" or "br" or "input")
        {
            ReconstructFormatting();
        }

        var inserted = Insert(name, attributes);
        if (RawText.Contains(name) || RcData.Contains(name))
        {
            inserted.Append(new TextNode(ReadRawText(name, RcData.Contains(name))));
            _open.Remove(inserted);
        }
        else if (selfClosing && !VoidElements.Contains(name))
        {
            _open.Remove(inserted);
        }
    }

    private void CloseListItem(string name, string[] closers)
    {
        for (var i = _open.Count - 1; i >= 2; i--)
        {
            var element = _open[i];
            if (closers.Contains(element.Name))
            {
                PopTo(element.Name);
                return;
            }

            if (element.Name is "ul" or "ol" or "dl" or "table" || (ClosesP.Contains(element.Name) && element.Name is not ("address" or "div" or "p")))
            {
                return;
            }
        }
    }

    private void CloseCell()
    {
        if (InScope("td", new HashSet<string> { "tr" }))
        {
            PopTo("td");
        }
        else if (InScope("th", new HashSet<string> { "tr" }))
        {
            PopTo("th");
        }
    }

    private void EnsureTableSection()
    {
        if (Current.Name == "table")
        {
            Insert("tbody", new());
        }
    }

    private void EndTag(string name)
    {
        switch (name)
        {
            case "html" or "head":
                return;
            case "body":
                return;
            case "p" when !InScope("p", new HashSet<string> { "button" }):
                EnterBody();
                Insert("p", new());
                PopTo("p");
                return;
            case "br":
                StartTag("br", new(), false);
                return;
        }

        if (!_inBody)
        {
            return;
        }

        var index = _open.FindLastIndex(e => e.Name == name);
        if (index < 2)
        {
            return;
        }

        // Do not close past a table or cell boundary for a formatting end tag.
        if (_open.Skip(index + 1).Any(e => Scoping.Contains(e.Name)) && Formatting.Contains(name))
        {
            return;
        }

        var reopen = _open.Skip(index + 1).Where(e => Formatting.Contains(e.Name) && e.Name != "a").ToList();
        _open.RemoveRange(index, _open.Count - index);
        _formatting.RemoveAll(e => e.Name == name);

        // Formatting closed implicitly (misnested <b><i>…</b>…</i>, or <p><b>…</p>) continues after the end tag,
        // except across table cells.
        if (name is "td" or "th" or "tr" or "table" or "caption" or "tbody" or "thead" or "tfoot")
        {
            _formatting.Clear();
        }
        else
        {
            _formatting.InsertRange(0, reopen);
        }
    }

    // ------------------------------------------------------------------ tokenizer

    private bool TryTag()
    {
        var p = _pos + 1;
        if (p >= _text.Length)
        {
            return false;
        }

        var c = _text[p];
        if (c == '!')
        {
            if (string.CompareOrdinal(_text, p, "!--", 0, 3) == 0)
            {
                var end = _text.IndexOf("-->", p + 3, StringComparison.Ordinal);
                _pos = end < 0 ? _text.Length : end + 3;
                return true;
            }

            var close = _text.IndexOf('>', p);
            _pos = close < 0 ? _text.Length : close + 1;
            return true;
        }

        if (c == '?')
        {
            var close = _text.IndexOf('>', p);
            _pos = close < 0 ? _text.Length : close + 1;
            return true;
        }

        var isEnd = c == '/';
        if (isEnd)
        {
            p++;
        }

        if (p >= _text.Length || !char.IsAsciiLetter(_text[p]))
        {
            if (isEnd && p < _text.Length && _text[p] == '>')
            {
                _pos = p + 1;
                return true;
            }

            return false;
        }

        var nameStart = p;
        while (p < _text.Length && !char.IsWhiteSpace(_text[p]) && _text[p] is not ('/' or '>'))
        {
            p++;
        }

        var name = _text[nameStart..p].ToLowerInvariant();
        var attributes = new List<KeyValuePair<string, string>>();
        var selfClosing = false;
        while (p < _text.Length)
        {
            while (p < _text.Length && char.IsWhiteSpace(_text[p]))
            {
                p++;
            }

            if (p >= _text.Length)
            {
                break;
            }

            if (_text[p] == '>')
            {
                p++;
                break;
            }

            if (_text[p] == '/')
            {
                selfClosing = p + 1 < _text.Length && _text[p + 1] == '>';
                p++;
                continue;
            }

            var attrStart = p;
            p++;
            while (p < _text.Length && !char.IsWhiteSpace(_text[p]) && _text[p] is not ('/' or '>' or '='))
            {
                p++;
            }

            var attrName = _text[attrStart..p].ToLowerInvariant();
            while (p < _text.Length && char.IsWhiteSpace(_text[p]))
            {
                p++;
            }

            var value = "";
            if (p < _text.Length && _text[p] == '=')
            {
                p++;
                while (p < _text.Length && char.IsWhiteSpace(_text[p]))
                {
                    p++;
                }

                if (p < _text.Length && _text[p] is '"' or '\'')
                {
                    var quote = _text[p];
                    var close = _text.IndexOf(quote, p + 1);
                    var end = close < 0 ? _text.Length : close;
                    value = _text[(p + 1)..end];
                    p = Math.Min(_text.Length, end + 1);
                }
                else
                {
                    var valueStart = p;
                    while (p < _text.Length && !char.IsWhiteSpace(_text[p]) && _text[p] != '>')
                    {
                        p++;
                    }

                    value = _text[valueStart..p];
                }

                value = DecodeText(value, attribute: true);
            }

            if (!isEnd && attributes.All(a => a.Key != attrName))
            {
                attributes.Add(new(attrName, value));
            }
        }

        _pos = p;
        if (isEnd)
        {
            EndTag(name);
        }
        else
        {
            StartTag(name, attributes, selfClosing);
        }

        return true;
    }

    /// <summary>Reads the content of a raw-text or RCDATA element up to its end tag (case-insensitive).</summary>
    private string ReadRawText(string name, bool decode)
    {
        var end = _text.IndexOf("</" + name, _pos, StringComparison.OrdinalIgnoreCase);
        var content = end < 0 ? _text[_pos..] : _text[_pos..end];
        if (end < 0)
        {
            _pos = _text.Length;
        }
        else
        {
            var close = _text.IndexOf('>', end);
            _pos = close < 0 ? _text.Length : close + 1;
        }

        return decode ? DecodeText(content, attribute: false) : content;
    }

    /// <summary>Replaces character references; in attributes, legacy names without a semicolon before '=' or an alphanumeric are left alone.</summary>
    internal static string DecodeText(string text, bool attribute)
    {
        var amp = text.IndexOf('&', StringComparison.Ordinal);
        if (amp < 0)
        {
            return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\0', '�');
        }

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\r')
            {
                sb.Append('\n');
                i += i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                continue;
            }

            if (c != '&')
            {
                sb.Append(c == '\0' ? '�' : c);
                i++;
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '#')
            {
                var hex = i + 2 < text.Length && text[i + 2] is 'x' or 'X';
                var start = i + (hex ? 3 : 2);
                var end = start;
                while (end < text.Length && end - start < 8 && (hex ? char.IsAsciiHexDigit(text[end]) : char.IsAsciiDigit(text[end])))
                {
                    end++;
                }

                if (end > start)
                {
                    var value = long.Parse(text[start..end], hex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture);
                    sb.Append(NumericReference(value));
                    i = end < text.Length && text[end] == ';' ? end + 1 : end;
                    continue;
                }

                sb.Append('&');
                i++;
                continue;
            }

            // Named reference: the longest name in the table that matches.
            var matched = false;
            for (var length = Math.Min(HtmlEntities.MaxNameLength, text.Length - i - 1); length >= 2; length--)
            {
                var name = text.Substring(i + 1, length);
                if (!HtmlEntities.TryGet(name, out var value))
                {
                    continue;
                }

                var after = i + 1 + length;
                if (!name.EndsWith(';') && attribute && after < text.Length && (text[after] == '=' || char.IsAsciiLetterOrDigit(text[after])))
                {
                    break;
                }

                sb.Append(value);
                i = after;
                matched = true;
                break;
            }

            if (!matched)
            {
                sb.Append('&');
                i++;
            }
        }

        return sb.ToString();
    }

    private static string NumericReference(long value)
    {
        if (value is >= 0x80 and <= 0x9F)
        {
            return C1[value - 0x80].ToString();
        }

        if (value == 0 || value > 0x10FFFF || value is >= 0xD800 and <= 0xDFFF)
        {
            return "�";
        }

        return char.ConvertFromUtf32((int)value);
    }
}
