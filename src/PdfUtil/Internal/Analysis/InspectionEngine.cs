using PdfUtil.Internal.Document;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;
using PdfUtil.Internal.Security;
using PdfUtil.Internal.Text;

namespace PdfUtil.Internal.Analysis;

/// <summary>Reads document details for <see cref="PdfInspector"/> and <see cref="PdfAnalyzer"/>.</summary>
internal sealed class InspectionEngine
{
    private readonly PdfUtilOptions _options;
    private readonly PdfLog _log;

    public InspectionEngine(PdfUtilOptions options, PdfLog log)
    {
        _options = options;
        _log = log;
    }

    public PdfDocumentInfo Inspect(PdfSource source, string? password, PdfInspectionDetails details, CancellationToken ct)
    {
        using var doc = PdfDocument.Open(source, _options, _log, strict: false, ct);
        return Inspect(doc, source.Length, password, details, null, ct);
    }

    /// <summary>
    /// Inspects an opened document. <paramref name="unlockCertificate"/> opens certificate-encrypted documents (it
    /// installs the security handler and returns whether it could); without it they are reported, not opened.
    /// </summary>
    public PdfDocumentInfo Inspect(PdfDocument doc, long fileSize, string? password, PdfInspectionDetails details, Func<PdfDocument, bool>? unlockCertificate, CancellationToken ct)
    {
        var info = new PdfDocumentInfo
        {
            HeaderVersion = doc.HeaderVersion,
            EffectiveVersion = doc.HeaderVersion,
            FileSize = fileSize,
            IsLinearized = doc.IsLinearized,
            IncrementalUpdateCount = doc.IncrementalUpdateCount,
            HasXrefStreams = doc.HasXrefStreams,
            HasObjectStreams = doc.HasObjectStreams,
            PermanentId = doc.GetPermanentId() is { } id0 ? (byte[])id0.Clone() : null,
            ChangingId = doc.GetChangingId() is { } id1 ? (byte[])id1.Clone() : null,
        };

        if (doc.IsEncrypted && doc.SecurityFilter == "Adobe.PubSec")
        {
            var pubSec = PubSecDictionary.Parse(doc.GetEncryptDictionary()!, doc.Resolve);
            var unlocked = unlockCertificate?.Invoke(doc) == true;
            info.Encryption = new PdfEncryptionInfo("Adobe.PubSec", pubSec.SubFilter, pubSec.V, 0, pubSec.KeyLengthBits, doc.Security?.P ?? 0, pubSec.Algorithm, pubSec.EncryptMetadata, pubSec.V >= 4 ? pubSec.StreamFilter : null, pubSec.V >= 4 ? pubSec.StringFilter : null, pubSec.V >= 4 ? pubSec.EmbeddedFileFilter : null, unlocked ? PdfPasswordMatch.User : PdfPasswordMatch.None)
            {
                RecipientCount = pubSec.Recipients.Count,
            };
            if (!unlocked)
            {
                info.RequiresCertificate = true;
                info.WasRepaired = doc.WasRepaired;
                info.Warnings = doc.Warnings.ToArray();
                return info;
            }
        }
        else if (doc.IsEncrypted)
        {
            var handler = doc.InitializeSecurity();
            var match = doc.Authenticate(password);
            var encryptDict = doc.GetEncryptDictionary()!;
            info.Encryption = new PdfEncryptionInfo(
                "Standard",
                (doc.Resolve(encryptDict["SubFilter"]) as PdfName)?.Value,
                handler.V,
                handler.R,
                handler.KeyLengthBits,
                handler.P,
                handler.Algorithm,
                handler.EncryptMetadata,
                handler.V >= 4 ? handler.StreamFilterName : null,
                handler.V >= 4 ? handler.StringFilterName : null,
                handler.V >= 4 ? handler.EmbeddedFileFilterName : null,
                match);
            if (match == PdfPasswordMatch.None)
            {
                if (password is not null)
                {
                    throw new PdfInvalidPasswordException("The password does not open the document.");
                }

                info.RequiresPassword = true;
                info.WasRepaired = doc.WasRepaired;
                info.Warnings = doc.Warnings.ToArray();
                return info;
            }
        }

        var catalog = doc.Catalog ?? throw new PdfCorruptException("The document catalog cannot be read.");
        if (doc.Resolve(catalog["Version"]) is PdfName cv && PdfVersion.TryParse(cv.Value, out var catalogVersion))
        {
            info.CatalogVersion = catalogVersion.ToString();
            PdfVersion.TryParse(doc.HeaderVersion, out var header);
            info.EffectiveVersion = PdfVersion.Max(header, catalogVersion).ToString();
        }

        if (doc.ResolveDictionary(catalog["Extensions"]) is { } extensions)
        {
            var list = new List<PdfExtension>();
            foreach (var (prefix, value) in extensions.Entries)
            {
                // A prefix maps to one extension dictionary or (PDF 2.0) an array of them.
                var items = doc.Resolve(value) is PdfArray arr ? arr.Items : new List<PdfObject> { value };
                foreach (var item in items)
                {
                    if (doc.ResolveDictionary(item) is { } ext)
                    {
                        list.Add(new PdfExtension(
                            prefix.Value,
                            (doc.Resolve(ext["BaseVersion"]) as PdfName)?.Value,
                            doc.Resolve(ext["ExtensionLevel"]) is PdfInteger lvl ? (int)lvl.Value : null));
                    }
                }
            }

            info.Extensions = list;
        }

        info.IsTagged = doc.ResolveDictionary(catalog["MarkInfo"]) is { } markInfo && doc.Resolve(markInfo["Marked"]) is PdfBoolean { Value: true };
        var pageNodes = PageTreeReader.ReadPageNodes(doc, _options.MaxNestingDepth, ct);
        var pages = pageNodes.Select(p => p.Info).ToList();
        info.Pages = pages;
        info.PageCount = pages.Count;
        info.Info = InfoReader.Read(doc);
        try
        {
            info.Xmp = XmpReader.Read(doc);
        }
        catch (PdfCorruptException ex) when (ex is not PdfLimitExceededException)
        {
            doc.AddWarning("The XMP metadata stream could not be read.");
        }

        info.PageLayout = (doc.Resolve(catalog["PageLayout"]) as PdfName)?.Value;
        info.PageMode = (doc.Resolve(catalog["PageMode"]) as PdfName)?.Value;
        info.Language = doc.Resolve(catalog["Lang"]) is PdfString lang ? TextString.Decode(lang.Value) : null;
        if (details != PdfInspectionDetails.None)
        {
            new DetailReader(doc, pageNodes, _options, ct).Fill(info, details);
        }

        info.IsSigned = ObjectGraph.HasSignedFields(doc, _options.MaxNestingDepth);
        info.WasRepaired = doc.WasRepaired;
        info.Warnings = doc.Warnings.ToArray();
        return info;
    }
}
