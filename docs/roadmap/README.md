# PdfUtil roadmap prompts

These files are prompts for an AI coding agent, one per phase. To run a phase, start a new session in this repository
and give the agent its phase file, for example:

> Implement `docs/roadmap/phase-1-core-editing.md`.

Every phase file tells the agent to read `00-common.md` first, which holds the shared context, global rules and
definition of done.

| Order | File | Features | Needs | Status |
|---|---|---|---|---|
| — | `00-common.md` | Shared context and rules | — | |
| 1 | `phase-1-core-editing.md` | Rewrite pipeline; F1 decrypt, F2 change security, F3 edit info, F4 batch, F5 progress | — | Done |
| 2 | `phase-2-analysis-cleanup-attachments.md` | F6 richer inspection, F7 validation, F8 repair, F9 sanitize, F10 attachments | 1 | Done |
| 3 | `phase-3-output-options.md` | F11 compressed output, F12 incremental save | 1 | Done |
| 4 | `phase-4-page-operations.md` | F13 rotate/delete pages, F14 merge/split | 1 (2 for attachments) | Done |
| 5 | `phase-5-content.md` | F15 text extraction, F16 watermark | 1 | Done |
| 6 | `phase-6-signatures-certificates.md` | F17 digital signatures, F18 certificate encryption | 3 (and 2) | Done |
| 7 | `phase-7-conversion-common.md` | Shared rules for conversion | 5 | Done |
| 7a | `phase-7a-pdf-to-html.md` | F19 PDF → HTML | 5 | Done |
| 7b | `phase-7b-pdf-to-word.md` | F20 PDF → Word | 7a | Done |
| 7c | `phase-7c-html-to-pdf.md` | F21 HTML → PDF (confirm scope first) | 7a | Done |
| 7d | `phase-7d-word-to-pdf.md` | F22 Word → PDF | 7b, 7c | Done |
| 8 | `phase-8-cli.md` | F23 command-line tool | Run last | Done |

Phases 2–5 only depend on Phase 1, so they can be done in any order after it. Run one phase per session, and review
each phase's report before starting the next.
