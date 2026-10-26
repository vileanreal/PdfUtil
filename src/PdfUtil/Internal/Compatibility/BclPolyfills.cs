// .NET Framework versions of the BCL members PdfUtil uses that arrived in .NET Core / .NET 5+. They are
// C# 14 extension members, so the calling code is the same for every target. Each one matches the
// behaviour of the .NET 8 member it stands in for. They live in the PdfUtil namespace, which encloses all
// PdfUtil code, so they are always in scope and take precedence over a BCL member of the same shape (on
// 4.7.1+ some exist, for example Enumerable.Append). Compiled only for .NET Framework targets.
#if NETFRAMEWORK
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PdfUtil;

internal static class ArgumentPolyfills
{
    extension(ArgumentNullException)
    {
        public static void ThrowIfNull([NotNull] object? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            if (argument is null)
            {
                throw new ArgumentNullException(paramName);
            }
        }
    }

    extension(ArgumentException)
    {
        public static void ThrowIfNullOrEmpty([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            if (argument is null)
            {
                throw new ArgumentNullException(paramName);
            }

            if (argument.Length == 0)
            {
                throw new ArgumentException("The value cannot be an empty string.", paramName);
            }
        }

        public static void ThrowIfNullOrWhiteSpace([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            if (argument is null)
            {
                throw new ArgumentNullException(paramName);
            }

            if (string.IsNullOrWhiteSpace(argument))
            {
                throw new ArgumentException("The value cannot be an empty string or composed entirely of whitespace.", paramName);
            }
        }
    }

    extension(ArgumentOutOfRangeException)
    {
        public static void ThrowIfNegative(int value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(paramName, value, "The value must be a non-negative number.");
            }
        }

        public static void ThrowIfNegativeOrZero(int value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(paramName, value, "The value must be a positive number.");
            }
        }

        public static void ThrowIfLessThan(int value, int other, [CallerArgumentExpression(nameof(value))] string? paramName = null)
        {
            if (value < other)
            {
                throw new ArgumentOutOfRangeException(paramName, value, $"The value must be greater than or equal to {other}.");
            }
        }

        public static void ThrowIfGreaterThan(int value, int other, [CallerArgumentExpression(nameof(value))] string? paramName = null)
        {
            if (value > other)
            {
                throw new ArgumentOutOfRangeException(paramName, value, $"The value must be less than or equal to {other}.");
            }
        }
    }

    extension(ObjectDisposedException)
    {
        public static void ThrowIf([DoesNotReturnIf(true)] bool condition, object instance)
        {
            if (condition)
            {
                throw new ObjectDisposedException(instance?.GetType().FullName);
            }
        }
    }
}

internal static class NumericPolyfills
{
    extension(Math)
    {
        public static int Clamp(int value, int min, int max)
        {
            if (min > max)
            {
                throw new ArgumentException($"'{min}' cannot be greater than {max}.");
            }

            return value < min ? min : value > max ? max : value;
        }

        public static long Clamp(long value, long min, long max)
        {
            if (min > max)
            {
                throw new ArgumentException($"'{min}' cannot be greater than {max}.");
            }

            return value < min ? min : value > max ? max : value;
        }

        public static double Clamp(double value, double min, double max)
        {
            if (min > max)
            {
                throw new ArgumentException($"'{min}' cannot be greater than {max}.");
            }

            // Like .NET: NaN stays NaN.
            return value < min ? min : value > max ? max : value;
        }

        public static float Clamp(float value, float min, float max)
        {
            if (min > max)
            {
                throw new ArgumentException($"'{min}' cannot be greater than {max}.");
            }

            return value < min ? min : value > max ? max : value;
        }
    }

    extension(double)
    {
        public static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
    }

    extension(float)
    {
        public static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    }




    extension(Enum)
    {
        public static bool IsDefined<TEnum>(TEnum value)
            where TEnum : struct, Enum => Enum.IsDefined(typeof(TEnum), value);

        public static TEnum[] GetValues<TEnum>()
            where TEnum : struct, Enum => (TEnum[])Enum.GetValues(typeof(TEnum));

        public static bool TryParse<TEnum>(string? value, bool ignoreCase, out TEnum result)
            where TEnum : struct, Enum => Enum.TryParse(value, ignoreCase, out result);
    }

    extension(OperatingSystem)
    {
        public static bool IsWindows() => Environment.OSVersion.Platform == PlatformID.Win32NT;

