# Requirements traceability

Code paths are relative to `src/PdfUtil/`, and tests are in `tests/PdfUtil.Tests/`.

## §2 Internal implementations

| Requirement | Code | Test / evidence |
|---|---|---|
| RC4 implemented internally | `Internal/Security/Ciphers.cs` (`Rc4`) | `CryptoKnownAnswerTests.Rc4_MatchesPublishedVectors` |
| SASLprep with embedded tables | `Internal/Security/SaslPrep.cs` | `PasswordEncodingTests.SaslPrep_Behaviour` |
| PDFDocEncoding table | `Internal/Text/PdfDocEncoding.cs` | `MetadataEncodingTests.TextString_DecodesAllForms` |
| DI and logging in a separate extension package | `../PdfUtil.Extensions.DependencyInjection/*.csproj` | `DiTests` |
| Test PDFs are generated, not loaded from sample files | `Infrastructure/TestPdfBuilder.cs` | All tests generate their PDFs |

## §4 Versions and reader compatibility

| Requirement | Code | Test |
|---|---|---|
| Read 1.0–2.0 and every structure | `Internal/Document/PdfDocument.cs` | `VersionMatrixTests.OutputVersion_…` (9 versions), `VersionMatrixTests.EveryStructure_…` (18 structures × 3 algorithms) |
| Encrypted input R2–R6 | `StandardSecurityHandler.Parse`, `SecurityAlgorithms` | `EncryptedInputTests` (5 revisions × 3 layouts) |
| Opens in standard viewers | Writer and `/Encrypt` rules below | `InteropTests.PdfJs_OpensDocuments`, `InteropTests.PdfKit_OpensDocuments` (≈60-file corpus), qpdf/mutool when installed, `docs/manual-compat-checklist.md` |
| Version = max(source, required); never lowered; catalog `/Version` consistent | `ProtectionEngine.BuildPlan`, `ProtectionPlan.TransformCatalog` | `VersionMatrixTests.OutputVersion_…`, `CatalogVersion_IsKeptConsistentWithHeader` |
| R6 → 1.7 + ADBE ext. 8, or keep 2.0 | `ProtectionPlan.TransformCatalog` | `VersionMatrixTests.OutputVersion_…` |
| Exact `/Encrypt` for R3/R4/R6; hex `/O /U /OE /UE /Perms`; `/EncryptMetadata` omitted by default | `StandardSecurityHandler.Create` | `VersionMatrixTests.AssertEncryptDictionary`, `OutputVerifier.VerifyEncryptDictionary`, `ContentPreservationTests.EncryptMetadataFalse_…` |
| Linearization removed | Reachability in `ObjectGraph.FindReachable` | `VersionMatrixTests.PreviouslyLinearizedOutput_…` |
| PDF/A: throw or proceed with a warning | `ProtectionEngine.BuildPlan` | `ComplianceTests.PdfA_*` |
| AES-256 default; Aes128 for old readers | `PdfProtectionOptions.Algorithm` | README "Algorithms and reader compatibility" |
| Byte-exact content, `/Length` updated, no re-compression | `ProtectionEngine.WriteObject`, `OutputVerifier.VerifyObject` | `ContentPreservationTests.*`, `StreamLengths_AreUpdatedToEncryptedLengths` |
| Single classic xref, 20-byte entries, one trailer | `Internal/Writing/PdfWriter.cs` | `VersionMatrixTests.AssertSingleTrailerStructure` |

## §7 Trailer `/ID` and UTF-16

| Requirement | Code | Test |
|---|---|---|
| `/ID` as raw bytes, never text-decoded, written as hex | `PdfDocument.GetPermanentId`, `ProtectionEngine.Write`, `FileIdentifier` | `IdHandlingTests.PermanentId_IsPreservedByteForByte_…` (FE FF, EF BB BF, NUL, `\()`, CR/LF, lengths 1/16/32/64), `IdBytes_AreNeverTextDecoded_ByTheReader` |
| Preserve `/ID[0]`; generate 16 random bytes if missing or malformed | `ProtectionEngine.BuildPlan` | `MissingOrMalformedId_GeneratesSixteenRandomBytes` |
| `/ID[0]` is the key-derivation input, fixed first | `FileIdentifier` → `StandardSecurityHandler.Create` | `ChangingPermanentId_BreaksLegacyKeys_…`, `OutputVerifier` (`/ID[0]` check) |
| `/ID[1]` regenerated and not used in the key | `ProtectionEngine.BuildPlan` | `ChangingId_IsRegenerated_…`, `ChangingId_IsNotPartOfKeyDerivation` |
| Last trailer wins; single output trailer | `PdfDocument.ReadXrefChain` | `IncrementalUpdate_LastTrailerIdWins`, `AssertSingleTrailerStructure` |
| Already encrypted: throw, or re-encrypt with the owner password | `ProtectionEngine.BuildPlan` | `VerificationAndFailureTests.AlreadyEncrypted_*`, `IdHandlingTests.ReEncryption_PreservesPermanentIdOfEncryptedSource` |
| Decode PDFDoc / UTF-16BE / UTF-8, surrogates, ESC lang ESC | `Internal/Text/TextString.cs` | `MetadataEncodingTests.TextString_DecodesAllForms`, `Utf16BeInfoStrings_…` (CJK, emoji, Arabic, ñ) |
| Decrypt before decode; encrypt the BOM; no double encoding | `PdfDocument.DecryptStrings`, `ObjectGraph.MapStrings` | `Utf16BeInfoStrings_…` (raw bytes check) |
| No UTF-8 text strings below 2.0 | `ProtectionPlan.TransformInfo` | `Utf8InfoStrings_AreConvertedToUtf16_BelowPdf20`, `Utf8InfoStrings_ArePreservedInPdf20` |

