# Vizient Scan Engine: Specification

Draft v1, October 2026. Scope: the Engine console app only. The Runner, database, spreadsheets, Nucleus export, and any UI are out of scope until stated otherwise.

## 1. Purpose

One console app (the **Engine**) that executes a single scan and produces JSON results. It:

- imports Rapid7 and CheckMarx findings,
- discovers and tracks repos in Azure DevOps (ADO) and GitHub, and clones or updates them,
- scans repos with TruffleHog, Semgrep, Snyk, SonarQube (standard) and SonarQube for .NET,
- skips repos that have not changed since their last successful scan,
- communicates only through files (no database, no UI).

## 2. Invocation

```
engine run --run-folder D:\Scanner\runs\<runId> [--resume] [--force-full] [--tools Semgrep,SonarStandard]
engine secrets set <name> [--expires <yyyy-mm-dd>] [--stdin]
engine secrets list
engine secrets remove <name>
engine secrets check
```

- Task Scheduler starts `engine run` Monday and Thursday at 3 AM under the service account.
- A lock file prevents overlapping runs. A long run delays the next start instead of overlapping it.
- **Exit codes (`run`):** 0 success, 1 completed with flagged failures, 2 fatal.
- **Resume:** run again against the same run folder. Repos and tools with a `.done.json` marker are not repeated.
- **Stop:** Ctrl+C or end the process. There is no command file.

## 3. Run flow

1. **Preflight:** validate `engine.json`, run the equivalent of `secrets check`, take the lock.
2. **In parallel:**
   - Rapid7 import,
   - CheckMarx import,
   - repo discovery, change tracking, and clone/fetch (section 4).
3. **Scan:** TruffleHog, Semgrep, Snyk, Sonar standard, Sonar for .NET. Per repo and tool:
   - apply the skip rules (section 5),
   - run with a timeout and one retry,
   - respect the per-tool parallelism limit.
4. **Finish:** write the final `status.json` and exit code.

A failure of a single repo or tool is flagged and recorded. It does not fail the run.

## 4. Repo discovery and tracking

- Repos are identified by stable ID, never by name: the ADO repo GUID or the GitHub repo ID. The repo key is `<source>-<id>`.
- **Statuses:** `Active`, `Archived` (GitHub flag), `Disabled` (ADO flag), `Deleted`, `Unreachable`.
- **Change events:** `New`, `StatusChanged`, `Renamed`, `Deleted`, `Reappeared`, `Unreachable`.
- **Deleted rule:** a repo is marked `Deleted` only if its org's enumeration completed successfully and a direct lookup of the repo returns "not found". Any error, partial enumeration, or access failure means `Unreachable`.
- **New repos** are cloned and scanned in the same run.
- **Clones** are never deleted automatically. A renamed repo's local folder is moved, or re-cloned if the move fails.
- **Policy settings:** `scanArchived` (default false) in `engine.json`.
- **Persistent state:** `D:\Scanner\state\repos.json`, read at the start of discovery and rewritten atomically when discovery finishes. A copy is written into the run folder as `repos.json`.
- **Response file:** `repo-changes.json` in each run folder.

## 5. Skipping unchanged repos

The goal is to skip repos that have not changed since their last successful scan. Where there is any doubt, scan.

