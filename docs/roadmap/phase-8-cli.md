# PdfUtil roadmap, Phase 8: command-line tool

**Before you start:** read `docs/roadmap/00-common.md` completely and follow its rules.
**Prerequisites:** run this last, so it covers every feature. If some phases were skipped, include only the commands
whose features exist and list the rest as not available.

## Feature

**F23. `dotnet` global tool.**
- New project `src/PdfUtil.Cli`, packaged with `PackAsTool`, command name `pdfutil`.
- Subcommands for every feature: `protect`, `decrypt`, `security`, `info`/`inspect`, `edit-info`, `validate`, `repair`, `sanitize`, `attach`, `detach`, `extract-attachment`, `rotate`, `delete-pages`, `merge`, `split`, `text`, `watermark`, `sign`, `verify-signatures`, `convert` (direction inferred from file extensions, or set with `--to html|docx|pdf`), and `batch`.
- Use `System.CommandLine` only if it is a Microsoft package you confirm with me first; otherwise write a small argument parser.
- **Passwords must not be passed as plain arguments by default.** Read them from a prompt with hidden input, stdin (`--password-stdin`), or an environment variable named by `--password-env`. Document the risk if a plain `--password` flag is added at all.
- Output: human-readable by default, and `--json` for machine-readable results.
- Exit codes: 0 success, 1 usage error, 2 wrong password, 3 invalid or corrupt input, 4 compliance or policy refusal, 5 verification failure, 6 conversion produced an empty or unusable result.
- Tests run the tool in-process and as a process.
- Keep `samples/PdfUtil.Sample` as a minimal example.

## When done

Follow the end-of-phase checklist in `00-common.md` (the Blazor sample is not affected), and report.
