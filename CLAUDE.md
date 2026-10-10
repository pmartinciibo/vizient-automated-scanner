# Claude instructions: Vizient AutomatedScanner

A standalone .NET scan engine for the Vizient engagement. It executes one scan of the
Vizient repo estate (ADO + GitHub) and produces raw JSON results, using a file-based
in and out communication path.

## Source of truth

- `docs/Vizient_Scan_Engine_Design_v1.txt` is the design. If anything conflicts, it wins
  (except the namespace, see below).
- `docs/Vizient_Scan_VM_Install_Instructions_v1.txt` covers installing the tools on the VM.
- `docs/reference/` is background only: the earlier full design
  (`AutomatedScanningDesign.txt`, far larger than this scope) and a superseded spec.

## Scope

- **In scope:** the Engine console app only. Eight "tools": Rapid7 import, CheckMarx
  import, RepoClone (discover, track, clone), TruffleHog, Semgrep, Snyk, SonarStandard
  (non-.NET repos), SonarDotNet (.NET repos).
- **Out of scope (do not design or build; flag a glaring problem instead):** Runner,
  database/loader, spreadsheets and reporting, delta and burn-down, ownership import,
  severity normalization, finding status model, dedup across tools, risk/health scores,
  secret aggregation, Nucleus export, any UI.

## Naming

- Default namespace is **Vizient.AutomatedScanner**. Projects:
  `Vizient.AutomatedScanner.Engine` (console), `Vizient.AutomatedScanner.Core` (library),
  `Vizient.AutomatedScanner.Tests` (xunit).
- The design document says `Vizient.Scanner.Engine` in places. The namespace above
  supersedes it.
- Target framework: net10.0 (SDK 10.0.401 installed here). The VM needs the matching
  runtime and SDK.

## Key decisions already made

- File-based in and out. Inputs: `engine.json`, `request.json`, `command.json`. Outputs:
  `status.json` (rewritten every 5 s), `repo-changes.json`, raw results and
  per-repo/per-tool markers (`.done.json`, `.failed.json`, `.skipped.json`).
- One run folder = one scan. Resume reuses the same folder. A new scheduled scan always
  gets a new folder.
- The loop over repos lives inside the app. Each tool is invoked once per repo as a child
  process (not through the old folder-walking scripts), with its own timeout, retry, and
  parallelism limit. Kill the whole process tree on timeout (use a Windows Job Object).
- Phase 1 runs Rapid7, CheckMarx, and RepoClone in parallel. Phase 2 runs the five
  scanners in parallel after RepoClone finishes.
- Repos are identified by stable ID (`<source>-<id>`), never by name. A repo is Deleted
  only if its org enumeration completed and a direct lookup returns not found; errors
  mean Unreachable.
- Skip a repo for a tool only if the whole skip key matches the last successful scan
  (HEAD SHA, scan mode, tool version, rules version, config hash). Any doubt or error
  means scan. Never skip imports or Snyk in dependency mode. Canary rescans (default 5%)
  and scheduled/forced full rescans are part of the design.
- A failure of one repo or tool is flagged, not fatal to the run. State for a repo/tool is
  written only after a clean finish.
- Secrets: one DPAPI-encrypted file (`CurrentUser` scope) read at runtime, managed by
  `engine secrets set|list|remove|check`. Each tool gets only its own secrets through
  environment variables. Never log or write secret values. Redact tool output before
  storing it.
- Snyk (dependency mode) and SonarDotNet build the code they scan, so they run under a
  locked-down account (`runAs`).

## Conventions

- Times are UTC ISO-8601. Every file has a `schemaVersion`.
- Every rewritten file is written to a temp file and then renamed over the target
  (atomic). Single writer per file (design section 4).
- Windows long paths are required (`core.longpaths`).
- Use the `git` command line for clone/fetch/ls-remote.
- Suggested libraries: CliWrap, Serilog, System.CommandLine, System.Text.Json.

## Build, test, run

```
dotnet build
dotnet test
dotnet run --project src/Engine -- run --request <path to request.json>
```

## Suggested build order

1. File models and atomic read/write (request, status, repos, markers).
2. `ProcessRunner` (timeout, kill process tree, Job Object), tested against a fake tool.
3. Status writer and command watcher.
4. RepoClone discovery and change tracking (APIs mocked), then clone/update.
5. One scan tool end to end (TruffleHog), then Semgrep, Snyk, SonarStandard, SonarDotNet.
6. Rapid7 and CheckMarx imports.
7. Skip policy, canaries, full rescans.
8. Secrets subcommands (DPAPI).

## Open items (design document section 15)

Eligibility filter (active since 2025, code repos only for Semgrep/Snyk/Sonar); output
format the downstream consumer expects; baseline comparability of scan settings; repo list
completeness (about 4,800 known vs roughly 6,000 expected, seed `FoundOrgAppRepo.json`); Vizient
credentials and SecOps sign-off for TruffleHog verification; the locked-down account for
build tools; Windows long paths; SonarQube edition and Java version; Archived/Disabled
repo policy; who consumes run folders.

## Existing material on this machine (reference only)

- `C:\envs\Vizient\*.ps1`: the current clone and scan scripts. Use them as a reference for
  flags and exclusions (`exclusions.txt`). **They contain plaintext tokens. Never copy
  tokens into this repo, and never print or log them. They need rotating.**
- `C:\envs\Vizient\Scripts\scan_pipeline\`: the Python pipeline that built the earlier
  reports (severity rules, workbooks).
- `C:\envs\Vizient\Matt_list_of_repos.csv`: about 4,825 known repos with last-commit dates
  and languages (useful for seeding and for pilot selection).
- `C:\envs\Vizient\MattSolution\vizient\`: Matt's existing Spring Boot/React/Python portal.
  Reference only. Do not modify it.

## Working preferences

- Keep answers short; prefer bullets over paragraphs.
- Do not fabricate facts. When something is unverified, say so.
- Do not design for out-of-scope pieces; flag glaring problems instead.
- Do not commit or push unless asked. Confirm before any outward-facing or hard-to-reverse
  action.
