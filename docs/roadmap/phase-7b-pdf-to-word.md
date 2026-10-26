# PdfUtil roadmap, Phase 7b: PDF → Word (DOCX)

**Before you start:** read `docs/roadmap/00-common.md` and `docs/roadmap/phase-7-conversion-common.md` completely.
**Prerequisites:** Phase 7a (shared document model and reflow analysis).

## Feature

**F20. PDF → Word (DOCX).**
- `ConvertToDocxAsync(input, output, password, PdfToDocxOptions)`, using the reflow analysis from F19.
- Writes a valid OOXML package by hand: `[Content_Types].xml`, `_rels`, `word/document.xml`, `styles.xml`, `numbering.xml`, `settings.xml`, `docProps/core.xml` (from Info/XMP) and `media/`.
- Content mapped:
  - paragraphs and runs with bold, italic, underline, size, colour and font name;
  - Heading 1–6 styles from the detected headings;
  - bulleted and numbered lists;
  - images inline;
  - hyperlinks;
  - page breaks between PDF pages (optional);
  - page size and margins from the first page.
- Tables only when the detection is confident (ruling lines plus aligned text); otherwise text.
- Keep the OOXML writer reusable: Phase 7d uses it to generate test inputs.
- Tests:
  - the package passes your own structure validator (content types, relationships, unique IDs);
  - text round-trips;
  - the document opens without a repair prompt in LibreOffice headless (`soffice --headless --convert-to pdf`) when installed (skip otherwise);
  - add Word, Pages and Google Docs to `docs/manual-compat-checklist.md`.

## Blazor sample

Add PDF → Word to the "Convert" page.

## When done

Follow the end-of-phase checklist in `00-common.md`, and report.