        // .NET Framework itself runs only on Windows; Mono reports macOS as Unix.
        public static bool IsMacOS() => Environment.OSVersion.Platform == PlatformID.MacOSX
            || (Environment.OSVersion.Platform == PlatformID.Unix && Directory.Exists("/System/Library/CoreServices"));

        public static bool IsLinux() => Environment.OSVersion.Platform == PlatformID.Unix && !OperatingSystem.IsMacOS();
    }
}

internal static class Int32Polyfills
{
    extension(int)
    {
        public static int Parse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider) => int.Parse(s.ToString(), style, provider);

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out int result) => int.TryParse(s.ToString(), style, provider, out result);
    }
}

internal static class Int64Polyfills
{
    extension(long)
    {
        public static long Parse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider) => long.Parse(s.ToString(), style, provider);

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out long result) => long.TryParse(s.ToString(), style, provider, out result);
    }
}

internal static class DoublePolyfills
{
    extension(double)
    {
        public static double Parse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider) => double.Parse(s.ToString(), style, provider);

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out double result) => double.TryParse(s.ToString(), style, provider, out result);
    }
}

internal static class CharPolyfills
{
    extension(char)
    {
        public static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

        public static bool IsAsciiHexDigit(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

        public static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

        public static bool IsAsciiLetterOrDigit(char c) => char.IsAsciiLetter(c) || char.IsAsciiDigit(c);

        public static bool IsAsciiLetterLower(char c) => c is >= 'a' and <= 'z';

        public static bool IsAsciiLetterUpper(char c) => c is >= 'A' and <= 'Z';

        public static bool IsAscii(char c) => c <= '\x7f';

        public static bool IsBetween(char c, char minInclusive, char maxInclusive) => (uint)(c - minInclusive) <= (uint)(maxInclusive - minInclusive);
    }

    extension(CharUnicodeInfo)
    {
        public static UnicodeCategory GetUnicodeCategory(int codePoint) =>
            codePoint <= 0xFFFF
                ? CharUnicodeInfo.GetUnicodeCategory((char)codePoint)
                : CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(codePoint), 0);
    }
}

internal static class StringPolyfills
{
    extension(string s)
    {
        public bool Contains(char value) => s.IndexOf(value) >= 0;

        public bool Contains(char value, StringComparison comparisonType) => s.IndexOf(value.ToString(), comparisonType) >= 0;

        public bool Contains(string value, StringComparison comparisonType) => s.IndexOf(value, comparisonType) >= 0;

        public bool StartsWith(char value) => s.Length != 0 && s[0] == value;

        public bool EndsWith(char value) => s.Length != 0 && s[s.Length - 1] == value;

        public int IndexOf(char value, StringComparison comparisonType) => s.IndexOf(value.ToString(), comparisonType);

        public string[] Split(char separator, StringSplitOptions options) => s.Split(new[] { separator }, options);

        public string[] Split(char separator, int count, StringSplitOptions options = StringSplitOptions.None) => s.Split(new[] { separator }, count, options);

        public string[] Split(string? separator, StringSplitOptions options = StringSplitOptions.None) => s.Split(new[] { separator ?? string.Empty }, options);

        public string Replace(string oldValue, string? newValue, StringComparison comparisonType)
        {
            if (comparisonType == StringComparison.Ordinal)
            {
                return s.Replace(oldValue, newValue);
            }

            ArgumentException.ThrowIfNullOrEmpty(oldValue);
            var sb = new StringBuilder(s.Length);
            var start = 0;
            int index;
            while ((index = s.IndexOf(oldValue, start, comparisonType)) >= 0)
            {
                sb.Append(s, start, index - start).Append(newValue);
                start = index + oldValue.Length;
            }

            return sb.Append(s, start, s.Length - start).ToString();
        }

        public int GetHashCode(StringComparison comparisonType) => StringComparer.FromComparison(comparisonType).GetHashCode(s);

