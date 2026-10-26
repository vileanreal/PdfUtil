# PdfUtil roadmap: common context for every phase

Read this file completely before starting any phase. Each phase file in this folder adds the features for that phase.

You are working in an existing .NET 8 solution at the repository root, **PdfUtil**. It is a PDF library that today
password-protects PDFs (AES-256/AES-128/RC4), verifies passwords, inspects documents, and can change document
information while protecting. Read the code before changing it; match its style and conventions exactly.

## Orientation

| Area | Location |
|---|---|
| Public API (services, options, results, exceptions) | `src/PdfUtil/*.cs` (`PdfServices.cs`, `Interfaces.cs`, `PdfProtectionOptions.cs`, `PdfResults.cs`, `PdfInfoChanges.cs`, `Pdf.cs` facade) |
| Reader: lexer, parser, xref, object streams, recovery, decryption | `src/PdfUtil/Internal/Parsing/`, `Internal/Document/PdfDocument.cs` |
| Writer (full rewrite, classic xref) | `Internal/Writing/PdfWriter.cs` |
| Security handler and crypto | `Internal/Security/` |
| Protection pipeline and self-verification | `Internal/Protection/ProtectionEngine.cs`, `OutputVerifier.cs` |
| Readers for pages, Info, XMP; Info/XMP editing | `Internal/Document/DocumentReaders.cs`, `InfoEditor.cs`, `ObjectGraph.cs` |
| DI package | `src/PdfUtil.Extensions.DependencyInjection/` |
| Tests (xUnit); all test PDFs come from a builder | `tests/PdfUtil.Tests/`, `Infrastructure/TestPdfBuilder.cs`, `InteropTests.cs` |
| Samples | `samples/PdfUtil.Sample` (console), `samples/PdfUtil.BlazorSample` (web app) |
| Docs | `README.md`, `docs/index.html` (tutorial, guides, API reference), `docs/requirements-traceability.md`, `docs/manual-compat-checklist.md` |

Key existing behaviour you must preserve:
- Output is a full rewrite: header + binary comment, plain objects, one classic xref with 20-byte entries, a single trailer.
- Every write is **self-verified** by re-reading the output in strict mode before anything reaches the caller. File output uses a temp file + atomic move; stream output is untouched on failure.
- The trailer `/ID` is handled as raw bytes; `/ID[0]` is preserved byte-for-byte.
- Passwords are never logged or put in messages; key buffers are zeroed.
- Hostile-input limits (`PdfUtilOptions`) apply to all new parsing.
- The `PasswordPolicy` rules (interoperable by default) apply to every feature that sets passwords.

## Global rules

1. **No breaking changes** to existing public APIs. Add new members, types and interfaces.
2. The core `PdfUtil` package keeps using only the .NET base class library. Features that need another Microsoft package (signatures, public-key encryption) or that are very large (conversion) go in **separate packages**, as their phase files describe.
3. **Generalize, don't duplicate.** Phase 1 refactors `ProtectionEngine` into a reusable internal rewrite pipeline: source → object transforms → optional encryption → writer → verification. `OutputVerifier` works from an "expected output" description, so every write feature gets self-verification for free. Later phases build on that pipeline. Protection must keep passing all its existing tests unchanged.
4. Every write feature must:
   - support stream and file input and output, with the atomic-file semantics;
   - support cancellation;
   - throw the existing typed exceptions, adding new ones only when needed;
   - return a result object with `Warnings`.
5. `TreatWarningsAsErrors` stays on. Write full XML docs for every public member. Keep engine types `internal`.
6. Thread-safe, stateless services that can be registered as singletons. Register every new service in `AddPdfUtil()` (or the package's own registration method).
7. Every long-running operation gets an `IProgress<PdfProgress>` overload (introduced in Phase 1, F5).
8. At the end of **each phase** (or sub-phase):
   - `dotnet build` passes with zero warnings and `dotnet test` is fully green;
   - the interop corpus in `InteropTests.cs` is extended with outputs of the new features, and the pdf.js and Apple PDFKit tests pass;
   - README, `docs/index.html` (guide + reference sections, with code samples you have compiled and run) and `docs/requirements-traceability.md` are updated;
   - the Blazor sample is updated where the phase file says so;
   - you report what was done, what was verified, and anything left open.
9. Ask before making a decision that changes existing behaviour or adds a dependency not listed in the phase file.
10. Proposed API names in the phase files are suggestions; keep them consistent with the existing naming.

## Definition of done (per feature)

- Public API with XML docs; facade methods where they make sense; DI registration.
- Self-verification of every written PDF, plus atomic output. Non-PDF outputs (HTML, DOCX) are validated structurally before they are returned.
- Unit tests covering normal paths, edge cases and hostile input (limits, cycles, malformed structures; for DOCX, zip bombs and zip-slip; for HTML, deep nesting). Every feature is tested on every input structure `TestPdfBuilder` supports, and on encrypted input where relevant.
- Interop corpus entries opened successfully by pdf.js and PDFKit (and qpdf, mutool and LibreOffice when installed).
- README section, an HTML docs guide and reference with code samples you compiled and ran, a traceability row, and Blazor sample support where listed.
- Final report: what was built, tests and interop results with numbers, known limitations, and any decision you made that I should review.
