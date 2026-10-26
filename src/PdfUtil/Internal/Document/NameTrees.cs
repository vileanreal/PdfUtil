using PdfUtil.Internal.Objects;

namespace PdfUtil.Internal.Document;

/// <summary>Reads and builds name trees (ISO 32000-1 §7.9.6) and reads number trees (§7.9.7), with cycle and size limits.</summary>
internal static class NameTree
{
    /// <summary>Orders name tree keys as the specification requires: lexically, by bytes.</summary>
    public static readonly IComparer<byte[]> KeyComparer = Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b));

    /// <summary>
    /// All (key, value) pairs of the name tree at <paramref name="root"/>, in tree order. Intermediate <c>/Kids</c> nodes
    /// are followed; values are returned as stored (usually references). Malformed nodes are skipped.
    /// </summary>
    public static List<(byte[] Key, PdfObject Value)> Read(PdfDocument doc, PdfObject? root, int maxDepth, int maxEntries) =>
        ReadTree(doc, root, "Names", maxDepth, maxEntries, key => key is PdfString s ? s.Value : null);

    /// <summary>A flat name tree root holding <paramref name="entries"/>, sorted by key; for equal keys, the last one wins.</summary>
    public static PdfDictionary Build(IEnumerable<(byte[] Key, PdfObject Value)> entries)
    {
        var sorted = new SortedDictionary<byte[], PdfObject>(KeyComparer);
        foreach (var (key, value) in entries)
        {
            sorted[key] = value;
        }

        var names = new PdfArray();
        foreach (var (key, value) in sorted)
        {
            names.Add(new PdfString(key));
            names.Add(value);
        }

        return new PdfDictionary { ["Names"] = names };
    }

    internal static List<(TKey Key, PdfObject Value)> ReadTree<TKey>(PdfDocument doc, PdfObject? root, string leafKey, int maxDepth, int maxEntries, Func<PdfObject?, TKey?> readKey)
    {
        var result = new List<(TKey, PdfObject)>();
        var visited = new HashSet<int>();
        var stack = new Stack<(PdfObject Node, int Depth)>();
        if (root is not null)
        {
            stack.Push((root, 0));
        }

        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (depth > maxDepth || (node is PdfReference r && !visited.Add(r.Id.Number)) || doc.ResolveDictionary(node) is not { } dict)
            {
                continue;
            }

            if (doc.Resolve(dict[leafKey]) is PdfArray pairs)
            {
                for (var i = 0; i + 1 < pairs.Count; i += 2)
                {
                    if (readKey(doc.Resolve(pairs[i])) is { } key)
                    {
                        if (result.Count >= maxEntries)
                        {
                            throw new PdfLimitExceededException("A name or number tree has more entries than the configured maximum object count.");
                        }

                        result.Add((key, pairs[i + 1]));
                    }
                }
            }

            if (doc.Resolve(dict["Kids"]) is PdfArray kids)
            {
                for (var i = kids.Count - 1; i >= 0; i--)
                {
                    stack.Push((kids[i], depth + 1));
                }
            }
        }

        return result;
    }
}

/// <summary>Reads number trees (ISO 32000-1 §7.9.7), such as page labels.</summary>
internal static class NumberTree
{
    public static List<(long Key, PdfObject Value)> Read(PdfDocument doc, PdfObject? root, int maxDepth, int maxEntries) =>
        NameTree.ReadTree<long?>(doc, root, "Nums", maxDepth, maxEntries, key => key is PdfInteger i ? i.Value : null)
            .Select(e => (e.Key!.Value, e.Value))
            .ToList();
}