        public IEnumerable<Rune> EnumerateRunes()
        {
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    yield return new Rune(char.ConvertToUtf32(c, s[i + 1]));
                    i++;
                }
                else
                {
                    // A lone surrogate becomes U+FFFD, as in .NET.
                    yield return char.IsSurrogate(c) ? Rune.ReplacementChar : new Rune(c);
                }
            }
        }
    }

    extension(string)
    {
        public static string Create<TState>(int length, TState state, System.Buffers.SpanAction<char, TState> action)
        {
            var chars = new char[length];
            action(chars, state);
            return new string(chars);
        }

        public static string Create(IFormatProvider? provider, FormattableString text) => text.ToString(provider);

        public static string Join(char separator, params string?[] value) => string.Join(separator.ToString(), value);

        public static string Join(char separator, IEnumerable<string?> values) => string.Join(separator.ToString(), values);

        public static string Join<T>(char separator, IEnumerable<T> values) => string.Join(separator.ToString(), values);
    }

    extension(StringComparer)
    {
        public static StringComparer FromComparison(StringComparison comparisonType) => comparisonType switch
        {
            StringComparison.CurrentCulture => StringComparer.CurrentCulture,
            StringComparison.CurrentCultureIgnoreCase => StringComparer.CurrentCultureIgnoreCase,
            StringComparison.InvariantCulture => StringComparer.InvariantCulture,
            StringComparison.InvariantCultureIgnoreCase => StringComparer.InvariantCultureIgnoreCase,
            StringComparison.Ordinal => StringComparer.Ordinal,
            StringComparison.OrdinalIgnoreCase => StringComparer.OrdinalIgnoreCase,
            _ => throw new ArgumentException("The string comparison type is not supported.", nameof(comparisonType)),
        };
    }

    extension(StringBuilder sb)
    {
        public StringBuilder Append(ReadOnlySpan<char> value) => sb.Append(value.ToString());

        public StringBuilder Append(IFormatProvider? provider, FormattableString value) => sb.Append(value.ToString(provider));

        public StringBuilder AppendLine(IFormatProvider? provider, FormattableString value) => sb.AppendLine(value.ToString(provider));
    }
}

internal static class RandomPolyfills
{
    extension(Random random)
    {
        public void NextBytes(Span<byte> buffer)
        {
            var bytes = new byte[buffer.Length];
            random.NextBytes(bytes);
            bytes.CopyTo(buffer);
        }
    }
}

internal static class CompressionLevelPolyfills
{
    extension(CompressionLevel)
    {
        /// <summary>.NET Framework has no higher level than <see cref="CompressionLevel.Optimal"/>.</summary>
        public static CompressionLevel SmallestSize => CompressionLevel.Optimal;
    }
}

internal static class ArrayPolyfills
{
    extension(Array)
    {
        public static int MaxLength => 0x7FFFFFC7;

        public static void Fill<T>(T[] array, T value)
        {
            ArgumentNullException.ThrowIfNull(array);
            for (var i = 0; i < array.Length; i++)
            {
                array[i] = value;
            }
        }
    }
}

internal static class ConvertPolyfills
{
    extension(Convert)
    {
        public static string ToHexString(byte[] inArray)
        {
            ArgumentNullException.ThrowIfNull(inArray);
            return Convert.ToHexString((ReadOnlySpan<byte>)inArray);
        }

        public static string ToHexString(byte[] inArray, int offset, int length) => Convert.ToHexString(inArray.AsSpan(offset, length));

        public static string ToHexString(ReadOnlySpan<byte> bytes)
        {
            const string Digits = "0123456789ABCDEF";
            var chars = new char[bytes.Length * 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                chars[2 * i] = Digits[bytes[i] >> 4];
                chars[(2 * i) + 1] = Digits[bytes[i] & 0xF];
            }

            return new string(chars);
        }

        public static string ToHexStringLower(byte[] inArray) => Convert.ToHexString(inArray).ToLowerInvariant();

        public static string ToHexStringLower(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        public static byte[] FromHexString(string s)
        {
            ArgumentNullException.ThrowIfNull(s);
            if (s.Length % 2 != 0)
            {
                throw new FormatException("The input is not a valid hex string as its length is not a multiple of 2.");
            }

            var result = new byte[s.Length / 2];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = (byte)((HexValue(s[2 * i]) << 4) | HexValue(s[(2 * i) + 1]));
            }

            return result;
        }

        private static int HexValue(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => throw new FormatException("The input is not a valid hex string as it contains a non-hex character."),
        };
    }
}

internal static class EncodingPolyfills
{
    private static Encoding? s_latin1;

    extension(Encoding)
    {
        /// <summary>ISO-8859-1 that, like .NET's <c>Encoding.Latin1</c>, writes '?' for characters above U+00FF instead of a best-fit match.</summary>
        public static Encoding Latin1 => s_latin1 ??= Encoding.GetEncoding(28591, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    extension(Encoding encoding)
    {
        public string GetString(ReadOnlySpan<byte> bytes) => encoding.GetString(bytes.ToArray());

        public byte[] GetBytes(ReadOnlySpan<char> chars) => encoding.GetBytes(chars.ToArray());

        public int GetBytes(ReadOnlySpan<char> chars, Span<byte> bytes)
        {
            var encoded = encoding.GetBytes(chars.ToArray());
            encoded.CopyTo(bytes);
            return encoded.Length;
        }

        public int GetByteCount(ReadOnlySpan<char> chars) => encoding.GetByteCount(chars.ToArray());
    }
}

internal static class CollectionPolyfills
{
    extension<TKey, TValue>(KeyValuePair<TKey, TValue> pair)
    {
        public void Deconstruct(out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }
    }

