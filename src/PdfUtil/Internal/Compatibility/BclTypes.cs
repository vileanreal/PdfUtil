// Types that .NET Framework does not have, in the namespaces where .NET 8 has them. Compiled only for
// .NET Framework targets.
#if NETFRAMEWORK
using System.Runtime.CompilerServices;
using PdfUtil;

namespace System.Text
{
    /// <summary>The members of <c>System.Text.Rune</c> that PdfUtil uses.</summary>
    internal readonly struct Rune : IEquatable<Rune>, IComparable<Rune>
    {
        public Rune(char ch)
        {
            if (char.IsSurrogate(ch))
            {
                throw new ArgumentOutOfRangeException(nameof(ch));
            }

            Value = ch;
        }

        public Rune(int value)
        {
            if (!IsValid(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            Value = value;
        }

        public static Rune ReplacementChar => new('�');

        public int Value { get; }

        public bool IsBmp => Value <= 0xFFFF;

        public bool IsAscii => Value <= 0x7F;

        public int Utf16SequenceLength => IsBmp ? 1 : 2;

        public int Utf8SequenceLength => Value switch
        {
            <= 0x7F => 1,
            <= 0x7FF => 2,
            <= 0xFFFF => 3,
            _ => 4,
        };

        public static bool IsValid(int value) => value is >= 0 and <= 0x10FFFF and (< 0xD800 or > 0xDFFF);

        public static bool operator ==(Rune left, Rune right) => left.Value == right.Value;

        public static bool operator !=(Rune left, Rune right) => left.Value != right.Value;

        public static bool operator <(Rune left, Rune right) => left.Value < right.Value;

        public static bool operator >(Rune left, Rune right) => left.Value > right.Value;

        public static bool operator <=(Rune left, Rune right) => left.Value <= right.Value;

        public static bool operator >=(Rune left, Rune right) => left.Value >= right.Value;

        public static explicit operator Rune(int value) => new(value);

        public static explicit operator Rune(char ch) => new(ch);

        public int EncodeToUtf16(Span<char> destination)
        {
            if (IsBmp)
            {
                destination[0] = (char)Value;
                return 1;
            }

            var s = char.ConvertFromUtf32(Value);
            destination[0] = s[0];
            destination[1] = s[1];
            return 2;
        }

        public int CompareTo(Rune other) => Value.CompareTo(other.Value);

        public bool Equals(Rune other) => Value == other.Value;

        public override bool Equals(object? obj) => obj is Rune other && Equals(other);

        public override int GetHashCode() => Value;

        public override string ToString() => char.ConvertFromUtf32(Value);
    }
}

namespace System.Collections.Generic
{
    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object?>, Collections.IEqualityComparer
    {
        private ReferenceEqualityComparer()
        {
        }

        public static ReferenceEqualityComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object? obj) => RuntimeHelpers.GetHashCode(obj!);
    }
}

namespace System.IO.Compression
{
    /// <summary>
    /// The zlib format (RFC 1950) around .NET Framework's raw <see cref="DeflateStream"/>: a two-byte
    /// header, the deflate data and an Adler-32 checksum of the uncompressed data.
    /// </summary>
    internal sealed class ZLibStream : Stream
    {
        private readonly Stream _stream;
        private readonly bool _leaveOpen;
        private readonly CompressionMode _mode;
        private readonly CompressionLevel _level;
        private DeflateStream? _deflate;
        private bool _headerDone;
        private uint _adlerA = 1;
        private uint _adlerB;
        private bool _disposed;

        public ZLibStream(Stream stream, CompressionLevel compressionLevel, bool leaveOpen = false)
        {
            ArgumentNullException.ThrowIfNull(stream);
            _stream = stream;
            _leaveOpen = leaveOpen;
            _mode = CompressionMode.Compress;
            _level = compressionLevel;
        }

        public ZLibStream(Stream stream, CompressionMode mode, bool leaveOpen = false)
        {
            ArgumentNullException.ThrowIfNull(stream);
            _stream = stream;
            _leaveOpen = leaveOpen;
            _mode = mode;
            _level = CompressionLevel.Optimal;
        }

        public override bool CanRead => _mode == CompressionMode.Decompress && !_disposed;

        public override bool CanWrite => _mode == CompressionMode.Compress && !_disposed;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_mode != CompressionMode.Decompress)
            {
                throw new NotSupportedException("The stream does not support reading.");
            }

            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_headerDone)
            {
                ReadHeader();
            }

