using System.Buffers;

namespace PdfUtil.Internal.Parsing;

/// <summary>
/// Random-access, windowed reader over a seekable stream. A pooled buffer holds the current window so that large
/// files are streamed instead of being loaded into memory. Not thread-safe: one instance per operation.
/// </summary>
internal sealed class PdfSource : IDisposable
{
    private const int WindowSize = 64 * 1024;

    private readonly Stream _stream;
    private readonly long _origin;
    private readonly bool _ownsStream;
    private byte[]? _buffer;
    private long _bufferStart;
    private int _bufferLength;
    private long _position;

    public PdfSource(Stream stream, bool ownsStream)
    {
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ArgumentException("A readable, seekable stream is required.", nameof(stream));
        }

        _stream = stream;
        _ownsStream = ownsStream;
        _origin = stream.Position;
        Length = stream.Length - _origin;
        _buffer = ArrayPool<byte>.Shared.Rent(WindowSize);
        _bufferStart = 0;
        _bufferLength = 0;
    }

    public static PdfSource FromBytes(byte[] data) => new(new MemoryStream(data, 0, data.Length, writable: false, publiclyVisible: true), ownsStream: true);

    public long Length { get; }

    public long Position
    {
        get => _position;
        set => _position = value < 0 ? 0 : value > Length ? Length : value;
    }

    public bool AtEnd => _position >= Length;

    /// <summary>Returns the byte at the current position without advancing, or -1 at end of data.</summary>
    public int Peek()
    {
        if (_position >= Length)
        {
            return -1;
        }

        if (_position < _bufferStart || _position >= _bufferStart + _bufferLength)
        {
            Fill(_position);
            if (_bufferLength == 0)
            {
                return -1;
            }
        }

        return Buffer[(int)(_position - _bufferStart)];
    }

    /// <summary>Returns the byte at the current position and advances, or -1 at end of data.</summary>
    public int Read()
    {
        var b = Peek();
        if (b >= 0)
        {
            _position++;
        }

        return b;
    }

    public int PeekAt(long offset)
    {
        var saved = _position;
        _position = offset;
        var b = offset >= 0 ? Peek() : -1;
        _position = saved;
        return b;
    }

    /// <summary>Reads exactly <paramref name="destination"/>.Length bytes at <paramref name="offset"/>, or fewer at end of data. Returns the count read.</summary>
    public int ReadAt(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset >= Length)
        {
            return 0;
        }

        var count = (int)Math.Min(destination.Length, Length - offset);
        if (offset >= _bufferStart && offset + count <= _bufferStart + _bufferLength)
        {
            Buffer.AsSpan((int)(offset - _bufferStart), count).CopyTo(destination);
            return count;
        }

        if (count <= WindowSize / 2)
        {
            Fill(offset);
            var available = (int)Math.Min(count, _bufferStart + _bufferLength - offset);
            Buffer.AsSpan((int)(offset - _bufferStart), available).CopyTo(destination);
            return available;
        }

        _stream.Position = _origin + offset;
        var total = 0;
        while (total < count)
        {
            var n = _stream.Read(destination.Slice(total, count - total));
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    public byte[] ReadBytes(long offset, long count)
    {
        if (count < 0 || offset < 0)
        {
            return Array.Empty<byte>();
        }

        var available = Math.Max(0, Math.Min(count, Length - offset));
        if (available > Array.MaxLength)
        {
            throw new PdfLimitExceededException("A single stream is too large to process.");
        }

        var result = new byte[available];
        var read = ReadAt(offset, result);
        return read == result.Length ? result : result.AsSpan(0, read).ToArray();
    }

    /// <summary>Copies a slice of the source to a destination stream without materialising it.</summary>
    public void CopyTo(long offset, long count, Stream destination)
    {
        var rented = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (count > 0)
            {
                var n = ReadAt(offset, rented.AsSpan(0, (int)Math.Min(rented.Length, count)));
                if (n <= 0)
                {
                    break;
                }

                destination.Write(rented, 0, n);
                offset += n;
                count -= n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Finds the first occurrence of <paramref name="pattern"/> at or after <paramref name="start"/> (before <paramref name="end"/>), or -1.</summary>
    public long IndexOf(ReadOnlySpan<byte> pattern, long start, long end)
    {
        end = Math.Min(end, Length);
        if (pattern.Length == 0 || start < 0)
        {
            return -1;
        }

        var chunk = ArrayPool<byte>.Shared.Rent(WindowSize + pattern.Length);
        try
        {
            var pos = start;
            while (pos < end)
            {
                var n = ReadAt(pos, chunk.AsSpan(0, (int)Math.Min(WindowSize + pattern.Length - 1, end - pos)));
                if (n < pattern.Length)
                {
                    return -1;
                }

                var idx = chunk.AsSpan(0, n).IndexOf(pattern);
                if (idx >= 0)
                {
                    return pos + idx;
                }

                pos += n - pattern.Length + 1;
            }

            return -1;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>Finds the last occurrence of <paramref name="pattern"/> starting within [<paramref name="start"/>, <paramref name="end"/>), or -1.</summary>
    public long LastIndexOf(ReadOnlySpan<byte> pattern, long start, long end)
    {
        start = Math.Max(0, start);
        end = Math.Min(end, Length);
        if (end - start < pattern.Length || end - start > int.MaxValue)
        {
            return -1;
        }

        var buf = ReadBytes(start, end - start);
        var idx = buf.AsSpan().LastIndexOf(pattern);
        return idx < 0 ? -1 : start + idx;
    }

    private byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(PdfSource));

    private void Fill(long offset)
    {
        var buf = Buffer;
        _stream.Position = _origin + offset;
        var total = 0;
        var want = (int)Math.Min(buf.Length, Length - offset);
        while (total < want)
        {
            var n = _stream.Read(buf, total, want - total);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        _bufferStart = offset;
        _bufferLength = total;
    }

    public void Dispose()
    {
        if (_buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }

        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}
