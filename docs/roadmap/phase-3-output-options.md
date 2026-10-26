# PdfUtil roadmap, Phase 3: output options

**Before you start:** read `docs/roadmap/00-common.md` completely and follow its rules.
**Prerequisites:** Phase 1 (rewrite pipeline).

## Features

**F11. Compressed output (opt-in).**
- `PdfWriteOptions.UseObjectStreams`: write object streams and an xref stream (PDF 1.5+, so the version rises to at least 1.5) to make files smaller.
- Encryption rules for object streams and xref streams must follow the specification.
- The default stays maximum-compatibility output. Add the compressed variant to the interop corpus.

**F12. Incremental save mode (opt-in).**
- For non-encrypting edits (F3 on unencrypted files, F10, and signing in Phase 6), append an incremental update instead of rewriting, so the original bytes are preserved.
- Requires correct `/Prev` handling, xref table or stream matching the source style, and the same `/ID[0]` with a new `/ID[1]`.
- Verification must check that the original bytes are unchanged and that the new revision reads correctly.
- Phase 6 signing depends on this, so design the API so a caller can reserve space in an object and fill it after the bytes are written.

## When done

Follow the end-of-phase checklist in `00-common.md`, and report.
