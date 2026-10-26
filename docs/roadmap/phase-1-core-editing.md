# PdfUtil roadmap, Phase 1: core editing

**Before you start:** read `docs/roadmap/00-common.md` completely and follow its rules.
**Prerequisites:** none. This phase also lays the foundation for every later phase.

## Foundation: the rewrite pipeline

Refactor `ProtectionEngine` into a reusable internal rewrite pipeline:
source → object transforms → optional encryption → writer → verification.
`OutputVerifier` must work from an "expected output" description, so that every write feature gets self-verification
for free. Protection must keep passing all of its existing tests unchanged. Describe the new design in your report.

## Features

**F1. Remove protection (decrypt).**
- `IPdfEditor.DecryptAsync(input, output, string ownerPassword, …)`, plus facade methods.
- Requires the **owner** password by default, and throws `PdfInvalidPasswordException` otherwise. Document why: the user password alone must not lift the owner's restrictions.
- Output is an unencrypted full rewrite: no `/Encrypt`, `/ID[0]` preserved, and content byte-exact after decryption.
- Tests:
  - every revision R2–R6, and every structure in `TestPdfBuilder`;
  - streams compared byte-exact;
  - unencrypted input is a no-op rewrite with a warning;
  - interop corpus entries.

**F2. Change passwords or permissions.**
- `ChangeSecurityAsync(input, output, currentOwnerPassword, PdfSecurityChanges)`, covering new user and owner passwords, permissions and algorithm.
- Unspecified values keep their current setting where possible. Document which settings can't be kept: the old owner password can only be kept if it is supplied.

**F3. Edit document information without encrypting.**
- `EditInfoAsync(input, output, PdfInfoChanges, string? password = null)`.
- Encrypted input: re-encrypt with the **same** settings and passwords. This requires the owner password. Unencrypted input stays unencrypted.
- Reuse `InfoEditor` and `XmpEditor`.

**F4. Batch processing.**
- `IPdfBatchProcessor` that runs any operation over many files or streams with bounded parallelism (`MaxDegreeOfParallelism`), per-item results (success, error, warnings, timing) and overall progress.
- One failing file never aborts the batch unless `StopOnFirstError` is set.

**F5. Progress reporting.**
- `PdfProgress { Stage, Completed, Total }`, with stages Reading, Writing and Verifying (later phases add Layout and Converting).
- Reported at a bounded rate (no more than about every 100 ms or every N objects).
- Add `IProgress<PdfProgress>` overloads to the existing protect operations too.

## Blazor sample

Add a "Remove protection" page and a "Change security" page, and add an "Edit info only" mode to the existing
information editing.

## When done

Follow the end-of-phase checklist in `00-common.md`, and report.