    extension<TKey, TValue>(IDictionary<TKey, TValue> dictionary)
    {
        public bool TryAdd(TKey key, TValue value)
        {
            if (dictionary.ContainsKey(key))
            {
                return false;
            }

            dictionary.Add(key, value);
            return true;
        }
    }

    extension<TKey, TValue>(Dictionary<TKey, TValue> dictionary)
        where TKey : notnull
    {
        public bool Remove(TKey key, out TValue value)
        {
            if (dictionary.TryGetValue(key, out value!))
            {
                dictionary.Remove(key);
                return true;
            }

            return false;
        }

        public TValue? GetValueOrDefault(TKey key) => dictionary.TryGetValue(key, out var value) ? value : default;

        public TValue GetValueOrDefault(TKey key, TValue defaultValue) => dictionary.TryGetValue(key, out var value) ? value : defaultValue;
    }

    extension<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> dictionary)
    {
        public TValue? GetValueOrDefault(TKey key) => dictionary.TryGetValue(key, out var value) ? value : default;

        public TValue GetValueOrDefault(TKey key, TValue defaultValue) => dictionary.TryGetValue(key, out var value) ? value : defaultValue;
    }

    extension<T>(Queue<T> queue)
    {
        public bool TryDequeue(out T result)
        {
            if (queue.Count == 0)
            {
                result = default!;
                return false;
            }

            result = queue.Dequeue();
            return true;
        }

        public bool TryPeek(out T result)
        {
            if (queue.Count == 0)
            {
                result = default!;
                return false;
            }

            result = queue.Peek();
            return true;
        }
    }

    extension<T>(Stack<T> stack)
    {
        public bool TryPop(out T result)
        {
            if (stack.Count == 0)
            {
                result = default!;
                return false;
            }

            result = stack.Pop();
            return true;
        }

        public bool TryPeek(out T result)
        {
            if (stack.Count == 0)
            {
                result = default!;
                return false;
            }

            result = stack.Peek();
            return true;
        }
    }

    extension<T>(HashSet<T> set)
    {
        public bool TryGetValue(T equalValue, out T actualValue)
        {
            foreach (var item in set)
            {
                if (set.Comparer.Equals(item, equalValue))
                {
                    actualValue = item;
                    return true;
                }
            }

            actualValue = equalValue;
            return false;
        }
    }
}

internal static class EnumerablePolyfills
{
    extension<TSource>(IEnumerable<TSource> source)
    {
        public HashSet<TSource> ToHashSet(IEqualityComparer<TSource>? comparer = null) => new(source, comparer);

        public IEnumerable<TSource> Append(TSource element)
        {
            foreach (var item in source)
            {
                yield return item;
            }

            yield return element;
        }

        public IEnumerable<TSource> Prepend(TSource element)
        {
            yield return element;
            foreach (var item in source)
            {
                yield return item;
            }
        }

        public IEnumerable<(TSource First, TSecond Second)> Zip<TSecond>(IEnumerable<TSecond> second) => source.Zip(second, (a, b) => (a, b));

        public IEnumerable<TSource[]> Chunk(int size)
        {
            if (size < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(size));
            }

            var chunk = new List<TSource>(size);
            foreach (var item in source)
            {
                chunk.Add(item);
                if (chunk.Count == size)
                {
                    yield return chunk.ToArray();
                    chunk.Clear();
                }
            }

            if (chunk.Count > 0)
            {
                yield return chunk.ToArray();
            }
        }

        public IOrderedEnumerable<TSource> Order() => source.OrderBy(x => x);

        public IOrderedEnumerable<TSource> Order(IComparer<TSource>? comparer) => source.OrderBy(x => x, comparer);

        public IOrderedEnumerable<TSource> OrderDescending() => source.OrderByDescending(x => x);

        public IEnumerable<TSource> SkipLast(int count)
        {
            var list = source.ToList();
            return list.Take(Math.Max(0, list.Count - count));
        }

        public IEnumerable<TSource> TakeLast(int count)
        {
            var list = source.ToList();
            return list.Skip(Math.Max(0, list.Count - count));
        }

