# Manual compatibility checklist

Automated tests already cover pdf.js (Firefox) and Apple PDFKit (Safari, Preview, iOS) on macOS, and qpdf and mutool when they are installed. Use this checklist for the remaining viewers, and re-run it before each release.

## 1. Generate the samples

```bash
dotnet run --project samples/PdfUtil.Sample -- demo ./compat-samples
```

| File | Algorithm | Output version | User password | Owner password |
|---|---|---|---|---|
| `aes256.pdf` | AES-256 (R6) | 1.7 + ADBE ext. 8 | `user` | `owner` |
| `aes128.pdf` | AES-128 (R4) | 1.6 | `user` | `owner` |
| `rc4-128.pdf` | RC4-128 (R3) | 1.4 | `user` | `owner` |
| `aes256-unicode.pdf` | AES-256 (R6) | 1.7 | `Пароль東京` | `owner` |
| `aes256-owner-only.pdf` | AES-256 (R6) | 1.7 | *(none: opens directly)* | `owner` |

Permissions: print and accessibility extraction only. Copying, editing and annotating are not allowed.

## 2. Per viewer and per file, check

1. The viewer asks for a password (except `aes256-owner-only.pdf`).
2. A wrong password is rejected.
3. The user password opens the file, which shows one page with “Hello page 1 - PdfUtil sample”.
4. Document properties show the title “PdfUtil compatibility sample”.
5. Opened with the user password, printing is allowed. Copying text is blocked (in viewers that enforce permissions).
6. Opened with the owner password, full access, where the viewer supports owner passwords.
7. There is no repair prompt, no “damaged file” warning, and no "fast web view" claim.

## 3. Results

Mark each cell with ✅, ❌ or n/a, and add the viewer version and the date.

| Viewer (version) | aes256 | aes128 | rc4-128 | aes256-unicode | owner-only | Notes |
|---|---|---|---|---|---|---|
| Chrome (PDFium) | | | | | | |
| Edge (PDFium) | | | | | | |
| Firefox (pdf.js) | | | | | | Also covered by automated tests |
| Safari (PDFKit) | | | | | | Also covered by automated tests |
| Adobe Acrobat Reader | | | | | | |
| Foxit PDF Reader | | | | | | |
| SumatraPDF | | | | | | |
| macOS Preview | | | | | | Also covered by automated tests (PDFKit) |
| iOS Files / Books | | | | | | |
| Android (built-in / Google Drive viewer) | | | | | | |
| `qpdf --check --password=user` | | | | | | Automated when installed |
| `mutool info -p user` | | | | | | Automated when installed |

## 4. If a viewer fails

* Keep the failing file and note the viewer version and its exact message.
* Run `dotnet run --project samples/PdfUtil.Sample -- info <file> owner` and `qpdf --check` on it.
* If the failure involves a password, check whether it contains non-ASCII characters (see “Password encoding rules” in the README). The `Interoperable` password policy exists because readers disagree on these.

## Signatures and certificate encryption (PdfUtil.Signatures)

| Check | Acrobat Reader | Foxit | Chrome / Edge | Firefox | Safari / Preview |
|---|---|---|---|---|---|
| A signed file (invisible signature) opens, and the signature panel shows it (validity depends on trusting the test certificate) | | | | | |
| A visible signature appears in its rectangle, also on a rotated page | | | | | |
| A file signed twice shows both signatures, the first as "modified after signing" with only signature changes | | | | | |
| A certificate-encrypted file opens with the recipient's certificate installed (Acrobat only; others are expected to refuse) | | | n/a | n/a | n/a |

## PDF to Word (PdfUtil.Conversion)

Open the DOCX files from `DocxConversionTests` (or convert your own PDFs) and check that no repair prompt appears.

| Check | Word (Windows) | Word (Mac) | Pages | Google Docs | LibreOffice |
|---|---|---|---|---|---|
| Opens without a repair or "unreadable content" prompt | | | | | |
| Headings appear in the navigation pane | | | | | |
| Bulleted and numbered lists continue and restart as in the PDF | | | | | |
| Tables show their grid, merged cells and a repeated header row | | | | | |
| Images show at their size; hyperlinks and links to pages work | | | | | |
| Page size and margins match the first PDF page | | | | | |

## HTML to PDF (PdfUtil.Conversion)

| Check | Acrobat Reader | Chrome / Edge | Firefox | Safari / Preview |
|---|---|---|---|---|
| Embedded TrueType text (Cyrillic, CJK, emoji) displays and copies as the original text | | | | |
| Links open; links to anchors jump to the right place; bookmarks list the headings | | | | |
| Tables continuing on a new page repeat their header row | | | | |
| Header and footer page numbers are right on every page | | | | |

## Word to PDF (PdfUtil.Conversion)

Compare a PDF made from your own DOCX files with Word's "Save as PDF".

| Check | Result |
|---|---|
| Headings, lists (numbering continues and restarts as in Word) and tables look the same | |
| Headers and footers, with the right page numbers, on first, odd and even pages | |
| Landscape sections and their margins | |
| Links and bookmarks work; the PDF's title and author match the document properties | |
