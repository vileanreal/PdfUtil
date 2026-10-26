using System.Formats.Asn1;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfUtil.Internal;
using PdfUtil.Internal.Content;
using PdfUtil.Internal.Document;
using PdfUtil.Internal.Fonts;
using PdfUtil.Internal.Objects;
using PdfUtil.Internal.Parsing;
using PdfUtil.Internal.Protection;
using PdfUtil.Internal.Text;
using PdfUtil.Internal.Writing;

namespace PdfUtil.Signatures.Internal;

/// <summary>
/// PAdES baseline B-B (and B-T with a timestamp) signing (F17): the signature dictionary reserves space for the byte
/// range and the CMS value, the update is appended incrementally, and the reserved space is filled after hashing.
/// The result is verified by the incremental writer and by <see cref="SignatureVerifier"/>.
/// </summary>
internal sealed class SigningEngine
{
    private const string SigningCertificateV2Oid = "1.2.840.113549.1.9.16.2.47";
    private const string TimestampTokenOid = "1.2.840.113549.1.9.16.2.14";
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly PdfUtilOptions _lib;
    private readonly PdfLog _log;

    public SigningEngine(PdfUtilOptions lib, PdfLog log)
    {
        _lib = lib;
        _log = log;
    }

    private sealed class ReservationTooSmallException : Exception
    {
        public ReservationTooSmallException(int needed) => Needed = needed;

        public int Needed { get; }
    }

    public static void Validate(PdfSignOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Certificate is not { } cert)
        {
            throw new ArgumentException("Certificate is required.", nameof(options));
        }

        if (options.PrivateKey is null && !cert.HasPrivateKey)
        {
            throw new ArgumentException("The certificate has no private key; attach one or set PrivateKey.", nameof(options));
        }

        if (options.PrivateKey is not (null or RSA or ECDsa) || (options.PrivateKey is null && cert.GetRSAPublicKey() is null && cert.GetECDsaPublicKey() is null))
        {
            throw new ArgumentException("Only RSA and ECDSA keys are supported for signing.", nameof(options));
        }

#if NETFRAMEWORK
        // .NET Framework's SignedCms signs only with the certificate's own key, and not with ECDSA.
        if (options.PrivateKey is not null)
        {
            throw new PlatformNotSupportedException("On .NET Framework the private key must be attached to the certificate; PrivateKey is supported on .NET 8 and later.");
        }

        if (cert.GetRSAPublicKey() is null)
        {
            throw new PlatformNotSupportedException("On .NET Framework only RSA certificates can sign; ECDSA is supported on .NET 8 and later.");
        }
#endif

        if (DigestOid(options.DigestAlgorithm) is null)
        {
            throw new ArgumentException("DigestAlgorithm must be SHA256, SHA384 or SHA512.", nameof(options));
        }

        if (options.ReservedSize is < 1024 or > 1 << 20)
        {
            throw new ArgumentException("ReservedSize must be between 1 KB and 1 MB.", nameof(options));
        }

        if (options.TimestampServer is { } tsa && tsa.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("TimestampServer must be an http or https URL.", nameof(options));
        }