- **Skip key:** repo, tool, HEAD SHA of the default branch, scan mode, tool version, rules version, config hash.
- **Rule:** skip a repo for a tool only if every part of the key exactly matches the stored state for that repo and tool. A missing or unknown part means scan.
- **SHA check:** `git ls-remote <url> HEAD` before cloning or fetching. Any error means scan (or flag the repo `Unreachable`).
- **State:** per repo and tool in `repos.json`. Written only after the tool finishes cleanly for that repo, and records the SHA actually checked out.
- **Never skipped:** the Rapid7 and CheckMarx imports, and Snyk when configured in dependency mode (new CVEs appear without code changes).
- **Pinned versions:** the Semgrep rule packs and the Sonar quality profile are pinned in `engine.json` so rule changes show up in the key.
- **Canary rescans:** each run, a random `canaryPercent` (default 5%) of otherwise skippable repo/tool pairs are scanned anyway. If a canary's `findingCount` differs from the stored count while the SHA is unchanged, the Engine raises a `CanaryMismatch` alert and sets that tool's `forceFull` in `repos.json`. The next run scans that tool on all repos, and the flag then clears.
- **Scheduled full rescan:** if `now - lastFullScanAt` for a tool exceeds `fullRescanEveryDays` (default 30), that tool scans all repos. `--force-full` does this for all tools.
- **First run:** no state exists, so everything is scanned.
- **Reporting:** a skipped repo gets a `.skipped.json` marker. Its findings carry forward and must never be read as fixed.

## 6. Files

### 6.1 Folder layout

```
C:\Scanner\
  bin\engine\            Vizient.Scanner.Engine.exe
  config\                engine.json, shared\exclusions.txt, per-tool files
  secrets\               secrets.dat (+ secrets.dat.bak)
  ops\                   Start-Scan.ps1, Resume-Scan.ps1, Get-Status.ps1
  logs\
C:\Tools\                trufflehog.exe, snyk.exe, sonar-scanner\, semgrep (python venv)

D:\Repos\
  ADO\<org>\<project>\<repo>\
  GitHub\<org>\<repo>\
D:\Scanner\
  state\repos.json
  runs\<runId>\
    status.json
    repo-changes.json
    repos.json                      (snapshot)
    results\<tool>\<repoKey>.json   (raw tool output)
    results\<tool>\<repoKey>.done.json | .failed.json | .skipped.json
  tmp\                              (TEMP/TMP for tools)

E:\  database (not used by the Engine)
F:\  SonarQube (native Windows install; verify Java version and edition)
```

- **Retention:** keep raw results for about the last 4 runs. `Start-Scan.ps1` deletes older run folders. Raw results can contain secrets and source snippets, so apply NTFS permissions and the retention and deletion terms of the written agreement.
- **Run folders:** one per scan. Never reuse a folder for a new scan, except to resume it.

### 6.2 engine.json (non-secret config)

```json
{
  "schemaVersion": 3,
  "paths": { "reposRoot": "D:\\Repos", "state": "D:\\Scanner\\state" },
  "secrets": { "path": "C:\\Scanner\\secrets\\secrets.dat", "requiredAccount": "DOMAIN\\svc-scanner", "expiryWarnDays": 30 },
  "sources": [
    { "source": "ADO", "org": "vizientinc", "tokenSecret": "ado-pat" },
    { "source": "GitHub", "org": "RnD-Vizient", "tokenSecret": "github-token" }
  ],
  "scanArchived": false,
  "skip": { "enabled": true, "canaryPercent": 5, "fullRescanEveryDays": 30 },
  "tools": {
    "TruffleHog":    { "enabled": true, "maxParallel": 8, "timeoutMinutes": 30, "version": "3.x", "rulesVersion": "detectors-3.x", "mode": "filesystem", "verify": false },
    "Semgrep":       { "enabled": true, "maxParallel": 8, "timeoutMinutes": 30, "version": "1.x", "rulesVersion": "p/default@2026-10-01", "packs": ["p/default", "p/owasp-top-ten", "p/cwe-top-25"] },
    "Snyk":          { "enabled": true, "maxParallel": 4, "timeoutMinutes": 45, "version": "1.x", "rulesVersion": "n/a", "mode": "code",
                       "env": { "SNYK_TOKEN": "snyk-token" },
                       "runAs": { "account": "DOMAIN\\svc-buildtools", "passwordSecret": "buildtools-account-password" } },
    "SonarStandard": { "enabled": true, "maxParallel": 4, "timeoutMinutes": 60, "version": "10.x", "rulesVersion": "profile-2026-10-01",
                       "env": { "SONAR_TOKEN": "sonar-token" } },
    "SonarDotNet":   { "enabled": true, "maxParallel": 4, "timeoutMinutes": 60, "version": "10.x", "rulesVersion": "profile-2026-10-01",
                       "env": { "SONAR_TOKEN": "sonar-token" },
                       "runAs": { "account": "DOMAIN\\svc-buildtools", "passwordSecret": "buildtools-account-password" } }
  },
  "imports": {
    "Rapid7":    { "enabled": true, "tokenSecret": "rapid7-token" },
    "CheckMarx": { "enabled": true, "tokenSecret": "checkmarx-token" }
  },
  "retry": { "maxAttempts": 2, "backoffSeconds": 60 }
}
```

