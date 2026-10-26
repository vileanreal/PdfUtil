# PdfUtil roadmap, Phase 5: content (text extraction and watermarks)

**Before you start:** read `docs/roadmap/00-common.md` completely and follow its rules.
**Prerequisites:** Phase 1 (rewrite pipeline).

## Features

**F15. Text extraction.**
- `IPdfTextExtractor.ExtractAsync(input, password, pages)` returns text per page, plus a positioned model: glyph runs with font, size, colour, bounding box and page coordinates. Phase 7 (conversion) builds on that model, so design it for reuse.
- Requires a content-stream tokenizer (reuse the lexer) and a graphics- and text-state machine (`BT/ET`, `Tf`, `Td/TD/Tm/T*`, `Tj/TJ/'/"`, `Tc/Tw/Tz/TL/Ts`, `cm`, `q/Q`, colour operators), and must recurse into form XObjects.
- Font decoding must cover:
  - `/ToUnicode` CMaps (`bfchar`/`bfrange`, multi-byte codes);
  - simple fonts with standard, WinAnsi, MacRoman and PDFDoc encodings plus `/Differences` and the Adobe glyph list (embed the needed glyph-name table as internal code);
  - Type0/CID fonts with Identity-H/V and predefined CMaps where the CID→Unicode mapping can be derived.
- Unknown glyphs map to U+FFFD and are counted in the result.
- Heuristics: insert spaces using glyph widths and `TJ` offsets; line breaks using baseline changes; a layout mode that orders lines by position.
- No OCR. Document the limits.
- Tests use builder-generated content with known text in each encoding.
- Interop: compare against pdf.js `getTextContent` on the corpus, normalising whitespace.

**F16. Watermark and stamp.**
- Text stamps use a Standard-14 font with WinAnsi text only (reject other characters with a clear error) and set font size, colour, opacity (ExtGState `/ca`), rotation and position (presets plus custom coordinates, respecting `/Rotate` and the crop box).
- Image stamps support JPEG (DCTDecode passthrough) and non-interlaced PNG (decoded with `ZLibStream`; alpha becomes an `/SMask`).
- Over or under the content, by wrapping existing content in `q … Q`. Stamp all pages or a page range.
- Never re-encode existing content streams; add new content streams to the page's `/Contents` array.
- Once Phase 7c adds TrueType embedding, extend text stamps to any Unicode text with an embedded font.

## Blazor sample

Add an "Extract text" page and a "Watermark" page with a live preview of the settings.

## When done

Follow the end-of-phase checklist in `00-common.md`, and report.
