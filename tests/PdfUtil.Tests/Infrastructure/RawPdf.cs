using System.Globalization;
using System.Text;

namespace PdfUtil.Tests.Infrastructure;

/// <summary>Builds a PDF from hand-written object bodies, for hostile structures that <see cref="TestPdfBuilder"/> cannot express.</summary>
internal static class RawPdf
{
    /// <summary>A stream object body with a correct /Length.</summary>
    public static string Stream(string dictionaryEntries, string content) =>
        $"<< {dictionaryEntries} /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    /// <summary>Writes the objects (number → body) with a classic cross-reference table and the given trailer entries.</summary>
    public static byte[] Build(IDictionary<int, string> objects, string trailer = "/Root 1 0 R")
    {
        var sb = new StringBuilder("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new SortedDictionary<int, int>();
        foreach (var (number, body) in objects.OrderBy(o => o.Key))
        {
            offsets[number] = Encoding.Latin1.GetByteCount(sb.ToString());
            sb.Append(CultureInfo.InvariantCulture, $"{number} 0 obj\n{body}\nendobj\n");
        }

        var size = offsets.Keys.Max() + 1;
        var xref = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {size}\n0000000000 65535 f\r\n");
        for (var i = 1; i < size; i++)
        {
            sb.Append(offsets.TryGetValue(i, out var off) ? off.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n\r\n" : "0000000000 00001 f\r\n");
        }

        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {size} {trailer} /ID [<0102030405060708> <0102030405060708>] >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>A minimal two-object document (catalog 1, one page under pages 2) plus <paramref name="extra"/> objects; <paramref name="catalogExtras"/> go into the catalog.</summary>
    public static byte[] Minimal(string catalogExtras = "", IDictionary<int, string>? extra = null, string pageExtras = "")
    {
        var objects = new Dictionary<int, string>
        {
            [1] = $"<< /Type /Catalog /Pages 2 0 R {catalogExtras} >>",
            [2] = "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            [3] = $"<< /Type /Page /Parent 2 0 R {pageExtras} >>",
        };
        foreach (var (k, v) in extra ?? new Dictionary<int, string>())
        {
            objects[k] = v;
        }

        return Build(objects);
    }
}
