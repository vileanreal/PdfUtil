using PdfUtil.Internal.Objects;

namespace PdfUtil.Internal.Document;

/// <summary>Object-graph utilities: reachability, deep copy, structural equality, signature detection.</summary>
internal static class ObjectGraph
{
    /// <summary>
    /// Object numbers reachable from the trailer's /Root and /Info (not /Encrypt). Unreachable objects such as the
    /// linearization dictionary, hint streams, old cross-reference streams and object-stream containers are left
    /// out. Indirect /Length values are not followed because the writer emits direct lengths.
    /// <paramref name="lookup"/> replaces <see cref="PdfDocument.GetObject"/> (to traverse a document with edits applied).
    /// </summary>
    public static SortedSet<int> FindReachable(PdfDocument doc, CancellationToken ct, out bool hasSignatures, Action<int>? onProgress = null, Func<int, PdfObject?>? lookup = null, bool includeInfo = true)
    {
        hasSignatures = false;
        lookup ??= doc.GetObject;
        var reachable = new SortedSet<int>();
        var stack = new Stack<PdfObject>();
        foreach (var key in includeInfo ? new[] { PdfName.Root, PdfName.Info } : new[] { PdfName.Root })
        {
            if (doc.Trailer[key] is { } o)
            {
                stack.Push(o);
            }
        }

        var steps = 0L;
        while (stack.Count > 0)
        {
            if ((++steps & 0xFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                onProgress?.Invoke(reachable.Count);
            }

            switch (stack.Pop())
            {
                case PdfReference r:
                    if (reachable.Contains(r.Id.Number))
                    {
                        break;
                    }

                    var target = lookup(r.Id.Number);
                    if (target is null)
                    {
                        break;
                    }

                    if (target is PdfStream ts && (ts.Dictionary[PdfName.Type] as PdfName)?.Value is "XRef" or "ObjStm")
                    {
                        break;
                    }

                    reachable.Add(r.Id.Number);
                    stack.Push(target);
                    break;
                case PdfArray a:
                    foreach (var item in a.Items)
                    {
                        if (item is PdfReference or PdfArray or PdfDictionary)
                        {
                            stack.Push(item);
                        }
                    }

                    break;
                case PdfDictionary d:
                    if (SignatureHelper.IsSignatureDictionary(d))
                    {
                        hasSignatures = true;
                    }

                    foreach (var e in d.Entries)
                    {
                        if (e.Value is PdfReference or PdfArray or PdfDictionary)
                        {
                            stack.Push(e.Value);
                        }
                    }

                    break;
                case PdfStream s:
                    foreach (var e in s.Dictionary.Entries)
                    {
                        if (e.Key.Value != "Length" && e.Value is PdfReference or PdfArray or PdfDictionary)
                        {
                            stack.Push(e.Value);
                        }
                    }

                    break;
            }
        }

        return reachable;
    }

    /// <summary>Whether the AcroForm declares signatures or contains signed signature fields.</summary>
    public static bool HasSignedFields(PdfDocument doc, int maxDepth)
    {
        var acroForm = doc.ResolveDictionary(doc.Catalog?["AcroForm"]);
        if (acroForm is null)
        {
            return false;
        }

        if (doc.Resolve(acroForm["SigFlags"]) is PdfInteger flags && (flags.Value & 1) != 0)
        {
            return true;
        }

        var visited = new HashSet<int>();
        var stack = new Stack<(PdfObject, int)>();
        if (doc.Resolve(acroForm["Fields"]) is PdfArray fields)
        {
            foreach (var f in fields.Items)
            {
                stack.Push((f, 0));
            }
        }

        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            if (depth > maxDepth || (node is PdfReference r && !visited.Add(r.Id.Number)))
            {
                continue;
            }

            if (doc.ResolveDictionary(node) is not { } field)
            {
                continue;
            }

            if ((doc.Resolve(field["FT"]) as PdfName)?.Value == "Sig" && doc.ResolveDictionary(field["V"]) is { } v && v.ContainsKey("Contents"))
            {
                return true;
            }

            if (doc.Resolve(field["Kids"]) is PdfArray kids)
            {
                foreach (var k in kids.Items)
                {
                    stack.Push((k, depth + 1));
                }
            }
        }

        return false;
    }