## §8 A correct password never becomes invalid

| Requirement | Code | Test |
|---|---|---|
| R6: SASLprep + UTF-8, 127-byte truncation in both paths | `PasswordEncoder` | `Aes256_NfcAndNfdForms_BothVerify`, `Aes256_UnicodeSpaces_…`, `Aes256_PasswordsLongerThan127Bytes_…` |
| R2–R4: PDFDocEncoding, reject unrepresentable characters, reject >32 bytes unless opted in | `PasswordEncoder` | `Legacy_NonPdfDocEncodingPasswords_AreRejected`, `Legacy_PasswordsLongerThan32Bytes_RequireOptIn` |
| Reader-dependent passwords rejected by default (from interop findings) | `PasswordEncoder` + `PdfPasswordPolicy` | `Legacy_InconsistentlyEncodedCharacters_AreRejected`, `Aes256_ReaderDependentPasswords_RequireSpecificationPolicy` |
| One shared encoder; no trimming or case folding; empty user password | `PasswordEncoder` | `Encoder_NeverTrimsOrCaseFolds`, `EmptyUserPassword_WithOwnerPassword` |
| Mandatory self-verification (user, owner, wrong, `/ID[0]`, all strings and streams, Flate, pages, metadata, xref offsets) | `Internal/Protection/OutputVerifier.cs` | Runs on every protection in every test |
| No partial output: temp file + atomic move; stream verified before copy | `PdfProtector.ProtectFileAsync`, `ProtectAsync` | `ProtectFile_IsAtomic_…`, `StreamOutput_IsUntouchedOnFailure` |
| `/O /U /OE /UE /Perms` per spec; Algorithm 2.B | `SecurityAlgorithms` | `CryptoKnownAnswerTests.*` (independent vectors) |
| AES: random IV, PKCS#7, empty input still padded | `AesCipher` | `Aes_KnownAnswerAndRoundTrip` |
| `/P` signed, reserved bits set, same value in key | `SecurityAlgorithms.ComputeP` | `ComputeP_SetsReservedBits`, `Permissions_RoundTrip` |
| `/EncryptMetadata` honoured | `StandardSecurityHandler`, `ProtectionPlan.IsOutputStreamEncrypted` | `EncryptMetadataFalse_*`, `Algorithm2_R4WithoutMetadataEncryption` |
| 1,000 randomized runs | | `DeterminismTests.ThousandRandomProtections_AllVerify` |

## Roadmap Phase 1: core editing

