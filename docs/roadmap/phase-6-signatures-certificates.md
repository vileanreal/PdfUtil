# PdfUtil roadmap, Phase 6: signatures and certificates (new package)

**Before you start:** read `docs/roadmap/00-common.md` completely and follow its rules.
**Prerequisites:** Phase 3 (F12 incremental save is required for signing). Phase 2 (F6 signature inspection) is
extended here.

## Package

Create `src/PdfUtil.Signatures/`, referencing the core and `System.Security.Cryptography.Pkcs` (Microsoft) only. Add
DI registration in the DI package behind a separate `AddPdfUtilSignatures()`.

## Features

**F17. Digital signatures.**
- **Verify first:**
  - For each signature, recompute the byte-range digest and validate the CMS (`SignedCms`).
  - Report the signer certificate, signing time (signed attribute, plus a timestamp token if present), digest algorithm, whether the document was modified after signing (revisions appended later, and what kind of changes), and an optional chain build with a caller-supplied `X509ChainPolicy`.
  - Support `adbe.pkcs7.detached`, `ETSI.CAdES.detached` and `ETSI.RFC3161` (document timestamps).
- **Then sign:**
  - PAdES B-B (`ETSI.CAdES.detached`, SHA-256) with RSA or ECDSA from an `X509Certificate2` that has a private key.
  - Visible or invisible signature fields, using the existing field or a new one.
  - Optional RFC 3161 timestamp from a caller-provided TSA URL (through `Rfc3161TimestampRequest`, with the HTTP client injectable).
  - Signing **must** use incremental save (F12) so earlier signatures stay valid; reserve the `/Contents` size and fill it after hashing.
- Tests:
  - create test certificates with `CertificateRequest`;
  - sign and verify;
  - tamper with one byte and the verification fails;
  - a second signature keeps the first one valid;
  - pdf.js and PDFKit still open signed files.

**F18. Certificate (public-key) encryption.**
- Read and write `/Adobe.PubSec` (`adbe.pkcs7.s5`, using crypt filters in V4/V5).
- Recipients come from `X509Certificate2` (RSA), with per-recipient permissions; the seed and key derivation follow the spec (SHA-1 for V4, SHA-256 for AES-256).
- Decrypt with a certificate that has a private key.
- When this package is used, `IsEncrypted`, verification and inspection report PubSec details instead of throwing `PdfUnsupportedEncryptionException`.

## Blazor sample

Add a "Sign" page (upload a PFX with its password, visible or invisible signature) and a "Verify signatures" page.
Make clear in the UI that uploaded certificates are held only in memory for the session.

## When done

Follow the end-of-phase checklist in `00-common.md`, and report.
