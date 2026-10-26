# PdfUtil roadmap, Phase 7a: PDF → HTML

**Before you start:** read `docs/roadmap/00-common.md` and `docs/roadmap/phase-7-conversion-common.md` completely.
**Prerequisites:** Phase 5 (F15). This sub-phase also creates the `PdfUtil.Conversion` package and the shared
document model.

## Feature

**F19. PDF → HTML.**
- `ConvertToHtmlAsync(input, output, password, PdfToHtmlOptions)` with two modes:
  - **Fixed layout:** one positioned container per page; text runs absolutely positioned with font family, size, weight, style and colour; images placed at their positions.
  - **Reflow:** paragraphs and headings reconstructed from line spacing and font size; lists from bullet and number patterns; reading order by columns where they can be detected.
- Links become `<a>`, and bookmarks become a navigation list. The document language and title go into `<html lang>` and `<title>`.
- Images:
  - JPEG is passed through;
  - Flate images in DeviceGray, DeviceRGB, Indexed or ICCBased colour are converted to PNG with an encoder you write (`ZLibStream` plus a CRC32);
  - other filters (JPX, JBIG2, CCITT) get a placeholder and a warning.
- Output is a single self-contained file (images as data URIs) or a folder with separate assets. The HTML must be well-formed and escaped.
- Tests:
  - text in the HTML equals the F15 extraction;
  - pages, images and links are present;
  - the output parses as XHTML when that mode is used;
  - if WebKit or Chromium is available, render it and check that it isn't blank.

## Blazor sample

Create the "Convert" page with PDF → HTML (with an in-page preview in a sandboxed iframe) and a download.

## When done

Follow the end-of-phase checklist in `00-common.md`. In the report, describe the shared document model and how 7b–7d
will extend it.