            return _deflate!.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_mode != CompressionMode.Compress)
            {
                throw new NotSupportedException("The stream does not support writing.");
            }

            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_headerDone)
            {
                WriteHeader();
            }

            UpdateAdler(buffer, offset, count);
            _deflate!.Write(buffer, offset, count);
        }

        public override void Flush()
        {
            if (_deflate is not null && _mode == CompressionMode.Compress)
            {
                _deflate.Flush();
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (disposing)
            {
                if (_mode == CompressionMode.Compress)
                {
                    if (!_headerDone)
                    {
                        WriteHeader();
                    }

                    // Closing the deflate stream writes the final block; the checksum follows it.
                    _deflate!.Dispose();
                    var adler = (_adlerB << 16) | _adlerA;
                    _stream.WriteByte((byte)(adler >> 24));
                    _stream.WriteByte((byte)(adler >> 16));
                    _stream.WriteByte((byte)(adler >> 8));
                    _stream.WriteByte((byte)adler);
                }
                else
                {
                    _deflate?.Dispose();
                }

                if (!_leaveOpen)
                {
                    _stream.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        private void WriteHeader()
        {
            // CMF 0x78 (deflate, 32K window); FLG chosen so that (CMF * 256 + FLG) % 31 == 0, as zlib writes them.
            var flg = _level switch
            {
                CompressionLevel.NoCompression => (byte)0x01,
                CompressionLevel.Fastest => (byte)0x5E,
                _ => (byte)0x9C,
            };
            _stream.WriteByte(0x78);
            _stream.WriteByte(flg);
            _deflate = new DeflateStream(_stream, _level, leaveOpen: true);
            _headerDone = true;
        }

        private void ReadHeader()
        {
            var cmf = _stream.ReadByte();
            var flg = _stream.ReadByte();
            if (cmf < 0 || flg < 0 || (cmf & 0x0F) != 8 || ((cmf << 8) | flg) % 31 != 0 || (flg & 0x20) != 0)
            {
                throw new InvalidDataException("The archive entry was compressed using an unsupported compression method.");
            }

            _deflate = new DeflateStream(_stream, CompressionMode.Decompress, leaveOpen: true);
            _headerDone = true;
        }

        private void UpdateAdler(byte[] buffer, int offset, int count)
        {
            const uint Mod = 65521;
            var a = _adlerA;
            var b = _adlerB;
            while (count > 0)
            {
                // 5552 is the largest block for which the sums cannot overflow before the modulo.
                var n = Math.Min(count, 5552);
                count -= n;
                while (n-- > 0)
                {
                    a += buffer[offset++];
                    b += a;
                }

                a %= Mod;
                b %= Mod;
            }

            _adlerA = a;
            _adlerB = b;
        }
    }
}

namespace System.Security.Cryptography
{
    internal static class CryptographicOperations
    {
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            var accum = 0;
            for (var i = 0; i < left.Length; i++)
            {
                accum |= left[i] - right[i];
            }

            return accum == 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        public static void ZeroMemory(Span<byte> buffer) => buffer.Clear();
    }

    /// <summary>The members of <c>IncrementalHash</c> (added in .NET Framework 4.7) that PdfUtil uses.</summary>
    internal sealed class IncrementalHash : IDisposable
    {
        private readonly HashAlgorithm _hash;

        private IncrementalHash(HashAlgorithm hash) => _hash = hash;

        public static IncrementalHash CreateHash(HashAlgorithmName hashAlgorithm) => new(PdfUtil.HashPolyfills.Create(hashAlgorithm));

        public void AppendData(byte[] data) => AppendData(data, 0, data.Length);

        public void AppendData(byte[] data, int offset, int count) => _hash.TransformBlock(data, offset, count, null, 0);

        public void AppendData(ReadOnlySpan<byte> data)
        {
            var array = data.ToArray();
            AppendData(array, 0, array.Length);
        }

        public byte[] GetHashAndReset()
        {
            _hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var result = _hash.Hash!;
            _hash.Initialize();
            return result;
        }

        public bool TryGetHashAndReset(Span<byte> destination, out int bytesWritten)
        {
            var result = GetHashAndReset();
            if (result.Length > destination.Length)
            {
                bytesWritten = 0;
                return false;
            }

            result.CopyTo(destination);
            bytesWritten = result.Length;
            return true;
        }

        public int GetHashAndReset(Span<byte> destination)
        {
            if (!TryGetHashAndReset(destination, out var written))
            {
                throw new ArgumentException("Destination is too short.", nameof(destination));
            }

            return written;
        }

        public void Dispose() => _hash.Dispose();
    }
}

namespace System.Buffers
{
    internal delegate void SpanAction<T, in TArg>(Span<T> span, TArg arg);
}
#endif