        public IEnumerable<TSource> DistinctBy<TKey>(Func<TSource, TKey> keySelector)
        {
            var seen = new HashSet<TKey>();
            foreach (var item in source)
            {
                if (seen.Add(keySelector(item)))
                {
                    yield return item;
                }
            }
        }

        public TSource? MaxBy<TKey>(Func<TSource, TKey> keySelector) => source.ExtremeBy(keySelector, 1);

        public TSource? MinBy<TKey>(Func<TSource, TKey> keySelector) => source.ExtremeBy(keySelector, -1);

        public TSource FirstOrDefault(TSource defaultValue)
        {
            foreach (var item in source)
            {
                return item;
            }

            return defaultValue;
        }

        public TSource FirstOrDefault(Func<TSource, bool> predicate, TSource defaultValue)
        {
            foreach (var item in source)
            {
                if (predicate(item))
                {
                    return item;
                }
            }

            return defaultValue;
        }

        public TSource LastOrDefault(TSource defaultValue)
        {
            var found = false;
            var last = defaultValue;
            foreach (var item in source)
            {
                last = item;
                found = true;
            }

            return found ? last : defaultValue;
        }

        public bool TryGetNonEnumeratedCount(out int count)
        {
            if (source is ICollection<TSource> collection)
            {
                count = collection.Count;
                return true;
            }

            if (source is IReadOnlyCollection<TSource> readOnly)
            {
                count = readOnly.Count;
                return true;
            }

            count = 0;
            return false;
        }

        private TSource? ExtremeBy<TKey>(Func<TSource, TKey> keySelector, int sign)
        {
            var comparer = Comparer<TKey>.Default;
            using var e = source.GetEnumerator();
            if (!e.MoveNext())
            {
                if (default(TSource) is null)
                {
                    return default;
                }

                throw new InvalidOperationException("Sequence contains no elements");
            }

            var best = e.Current;
            var bestKey = keySelector(best);
            while (e.MoveNext())
            {
                var key = keySelector(e.Current);
                if (comparer.Compare(key, bestKey) * sign > 0)
                {
                    best = e.Current;
                    bestKey = key;
                }
            }

            return best;
        }
    }

    extension<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>> source)
        where TKey : notnull
    {
        public Dictionary<TKey, TValue> ToDictionary(IEqualityComparer<TKey>? comparer = null) => source.ToDictionary(p => p.Key, p => p.Value, comparer);
    }

    extension<TKey, TValue>(IEnumerable<(TKey Key, TValue Value)> source)
        where TKey : notnull
    {
        public Dictionary<TKey, TValue> ToDictionary(IEqualityComparer<TKey>? comparer = null) => source.ToDictionary(p => p.Key, p => p.Value, comparer);
    }
}

internal static class StreamPolyfills
{
    extension(Stream stream)
    {
        public int Read(Span<byte> buffer)
        {
            var rented = new byte[buffer.Length];
            var n = stream.Read(rented, 0, rented.Length);
            rented.AsSpan(0, n).CopyTo(buffer);
            return n;
        }

        public void Write(ReadOnlySpan<byte> buffer)
        {
            // MemoryStream.Write(byte[]) copies, so a temporary array is the simplest correct route.
            var array = buffer.ToArray();
            stream.Write(array, 0, array.Length);
        }

        public void ReadExactly(Span<byte> buffer)
        {
            var array = new byte[buffer.Length];
            stream.ReadExactly(array, 0, array.Length);
            array.CopyTo(buffer);
        }

        public void ReadExactly(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var n = stream.Read(buffer, offset, count);
                if (n == 0)
                {
                    throw new EndOfStreamException("Unable to read beyond the end of the stream.");
                }

                offset += n;
                count -= n;
            }
        }

        public int ReadAtLeast(Span<byte> buffer, int minimumBytes, bool throwOnEndOfStream = true)
        {
            var array = new byte[buffer.Length];
            var total = 0;
            while (total < minimumBytes)
            {
                var n = stream.Read(array, total, array.Length - total);
                if (n == 0)
                {
                    if (throwOnEndOfStream)
                    {
                        throw new EndOfStreamException("Unable to read beyond the end of the stream.");
                    }

                    break;
                }

                total += n;
            }

            array.AsSpan(0, total).CopyTo(buffer);
            return total;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray<byte>(buffer, out var segment))
            {
                return await stream.ReadAsync(segment.Array!, segment.Offset, segment.Count, cancellationToken).ConfigureAwait(false);
            }

