# PdfUtil roadmap, Phase 7c: HTML → PDF

**Before you start:** read `docs/roadmap/00-common.md` and `docs/roadmap/phase-7-conversion-common.md` completely.
**Prerequisites:** Phase 7a (shared document model). **Confirm the scope below with me before writing code.**

## Feature

**F21. HTML → PDF.**
- `ConvertFromHtmlAsync(htmlInput, output, HtmlToPdfOptions)` with page size, margins, base URI, resource resolver, default fonts, header and footer templates with page numbers, and optional protection.
- **HTML parser:** a tolerant HTML5-style tokenizer and tree builder covering the common recovery rules: implied `html`/`head`/`body`, auto-closed `p` and `li`, misnested inline tags, and character references including the named entity table.
- **Supported elements:** `h1–h6`, `p`, `div`, `span`, `br`, `hr`, `b/strong`, `i/em`, `u`, `s`, `sub/sup`, `code/pre`, `blockquote`, `ul/ol/li` (nested), `table/thead/tbody/tr/td/th` (colspan and rowspan), `img`, `a` (links become PDF link annotations; anchors become named destinations), and `section`/`article`/`header`/`footer` as blocks.
- **CSS subset:**
  - `<style>` blocks and `style` attributes, with type, class, id, descendant and child selectors, and specificity and inheritance;
  - fonts: `font-family`, `font-size`, `font-weight`, `font-style`;
  - text: `color`, `background-color`, `text-align` (including justify), `text-decoration`, `line-height`, `white-space`;
  - box: `margin`, `padding`, `border` (solid only), `width` (px, %, pt), `display` (block, inline, none, list-item, table…);
  - lists: `list-style-type`;
  - pages: `page-break-before/after/inside`, `@page` size and margins.
  - Anything else is ignored, each kind with a single warning.
- **Fonts:**
  - Standard-14 for WinAnsi-only text;
  - for any other text, embed TrueType or OpenType (glyf) fonts supplied by the caller or found in configured font folders;
  - write a TrueType parser (cmap formats 4 and 12, hmtx, glyf/loca) and a **subsetter**;
  - embed as Type0/CIDFontType2 with Identity-H and a correct `/ToUnicode`, so copying text from the PDF works.
- **Layout engine:**
  - block and inline formatting;
  - line breaking with a simplified UAX #14 (spaces, hyphens, CJK break opportunities);
  - pagination with widow and orphan control;
  - tables with auto and fixed column widths, splitting across pages with repeated header rows;
  - images scaled to fit.
- **Out of scope** (documented): JavaScript, flexbox and grid, floats (render as blocks), positioned elements, CSS transforms and animations, web fonts from URLs without a resolver, bidirectional and complex-script shaping (Arabic, Indic, Thai text is laid out left-to-right without shaping, with a warning), SVG (warning, or rasterised placeholder) and forms.
- After this sub-phase, extend F16 text stamps (Phase 5) to any Unicode text using the new font embedding.
- Tests:
  - golden structural tests (page count, text positions within tolerance);
  - text round-trip through F15;
  - links and destinations work;
  - fonts are embedded and subset, and the ToUnicode mapping is correct;
  - outputs added to the interop corpus (pdf.js and PDFKit).

## Blazor sample

Add HTML → PDF to the "Convert" page (paste HTML or upload a file).

## When done

Follow the end-of-phase checklist in `00-common.md`, and report.