- The `configHash` in the skip key is computed by the Engine from a tool's settings plus the shared exclusions file.
- Snyk in `"mode": "dependencies"` is never skipped.
- `env` maps an environment variable to a secret name. Only those variables are set for that tool's child process.
- `runAs` (optional) starts a build-first tool under the locked-down account.

### 6.3 repos.json (persistent state)

```json
{
  "schemaVersion": 3,
  "updatedAt": "2026-10-14T03:20:00Z",
  "tools": { "Semgrep": { "lastFullScanAt": "2026-10-14T03:00:00Z", "forceFull": false } },
  "repos": [
    {
      "key": "ado-3f9c1a…", "source": "ADO", "id": "3f9c1a…", "org": "vizientinc", "project": "Claro", "name": "foo",
      "status": "Active", "statusChangedAt": "2026-09-01T00:00:00Z",
      "firstSeenRunId": "2026-10-14T03-00-00Z", "lastSeenRunId": "2026-10-14T03-00-00Z",
      "localPath": "D:\\Repos\\ADO\\vizientinc\\Claro\\foo", "lastCloneResult": "Ok",
      "scan": {
        "Semgrep": { "sha": "a1b2c3…", "scanMode": "worktree", "toolVersion": "1.x", "rulesVersion": "p/default@2026-10-01",
                     "configHash": "9f2e…", "scannedAt": "2026-10-14T05:02:00Z", "findingCount": 14 }
      }
    }
  ]
}
```

### 6.4 Markers (per repo and tool, in `results\<tool>\`)

- `<repoKey>.json`: raw tool output.
- `<repoKey>.done.json`: `{ "sha", "scanMode", "toolVersion", "rulesVersion", "configHash", "startedAt", "endedAt", "findingCount", "canary" }`
- `<repoKey>.failed.json`: `{ "category", "attempts", "message", "sha" }`. The message is scrubbed of secret values.
- `<repoKey>.skipped.json`: `{ "reason": "Unchanged", "sha", "key", "lastScannedAt" }`

### 6.5 status.json (rewritten every 5 seconds, atomically)

```json
{
  "schemaVersion": 3,
  "runId": "2026-10-14T03-00-00Z",
  "state": "Running",
  "pid": 4812,
  "mode": "Normal",
  "startedAt": "2026-10-14T03:00:02Z",
  "updatedAt": "2026-10-14T05:12:37Z",
  "endedAt": null,
  "phase": "Scan",
  "steps": [
    { "name": "Discovery", "status": "Completed", "done": 6004, "total": 6004, "failed": 0 },
    { "name": "Semgrep", "status": "Running", "done": 140, "total": 220, "skipped": 5400, "canaries": 11, "failed": 2, "running": 8 }
  ],
  "alerts": [ { "tool": "Semgrep", "type": "CanaryMismatch", "repo": "gh-1234", "was": 14, "now": 19 } ]
}
```

- **States:** `Starting`, `Running`, `Completed`, `CompletedWithFlags`, `Failed`, `Stopped`.
- **Alerts:** the latest 20 only. Types include `CanaryMismatch` and `ExpiringSecret`.
- **Dead-run detection:** a reader treats `updatedAt` older than 30 to 60 seconds, or a missing process ID, as a dead run.

### 6.6 repo-changes.json (response file, one per run)