            var array = new byte[buffer.Length];
            var n = await stream.ReadAsync(array, 0, array.Length, cancellationToken).ConfigureAwait(false);
            array.AsMemory(0, n).CopyTo(buffer);
            return n;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(buffer, out var segment))
            {
                return new ValueTask(stream.WriteAsync(segment.Array!, segment.Offset, segment.Count, cancellationToken));
            }

            var array = buffer.ToArray();
            return new ValueTask(stream.WriteAsync(array, 0, array.Length, cancellationToken));
        }

        public Task CopyToAsync(Stream destination, CancellationToken cancellationToken) => stream.CopyToAsync(destination, 81920, cancellationToken);

        public ValueTask DisposeAsync()
        {
            stream.Dispose();
            return default;
        }
    }

    extension(File)
    {
        public static void Move(string sourceFileName, string destFileName, bool overwrite)
        {
            if (overwrite && File.Exists(destFileName))
            {
                File.Delete(destFileName);
            }

            File.Move(sourceFileName, destFileName);
        }

        public static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var length = stream.Length;
            if (length > int.MaxValue)
            {
                throw new IOException("The file is too long.");
            }

            var bytes = new byte[length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var n = await stream.ReadAsync(bytes, offset, bytes.Length - offset, cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    throw new EndOfStreamException("Unable to read beyond the end of the stream.");
                }

                offset += n;
            }

            return bytes;
        }

        public static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
            await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        }

        public static Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(File.ReadAllText(path));
        }

        public static Task WriteAllTextAsync(string path, string? contents, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllText(path, contents);
            return Task.CompletedTask;
        }
    }

    extension(Directory)
    {
        public static DirectoryInfo CreateTempSubdirectory(string? prefix = null)
        {
            var path = Path.Combine(Path.GetTempPath(), (prefix ?? string.Empty) + Path.GetRandomFileName());
            return Directory.CreateDirectory(path);
        }
    }

    extension(Path)
    {
        public static string GetRelativePath(string relativeTo, string path)
        {
            var from = new Uri(AppendSeparator(Path.GetFullPath(relativeTo)));
            var to = new Uri(Path.GetFullPath(path));
            var relative = Uri.UnescapeDataString(from.MakeRelativeUri(to).ToString());
            return relative.Length == 0 ? "." : relative.Replace('/', Path.DirectorySeparatorChar);
        }

        public static string Join(string? path1, string? path2)
        {
            if (string.IsNullOrEmpty(path1))
            {
                return path2 ?? string.Empty;
            }

            if (string.IsNullOrEmpty(path2))
            {
                return path1;
            }

            var separated = path1[path1.Length - 1] is '/' or '\\' || path2[0] is '/' or '\\';
            return separated ? path1 + path2 : path1 + Path.DirectorySeparatorChar + path2;
        }

        private static string AppendSeparator(string path) =>
            path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    }
}

internal static class HashPolyfills
{
    public static HashAlgorithm Create(HashAlgorithmName name)
    {
        if (name == HashAlgorithmName.MD5)
        {
            return MD5.Create();
        }

        if (name == HashAlgorithmName.SHA1)
        {
            return SHA1.Create();
        }

        if (name == HashAlgorithmName.SHA256)
        {
            return SHA256.Create();
        }

        if (name == HashAlgorithmName.SHA384)
        {
            return SHA384.Create();
        }

        if (name == HashAlgorithmName.SHA512)
        {
            return SHA512.Create();
        }

        throw new CryptographicException($"'{name.Name}' is not a known hash algorithm.");
    }

    internal static byte[] Hash(HashAlgorithmName name, ReadOnlySpan<byte> source)
    {
        using var hash = Create(name);
        return hash.ComputeHash(source.ToArray());
    }

    internal static int Hash(HashAlgorithmName name, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var result = Hash(name, source);
        if (result.Length > destination.Length)
        {
            throw new ArgumentException("Destination is too short.", nameof(destination));
        }

        result.CopyTo(destination);
        return result.Length;
    }

    extension(RandomNumberGenerator)
    {
        public static byte[] GetBytes(int count)
        {
            var bytes = new byte[count];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return bytes;
        }

        public static void Fill(Span<byte> data) => RandomNumberGenerator.GetBytes(data.Length).CopyTo(data);

        public static int GetInt32(int toExclusive)
        {
            if (toExclusive <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(toExclusive));
            }

            // Rejection sampling, so every value is equally likely.
            var limit = uint.MaxValue - (uint.MaxValue % (uint)toExclusive);
            uint value;
            do
            {
                value = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4), 0);
            }
            while (value >= limit);
            return (int)(value % (uint)toExclusive);
        }
    }
}