        if (options.FieldName is { Length: 0 } || options.FieldName?.Contains('.') == true)
        {
            throw new ArgumentException("FieldName must be a non-empty partial name without periods.", nameof(options));
        }
    }

    private static string? DigestOid(HashAlgorithmName name) => name.Name switch
    {
        "SHA256" => "2.16.840.1.101.3.4.2.1",
        "SHA384" => "2.16.840.1.101.3.4.2.2",
        "SHA512" => "2.16.840.1.101.3.4.2.3",
        _ => null,
    };

    public PdfSignResult Sign(PdfSource input, Stream buffer, PdfSignOptions options, CancellationToken ct)
    {
        Validate(options);
        var cert = options.Certificate!;
        var reserved = options.ReservedSize ?? EstimateSize(options);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return SignOnce(input, buffer, options, cert, reserved, ct);
            }
            catch (ReservationTooSmallException ex) when (attempt == 0)
            {
                _log.Info("The reserved signature space was too small; signing again with more space.");
                reserved = ex.Needed + 2048;
                buffer.SetLength(0);
            }
            catch (ReservationTooSmallException ex)
            {
                throw new PdfUtilException($"The signature needs {ex.Needed} bytes but only {reserved} were reserved; set ReservedSize.");
            }
        }
    }

    private static int EstimateSize(PdfSignOptions options)
    {
        var certs = options.Certificate!.RawData.Length + (options.AdditionalCertificates?.Cast<X509Certificate2>().Sum(c => c.RawData.Length) ?? 0);
        return certs + 2048 + (options.TimestampServer is null ? 0 : 8192);
    }

    private PdfSignResult SignOnce(PdfSource input, Stream buffer, PdfSignOptions options, X509Certificate2 cert, int reserved, CancellationToken ct)
    {
        using var doc = PdfDocument.Open(input, _lib, _log, strict: false, ct);
        if (doc.IsEncrypted)
        {
            throw new NotSupportedException("Signing encrypted documents is not supported: a signature must be appended as an incremental update, which PdfUtil writes only for unencrypted documents.");
        }

        IncrementalEngine.EnsureSupported(doc);
        var pages = PageTreeReader.ReadPageNodes(doc, _lib.MaxNestingDepth, ct);
        var fields = new DetailReader(doc, pages, _lib, ct).ReadFieldNodes();
        var catalogNumber = ((PdfReference)doc.Trailer[PdfName.Root]!).Id.Number;
        var edits = new ObjectEdits(doc);
        var now = options.SigningTime ?? DateTimeOffset.Now;
        var signerName = options.SignerName ?? cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

        // The signature dictionary, with fixed-size placeholders for the byte range and the value.
        var byteRange = ReservedSpace.Text("[0 0 0 0]", 64);
        var contents = ReservedSpace.HexString(reserved);
        var sig = new PdfDictionary
        {
            [PdfName.Type] = PdfName.Get("Sig"),
            [PdfName.Filter] = PdfName.Get("Adobe.PPKLite"),
            ["SubFilter"] = PdfName.Get("ETSI.CAdES.detached"),
            ["ByteRange"] = byteRange,
            ["Contents"] = contents,
            ["M"] = new PdfString(Encoding.ASCII.GetBytes(PdfDate.Format(now))),
        };
        foreach (var (key, value) in new[] { ("Name", signerName), ("Reason", options.Reason), ("Location", options.Location), ("ContactInfo", options.ContactInfo) })
        {
            if (!string.IsNullOrEmpty(value))
            {
                sig[key] = new PdfString(TextString.Encode(value));
            }
        }

        sig["Prop_Build"] = new PdfDictionary { ["App"] = new PdfDictionary { ["Name"] = PdfName.Get("PdfUtil") } };
        var sigRef = new PdfReference(edits.Add(sig), 0);

        // The field: an existing unsigned signature field, or a new merged field and widget.
        var existing = options.FieldName is null ? null : fields.Cast<FieldNode?>().FirstOrDefault(f => f!.Value.FullName == options.FieldName);
        string fieldName;
        if (existing is { } field)
        {
            if (field.FieldType != "Sig")
            {
                throw new ArgumentException($"The field \"{options.FieldName}\" is not a signature field.", nameof(options));
            }

            if (field.Value is not null && doc.Resolve(field.Value) is PdfDictionary)
            {
                throw new ArgumentException($"The field \"{options.FieldName}\" is already signed.", nameof(options));
            }

            if (field.ObjectNumber is not int number)
            {
                throw new ArgumentException($"The field \"{options.FieldName}\" is a direct object and cannot be signed.", nameof(options));
            }

            var copy = field.Field.ShallowClone();
            copy["V"] = sigRef;
            if (options.Appearance is not null && (doc.Resolve(copy["Subtype"]) as PdfName)?.Value == "Widget" && doc.Resolve(copy["Rect"]) is PdfArray rect)
            {
                copy["AP"] = new PdfDictionary { ["N"] = new PdfReference(edits.Add(AppearanceStream(options, signerName, now, RectSize(doc, rect), Matrix.Identity)), 0) };
            }

            edits.Replace(number, copy);
            fieldName = field.FullName;
        }
        else
        {
            fieldName = options.FieldName ?? NextName(fields);
            var pageIndex = (options.Appearance?.Page ?? 1) - 1;
            if (pageIndex < 0 || pageIndex >= pages.Count || pages[pageIndex].ObjectNumber == 0)
            {
                throw new ArgumentException($"The document has no page {pageIndex + 1} to show the signature on.", nameof(options));
            }

            var page = pages[pageIndex];
            var widget = new PdfDictionary
            {
                [PdfName.Type] = PdfName.Get("Annot"),
                ["Subtype"] = PdfName.Get("Widget"),
                ["FT"] = PdfName.Get("Sig"),
                ["T"] = new PdfString(TextString.Encode(fieldName)),
                ["V"] = sigRef,
                ["F"] = new PdfInteger(132), // Print, Locked
                ["P"] = new PdfReference(page.ObjectNumber, doc.GetGeneration(page.ObjectNumber)),
            };
            if (options.Appearance is { } appearance)
            {
                var toUser = PageGeometry.DisplayToUser(page.Info.Rotate, PageGeometry.VisibleBox(page.Info));
                var b = appearance.Bounds;
                var corners = new[] { toUser.Transform(b.LowerLeftX, b.LowerLeftY), toUser.Transform(b.UpperRightX, b.UpperRightY), toUser.Transform(b.LowerLeftX, b.UpperRightY), toUser.Transform(b.UpperRightX, b.LowerLeftY) };
                widget["Rect"] = new PdfArray(new PdfObject[] { new PdfReal(corners.Min(c => c.X)), new PdfReal(corners.Min(c => c.Y)), new PdfReal(corners.Max(c => c.X)), new PdfReal(corners.Max(c => c.Y)) });
                var rotation = new Matrix(toUser.A, toUser.B, toUser.C, toUser.D, 0, 0);
                widget["AP"] = new PdfDictionary { ["N"] = new PdfReference(edits.Add(AppearanceStream(options, signerName, now, (b.Width, b.Height), rotation)), 0) };
            }
            else
            {
                widget["Rect"] = new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0) });
            }

            var widgetRef = new PdfReference(edits.Add(widget), 0);
            var pageCopy = page.Page.ShallowClone();
            var annots = doc.Resolve(page.Page["Annots"]) is PdfArray a ? new PdfArray(a.Items) : new PdfArray();
            annots.Add(widgetRef);
            pageCopy["Annots"] = annots;
            edits.Replace(page.ObjectNumber, pageCopy);

            var catalog = ((PdfDictionary)doc.GetObject(catalogNumber)!).ShallowClone();
            var form = doc.ResolveDictionary(catalog["AcroForm"])?.ShallowClone() ?? new PdfDictionary();
            var formFields = doc.Resolve(form["Fields"]) is PdfArray ff ? new PdfArray(ff.Items) : new PdfArray();
            formFields.Add(widgetRef);
            form["Fields"] = formFields;
            form["SigFlags"] = new PdfInteger(3);
            if (catalog["AcroForm"] is PdfReference formRef)
            {
                edits.Replace(formRef.Id.Number, form);
            }
            else
            {
                catalog["AcroForm"] = form;
                edits.Replace(catalogNumber, catalog);
            }
        }

        // Write, then fill the byte range and the CMS value before verification.
        IReadOnlyList<long> signedRange = Array.Empty<long>();
        var signatureSize = 0;
        DateTimeOffset? timestampTime = null;
        var update = new IncrementalEngine(_lib, _log).Append(doc, new IncrementalSettings
        {
            Progress = new ProgressReporter(options.Progress),
            Edits = edits,
            Fill = output =>
            {
                var start = contents.Offset;
                var end = start + contents.Width;
                signedRange = new[] { 0, start, end, output.Length - end };
                output.Fill(byteRange, Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"[0 {start} {end} {output.Length - end}]")));
                var data = new byte[start + (output.Length - end)];
                output.Read(0, (int)start).CopyTo(data, 0);
                output.Read(end, (int)(output.Length - end)).CopyTo(data, start);
                var (cms, time) = CreateSignature(data, options, cert, ct);
                timestampTime = time;
                signatureSize = cms.Length;
                if (cms.Length > contents.Capacity)
                {
                    throw new ReservationTooSmallException(cms.Length);
                }

                output.Fill(contents, cms);
            },
        }, buffer, ct);

        // The signature must verify before anyone gets the file.
        buffer.Position = 0;
        using (var check = new PdfSource(buffer, ownsStream: false))
        {
            var result = new SignatureVerifier(_lib, _log).Verify(check, new PdfSignatureVerificationOptions(), ct);
            var mine = result.Signatures.FirstOrDefault(s => s.FieldName == fieldName);
            if (mine is null || !mine.DigestMatches || !mine.SignatureIsValid || !mine.CoversWholeFile)
            {
                throw new PdfVerificationException("The signed output failed self-verification: " + string.Join(" ", mine?.Problems ?? new[] { "the signature was not found." }));
            }

            var earlier = result.Signatures.Where(s => s.FieldName != fieldName && !(s.DigestMatches && s.SignatureIsValid)).ToList();
            if (earlier.Count > 0 && result.Signatures.Count > 1)
            {
                _log.Warn("An earlier signature of the input was already invalid.");
            }
        }

        buffer.Position = 0;
        return new PdfSignResult
        {
            FieldName = fieldName,
            ByteRange = signedRange,
            SignatureSize = signatureSize,
            ReservedSize = reserved,
            TimestampTime = timestampTime,
            Warnings = update.Warnings.Where(w => !w.StartsWith("The document is signed.", StringComparison.Ordinal)).ToList(),
        };
    }

    private static string NextName(List<FieldNode> fields)
    {
        var names = fields.Select(f => f.FullName).ToHashSet(StringComparer.Ordinal);
        for (var i = 1; ; i++)
        {
            var name = "Signature" + i.ToString(CultureInfo.InvariantCulture);
            if (!names.Contains(name))
            {
                return name;
            }
        }
    }

    private static (double Width, double Height) RectSize(PdfDocument doc, PdfArray rect)
    {
        var v = rect.Items.Take(4).Select(i => doc.Resolve(i) switch { PdfInteger n => (double)n.Value, PdfReal r => r.Value, _ => 0 }).ToArray();
        return v.Length == 4 ? (Math.Abs(v[2] - v[0]), Math.Abs(v[3] - v[1])) : (0, 0);
    }

    /// <summary>A form XObject showing the signature text in Helvetica, fitted to the box, with a thin border.</summary>
    private static PdfStream AppearanceStream(PdfSignOptions options, string signerName, DateTimeOffset now, (double Width, double Height) size, Matrix rotation)
    {
        var (w, h) = size;
        var text = options.Appearance?.Text ?? string.Join("\n", new[]
        {
            "Digitally signed by " + signerName,
            "Date: " + now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            options.Reason is null ? null : "Reason: " + options.Reason,
            options.Location is null ? null : "Location: " + options.Location,
        }.Where(l => l is not null));
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var widest = lines.Max(l => l.Sum(c => StandardFonts.Width("Helvetica", c)) / 1000.0);
        var fontSize = Math.Min(widest > 0 ? (w - 8) / widest : 12, (h - 8) / (lines.Length * 1.2));
        fontSize = Math.Clamp(fontSize, 2, 14);
        string F(double v) => PdfReal.Format(Math.Round(v, 3));
        using var content = new MemoryStream();
        void Write(string s) => content.Write(Encoding.ASCII.GetBytes(s));
        Write($"q 0.5 G 0.5 w 0.25 0.25 {F(w - 0.5)} {F(h - 0.5)} re S Q\nBT /Helv {F(fontSize)} Tf 0 g\n");
        for (var i = 0; i < lines.Length; i++)
        {
            var shown = new string(lines[i].Select(c => FontEncodings.EncodeWinAnsi(c.ToString(), out var bad) is not null && bad.Count == 0 ? c : '?').ToArray());
            var bytes = FontEncodings.EncodeWinAnsi(shown, out _);
            Write($"1 0 0 1 4 {F(h - 4 - (fontSize * 0.9) - (i * fontSize * 1.2))} Tm\n");
            content.Write(PdfWriter.Serialize(new PdfString(bytes), 4));
            Write(" Tj\n");
        }

        Write("ET\n");
        var dict = new PdfDictionary
        {
            [PdfName.Type] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Form"),
            ["BBox"] = new PdfArray(new PdfObject[] { new PdfInteger(0), new PdfInteger(0), new PdfReal(w), new PdfReal(h) }),
            ["Resources"] = new PdfDictionary
            {
                ["Font"] = new PdfDictionary
                {
                    ["Helv"] = new PdfDictionary { [PdfName.Type] = PdfName.Get("Font"), ["Subtype"] = PdfName.Get("Type1"), ["BaseFont"] = PdfName.Get("Helvetica"), ["Encoding"] = PdfName.Get("WinAnsiEncoding") },
                },
            },
        };
        if (rotation != Matrix.Identity)
        {
            dict["Matrix"] = new PdfArray(new PdfObject[] { new PdfReal(rotation.A), new PdfReal(rotation.B), new PdfReal(rotation.C), new PdfReal(rotation.D), new PdfInteger(0), new PdfInteger(0) });
        }

        return new PdfStream(dict, content.ToArray());
    }

    /// <summary>Creates the detached CAdES signature over <paramref name="data"/>, with an ESS signing-certificate-v2 attribute and, if requested, a timestamp.</summary>
    private static (byte[] Cms, DateTimeOffset? TimestampTime) CreateSignature(byte[] data, PdfSignOptions options, X509Certificate2 cert, CancellationToken ct)
    {
        var cms = new SignedCms(new ContentInfo(data), detached: true);
#if NETFRAMEWORK
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert)
#else
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert, options.PrivateKey)
#endif
        {
            DigestAlgorithm = new Oid(DigestOid(options.DigestAlgorithm)!),
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        if (options.AdditionalCertificates is { } extra)
        {
            signer.Certificates.AddRange(extra);
        }

        signer.SignedAttributes.Add(new AsnEncodedData(new Oid(SigningCertificateV2Oid), SigningCertificateV2(cert)));
        cms.ComputeSignature(signer, silent: true);
        DateTimeOffset? time = null;
#if NETFRAMEWORK
        if (options.TimestampServer is { } tsa)
        {
            var encoded = cms.Encode();
            var signature = Rfc3161.SignatureValue(encoded);
            var nonce = RandomNumberGenerator.GetBytes(8);
            using var body = new ByteArrayContent(Rfc3161.CreateRequest(signature, nonce));
            body.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
            var client = options.TimestampHttpClient ?? SharedClient;
            using var response = client.PostAsync(tsa, body, ct).ConfigureAwait(false).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                throw new PdfUtilException($"The timestamp server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var bytes = response.Content.ReadAsByteArrayAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            TimestampToken token;
            try
            {
                token = Rfc3161.ProcessResponse(bytes, signature, nonce);
            }
            catch (CryptographicException ex)
            {
                throw new PdfUtilException("The timestamp server's response is not a valid timestamp for this signature: " + ex.Message, ex);
            }

            return (Rfc3161.AddUnsignedAttribute(encoded, TimestampTokenOid, token.Encoded), token.Timestamp);
        }
#else
        if (options.TimestampServer is { } tsa)
        {
            var info = cms.SignerInfos[0];
            var request = Rfc3161TimestampRequest.CreateFromSignerInfo(info, HashAlgorithmName.SHA256, requestSignerCertificates: true, nonce: RandomNumberGenerator.GetBytes(8));
            using var body = new ByteArrayContent(request.Encode());
            body.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
            var client = options.TimestampHttpClient ?? SharedClient;
            using var response = client.PostAsync(tsa, body, ct).ConfigureAwait(false).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                throw new PdfUtilException($"The timestamp server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var bytes = response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false).GetAwaiter().GetResult();
            Rfc3161TimestampToken token;
            try
            {
                token = request.ProcessResponse(bytes, out _);
            }
            catch (CryptographicException ex)
            {
                throw new PdfUtilException("The timestamp server's response is not a valid timestamp for this signature: " + ex.Message, ex);
            }

            info.AddUnsignedAttribute(new AsnEncodedData(new Oid(TimestampTokenOid), token.AsSignedCms().Encode()));
            time = token.TokenInfo.Timestamp;
        }
#endif

        return (cms.Encode(), time);
    }

    /// <summary>
    /// The ESS signing-certificate-v2 attribute value (RFC 5035), required by PAdES:
    /// SEQUENCE { SEQUENCE OF ESSCertIDv2 { certHash (SHA-256), issuerSerial } }.
    /// </summary>
    internal static byte[] SigningCertificateV2(X509Certificate2 cert)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                {
                    writer.WriteOctetString(SHA256.HashData(cert.RawData));
                    using (writer.PushSequence())
                    {
                        using (writer.PushSequence())
                        {
                            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, isConstructed: true)))
                            {
                                writer.WriteEncodedValue(cert.IssuerName.RawData);
                            }
                        }

                        writer.WriteInteger(cert.SerialNumberBytes.Span);
                    }
                }
            }
        }

        return writer.Encode();
    }
}
