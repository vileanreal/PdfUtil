# PdfUtil roadmap, Phase 4: page operations

**Before you start:** read `docs/roadmap/00-common.md` completely and follow its rules.
**Prerequisites:** Phase 1 (rewrite pipeline). Phase 2 (F10 attachments) is used when merging attachments.

## Features

**F13. Rotate and delete pages.**
- Rotate by page ranges (90° steps, combined with inherited `/Rotate`).
- Delete pages, rebuilding `/Kids` and `/Count`, and removing dangling outline destinations, link annotations and form-field widgets that point at deleted pages (report each removal).
- A shared page-range syntax and parser: `"1-3,5,8-"`.

**F14. Merge and split.**
- Merge N documents by renumbering objects and copying page trees with inherited attributes resolved onto each page.
- Merging covers:
  - outlines, appended under per-document or flat entries (selectable);
  - named destinations, renamed on conflict;
  - AcroForm fields, renamed on conflict, with `/DR` merged and `/NeedAppearances` kept;
  - attachments.
- Shared identical resources may be deduplicated by content hash (opt-in).
- Split by page ranges or every N pages into multiple outputs, copying only the objects each part needs.
- The output takes the highest input version.
- Encrypted inputs need their passwords; the output is unencrypted unless protection options are given.
- Tests: page counts, content stream bytes, link destinations still valid, form field values preserved; pdf.js and PDFKit open every output.

## Blazor sample

Add Merge, Split and Pages (rotate/delete) pages, with drag-to-reorder for merge.

## When done

Follow the end-of-phase checklist in `00-common.md`, and report.
