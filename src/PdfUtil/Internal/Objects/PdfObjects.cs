using System.Globalization;
using System.Text;

namespace PdfUtil.Internal.Objects;

/// <summary>Base of the PDF object model (ISO 32000-1 §7.3).</summary>
internal abstract class PdfObject
{
}

internal sealed class PdfNull : PdfObject
{
    public static readonly PdfNull Instance = new();

    private PdfNull() { }

    public override string ToString() => "null";
}

internal sealed class PdfBoolean : PdfObject
{
    public static readonly PdfBoolean True = new(true);
    public static readonly PdfBoolean False = new(false);

    private PdfBoolean(bool value) => Value = value;

    public bool Value { get; }

    public static PdfBoolean Get(bool value) => value ? True : False;

    public override string ToString() => Value ? "true" : "false";
}

internal sealed class PdfInteger : PdfObject
{
    public PdfInteger(long value) => Value = value;

    public long Value { get; }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

internal sealed class PdfReal : PdfObject
{
    /// <param name="value">Numeric value.</param>
    /// <param name="raw">Original token text, kept so that rewriting is byte-exact.</param>
    public PdfReal(double value, string? raw = null)
    {
        Value = value;
        Raw = raw;
    }

    public double Value { get; }

    public string? Raw { get; }

    public override string ToString() => Raw ?? Format(Value);

    public static string Format(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "0";
        }

        var s = value.ToString("0.##########", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}

/// <summary>A name. <see cref="Value"/> holds the decoded bytes as Latin-1 characters (one char per byte).</summary>
internal sealed class PdfName : PdfObject, IEquatable<PdfName>
{
    private static readonly Dictionary<string, PdfName> Interned = new(StringComparer.Ordinal);
    private static readonly object InternLock = new();

    public PdfName(string value) => Value = value;

    public string Value { get; }

    public static PdfName Get(string value)
    {
        lock (InternLock)
        {
            if (!Interned.TryGetValue(value, out var n))
            {
                n = new PdfName(value);
                if (Interned.Count < 4096)
                {
                    Interned[value] = n;
                }
            }

            return n;
        }
    }

    public bool Equals(PdfName? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is PdfName n && Equals(n);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => "/" + Value;

    // Common names.
    public static readonly PdfName Type = Get("Type");
    public static readonly PdfName Length = Get("Length");
    public static readonly PdfName Filter = Get("Filter");
    public static readonly PdfName DecodeParms = Get("DecodeParms");
    public static readonly PdfName Root = Get("Root");
    public static readonly PdfName Info = Get("Info");
    public static readonly PdfName ID = Get("ID");
    public static readonly PdfName Encrypt = Get("Encrypt");
    public static readonly PdfName Size = Get("Size");
    public static readonly PdfName Prev = Get("Prev");
    public static readonly PdfName XRefStm = Get("XRefStm");
    public static readonly PdfName FlateDecode = Get("FlateDecode");
    public static readonly PdfName Crypt = Get("Crypt");
    public static readonly PdfName Name = Get("Name");
    public static readonly PdfName Metadata = Get("Metadata");
}

/// <summary>A string: always raw bytes. Decoding as text is done only by <c>TextString</c> where the context requires it.</summary>
internal sealed class PdfString : PdfObject
{
    public PdfString(byte[] value, bool isHex = false)
    {
        Value = value;
        IsHex = isHex;
    }

    public byte[] Value { get; }

    /// <summary>Whether the source used hexadecimal form. Only a formatting hint.</summary>
    public bool IsHex { get; }

    public override string ToString() => IsHex ? "<" + Convert.ToHexString(Value) + ">" : "(" + Encoding.Latin1.GetString(Value) + ")";
}

internal sealed class PdfArray : PdfObject
{
    public PdfArray() => Items = new List<PdfObject>();

    public PdfArray(IEnumerable<PdfObject> items) => Items = new List<PdfObject>(items);

    public List<PdfObject> Items { get; }

    public int Count => Items.Count;

    public PdfObject this[int index] => Items[index];

    public void Add(PdfObject item) => Items.Add(item);

    public override string ToString() => "[" + string.Join(" ", Items) + "]";
}

/// <summary>A dictionary that preserves key order.</summary>
internal sealed class PdfDictionary : PdfObject
{
    private readonly List<KeyValuePair<PdfName, PdfObject>> _entries = new();
    private readonly Dictionary<PdfName, int> _index = new();

    public int Count => _entries.Count;

    public IEnumerable<KeyValuePair<PdfName, PdfObject>> Entries => _entries;

    public IEnumerable<PdfName> Keys => _entries.Select(e => e.Key);

    public PdfObject? this[PdfName key]
    {
        get => _index.TryGetValue(key, out var i) ? _entries[i].Value : null;
        set
        {
            if (value is null)
            {
                Remove(key);
                return;
            }

            if (_index.TryGetValue(key, out var i))
            {
                _entries[i] = new KeyValuePair<PdfName, PdfObject>(key, value);
            }
            else
            {
                _index[key] = _entries.Count;
                _entries.Add(new KeyValuePair<PdfName, PdfObject>(key, value));
            }
        }
    }

    public PdfObject? this[string key]
    {
        get => this[PdfName.Get(key)];
        set => this[PdfName.Get(key)] = value;
    }

    public bool ContainsKey(PdfName key) => _index.ContainsKey(key);

    public bool ContainsKey(string key) => _index.ContainsKey(PdfName.Get(key));

    public bool Remove(PdfName key)
    {
        if (!_index.TryGetValue(key, out var i))
        {
            return false;
        }

        _entries.RemoveAt(i);
        _index.Remove(key);
        for (var j = i; j < _entries.Count; j++)
        {
            _index[_entries[j].Key] = j;
        }

        return true;
    }

    public PdfDictionary ShallowClone()
    {
        var d = new PdfDictionary();
        foreach (var e in _entries)
        {
            d[e.Key] = e.Value;
        }

        return d;
    }

    public override string ToString() => "<<" + string.Join(" ", _entries.Select(e => e.Key + " " + e.Value)) + ">>";
}

internal readonly record struct ObjectId(int Number, int Generation)
{
    public override string ToString() => $"{Number} {Generation}";
}

internal sealed class PdfReference : PdfObject
{
    public PdfReference(int number, int generation) => Id = new ObjectId(number, generation);

    public PdfReference(ObjectId id) => Id = id;

    public ObjectId Id { get; }

    public override string ToString() => $"{Id.Number} {Id.Generation} R";
}

/// <summary>
/// A stream. The data is either a slice of the source file (<see cref="SourceOffset"/>/<see cref="SourceLength"/>,
/// still encrypted if the file is) or an in-memory buffer (<see cref="Data"/>, already plain).
/// </summary>
internal sealed class PdfStream : PdfObject
{
    public PdfStream(PdfDictionary dictionary, long sourceOffset, long sourceLength)
    {
        Dictionary = dictionary;
        SourceOffset = sourceOffset;
        SourceLength = sourceLength;
    }

    public PdfStream(PdfDictionary dictionary, byte[] data)
    {
        Dictionary = dictionary;
        Data = data;
        SourceOffset = -1;
        SourceLength = data.Length;
    }

    public PdfDictionary Dictionary { get; }

    public long SourceOffset { get; }

    public long SourceLength { get; }

    public byte[]? Data { get; }

    /// <summary>Whether <see cref="Data"/> is set (decoded from an object stream or built in memory).</summary>
    public bool IsInMemory => Data is not null;

    public override string ToString() => Dictionary + " stream";
}