internal static class Md5Polyfills
{
    extension(MD5)
    {
        public static byte[] HashData(byte[] source) => HashPolyfills.Hash(HashAlgorithmName.MD5, source);

        public static byte[] HashData(ReadOnlySpan<byte> source) => HashPolyfills.Hash(HashAlgorithmName.MD5, source);

        public static int HashData(ReadOnlySpan<byte> source, Span<byte> destination) => HashPolyfills.Hash(HashAlgorithmName.MD5, source, destination);
    }
}

internal static class Sha1Polyfills
{
    extension(SHA1)
    {
        public static byte[] HashData(byte[] source) => HashPolyfills.Hash(HashAlgorithmName.SHA1, source);

        public static byte[] HashData(ReadOnlySpan<byte> source) => HashPolyfills.Hash(HashAlgorithmName.SHA1, source);

        public static int HashData(ReadOnlySpan<byte> source, Span<byte> destination) => HashPolyfills.Hash(HashAlgorithmName.SHA1, source, destination);
    }
}

internal static class Sha256Polyfills
{
    extension(SHA256)
    {
        public static byte[] HashData(byte[] source) => HashPolyfills.Hash(HashAlgorithmName.SHA256, source);

        public static byte[] HashData(ReadOnlySpan<byte> source) => HashPolyfills.Hash(HashAlgorithmName.SHA256, source);

        public static int HashData(ReadOnlySpan<byte> source, Span<byte> destination) => HashPolyfills.Hash(HashAlgorithmName.SHA256, source, destination);
    }
}

internal static class Sha384Polyfills
{
    extension(SHA384)
    {
        public static byte[] HashData(byte[] source) => HashPolyfills.Hash(HashAlgorithmName.SHA384, source);

        public static byte[] HashData(ReadOnlySpan<byte> source) => HashPolyfills.Hash(HashAlgorithmName.SHA384, source);

        public static int HashData(ReadOnlySpan<byte> source, Span<byte> destination) => HashPolyfills.Hash(HashAlgorithmName.SHA384, source, destination);
    }
}

internal static class Sha512Polyfills
{
    extension(SHA512)
    {
        public static byte[] HashData(byte[] source) => HashPolyfills.Hash(HashAlgorithmName.SHA512, source);

        public static byte[] HashData(ReadOnlySpan<byte> source) => HashPolyfills.Hash(HashAlgorithmName.SHA512, source);

        public static int HashData(ReadOnlySpan<byte> source, Span<byte> destination) => HashPolyfills.Hash(HashAlgorithmName.SHA512, source, destination);
    }
}

internal static class X509Polyfills
{
    extension(X509Certificate2 certificate)
    {
        /// <summary>The serial number, big-endian as in the certificate (<c>GetSerialNumber</c> is little-endian).</summary>
        public ReadOnlyMemory<byte> SerialNumberBytes
        {
            get
            {
                var serial = certificate.GetSerialNumber();
                Array.Reverse(serial);
                return serial;
            }
        }
    }

    extension(X509ChainPolicy policy)
    {
        public X509ChainPolicy Clone()
        {
            var clone = new X509ChainPolicy
            {
                RevocationFlag = policy.RevocationFlag,
                RevocationMode = policy.RevocationMode,
                UrlRetrievalTimeout = policy.UrlRetrievalTimeout,
                VerificationFlags = policy.VerificationFlags,
                VerificationTime = policy.VerificationTime,
            };
            foreach (var oid in policy.ApplicationPolicy)
            {
                clone.ApplicationPolicy.Add(oid);
            }

            foreach (var oid in policy.CertificatePolicy)
            {
                clone.CertificatePolicy.Add(oid);
            }

            clone.ExtraStore.AddRange(policy.ExtraStore);
            return clone;
        }
    }
}

internal static class SymmetricPolyfills
{
    extension(SymmetricAlgorithm algorithm)
    {
        public byte[] EncryptCbc(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> iv, PaddingMode paddingMode = PaddingMode.PKCS7) =>
            algorithm.Transform(CipherMode.CBC, paddingMode, iv.ToArray(), plaintext, encrypt: true);

        public byte[] DecryptCbc(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> iv, PaddingMode paddingMode = PaddingMode.PKCS7) =>
            algorithm.Transform(CipherMode.CBC, paddingMode, iv.ToArray(), ciphertext, encrypt: false);

        public int EncryptCbc(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> iv, Span<byte> destination, PaddingMode paddingMode = PaddingMode.PKCS7) =>
            CopyResult(algorithm.EncryptCbc(plaintext, iv, paddingMode), destination);