    /// <summary>Deep copy in which every string is replaced by <paramref name="transform"/>(bytes), except signature /Contents.</summary>
    public static PdfObject MapStrings(PdfObject obj, Func<byte[], PdfString> transform, int depth, int maxDepth)
    {
        if (depth > maxDepth)
        {
            throw new PdfLimitExceededException("Maximum object nesting depth exceeded.");
        }

        switch (obj)
        {
            case PdfString s:
                return transform(s.Value);
            case PdfArray a:
            {
                var copy = new PdfArray();
                foreach (var item in a.Items)
                {
                    copy.Add(MapStrings(item, transform, depth + 1, maxDepth));
                }

                return copy;
            }

            case PdfDictionary d:
            {
                var isSignature = SignatureHelper.IsSignatureDictionary(d);
                var copy = new PdfDictionary();
                foreach (var e in d.Entries)
                {
                    copy[e.Key] = isSignature && e.Key.Value == "Contents" && e.Value is PdfString
                        ? e.Value
                        : MapStrings(e.Value, transform, depth + 1, maxDepth);
                }

                return copy;
            }

            default:
                return obj;
        }
    }

    /// <summary>A direct deep copy of <paramref name="obj"/> with every reference resolved (for copying the /Encrypt dictionary).</summary>
    public static PdfObject ResolveDeep(PdfObject? obj, Func<PdfObject?, PdfObject?> resolve, int depth = 0)
    {
        if (depth > 64)
        {
            throw new PdfLimitExceededException("The object is nested too deeply to copy.");
        }

        switch (resolve(obj))
        {
            case PdfArray a:
                return new PdfArray(a.Items.Select(i => ResolveDeep(i, resolve, depth + 1)));
            case PdfDictionary d:
                var copy = new PdfDictionary();
                foreach (var e in d.Entries)
                {
                    copy[e.Key] = ResolveDeep(e.Value, resolve, depth + 1);
                }

                return copy;
            case null:
            case PdfStream:
                return PdfNull.Instance;
            case var other:
                return other;
        }
    }

    /// <summary>Structural equality. <paramref name="ignoreKey"/> skips dictionary keys at the top level only.</summary>
    public static bool DeepEquals(PdfObject? a, PdfObject? b, string? ignoreKey = null, int depth = 0)
    {
        if (depth > 512)
        {
            return false;
        }

        switch (a)
        {
            case null:
                return b is null;
            case PdfNull:
                return b is PdfNull;
            case PdfBoolean ab:
                return b is PdfBoolean bb && ab.Value == bb.Value;
            case PdfInteger ai:
                return (b is PdfInteger bi && ai.Value == bi.Value) || (b is PdfReal br && br.Value == ai.Value);
            case PdfReal ar:
                return b switch
                {
                    PdfReal r2 => Math.Abs(ar.Value - r2.Value) <= 1e-9 * Math.Max(1, Math.Abs(ar.Value)),
                    PdfInteger i2 => ar.Value == i2.Value,
                    _ => false,
                };
            case PdfName an:
                return b is PdfName bn && an.Equals(bn);
            case PdfString @as:
                return b is PdfString bs && @as.Value.AsSpan().SequenceEqual(bs.Value);
            case PdfReference ar2:
                return b is PdfReference br2 && ar2.Id == br2.Id;
            case PdfArray aa:
                if (b is not PdfArray ba || aa.Count != ba.Count)
                {
                    return false;
                }

                for (var i = 0; i < aa.Count; i++)
                {
                    if (!DeepEquals(aa[i], ba[i], null, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            case PdfDictionary ad:
                if (b is not PdfDictionary bd)
                {
                    return false;
                }

                var keysA = ad.Keys.Where(k => k.Value != ignoreKey).ToHashSet();
                var keysB = bd.Keys.Where(k => k.Value != ignoreKey).ToHashSet();
                if (!keysA.SetEquals(keysB))
                {
                    return false;
                }

                foreach (var k in keysA)
                {
                    if (!DeepEquals(ad[k], bd[k], null, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            case PdfStream ast:
                return b is PdfStream bst && DeepEquals(ast.Dictionary, bst.Dictionary, ignoreKey, depth + 1);
            default:
                return false;
        }
    }
}
