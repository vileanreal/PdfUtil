# PdfUtil roadmap, Phase 7d: Word (DOCX) → PDF

**Before you start:** read `docs/roadmap/00-common.md` and `docs/roadmap/phase-7-conversion-common.md` completely.
**Prerequisites:** Phase 7b (OOXML writer, used to generate test inputs) and Phase 7c (layout engine and font
embedding).

## Feature

**F22. Word (DOCX) → PDF.**
- `ConvertFromDocxAsync(docxInput, output, DocxToPdfOptions)`, reusing the F21 layout engine and font embedding.
- Read the OOXML package: document, styles (with `basedOn` inheritance and document defaults), numbering (bullet and decimal formats with levels), relationships, media, theme fonts, section properties (page size, orientation, margins, columns: 1 only, others warn), and headers and footers (default, first and even) with `PAGE` and `NUMPAGES` fields.
- Supported content:
  - paragraphs: alignment, indentation, spacing, keep-with-next, page breaks;
  - runs: bold, italic, underline, strike, size, colour, highlight, font, superscript and subscript;
  - tables: grid, widths, merged cells, borders, shading;
  - inline images (`wp:inline`);
  - hyperlinks and bookmarks;
  - document properties go to Info/XMP.
- **Out of scope** (documented, each with a warning): floating shapes and anchored images (rendered inline), text boxes, SmartArt, charts, equations (OMML), tracked changes (render the accepted state), comments, fields other than PAGE and NUMPAGES, multi-column sections, and complex-script shaping.
- Tests:
  - generate DOCX inputs programmatically with your own OOXML writer from F20, plus a few hand-written XML fixtures;
  - text round-trip; heading, list and table structure; images present; page size and margins;
  - the interop corpus;
  - optionally, compare page count with LibreOffice output when installed.

## Blazor sample

Add Word → PDF to the "Convert" page.

## When done

Follow the end-of-phase checklist in `00-common.md`, and report.