```json
{
  "schemaVersion": 3,
  "runId": "2026-10-14T03-00-00Z",
  "generatedAt": "2026-10-14T03:20:00Z",
  "discovery": [
    { "source": "ADO", "org": "vizientinc", "completed": true, "reposFound": 2310, "error": null },
    { "source": "GitHub", "org": "RnD-Vizient", "completed": false, "reposFound": 0, "error": "401 Unauthorized" }
  ],
  "totals": { "known": 6004, "active": 5800, "archived": 150, "disabled": 20, "deleted": 30, "unreachable": 4, "new": 12, "changed": 7 },
  "changes": [
    { "type": "New", "key": "gh-91234", "source": "GitHub", "org": "RnD-Vizient", "name": "newrepo", "status": "Active" },
    { "type": "StatusChanged", "key": "ado-77ab…", "from": "Active", "to": "Archived", "detectedAt": "2026-10-14T03:18:00Z" },
    { "type": "Renamed", "key": "ado-3f9c…", "from": "vizientinc/Claro/old", "to": "vizientinc/Claro/foo" },
    { "type": "Deleted", "key": "gh-55021", "lastSeenRunId": "2026-10-11T03-00-00Z" },
    { "type": "Reappeared", "key": "gh-4410", "from": "Deleted", "to": "Active" },
    { "type": "Unreachable", "key": "ado-91cc…", "reason": "403" }
  ],
  "scanPlan": {
    "mode": "Normal",
    "perTool": { "Semgrep": { "toScan": 220, "skippedUnchanged": 5400, "canaries": 270, "skippedStatus": 150 } },
    "forcedFullTools": []
  }
}
```

## 7. Secrets

All secrets the Engine needs are held in a single encrypted file using Windows DPAPI.

### 7.1 Subcommands

```
engine secrets set <name> [--expires <yyyy-mm-dd>] [--stdin]
engine secrets list
engine secrets remove <name>
engine secrets check
```

- **`set`:** creates the file if absent and adds or replaces the secret. It prompts for the value without echoing it. `--stdin` reads the value from standard input for piping. There is deliberately no `--value` flag, because it would show in the process list and shell history. `--expires` sets the expiry date. The created date is set automatically.
- **`list`:** names, created date, expiry date and status (`OK`, `ExpiringSoon`, `Expired`). It never prints values.
- **`remove`:** deletes a secret.
- **`check`:** confirms the file decrypts and every secret named in `engine.json` exists and has not expired.
- **Exit codes (`secrets`):** 0 all good, 1 warnings only (expiring soon), 2 error (missing, expired, cannot decrypt, wrong account).
- **Account guard:** `set` and `remove` refuse to run unless the current Windows account matches `secrets.requiredAccount`. This prevents creating a file the service account cannot read.

### 7.2 File

- **Path:** `C:\Scanner\secrets\secrets.dat`. Read/write for the service account only.
- **Layout:** an 8-byte header `VSCRT001` in the clear, followed by the DPAPI blob (`DataProtectionScope.CurrentUser`, fixed application-specific entropy bytes).
- **Plaintext before encryption:**

```json
{
  "schemaVersion": 1,
  "secrets": {
    "ado-pat":          { "value": "…", "createdAt": "2026-10-12T14:00:00Z", "expiresAt": "2027-01-10" },
    "github-token":     { "value": "…", "createdAt": "…", "expiresAt": null },
    "rapid7-token":     { "value": "…", "createdAt": "…", "expiresAt": null },
    "checkmarx-token":  { "value": "…", "createdAt": "…", "expiresAt": null },
    "snyk-token":       { "value": "…", "createdAt": "…", "expiresAt": null },
    "sonar-token":      { "value": "…", "createdAt": "…", "expiresAt": null },
    "buildtools-account-password": { "value": "…", "createdAt": "…", "expiresAt": null }
  }
}
```

- **Writes:** write to a temp file, then replace the original. Keep one `secrets.dat.bak` copy (same encryption and permissions).