        public int DecryptCbc(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> iv, Span<byte> destination, PaddingMode paddingMode = PaddingMode.PKCS7) =>
            CopyResult(algorithm.DecryptCbc(ciphertext, iv, paddingMode), destination);

        public byte[] EncryptEcb(ReadOnlySpan<byte> plaintext, PaddingMode paddingMode) =>
            algorithm.Transform(CipherMode.ECB, paddingMode, null, plaintext, encrypt: true);

        public byte[] DecryptEcb(ReadOnlySpan<byte> ciphertext, PaddingMode paddingMode) =>
            algorithm.Transform(CipherMode.ECB, paddingMode, null, ciphertext, encrypt: false);

        public int EncryptEcb(ReadOnlySpan<byte> plaintext, Span<byte> destination, PaddingMode paddingMode) =>
            CopyResult(algorithm.EncryptEcb(plaintext, paddingMode), destination);

        public int DecryptEcb(ReadOnlySpan<byte> ciphertext, Span<byte> destination, PaddingMode paddingMode) =>
            CopyResult(algorithm.DecryptEcb(ciphertext, paddingMode), destination);

        public int GetCiphertextLengthCbc(int plaintextLength, PaddingMode paddingMode = PaddingMode.PKCS7)
        {
            var block = algorithm.BlockSize / 8;
            return paddingMode switch
            {
                PaddingMode.None when plaintextLength % block != 0 => throw new ArgumentException("The input data is not a complete block.", nameof(plaintextLength)),
                PaddingMode.None => plaintextLength,
                PaddingMode.Zeros when plaintextLength % block == 0 => plaintextLength,
                _ => ((plaintextLength / block) + 1) * block,
            };
        }

        private byte[] Transform(CipherMode mode, PaddingMode padding, byte[]? iv, ReadOnlySpan<byte> input, bool encrypt)
        {
            // The one-shot .NET methods use the key and ignore the Mode/Padding/IV properties; so does this.
            algorithm.Mode = mode;
            algorithm.Padding = padding;
            var key = algorithm.Key;
            using var transform = encrypt
                ? algorithm.CreateEncryptor(key, iv ?? new byte[algorithm.BlockSize / 8])
                : algorithm.CreateDecryptor(key, iv ?? new byte[algorithm.BlockSize / 8]);
            var data = input.ToArray();
            return transform.TransformFinalBlock(data, 0, data.Length);
        }
    }

    private static int CopyResult(byte[] result, Span<byte> destination)
    {
        if (result.Length > destination.Length)
        {
            throw new ArgumentException("Destination is too short.", nameof(destination));
        }

        result.CopyTo(destination);
        return result.Length;
    }
}

internal static class TaskPolyfills
{
    extension(Parallel)
    {
        public static Task ForEachAsync<TSource>(IEnumerable<TSource> source, ParallelOptions parallelOptions, Func<TSource, CancellationToken, ValueTask> body)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(parallelOptions);
            ArgumentNullException.ThrowIfNull(body);
            return Run(source, parallelOptions, body);
        }
    }

    extension(Task task)
    {
        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled || task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return;
            }

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            await task.ConfigureAwait(false);
        }
    }

    private static async Task Run<TSource>(IEnumerable<TSource> source, ParallelOptions options, Func<TSource, CancellationToken, ValueTask> body)
    {
        // Like .NET: -1 means one worker per processor, the loop stops at the first failure, and every
        // failure is reported.
        var degree = options.MaxDegreeOfParallelism == -1 ? Environment.ProcessorCount : options.MaxDegreeOfParallelism;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken);
        var exceptions = new ConcurrentQueue<Exception>();
        using var e = source.GetEnumerator();
        var gate = new object();

        async Task Worker()
        {
            while (!cts.IsCancellationRequested)
            {
                TSource item;
                lock (gate)
                {
                    if (!e.MoveNext())
                    {
                        return;
                    }

                    item = e.Current;
                }

                try
                {
                    await body(item, cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                    cts.Cancel();
                    return;
                }
            }
        }

        var workers = new Task[Math.Max(1, degree)];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Run(Worker);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        if (!exceptions.IsEmpty)
        {
            var real = exceptions.Where(ex => ex is not OperationCanceledException).ToList();
            if (real.Count == 0)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                throw exceptions.First();
            }

            if (real.Count == 1)
            {
                ExceptionDispatchInfo.Capture(real[0]).Throw();
            }

            throw new AggregateException(real);
        }

        options.CancellationToken.ThrowIfCancellationRequested();
    }
}
#endif
