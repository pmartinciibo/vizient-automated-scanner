# Repository Cloning Scripts: Session Handoff

The files mentioned in thos document may be found in C:\envs\Vizient.

Date: 2026-10-10

This document summarizes the GitHub and Azure DevOps repository-cloning work completed today, including current behavior, authentication setup, configuration, troubleshooting, and validation. It is intended to be copied into a new project as a handoff.

## Current Files

- `CloneGitHubRepos.ps1`: discovers GitHub organizations available to each configured GitHub token and mirrors their repositories.
- `CloneAzureDevOpsRepos.ps1`: uses Azure CLI / Microsoft Entra access tokens to discover ADO organizations and mirror repositories.
- `configuration.json`: shared configuration file with GitHub token values and ADO tenant IDs/root paths.

Keep `configuration.json` private. It contains credential placeholders and should not be checked into source control if real tokens are entered there.

## Current Configuration Shape

The workspace configuration currently has this structure; paths are example/current workspace values, and token arrays are intentionally blank:

```json
{
  "GitHubTokens": [
    ""
  ],
  "GitHubRootDestinationFolder": "D:\\Repos\\GitHub",
  "ADOTenantIds": [],
  "ADORootDestinationFolder": "D:\\Repos\\ADO"
}
```

`ADOTenantIds` may be left empty to use tenant IDs returned by `az account list --all`, or populated with tenant GUIDs to restrict which tenants the ADO script processes.

## GitHub Script

`CloneGitHubRepos.ps1`:

- Reads `../configuration.json` relative to the script directory.
- Uses `GitHubTokens` to call the GitHub API and enumerate organizations and repositories, with page size 100.
- Creates a hierarchy of `<GitHubRootDestinationFolder>\<organization>\<repository>`.
- Uses a temporary Git HTTP authorization header for `git clone` and mirror updates. The token is not put in a repository URL or Git command-line arguments.
- Clones missing repositories as mirrors, updates existing valid bare mirrors with `git remote update --prune`, and removes/reclones an existing destination only if it is not a valid bare repository.
- Keeps an existing valid mirror when its remote update fails.
- Skips duplicate destination paths within one run when multiple tokens can see the same repositories.

The GitHub PAT must have the access/scopes needed to list organizations and clone the intended repositories. For private repositories, the token and its owner need access to those repos, including any organization SSO authorization.

One previously pasted plaintext GitHub token was removed from the script. Treat that credential as exposed and revoke/rotate it; use only replacement tokens in the private configuration file.

## Azure DevOps Script

`CloneAzureDevOpsRepos.ps1`:

- Looks for `configuration.json` beside the script, then in its parent directory.
- Requires `ADORootDestinationFolder` and Azure CLI (`az`) on `PATH`.
- Does not use ADO PATs. It gets an Azure DevOps access token from the signed-in Azure CLI session with resource ID `499b84ac-1321-427f-aa17-267ca6975798`.
- Uses `ADOTenantIds` if configured. If empty, enumerates tenant IDs from `az account list --all`.
- For each tenant, uses the Entra bearer token to call the ADO profile and accounts APIs for organization discovery, then enumerates projects (continuation-token pagination) and repositories.
- Creates `<ADORootDestinationFolder>\<organization>\<project>\<repository>`.
- Uses the Entra bearer token for ADO REST requests and as a temporary Git HTTP authorization header.
- Clones missing repos as mirrors, updates valid mirrors incrementally, and recovers incomplete/non-repository destination folders by removing and recloning those repo folders.
- Does not delete a valid local mirror if its remote update fails.
- Skips duplicate repository destinations during a run across tenant results.

The signed-in Entra identity must be a member of the target ADO organizations and have read permission to the relevant projects and repositories. The Profile and Accounts APIs used for discovery require Entra authentication; PAT authentication to those endpoints returned HTTP 401. The ADO REST and Git operations also require the signed-in identity to have repository access.

For interactive setup, run `az login`. If MFA or tenant policy requires it, sign in to the tenant explicitly and complete MFA:

```powershell
az login --tenant <tenant-guid>
```

To inspect tenant IDs available to the current Azure CLI session:

```powershell
az account list --all --query "[].tenantId" -o tsv
```

The script's fallback was changed from `az account tenant list` to the core `az account list --all` command because the former triggered an Azure CLI preview-extension warning in the user's environment. The workspace environment used for editing did not have `az` installed, so this fallback was not live-tested there. If the warning persists on the other machine, populate `ADOTenantIds` explicitly in `configuration.json` to bypass tenant enumeration.

Azure CLI user sign-in may require interaction again after token expiration or Conditional Access/MFA policy changes. For unattended execution, use an approved noninteractive Entra identity/authentication design and ensure that identity is granted access to every target organization. Tenant access is distinct: an identity/token for one tenant may not cover organizations in another tenant.

## Troubleshooting Notes

### Git asks for a username during clone

Git clone is a separate process from the API request. Ensure the relevant script is updated to pass an HTTP authorization header to Git. The current GitHub script uses a token-derived Basic header; the ADO script uses a Bearer header from Azure CLI. Both temporarily configure Git through environment variables and disable terminal prompts.

### `fatal: not a git repository` while updating

This can happen when an earlier run was interrupted during its first clone and left a destination directory without valid Git metadata. The current scripts test for a valid bare repository before updating; invalid/incomplete repo folders are removed and cloned again.

### ADO profile request returns `401 Unauthorized`

The earlier ADO implementation sent a PAT to the Profiles/Accounts discovery endpoints. Those APIs require Entra auth, so adding PAT scopes does not fix that failure. The current ADO script instead obtains an Entra access token through Azure CLI.

### `az login` returns `AADSTS50076`

This indicates the tenant requires MFA or another interactive Conditional Access step. Run `az login --tenant <tenant-guid>` and complete the prompt. It is not an ADO PAT error.

### Azure CLI preview extension warning

The script no longer calls `az account tenant list`; it uses `az account list --all`. If using an older copy under another directory, copy the current script there. Setting `ADOTenantIds` explicitly bypasses automatic tenant enumeration.

## Validation Performed

- PowerShell parser checks passed for both cloning scripts after edits.
- Editor diagnostics reported no errors for the ADO script and shared configuration at the end of the changes.
- GitHub's environment-configured authorization-header behavior was checked with a dummy token string, without contacting GitHub.
- A local bare-mirror test confirmed `remote update --prune` advances an existing mirror when the source changes.
- Local ADO helper tests confirmed invalid destination recovery and incremental mirror updates, without contacting Azure DevOps.
- Configuration JSON was parsed and validated after adding ADO settings.
- No real GitHub or ADO clone/API run was performed as part of the final edits; credential placeholders were blank and Azure CLI was not installed in the editing environment.

## Important Operational Caveats

- Ensure the script is run alongside the intended `configuration.json`, or at a path where its config lookup can find the file. The GitHub script expects the config one directory above it; the ADO script checks beside itself and then one directory above.
- A prior run was made from `C:\Tools\RepoCloning`, separate from the workspace path `C:\envs\Vizient`. Copy updated scripts and configuration deliberately; editing one copy does not update the other.
- Root destinations and nested folder names are used directly from organization/project/repository names. If names contain path-invalid characters on Windows, add a safe path-component normalization step before production use.
- Current scripts abort on many API/clone errors rather than generating a complete per-repository failure report. A later improvement could catch errors at organization/repository scope and summarize success/failure counts.
- Treat GitHub and Entra tokens as passwords. Never paste them into chat, source code, command logs, or a committed configuration file. Rotate credentials if exposed.