```csharp
// write
var blob = ProtectedData.Protect(jsonBytes, entropy, DataProtectionScope.CurrentUser);
// read
var jsonBytes = ProtectedData.Unprotect(File.ReadAllBytes(path), entropy, DataProtectionScope.CurrentUser);
```

Requires the `System.Security.Cryptography.ProtectedData` package (Windows only).

### 7.3 Runtime rules

- **Preflight:** `engine run` does the equivalent of `secrets check` first. A missing or expired required secret, or a file that cannot be decrypted, stops the run with exit code 2. A secret expiring within `expiryWarnDays` allows the run to proceed and adds an `ExpiringSecret` alert (name and date only) to `status.json`.
- **Permission check:** the preflight warns if the file is readable by accounts other than the service account and Administrators.
- **Load once** at startup into memory. Pass each tool only the secrets in its `env` map. Clear any inherited environment variables that match secret names before starting child processes.
- **Never write secret values** to `status.json`, result files, markers, or logs. Redact any secret value found in tool output (stderr sometimes echoes tokens) before it is stored.
- **Wipe** byte arrays after use (best effort).
- **Locked-down account:** Snyk and Sonar for .NET build the code they scan. They run under `runAs`, which cannot decrypt this file. The Engine hands each one only its own token.

### 7.4 Limits to know

- The file works only for that account on that machine. If the VM is rebuilt or an administrator resets the account's password, DPAPI can lose the key. Keep a procedure to re-enter secrets from Vizient.
- Anyone with administrator rights on the VM can decrypt the file.
- Test under the real Task Scheduler setup ("Run whether user is logged on or not" with a stored password), using a normal service account. A managed service account (gMSA) does not work well with DPAPI.

## 8. Time estimate (not measured)

| Run type | Elapsed | Driver |
|---|---|---|
| Typical Monday or Thursday run | about 5 to 6 hours (range 3 to 10) | Sonar server queue, about 5 hours |
| First run, or forced full rescan | about 1 day with a code-only, active-since-2025 filter; 2 to 4 days without it | Sonar, 40 to 100 hours of queue |

Assumptions: about 200 changed repos per Monday-to-Thursday cycle (from Matt's repo list, scaled for unlisted repos), 5% canaries, per-repo guesses of TruffleHog 8 s, Semgrep 25 s, Snyk 45 s, Sonar 40 s, Sonar for .NET about 5 minutes, and Sonar's server processing analyses one at a time. A timed pilot of 100 to 200 repos (including some .NET repos) should replace these guesses.

## 9. Out of scope

Runner, database, spreadsheets and reporting, delta and burn-down, ownership import, Nucleus export, UI, dedup across tools, risk and health scores, finding status model, secret aggregation.

## 10. Open items and risks

1. **Eligibility filter** (active since 2025, code repos only for Semgrep/Snyk/Sonar): not decided.
2. **Output format:** the format the downstream consumer expects (the existing Python pipeline) is not settled.
3. **Baseline comparability:** scan settings (Semgrep rules, TruffleHog options, exclusions) must match how the baseline was produced, or the delta is noise.
4. **Repo list:** about 4,800 known against roughly 6,000 expected. `repos.json` must be seeded from the existing list, or the first run reports every repo as `New`. Discovery covers only the orgs listed in `engine.json`.
5. **`repos.json` is critical state.** If it is lost, every repo looks new and deletions go undetected. Back it up. The per-run snapshot helps.
6. **Inputs from Vizient:** read-only ADO, GitHub, Rapid7 and CheckMarx credentials; SecOps sign-off for TruffleHog verification; a decision on the locked-down account (or exclusion) for Snyk and Sonar for .NET.
7. **Windows long paths:** enable long-path support in Windows and Git (`core.longpaths`).
8. **SonarQube on Windows Server:** install natively (not Docker). Verify the Java version and the edition, since compute-engine workers affect throughput.
9. **Policy decisions:** whether to scan Archived and Disabled repos, and how long to keep a deleted repo's local clone.
10. **Plaintext tokens** in the existing PowerShell scripts must be rotated before those scripts are used on the VM.