| Requirement | Code | Test |
|---|---|---|
| Reusable rewrite pipeline with self-verification from an expected-output plan | `Internal/Protection/RewriteEngine.cs`, `RewritePlan.cs`, `OutputVerifier.cs` | All protection tests unchanged (334 before the refactor); every editing test below |
| Three output security modes: none, new, preserved | `RewritePlan.cs` (`NoOutputEncryption`, `NewOutputEncryption`, `PreservedOutputEncryption`) | `DecryptTests`, `ChangeSecurityTests`, `EditInfoTests` |
| F1 decrypt; owner password required by default | `Internal/Protection/EditEngine.cs` (`Decrypt`), `PdfEditor` | `DecryptTests` (R2–R6 × classic/object streams/hybrid, every protected structure, user password refused/allowed, unencrypted no-op, atomic in-place) |
| F2 change security; unspecified settings kept | `EditEngine.ChangeSecurity`, `StandardSecurityHandler.RecoverUserPassword` | `ChangeSecurityTests` (keep legacy passwords byte for byte, legacy → AES-256, AES-256 user password rules, deprecated revisions upgraded, `PdfNotEncryptedException`) |
| F3 edit info without changing protection | `EditEngine.EditInfo` (preserved `/Encrypt` and file key) | `EditInfoTests` (R3/R4/R6: unknown user password still works, `/O /U /P` unchanged, unencrypted stays unencrypted, R2/R5 refused) |
| F4 batch processing | `PdfBatch.cs` (`PdfBatchProcessor`) | `BatchAndProgressTests` (one failure doesn't stop the batch, `StopOnFirstError`, bounded parallelism, order, cancellation, DI) |
| F5 progress, throttled | `PdfProgress.cs`, `Internal/ProgressReporter.cs` | `BatchAndProgressTests.Progress_*` (stage order, first and last report, throttling, throwing callback) |
| Stream/file output guarantees shared by every service | `Internal/IO/OutputRunner.cs` | `DecryptFile_InPlace_IsAtomic`, `ProtectFile_IsAtomic_…`, `StreamOutput_IsUntouchedOnFailure` |
| Interop of Phase 1 outputs | `InteropTests.Corpus` (decrypted, edit-info preserved R3/R4/R6, change security) | `InteropTests.PdfJs_OpensDocuments`, `InteropTests.PdfKit_OpensDocuments` |

## Roadmap Phase 2: analysis, cleanup and attachments

| Requirement | Code | Test |
|---|---|---|
| Object edits in the rewrite pipeline (replaced and new objects, unreachable ones dropped), verified like every other write | `RewritePlan.cs` (`ObjectEdits`, `Transform`), `RewriteEngine.cs` (`Edits`, `RemoveInfo`), `ObjectGraph.FindReachable` (lookup) | All earlier tests unchanged; `RepairTests`, `SanitizeTests`, `AttachmentTests` |
| Reader diagnostics with codes and object numbers | `PdfDocument.AddWarning`/`Diagnostics`, `PageTreeReader` | `ValidationTests` |
| F6 opt-in details: fonts, images, attachments, bookmarks, form fields, signatures, page labels, layout/mode/language, actions | `PdfInspection.cs`, `PdfAnalyzer.InspectAsync`, `Internal/Analysis/InspectionEngine.cs`, `Internal/Document/DetailReader.cs`, `NameTrees.cs` | `InspectionTests` (every font kind and colour space, `/Kids` trees, named destinations, every field type, byte range coverage, label formats, action locations, every structure × R2–R6, hostile cycles) |
| F7 validation report that never throws for a readable damaged file | `PdfValidation.cs`, `PdfAnalyzer.ValidateAsync`, `Internal/Analysis/Validator.cs`, `PdfDate.Check` | `ValidationTests` (xref, lengths, dangling references, dates, page tree, encryption dictionary, Flate truncation and garbage, unreadable input, limits, ordering, cap, progress, cancellation) |
| F8 repair: clean single revision, exact repair list, pages never silently dropped, security kept or removed | `PdfCleanup.cs` (`PdfCleaner.RepairAsync`), `CleanupEngine.Repair`, `PageTreeNormalizer` | `RepairTests` (each kind of damage repaired, output validates clean, page loss and orphan pages reported, R3/R4/R6 kept, removal, atomic in-place) |
| F9 sanitize with independent switches and a list of everything removed | `CleanupEngine.Sanitize`, `Sanitizer` | `SanitizeTests` (JavaScript everywhere incl. `/Next` chains, actions, attachments, annotations with/without links, Info, XMP, PDF/A warning, preserved security, every structure) |
| F10 attachments: add (F/UF, Params with MD5, MIME, AFRelationship + /AF), remove, extract, list; sorted flat tree from `/Kids` trees | `PdfAttachments.cs`, `Internal/Protection/AttachmentEngine.cs`, `NameTree.Build` | `AttachmentTests` (parameters, Kids flattening, Unicode names, replace, large content, PDF/A warnings, version 1.3, R3/R4/R6, external and LZW files, Kids cycle) |
| Facade and DI | `Pdf.cs` (Inspect, Validate, Repair, Sanitize, List/Add/Remove/ExtractAttachment), `AddPdfUtil` | `DiTests.AddPdfUtil_RegistersPhase2Services`, `DiTests.Phase2Services_AreThreadSafe` |
| Interop of Phase 2 outputs | `InteropTests.Corpus` (repaired, sanitized, attachments added/removed, rich content plain and protected) | `InteropTests.PdfJs_OpensDocuments`, `InteropTests.PdfKit_OpensDocuments` |

## Roadmap Phase 3: output options

| Requirement | Code | Test |
|---|---|---|
| F11 opt-in object streams and cross-reference stream; version raised to 1.5 | `PdfWriteOptions.cs`, `RewriteEngine.WriteObjectStreams`, `PdfWriter.WriteXrefStream`, `Write` on every write options type | `CompressedOutputTests` (every algorithm, every operation, preserved R3/R4/R6, several object streams, every input structure, smaller output) |
| F11 encryption rules: object stream encrypted as a whole, xref stream never, signature and encryption dictionaries not packed | `RewriteEngine.CanPack`, `WriteObjectStreams` | `CompressedOutputTests.EncryptionRules_*`, `AssertCompressedLayout` |
| F11 self-verification of the compressed layout | `OutputVerifier` (`Packed`, `ContainerNumbers`, `WrittenSize`) | All `CompressedOutputTests` |
| F12 incremental save for edit info and attachments: source xref style, `/Prev`, same `/ID[0]`, new `/ID[1]` | `PdfSaveMode`, `Internal/Protection/IncrementalEngine.cs`, `PdfWriter.WriteIncrementalXrefTable`, `PdfDocument.StartXref`/`LatestSectionIsStream` | `IncrementalTests` (every layout, chained updates, no Info/ID, no trailing newline, linearized, signed, attachments, file API) |
| F12 verification: original bytes unchanged, new revision reads correctly, other objects untouched | `IncrementalEngine.Verify` | `IncrementalTests.AssertAppended` |
| F12 unsupported cases refused | `IncrementalEngine.EnsureSupported`, `EditEngine.ValidateSaveMode` | `IncrementalTests.Unsupported_EncryptedOrDamaged` |
| Signing hook: reserve space, fill after writing, before verification | `ReservedSpace`, `IncrementalSettings.Fill`, `IncrementalOutput` | `IncrementalTests.ReservedSpace_IsFilledAfterWriting` (byte range covers the whole file) |
| Interop of Phase 3 outputs | `InteropTests.Corpus` (compressed × every algorithm, decrypted, repaired from 1.0, preserved R6; incremental classic/xref stream/hybrid/linearized/signed/attachment) | `InteropTests.PdfJs_OpensDocuments`, `InteropTests.PdfKit_OpensDocuments` |

## Roadmap Phase 4: page operations

| Requirement | Code | Test |
|---|---|---|
| Shared page-range syntax `"1-3,5,8-"` (plus `-n`, `odd`, `even`, `last`) | `PdfPageRange.cs` | `PageOperationTests.PageRange_*` |
| F13 rotate by ranges, combined with inherited `/Rotate`; expected pages verified | `Internal/Pages/PageEditEngine.cs` (`Rotate`), `RewritePlan.ExpectedPages` | `PageOperationTests.Rotate_*` (inherited rotation, protected R4/R6, invalid angle) |
| F13 delete: flat `/Kids` and `/Count` with inherited attributes, removal of links, outline destinations, named destinations, open action, form fields and widgets, other references; page labels renumbered; each removal reported | `PageRemover` | `PageOperationTests.Delete_*` (nested tree, rich document with internal links, protected, every page refused) |
| F14 merge: renumbered copies, inherited attributes resolved, outlines per document / flat / none, named destinations and fields renamed on conflict, `/DR` merged, `/NeedAppearances` kept, attachments, optional deduplication, highest version, passwords, optional protection | `Internal/Pages/ComposeEngine.cs` (`SourceCopier`, `Composer`, `ComposeEngine.Merge`) | `PageOperationTests.Merge_*` (order and content bytes, link targets, renames, page selection, protected inputs and output, versions, deduplication, every structure, files) |
| F14 split by ranges or every N pages, copying only what each part needs | `ComposeEngine.Split` | `PageOperationTests.Split_*` |
| Verification of composed documents: every object round-trips, pages match the source's geometry and content bytes | `ComposeEngine.VerifyComposed` | All merge and split tests |
| Interop of Phase 4 outputs | `InteropTests.Corpus` (rotated, rotated R6, deleted, merged, merged protected, split part) | `InteropTests.PdfJs_OpensDocuments`, `InteropTests.PdfKit_OpensDocuments` |

## Roadmap Phase 5: content

| Requirement | Code | Test |
|---|---|---|
| F15 content-stream tokenizer and graphics/text state machine (BT/ET, Tf, Td/TD/Tm/T*, Tj/TJ/'/", Tc/Tw/Tz/TL/Ts, cm, q/Q, colours), form XObjects, inline images | `Internal/Content/ContentInterpreter.cs` | `TextExtractionTests` (fixtures `lines`, `tj-spacing`, `form-xobject`, `inline-image`, colour and size) |
| F15 font decoding: ToUnicode (bfchar, bfrange, arrays, multi-byte), standard/WinAnsi/MacRoman/PDFDoc/Symbol encodings with /Differences and glyph names, Type0 Identity and Unicode CMaps, Type 3 | `Internal/Fonts/` (`CMap`, `FontDecoder`, `FontEncodings`, `GlyphNames`) | `TextExtractionTests.EveryEncoding_*` (15 fixtures), `EncodingTables_AreComplete` |
| F15 unknown glyphs → U+FFFD, counted | `FontDecoder.Decode`, `PageContent.UnknownGlyphs` | `TextExtractionTests.UnknownGlyphs_*` |
| F15 spacing and line heuristics, layout order | `Internal/Content/TextAssembler.cs` | `TextExtractionTests.LayoutOrder_*`, fixtures |
| F15 positioned model for reuse (runs, glyphs, images, ruling lines; display space) | `PdfText.cs`, `PageContent` | `Runs_*`, `Glyphs_*`, `RotatedPage_*`, `Images_*` |
| Content filters Flate, LZW, ASCIIHex, ASCII85, RunLength | `StreamFilters.DecodeAll` | `TextExtractionTests.Filters_DecodeKnownVectors`, fixture `ascii-hex-content` |
| F15 interop with pdf.js `getTextContent` (whitespace normalised) | `InteropTests.PdfJs_TextMatchesExtraction` | 17 documents |
| F16 text stamps: standard 14 fonts, WinAnsi only (clear error), size, colour, opacity (ExtGState), rotation, positions respecting /Rotate and the crop box, over/under with q … Q, new content streams only | `PdfStamps.cs`, `Internal/Pages/StampEngine.cs`, `Internal/Fonts/StandardFonts.cs` | `StampTests` (content untouched, positions, rotated and cropped pages, multiline, rejection, protected) |
| F16 image stamps: JPEG passthrough, PNG decode (all colour types and depths, tRNS) with alpha as /SMask | `Internal/Images/ImageCodecs.cs` | `StampTests.Png_*`, `Image_Png_WithSoftMask`, `Image_Jpeg_IsPassedThrough` |
| Interop of Phase 5 outputs | `InteropTests.Corpus` (text stamp, under stamp R6, PNG stamp) | `InteropTests.PdfJs_OpensDocuments`, `InteropTests.PdfKit_OpensDocuments` |

## Roadmap Phase 6: signatures and certificates

| Requirement | Code | Test |
|---|---|---|
| Separate package referencing the core and System.Security.Cryptography.Pkcs only; `AddPdfUtilSignatures()` | `src/PdfUtil.Signatures/`, `PdfUtilSignaturesServiceCollectionExtensions` | `SignatureTests.FileApi_AndDi` |
| F17 verify: byte-range digest, CMS validation (detached, CAdES, sha1, RFC 3161 document timestamps), signer, signed time, timestamp token, digest algorithm, revision and changes after signing, optional chain with a caller policy | `Internal/SignatureVerifier.cs` | `SignatureTests` (`Invisible_*`, `Tampering_IsDetected`, `SecondSignature_*`, `ChangesAfterSigning_*`, `ChainPolicy_*`, `Verify_AdbePkcs7Detached_AndSha1`, `Verify_DocumentTimestamp`) |
| F17 sign: PAdES B-B (CAdES detached, SHA-256/384/512, ESS signing-certificate-v2), RSA and ECDSA, visible or invisible, existing or new field, RFC 3161 timestamp with injectable HTTP client, incremental save with reserved `/Contents` filled after hashing, self-verified | `Internal/SigningEngine.cs`, core `IncrementalEngine`, `ReservedSpace` | `SignatureTests` (`Ecdsa_AndEveryDigest`, `Visible_OnARotatedPage`, `ExistingUnsignedField_*`, `Timestamp_*`, `ReservedSpace_*`, `InvalidInputs`) |
| F18 certificate encryption: `/Adobe.PubSec` `adbe.pkcs7.s5` with V4/V5 crypt filters, per-recipient permissions, SHA-1 (V4) / SHA-256 (V5) key derivation, decrypt with a private key | `Internal/CertificateSecurityEngine.cs`, core `PubSecDictionary`, `StandardSecurityHandler.FromFileKey`, `KeyedOutputEncryption` | `CertificateSecurityTests` |
| F18 IsEncrypted, verification and inspection report PubSec details instead of throwing | core `PdfOpener`, `InspectionEngine` | `CertificateSecurityTests`, `VerificationAndFailureTests.PublicKeySecurity_IsReported_NotACrash` |
| Interop of signed outputs | `InteropTests.Corpus` (signed invisible, signed twice with visible appearance, signed visible with object streams) | `InteropTests.PdfJs_OpensDocuments`, `InteropTests.PdfKit_OpensDocuments` |

## Roadmap Phase 7a: PDF to HTML

| Requirement | Code | Test |
|---|---|---|
| Separate package (core + BCL only), `AddPdfUtilConversion()`, shared document model (sections, blocks, inlines) | `src/PdfUtil.Conversion/`, `Internal/Model/DocumentModel.cs`, `PdfUtilConversionServiceCollectionExtensions` | `HtmlConversionTests.Di_RegistersTheConverter` |
| F19 fixed layout: positioned page boxes, text runs with font, size, weight, style, colour, rotation; images; link overlays | `Internal/Html/HtmlWriters.cs` (`FixedHtmlWriter`), `Internal/Pdf/PdfSourceReader.cs` | `Fixed_PositionsPagesTextImagesAndLinks`, `RotatedPage_UsesDisplayedOrientation` |
| F19 reflow: paragraphs, headings, lists (bullets, numbers, letters, roman, nesting), columns in reading order, superscripts, paragraphs across pages | `Internal/Pdf/ReflowAnalyzer.cs`, `ReflowHtmlWriter` | `Reflow_RebuildsStructure`, `Reflow_ReadsColumnsInOrder_AndConvertsLinksImagesAndBookmarks`, `Reflow_ParagraphsAcrossPagesAndShortLines` |
| Links become `<a>` (unsafe schemes dropped), bookmarks become navigation, `/Lang` and title in the head | `PdfSourceReader.ReadLinks`, `UrlSafety`, `HtmlDocument` | `Reflow_ReadsColumnsInOrder_AndConvertsLinksImagesAndBookmarks`, `PageSelection_AndSeparators` |
| Images: JPEG passed through; Flate/LZW/RL/ASCII in Gray, RGB, CMYK, Cal, ICC, Indexed, Separation to PNG with masks; JPX/JBIG2/CCITT placeholders | `Internal/Pdf/ImageExtractor.cs`, core `ImageCodecs.EncodePng` | `Images_EveryColourSpace`, `Images_PixelsRoundTrip`, `Images_SoftMaskBecomesAlpha`, `Images_UnsupportedCodecs_BecomePlaceholders` |
| Single file with data URIs or a folder of assets, atomic output | `Internal/ConversionOutput.cs`, `ImageSink` | `FileOutput_SeparateAssets` |
| Well-formed, escaped output, XHTML mode; structural validation before returning | `Internal/Html/HtmlBuilder.cs`, `Internal/Html/HtmlValidator.cs` | `Xhtml_ParsesAsXml_AndTextIsEscaped` |
| Text in the HTML equals the F15 extraction, for every font encoding and input structure, and encrypted input | `ReflowAnalyzer`, `FixedHtmlWriter` | `TextEqualsExtraction_EveryFontEncoding`, `TextEqualsExtraction_EveryInputStructure`, `EncryptedInput_NeedsAPassword` |
| Limits, cancellation, progress, failures leave output untouched | `PdfConversionLimits`, `HtmlBuilder` | `InvalidOptions_LimitsAndFailures`, `Images_UnsupportedCodecs_BecomePlaceholders` |
| Rendered by WebKit, not blank (when `swift` is available) | | `WebKit_RendersOutput` |

## Roadmap Phase 7b: PDF to Word

| Requirement | Code | Test |
|---|---|---|
| F20 `ConvertToDocxAsync` / `ConvertToDocxFileAsync` using the reflow analysis | `PdfConverter`, `Internal/DocxConversionEngine.cs`, `ReflowAnalyzer` | `DocxConversionTests.Structure_HeadingsListsLinksImagesAndProperties` |
| Hand-written OOXML package: content types, relationships, document, styles, numbering, settings, core and app properties, media | `Internal/Docx/DocxWriter.cs` | `Structure_HeadingsListsLinksImagesAndProperties`, `Validator_RejectsBrokenPackages` |
| Runs (bold, italic, underline, strike, size, colour, font, super/subscript), Heading 1–6, lists (bullet, decimal, letters, roman, nesting, restarts), inline images, hyperlinks and bookmarks, page breaks, page size and margins | `DocxWriter` | `Structure_HeadingsListsLinksImagesAndProperties`, `PageBreaks_EncryptedInput_AndOptions` |
| Tables only when confident: ruling lines plus text that fits the cells; spans from missing borders; header row | `Internal/Pdf/TableDetector.cs`, `ReflowAnalyzer.FitTable` | `Tables_FromRulingLines`, `Tables_OnlyWhenTheTextFits` |
| Reusable writer for 7d inputs (works from the shared model) | `DocxWriter.Write(DocModel, …)` | used by Phase 7d tests |
| Own structure validator (content types, relationships, ids, styles, numbering, bookmarks, table grids) run before returning | `Internal/Docx/DocxValidator.cs` | `Validator_RejectsBrokenPackages` |
| Hostile packages: zip-slip names, duplicates, entry count, size and ratio limits, DTDs | `Internal/Docx/DocxPackage.cs` | `Package_HostileInput` |
| Text round-trips for every font encoding and input structure; encrypted input; limits and failures | | `TextRoundTrips_EveryFontEncoding`, `TextRoundTrips_EveryInputStructure`, `PageBreaks_EncryptedInput_AndOptions`, `FileOutput_LimitsAndFailures` |
| Opens in another OOXML reader: Apple `textutil` (LibreOffice is not installed here; Word, Pages and Google Docs are in the manual checklist) | | `Textutil_ReadsOutput` |

## Roadmap Phase 7c: HTML to PDF

| Requirement | Code | Test |
|---|---|---|
| F21 `ConvertFromHtmlAsync` / `ConvertFromHtmlStringAsync` / `ConvertFromHtmlFileAsync` with page size, margins, base URI, resolver, fonts, default fonts, header and footer templates with page numbers, bookmarks, optional protection | `PdfConverter`, `HtmlToPdf.cs`, `Internal/HtmlToPdfEngine.cs` | `HtmlToPdfTests.PageSetup_HeaderFooterAndProtection`, `LinksAnchorsAndBookmarks` |
| Tolerant HTML parser: implied html/head/body, auto-closed p/li/cells/rows/sections, implied tbody/tr, foster parenting, misnested formatting, full named entity table, numeric references, raw text and RCDATA, encodings | `Internal/Html/HtmlParser.cs`, `HtmlEntities.cs` | `Parser_ImpliesStructureAndRecovers`, `Parser_CharacterReferencesAndRawText` |
| Supported elements, mapped to the shared model | `Internal/Html/HtmlModelBuilder.cs` | `Text_RoundTripsThroughExtraction`, `Tables_WidthsSpansAndBorders`, `Images_PngJpegAndUnsupported` |
| CSS subset: style blocks, links, imports, style attributes; type/class/id/attribute/descendant/child selectors; specificity, `!important`, inheritance; font, text, box, list and page properties; `@page`; one warning per unsupported kind | `Internal/Css/Css.cs`, `Internal/Css/Cascade.cs` | `Css_CascadeSpecificityAndInheritance`, `Css_ValuesAndShorthands` |
| Fonts: standard 14 for WinAnsi; TrueType parser (cmap 0/4/6/12, hmtx, glyf/loca, name, OS/2, post) and subsetter (composite glyphs renumbered); Type0/CIDFontType2 Identity-H with ToUnicode; folders, system fonts, `@font-face`; embedding permissions; synthetic bold/italic | core `Internal/Fonts/TrueTypeFont.cs`, `EmbeddedFont.cs`; `Internal/Layout/Fonts.cs` | `Fonts_EmbeddedSubsetWithToUnicode`, `Fonts_MissingRestrictedAndBroken`, `FontFace_LoadsThroughTheResolver` |
| Layout: block/inline formatting, simplified UAX #14, justification, pagination with widows/orphans and keep-with-next, tables (auto/fixed widths, spans, repeated headers), images scaled to fit | `Internal/Layout/LineLayout.cs`, `LayoutEngine.cs` | `Layout_PositionsAndSizes`, `Pagination_BreaksWidowsAndRepeatedHeaders`, `Tables_WidthsSpansAndBorders` |
| Links become link annotations, anchors named destinations | `Internal/Layout/PdfBuilder.cs` | `LinksAnchorsAndBookmarks` |
| No network: resources only through the caller's resolver; directory resolver refuses traversal; data URIs | `HtmlResources`, `HtmlToPdfEngine.Fetch` | `Resources_OnlyThroughTheResolver` |
| Limits (input size, elements, depth, resources, output), cancellation, output untouched on failure; self-verification by reading back the text | `PdfConversionLimits`, `HtmlToPdfEngine.Verify` | `Parser_Limits`, `OutputSafety_LimitsAndValidation` |
| Interop: pdf.js and PDFKit open the outputs (plain and AES-256) | `InteropTests.Corpus` (`html-basic`, `html-footer`) | `InteropTests.PdfJs_OpensDocuments`, `PdfKit_OpensDocuments` |
| F16 extended: text stamps in any script with an embedded TrueType font | core `StampEngine.PrepareEmbedded`, `PdfStampOptions.FontData` | `StampTests.Text_AnyScript_WithAnEmbeddedFont`, interop sample `stamp-unicode` |

## Roadmap Phase 7d: Word to PDF

| Requirement | Code | Test |
|---|---|---|
| F22 `ConvertFromDocxAsync` / `ConvertFromDocxFileAsync`, reusing the F21 layout engine, font embedding, self-verification and protection | `PdfConverter`, `DocxToPdf.cs`, `Internal/DocxToPdfEngine.cs`, `Internal/ModelRenderer.cs` | `DocxToPdfTests.FromModel_StructureTextImagesAndLinks`, `FileOutput_ProtectionAndFonts` |
| OOXML package: relationships, styles with `basedOn` and document defaults, numbering (formats, levels, overrides, continuation), theme fonts, media, settings | `Internal/Docx/DocxReader.cs`, `DocxPackage.cs` | `Styles_InheritanceAndDefaults`, `Numbering_LevelsContinuationAndRestart` |
| Sections: page size, orientation, margins; headers and footers (default, first, even) with PAGE and NUMPAGES; one column (others warn) | `DocxReader.SectionOf`, `LayoutEngine.Finish` | `Sections_HeadersFootersAndFields` |
| Paragraphs, runs, tables (grid, widths, merged cells, borders, shading), inline images, hyperlinks and bookmarks, properties to Info/XMP | `DocxReader` | `Tables_GridMergesBordersAndShading`, `Content_LinksBookmarksImagesAndUnsupported`, `FromModel_StructureTextImagesAndLinks` |
| Out of scope with one warning each: floating shapes, text boxes, SmartArt, charts, equations, tracked changes (accepted state), comments, other fields, columns, complex scripts | `DocxReader` | `Content_LinksBookmarksImagesAndUnsupported`, `Sections_HeadersFootersAndFields` |
| Inputs from the 7b OOXML writer and hand-written fixtures; PDF → Word → PDF round trip | `DocxWriter`, test `DocxFixture` | `FromModel_StructureTextImagesAndLinks`, `RoundTrip_PdfToWordToPdf` |
| Hostile packages: zip bombs, zip-slip names, entry and size limits, DTDs, nesting and element limits, legacy .doc | `DocxPackage.Read`, `DocxReader.Count` | `HostileAndInvalidPackages` |
| Interop: pdf.js and PDFKit open the outputs; page count compared with LibreOffice when installed | `InteropTests.Corpus` (`docx-model`, `docx-fixture`) | `InteropTests.PdfJs_OpensDocuments`, `PdfKit_OpensDocuments`, `LibreOffice_PageCountMatches` (skipped here) |

## Roadmap Phase 8: command-line tool

| Requirement | Code | Test |
|---|---|---|
| `src/PdfUtil.Cli`, `PackAsTool`, command `pdfutil`; own argument parser (no System.CommandLine) | `PdfUtil.Cli.csproj`, `Infrastructure.cs` (`Arguments`) | `CliTests.Usage_HelpAndErrors`; packed and installed with `dotnet tool install` |
| Subcommands for every feature: protect, decrypt, security, info/inspect, edit-info, validate, repair, sanitize, attach, detach, extract-attachment, rotate, delete-pages, merge, split, text, watermark, sign, verify-signatures, convert (direction from extensions or `--to`), batch | `CliApp.cs` | `Protect_Decrypt_PasswordSources_AndExitCodes`, `Inspect_Validate_Repair_Sanitize_EditInfo`, `Pages_Attachments_Text_Watermark`, `Sign_AndVerifySignatures`, `Convert_EveryDirection`, `Batch_ManyFiles` |
| No plain password arguments: hidden prompt, `--password-stdin`, `--*-password-env` | `Passwords`, `CliConsole` | `Protect_Decrypt_PasswordSources_AndExitCodes`, `Usage_HelpAndErrors` |
| Human output by default, `--json` for machines | `CliApp.RunAsync`, `Human` | `Protect_Decrypt_PasswordSources_AndExitCodes`, `Batch_ManyFiles` |
| Exit codes 0–6 | `CliApp.ExitCode` | every CLI test |
| Tests in-process and as a process | | `CliTests` (in-process), `Process_RunsTheTool` |

## Chaining operations (PdfPipeline)

Added after the roadmap, on request; purely additive.

| Requirement | Code | Test |
|---|---|---|
| Fluent, lazy pipeline over one document: stamp/watermark, protect, decrypt, change security, edit info, rotate, delete pages, append (merge), repair, sanitize, attachments, custom steps | `PdfPipeline.cs` | `PipelineTests.Watermark_Rotate_EditInfo_Protect`, `Append_DeletePages_Repair_Sanitize`, `Then_CustomStep_GetsCurrentPassword` |
| Passwords carry forward (`WithPassword`, new owner password after `Protect`/`ChangeSecurity`, none after `Decrypt`); caller options unchanged | `PdfPipeline.WithOpenPassword`, `Protect`, `ChangeSecurity`, `Decrypt` | `OwnerPassword_CarriesForward_WithoutChangingOptions`, `WithPassword_OpensProtectedInput_AndDecryptRemovesIt`, `ChangeSecurity_NewOwnerPassword_IsUsedNext`, `WrongPassword_FailsInFirstStep` |
| Failing step keeps its exception type, is named in `Data["PdfPipelineStep"]`, nothing is written; atomic save; cancellation; reruns | `PdfPipeline.RunAsync`, `SaveAsync` (`OutputRunner.CommitFile`) | `FailingStep_KeepsExceptionType_NamesStep_AndWritesNothing`, `Save_File_Stream_AndRunsTwice`, `Cancellation_StopsBeforeRunning` |
| `Sign` step in PdfUtil.Signatures | `PdfPipelineSignatures.Sign` | `Sign_AsLastStep_IsValid` |
| `IPdfPipelineFactory` registered by `AddPdfUtil` with the application's options | `PdfPipelineFactory`, `PdfUtilServiceCollectionExtensions` | `Factory_FromDependencyInjection_UsesApplicationOptions` |
