using System.Globalization;
using System.IO.Compression;
using System.Text;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Objects;

namespace PdfUtil.Internal.Writing;

/// <summary>
/// Serialises a complete file for maximum reader compatibility: header plus binary comment, plain indirect objects
/// only (no object streams, no cross-reference stream), one classic cross-reference table with exactly 20-byte
/// entries, then <c>trailer</c>, <c>startxref</c> and <c>%%EOF</c>.
/// </summary>
internal sealed class PdfWriter : IDisposable
{
    private readonly Stream _output;
    private readonly BufferedStream _buffer;
    private readonly Dictionary<int, (long Offset, int Generation)> _offsets = new();
    private readonly Dictionary<int, (int Stream, int Index)> _compressed = new();
    private readonly List<ReservedSpace> _reserved = new();
    private readonly int _maxDepth;
    private long _position;

    /// <param name="output">The destination.</param>
    /// <param name="maxDepth">Maximum nesting depth of written objects.</param>
    /// <param name="startPosition">File offset of the first byte written (non-zero when appending an incremental update).</param>
    public PdfWriter(Stream output, int maxDepth, long startPosition = 0)
    {
        _output = output;
        _buffer = new BufferedStream(output, 64 * 1024);
        _maxDepth = maxDepth;
        _position = startPosition;
    }

    public long Position => _position;

    public IReadOnlyDictionary<int, (long Offset, int Generation)> Offsets => _offsets;

    /// <summary>Objects written inside object streams: number → (object stream number, index).</summary>
    public IReadOnlyDictionary<int, (int Stream, int Index)> Compressed => _compressed;

    /// <summary>Placeholders written so far, with their file offsets.</summary>
    public IReadOnlyList<ReservedSpace> Reserved => _reserved;

    /// <summary>The serialized form of a direct object (used for object stream content).</summary>
    public static byte[] Serialize(PdfObject obj, int maxDepth)
    {
        using var ms = new MemoryStream();
        using (var writer = new PdfWriter(ms, maxDepth))
        {
            writer.WriteObject(obj, 0);
        }

        return ms.ToArray();
    }

    /// <summary>Records that an object was written inside an object stream.</summary>
    public void RecordCompressed(int number, int streamNumber, int index) => _compressed[number] = (streamNumber, index);

    /// <summary>Writes an end-of-line if the existing data (when appending) does not end with one.</summary>
    public void WriteEol() => WriteAscii("\n");

