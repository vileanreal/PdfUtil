# PdfUtil roadmap, Phase 7: conversion with HTML and Word (shared rules)

**Before you start any sub-phase (7a–7d):** read `docs/roadmap/00-common.md` and this file completely.
**Prerequisites:** Phase 5 (F15 text extraction and its positioned glyph model).

Phase 7 adds two-way conversion between PDF and HTML, and between PDF and Word (DOCX). It is large, so it is
delivered as four sub-phases, each with its own build, tests, docs and report:

| Sub-phase | File | Feature |
|---|---|---|
| 7a | `phase-7a-pdf-to-html.md` | F19 PDF → HTML |
| 7b | `phase-7b-pdf-to-word.md` | F20 PDF → Word |
| 7c | `phase-7c-html-to-pdf.md` | F21 HTML → PDF (layout engine, fonts) |
| 7d | `phase-7d-word-to-pdf.md` | F22 Word → PDF |

Do them in order. **Confirm the scope with me before starting 7c**, which contains the layout engine and font embedding.

## Package

Create `src/PdfUtil.Conversion/`, referencing only the core and the .NET base class library: `System.IO.Compression`
for DOCX zip packages, `System.Xml` for OOXML. **No third-party HTML, CSS, font or OOXML libraries.** Register it
with a separate `AddPdfUtilConversion()`.

## Shared architecture

Design this in 7a and describe it in the 7a report; later sub-phases extend it.
- A **document model**: sections with page size and margins; blocks such as paragraphs, headings, lists, tables and images; inlines such as runs with style, links and line breaks.
- **Readers** that produce the model: PDF analysis built on F15 (7a), HTML (7c), DOCX (7d).
- **Writers** that consume the model: HTML (7a), DOCX (7b), and a PDF layout engine (7c).

The two directions:
- **PDF → HTML/DOCX:** analyse PDF pages (F15 glyph runs, images, links, outline) into the model, then write it.
- **HTML/DOCX → PDF:** parse into the model, lay it out, write PDF.

## Security and safety rules for all conversion

- **Never fetch network resources.** External images, fonts and stylesheets are resolved only through a caller-supplied resolver. Without a resolver they are skipped, each with a warning.
- No script execution. HTML `<script>`, event handlers and `javascript:` URLs are ignored.
- Apply input limits: maximum input size, element count, nesting depth, image pixel count and decompressed size. Treat DOCX zips as hostile: limit entry count and sizes, and block zip-slip paths.
- Converted PDFs go through the normal self-verification, and can optionally be protected in the same call by taking `PdfProtectionOptions`.
- HTML and DOCX outputs are validated structurally before they are returned.

## Documentation for conversion

Besides the usual docs, each sub-phase adds a "What is supported" table and a known-limitations list to the README and
`docs/index.html`.

## Blazor sample

Add a single "Convert" page in 7a and extend it in each sub-phase: PDF → HTML (with preview), PDF → Word,
HTML → PDF (paste HTML or upload) and Word → PDF, each with a download and the warnings list.

## CLI

Phase 8 adds a `convert` command; nothing to do here.
