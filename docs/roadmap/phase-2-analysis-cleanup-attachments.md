# PdfUtil roadmap, Phase 2: analysis, cleanup and attachments

**Before you start:** read `docs/roadmap/00-common.md` completely and follow its rules.
**Prerequisites:** Phase 1 (rewrite pipeline, progress reporting).

## Features

**F6. Richer inspection.** Extend `PdfDocumentInfo` through an opt-in `PdfInspectionOptions` (to keep the default inspection fast) with:
- **Fonts:** name, subtype, embedded or subset, encoding.
- **Images:** page, pixel size, bits per component, colour space, filter.
- **Attachments:** name, description, size, MIME type, dates.
- **Bookmarks:** the outline tree, with titles decoded and destinations resolved to page numbers.
- **Form fields:** fully qualified name, type, value, flags.
- **Signatures:** field name, signer name, signing time, reason, location, `/SubFilter`, byte range, and whether the byte range covers the whole file except the signature value. Cryptographic validation comes in Phase 6 (F17).
- **Page labels, `/PageLayout`, `/PageMode`, and the document language.**
- **JavaScript and actions:** whether the document contains any (names only).

**F7. Validation report.**
- `ValidateAsync` returns a structured report with severity, code, message and object number. It covers:
  - xref problems and repairs;
  - wrong stream lengths;
  - missing or dangling references;
  - invalid dates;
  - page tree problems;
  - encryption dictionary inconsistencies;
  - Flate streams that fail to decode.
- It never throws for a damaged file that can still be read.

**F8. Repair and normalize.**
- `RepairAsync` writes a clean, single-revision file through the recovery reader, unencrypted or re-encrypted with the same settings.
- The result reports exactly what was repaired.
- Must never silently drop pages: compare page counts and report any loss.

**F9. Sanitize.**
- `SanitizeAsync(input, output, PdfSanitizeOptions)` with independent switches:
  - remove Info, remove XMP;
  - remove attachments;
  - remove JavaScript (document, page and annotation actions, and the `/Names /JavaScript` tree);
  - remove `/OpenAction` and `/AA`;
  - remove annotations, optionally keeping links.
- The result lists everything that was removed.

**F10. Attachments.**
- `AddAttachmentAsync` and `RemoveAttachmentAsync` for document-level attachments: an `/EmbeddedFiles` name tree entry, a filespec with `/F` and `/UF`, `/Params` (Size, CreationDate, ModDate, CheckSum MD5), `/Subtype` MIME type, and an optional `/AFRelationship` for PDF 2.0 and PDF/A-3.
- `ExtractAttachmentAsync` returns the decoded bytes.
- Keep the name tree sorted and valid. Handle existing `/Kids`-based trees.

## Test builder

Extend `TestPdfBuilder` as needed: fonts of each kind, images with each colour space, outlines with nested items,
AcroForm fields of each type, JavaScript actions, page labels, and attachment name trees with `/Kids`.

## Blazor sample

Extend the Inspect page with sections for fonts, images, attachments (with download), bookmarks, form fields and
signatures. Add Validate, Sanitize and Attachments pages.

## When done

Follow the end-of-phase checklist in `00-common.md`, and report.
