using System.Globalization;

namespace PdfUtil.Internal.Document;

/// <summary>A PDF version such as 1.4 or 2.0.</summary>
internal readonly record struct PdfVersion(int Major, int Minor) : IComparable<PdfVersion>
{
    public static readonly PdfVersion V1_4 = new(1, 4);
    public static readonly PdfVersion V1_6 = new(1, 6);
    public static readonly PdfVersion V1_7 = new(1, 7);
    public static readonly PdfVersion V2_0 = new(2, 0);

    public static bool TryParse(string? text, out PdfVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var parts = text.Split('.');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || major is < 1 or > 9 || minor is < 0 or > 9)
        {
            return false;
        }

        version = new PdfVersion(major, minor);
        return true;
    }

    public static PdfVersion Max(PdfVersion a, PdfVersion b) => a.CompareTo(b) >= 0 ? a : b;

    public int CompareTo(PdfVersion other) => Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

    public static bool operator <(PdfVersion a, PdfVersion b) => a.CompareTo(b) < 0;

    public static bool operator >(PdfVersion a, PdfVersion b) => a.CompareTo(b) > 0;

    public static bool operator <=(PdfVersion a, PdfVersion b) => a.CompareTo(b) <= 0;

    public static bool operator >=(PdfVersion a, PdfVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}";
}