    public void WriteHeader(PdfVersion version)
    {
        WriteAscii("%PDF-" + version + "\n");
        // Binary comment: four bytes above 127 so that transfer tools treat the file as binary.
        WriteBytes(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });
    }

    /// <summary>Writes "<c>n g obj ... endobj</c>". For streams, <paramref name="streamData"/> is written as-is and <c>/Length</c> must already match.</summary>
    public void WriteIndirectObject(ObjectId id, PdfObject obj, byte[]? streamData)
    {
        if (_offsets.ContainsKey(id.Number))
        {
            throw new InvalidOperationException($"Object {id.Number} written twice.");
        }

        _offsets[id.Number] = (_position, id.Generation);
        WriteAscii(id.Number.ToString(CultureInfo.InvariantCulture) + " " + id.Generation.ToString(CultureInfo.InvariantCulture) + " obj\n");
        if (obj is PdfStream stream)
        {
            var data = streamData ?? throw new ArgumentNullException(nameof(streamData));
            if (stream.Dictionary[PdfName.Length] is not PdfInteger len || len.Value != data.Length)
            {
                throw new InvalidOperationException("Stream /Length does not match the data.");
            }

            WriteObject(stream.Dictionary, 0);
            WriteAscii("\nstream\n");
            WriteBytes(data);
            WriteAscii("\nendstream\nendobj\n");
        }
        else
        {
            WriteObject(obj, 0);
            WriteAscii("\nendobj\n");
        }
    }

    /// <summary>Writes the cross-reference table and trailer. <paramref name="trailer"/> must not contain /Size, /Prev or /XRefStm; /Size is computed.</summary>
    public void WriteXrefAndTrailer(PdfDictionary trailer)
    {
        var size = _offsets.Count == 0 ? 1 : _offsets.Keys.Max() + 1;
        var xrefOffset = _position;
        var sb = new StringBuilder(64 + (size * 20));
        sb.Append("xref\n0 ").Append(size.ToString(CultureInfo.InvariantCulture)).Append('\n');

        // Free entries form a linked list starting at object 0.
        var free = new List<int>();
        for (var i = 1; i < size; i++)
        {
            if (!_offsets.ContainsKey(i))
            {
                free.Add(i);
            }
        }

        var nextFree = new Dictionary<int, int>();
        var previous = 0;
        foreach (var f in free)
        {
            nextFree[previous] = f;
            previous = f;
        }

        nextFree[previous] = 0;
        for (var i = 0; i < size; i++)
        {
            if (i != 0 && _offsets.TryGetValue(i, out var e))
            {
                sb.Append(e.Offset.ToString("D10", CultureInfo.InvariantCulture)).Append(' ')
                  .Append(e.Generation.ToString("D5", CultureInfo.InvariantCulture)).Append(" n\r\n");
            }
            else
            {
                var next = nextFree.TryGetValue(i, out var nf) ? nf : 0;
                var gen = i == 0 ? 65535 : 0;
                sb.Append(next.ToString("D10", CultureInfo.InvariantCulture)).Append(' ')
                  .Append(gen.ToString("D5", CultureInfo.InvariantCulture)).Append(" f\r\n");
            }

            if (sb.Length > 1 << 20)
            {
                WriteAscii(sb.ToString());
                sb.Clear();
            }
        }

        WriteAscii(sb.ToString());

        var full = new PdfDictionary { [PdfName.Size] = new PdfInteger(size) };
        foreach (var e in trailer.Entries)
        {
            if (e.Key.Value is not ("Size" or "Prev" or "XRefStm"))
            {
                full[e.Key] = e.Value;
            }
        }

        WriteAscii("trailer\n");
        WriteObject(full, 0);
        WriteAscii("\nstartxref\n" + xrefOffset.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");
    }

    /// <summary>
    /// Writes the cross-reference section of an incremental update: one subsection per run of consecutive object
    /// numbers written by this writer, then <paramref name="trailer"/> exactly as given (it must contain /Size and /Prev).
    /// </summary>
    public void WriteIncrementalXrefTable(PdfDictionary trailer)
    {
        var xrefOffset = _position;
        var sb = new StringBuilder("xref\n");
        foreach (var run in Runs(_offsets.Keys))
        {
            sb.Append(run.Start.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(run.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (var n = run.Start; n < run.Start + run.Count; n++)
            {
                var (offset, generation) = _offsets[n];
                sb.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(' ').Append(generation.ToString("D5", CultureInfo.InvariantCulture)).Append(" n\r\n");
            }
        }

        WriteAscii(sb.ToString());
        WriteAscii("trailer\n");
        WriteObject(trailer, 0);
        WriteAscii("\nstartxref\n" + xrefOffset.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");
    }

    /// <summary>
    /// Writes a cross-reference stream as object <paramref name="xrefNumber"/>, then <c>startxref</c> and <c>%%EOF</c>.
    /// With <paramref name="full"/>, it covers every object number below <paramref name="size"/> (free entries for the
    /// gaps); otherwise (incremental update) only the objects written by this writer. <paramref name="trailer"/> holds
    /// the trailer entries (/Root, /Info, /ID, /Encrypt, /Prev); /Size, /W, /Index and /Filter are added. The stream is
    /// never encrypted.
    /// </summary>
    public void WriteXrefStream(int xrefNumber, int size, PdfDictionary trailer, bool full)
    {
        var xrefOffset = _position;
        var entries = new SortedDictionary<int, (int Type, long Field2, int Field3)>();
        foreach (var (n, (offset, generation)) in _offsets)
        {
            entries[n] = (1, offset, generation);
        }

        foreach (var (n, (stream, index)) in _compressed)
        {
            entries[n] = (2, stream, index);
        }

        entries[xrefNumber] = (1, xrefOffset, 0);
        var numbers = full ? Enumerable.Range(0, size).ToList() : entries.Keys.ToList();
        var maxField2 = entries.Values.Select(e => e.Field2).DefaultIfEmpty(0).Max();
        var w2 = 1;
        while (w2 < 8 && maxField2 >= 1L << (8 * w2))
        {
            w2++;
        }

        var row = 1 + w2 + 2;
        var data = new byte[numbers.Count * row];
        for (var i = 0; i < numbers.Count; i++)
        {
            var n = numbers[i];
            var (type, field2, field3) = entries.TryGetValue(n, out var e) ? e : (0, 0L, n == 0 ? 65535 : 0);
            var at = i * row;
            data[at] = (byte)type;
            for (var b = 0; b < w2; b++)
            {
                data[at + 1 + b] = (byte)(field2 >> (8 * (w2 - 1 - b)));
            }

            data[at + 1 + w2] = (byte)(field3 >> 8);
            data[at + 2 + w2] = (byte)field3;
        }

        var dict = new PdfDictionary
        {
            [PdfName.Type] = PdfName.Get("XRef"),
            [PdfName.Size] = new PdfInteger(size),
            ["W"] = new PdfArray(new PdfObject[] { new PdfInteger(1), new PdfInteger(w2), new PdfInteger(2) }),
        };
        if (!full)
        {
            var index = new PdfArray();
            foreach (var run in Runs(numbers))
            {
                index.Add(new PdfInteger(run.Start));
                index.Add(new PdfInteger(run.Count));
            }

            dict["Index"] = index;
        }

        foreach (var e in trailer.Entries)
        {
            if (e.Key.Value is not ("Size" or "XRefStm" or "W" or "Index" or "Type" or "Filter" or "DecodeParms" or "Length"))
            {
                dict[e.Key] = e.Value;
            }
        }

        var compressed = Deflate(data);
        dict[PdfName.Filter] = PdfName.FlateDecode;
        dict[PdfName.Length] = new PdfInteger(compressed.Length);
        WriteIndirectObject(new ObjectId(xrefNumber, 0), new PdfStream(dict, compressed), compressed);
        WriteAscii("startxref\n" + xrefOffset.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");
    }

    public static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static IEnumerable<(int Start, int Count)> Runs(IEnumerable<int> numbers)
    {
        var sorted = numbers.Distinct().OrderBy(n => n).ToList();
        var i = 0;
        while (i < sorted.Count)
        {
            var start = sorted[i];
            var count = 1;
            while (i + count < sorted.Count && sorted[i + count] == start + count)
            {
                count++;
            }

            yield return (start, count);
            i += count;
        }
    }

    public void Flush()
    {
        _buffer.Flush();
        _output.Flush();
    }

    private void WriteObject(PdfObject obj, int depth)
    {
        if (depth > _maxDepth)
        {
            throw new PdfLimitExceededException("Maximum object nesting depth exceeded while writing.");
        }

        switch (obj)
        {
            case PdfNull:
                WriteAscii("null");
                break;
            case PdfBoolean b:
                WriteAscii(b.Value ? "true" : "false");
                break;
            case PdfInteger i:
                WriteAscii(i.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case PdfReal r:
                WriteAscii(r.Raw is { } raw && IsSafeNumber(raw) ? raw : PdfReal.Format(r.Value));
                break;
            case PdfName n:
                WriteName(n);
                break;
            case PdfString s:
                WriteString(s);
                break;
            case PdfReference reference:
                WriteAscii(reference.Id.Number.ToString(CultureInfo.InvariantCulture) + " " + reference.Id.Generation.ToString(CultureInfo.InvariantCulture) + " R");
                break;
            case PdfArray a:
                WriteByte((byte)'[');
                for (var k = 0; k < a.Count; k++)
                {
                    if (k > 0)
                    {
                        WriteByte((byte)' ');
                    }

                    WriteObject(a[k], depth + 1);
                }

                WriteByte((byte)']');
                break;
            case PdfDictionary d:
                WriteAscii("<<");
                foreach (var e in d.Entries)
                {
                    WriteName(e.Key);
                    WriteByte((byte)' ');
                    WriteObject(e.Value, depth + 1);
                    WriteByte((byte)' ');
                }

                WriteAscii(">>");
                break;
            case ReservedSpace reserved:
                reserved.Offset = _position;
                WriteAscii(reserved.Placeholder);
                _reserved.Add(reserved);
                break;
            case PdfStream:
                throw new InvalidOperationException("A stream cannot be written as a direct object.");
            default:
                throw new InvalidOperationException("Unknown object type.");
        }
    }

    private static bool IsSafeNumber(string raw)
    {
        // Only re-emit raw real tokens that are well-formed PDF numbers.
        var seenDot = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c is '+' or '-' && i == 0)
            {
                continue;
            }

            if (c == '.' && !seenDot)
            {
                seenDot = true;
                continue;
            }

            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return raw.Length > 0 && raw != "." && raw != "-" && raw != "+";
    }

    private void WriteName(PdfName name)
    {
        WriteByte((byte)'/');
        foreach (var ch in name.Value)
        {
            var b = (byte)ch;
            if (b < 0x21 || b > 0x7E || b == '#' || PdfParsingChars.IsDelimiter(b))
            {
                WriteAscii("#" + b.ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                WriteByte(b);
            }
        }
    }

    private void WriteString(PdfString s)
    {
        if (s.IsHex)
        {
            WriteByte((byte)'<');
            WriteAscii(Convert.ToHexString(s.Value));
            WriteByte((byte)'>');
            return;
        }

        var bytes = new List<byte>(s.Value.Length + 2) { (byte)'(' };
        foreach (var b in s.Value)
        {
            switch (b)
            {
                case (byte)'(':
                case (byte)')':
                case (byte)'\\':
                    bytes.Add((byte)'\\');
                    bytes.Add(b);
                    break;
                case (byte)'\r':
                    bytes.Add((byte)'\\');
                    bytes.Add((byte)'r');
                    break;
                case (byte)'\n':
                    bytes.Add((byte)'\\');
                    bytes.Add((byte)'n');
                    break;
                default:
                    bytes.Add(b);
                    break;
            }
        }

        bytes.Add((byte)')');
        WriteBytes(bytes.ToArray());
    }

    private void WriteAscii(string s) => WriteBytes(Encoding.ASCII.GetBytes(s));

    private void WriteByte(byte b)
    {
        _buffer.WriteByte(b);
        _position++;
    }

    private void WriteBytes(byte[] data)
    {
        _buffer.Write(data, 0, data.Length);
        _position += data.Length;
    }

    public void Dispose() => _buffer.Flush();
}

/// <summary>
/// A placeholder of fixed size, written now and filled in after the whole file is written (Phase 6 signing: the
/// signature value and its byte range). <see cref="Offset"/> is the file offset of its first byte once written.
/// </summary>
internal sealed class ReservedSpace : PdfObject
{
    private ReservedSpace(string placeholder, bool isHex, int capacity)
    {
        Placeholder = placeholder;
        IsHex = isHex;
        Capacity = capacity;
    }

    /// <summary>A hexadecimal string with room for <paramref name="byteCapacity"/> bytes, written as zeros.</summary>
    public static ReservedSpace HexString(int byteCapacity) => new("<" + new string('0', byteCapacity * 2) + ">", true, byteCapacity);

    /// <summary>
    /// Raw PDF syntax of at most <paramref name="width"/> bytes, written as <paramref name="initial"/> padded with
    /// spaces (the initial text must itself be valid syntax, such as <c>[0 0 0 0]</c>).
    /// </summary>
    public static ReservedSpace Text(string initial, int width) =>
        initial.Length > width ? throw new ArgumentException("The initial text is longer than the reserved width.", nameof(initial)) : new(initial.PadRight(width), false, width);

    public string Placeholder { get; }

    public bool IsHex { get; }

    /// <summary>Bytes of value that fit: the byte count for hex strings, the character width for text.</summary>
    public int Capacity { get; }

    /// <summary>Size in the file, delimiters included.</summary>
    public int Width => Placeholder.Length;

    public long Offset { get; set; } = -1;

    /// <summary>
    /// The bytes that replace the placeholder: hex strings are zero-padded to their capacity, text is padded with spaces.
    /// </summary>
    public byte[] Encode(byte[] value)
    {
        if (value.Length > Capacity)
        {
            throw new ArgumentException($"The value ({value.Length} bytes) does not fit in the reserved space ({Capacity} bytes).", nameof(value));
        }

        if (IsHex)
        {
            return Encoding.ASCII.GetBytes("<" + Convert.ToHexString(value) + new string('0', (Capacity - value.Length) * 2) + ">");
        }

        var text = new byte[Width];
        Array.Fill(text, (byte)' ');
        value.CopyTo(text, 0);
        return text;
    }
}

internal static class PdfParsingChars
{
    public static bool IsDelimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
}
