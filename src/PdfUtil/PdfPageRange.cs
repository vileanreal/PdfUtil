using System.Globalization;

namespace PdfUtil;

/// <summary>
/// A selection of pages written as comma-separated items: a page (<c>5</c>), a range (<c>1-3</c>), an open range to
/// the last page (<c>8-</c>) or from the first (<c>-4</c>), or <c>odd</c>, <c>even</c> and <c>last</c>. Whitespace is
/// ignored. Example: <c>"1-3, 5, 8-"</c>. Pages are numbered from 1.
/// </summary>
public sealed class PdfPageRange
{
    private readonly IReadOnlyList<(int? From, int? To, string? Keyword)> _items;

    private PdfPageRange(string text, IReadOnlyList<(int? From, int? To, string? Keyword)> items)
    {
        Text = text;
        _items = items;
    }

    /// <summary>Every page.</summary>
    public static PdfPageRange All { get; } = new("1-", new[] { ((int?)1, (int?)null, (string?)null) });

    /// <summary>The text the range was parsed from.</summary>
    public string Text { get; }

    /// <summary>Parses a page range. Throws <see cref="FormatException"/> with the position of the problem when the syntax is wrong.</summary>
    public static PdfPageRange Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(text, out var range, out var error) ? range! : throw new FormatException(error);
    }

    /// <summary>Parses a page range; returns <c>false</c> with a description of the problem when the syntax is wrong.</summary>
    public static bool TryParse(string? text, out PdfPageRange? range, out string? error)
    {
        range = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The page range is empty.";
            return false;
        }

        var items = new List<(int?, int?, string?)>();
        foreach (var raw in text.Split(','))
        {
            var item = raw.Trim().ToLowerInvariant();
            if (item.Length == 0)
            {
                error = $"The page range \"{text}\" has an empty item.";
                return false;
            }

            if (item is "odd" or "even" or "last")
            {
                items.Add((null, null, item));
                continue;
            }

            var dash = item.IndexOf('-');
            if (dash < 0)
            {
                if (!TryPage(item, out var page))
                {
                    error = $"\"{raw.Trim()}\" is not a page number.";
                    return false;
                }

                items.Add((page, page, null));
                continue;
            }

            var left = item[..dash].Trim();
            var right = item[(dash + 1)..].Trim();
            int? from = null, to = null;
            if (left.Length > 0)
            {
                if (!TryPage(left, out var f))
                {
                    error = $"\"{raw.Trim()}\" is not a page range.";
                    return false;
                }

                from = f;
            }

            if (right.Length > 0)
            {
                if (!TryPage(right, out var t))
                {
                    error = $"\"{raw.Trim()}\" is not a page range.";
                    return false;
                }

                to = t;
            }

            if (from is null && to is null)
            {
                error = $"\"{raw.Trim()}\" has no page numbers.";
                return false;
            }

            if (from > to)
            {
                error = $"\"{raw.Trim()}\" runs backwards.";
                return false;
            }

            items.Add((from ?? 1, to, null));
        }

        range = new PdfPageRange(text, items);
        return true;
    }

    private static bool TryPage(string s, out int page) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out page) && page >= 1;

    /// <summary>
    /// The selected page numbers for a document of <paramref name="pageCount"/> pages, in the order written and without
    /// duplicates. Throws <see cref="ArgumentException"/> when the range names a page beyond the last one.
    /// </summary>
    public IReadOnlyList<int> Resolve(int pageCount)
    {
        var result = new List<int>();
        var seen = new HashSet<int>();
        void Add(int page)
        {
            if (seen.Add(page))
            {
                result.Add(page);
            }
        }

        foreach (var (from, to, keyword) in _items)
        {
            switch (keyword)
            {
                case "odd":
                    for (var p = 1; p <= pageCount; p += 2)
                    {
                        Add(p);
                    }

                    continue;
                case "even":
                    for (var p = 2; p <= pageCount; p += 2)
                    {
                        Add(p);
                    }

                    continue;
                case "last":
                    if (pageCount > 0)
                    {
                        Add(pageCount);
                    }

                    continue;
            }

            var last = to ?? pageCount;
            if (from > pageCount || last > pageCount)
            {
                throw new ArgumentException($"The page range \"{Text}\" refers to page {Math.Max(from ?? 0, last)}, but the document has {pageCount} page(s).", nameof(pageCount));
            }

            for (var p = from!.Value; p <= last; p++)
            {
                Add(p);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public override string ToString() => Text;
}
