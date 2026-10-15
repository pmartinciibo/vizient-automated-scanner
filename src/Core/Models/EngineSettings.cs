namespace Vizient.AutomatedScanner.Core.Models;

/// <summary>
/// The engine.json settings beyond paths and intervals (design section 6.1): where to look for repos, how to
/// retry and skip, and each tool's own settings. Secret values never appear here, only secret names.
/// </summary>
public sealed record EngineSettings
{
    public List<SourceSettings> Sources { get; init; } = new();
    public bool ScanArchived { get; init; }
    public SkipSettings Skip { get; init; } = new();
    public RetrySettings Retry { get; init; } = new();
    public SecretsSettings Secrets { get; init; } = new();
    public ScanSettings Scan { get; init; } = new();

    /// <summary>Keyed by tool name. A tool with no entry uses the built-in defaults.</summary>
    public Dictionary<string, ToolSettings> Tools { get; init; } = new();

    public ToolSettings Tool(string name) => Tools.GetValueOrDefault(name) ?? new ToolSettings();
}

/// <summary>
/// A place to find repos, and the name of the secret that holds its token. A GitHub entry that names no org: the engine
/// asks GitHub which organizations the token belongs to and scans them all. One that names orgs scans only those. An Azure DevOps entry names its org.
/// </summary>
public sealed record SourceSettings
{
    public const string Ado = "ADO";
    public const string GitHub = "GitHub";

    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// What request.json calls this entry (tools.RepoClone.sources.&lt;name&gt;), to turn it off or only list it for one run.
    /// Required, and unique among the entries.
    /// </summary>
    public string? Name { get; init; }

    public string Org { get; init; } = string.Empty;
    public string? TokenSecret { get; init; }

    /// <summary>
    /// GitHub only. Also scan the repos owned by the account the token belongs to (a person, not an organization), shown
    /// under the account's name as its org. Off by default: the organizations are the estate, and a personal
    /// account's repos are not. A fine-grained token made for a personal account reaches nothing else.
    /// </summary>
    public bool IncludeOwnRepos { get; init; }

    /// <summary>
    /// The orgs this entry names: <see cref="Org"/> and <see cref="Orgs"/> together, each once. An Azure DevOps entry may
    /// name one or several, all read with the one token.
    /// </summary>
    public IReadOnlyList<string> NamedOrgs() =>
        (Orgs ?? []).Prepend(Org)
            .Select(o => o?.Trim() ?? string.Empty)
            .Where(o => o.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Organizations to scan. GitHub: only these, so several entries can share one token and each keep its own orgs
    /// (this also reaches orgs the token cannot find by itself, such as one whose public repos the account can read but
    /// which it does not belong to). Left out means discovery: every org the token belongs to. Azure DevOps: more orgs for the same token, besides "org". An org that cannot be listed is
    /// flagged for that org only.
    /// </summary>
    public List<string>? Orgs { get; init; }
}

/// <summary>How the scan phase (the five scanners) works through the repos.</summary>
public sealed record ScanSettings
{
    /// <summary>
    /// How many repos have a working copy on disk at once. Each one is expanded from its mirror, scanned by every
    /// tool that applies, and deleted. Left out means the largest maxParallel of the scanners that are running, so
    /// the slowest tool is never starved of repos.
    /// </summary>
    public int? MaxParallelRepos { get; init; }
}

public sealed record SkipSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Share of repos that are rescanned even if unchanged, to catch drift. A request can override it.</summary>
    public int CanaryPercent { get; init; } = 5;

    public int FullRescanEveryDays { get; init; } = 30;
}

/// <summary>How the secrets file is managed and checked. The secrets themselves are in secrets.dat, never here.</summary>
public sealed record SecretsSettings
{
    /// <summary>
    /// If set, "secrets set" and "secrets remove" refuse to run as any other Windows account. The file can only be
    /// read by the account that wrote it, so this stops someone creating one nobody can use.
    /// </summary>
    public string? RequiredAccount { get; init; }

    /// <summary>A secret that expires within this many days raises an ExpiringSecret alert.</summary>
    public int ExpiryWarnDays { get; init; } = 30;
}

public sealed record RetrySettings
{
    public int MaxAttempts { get; init; } = 2;
    public int BackoffSeconds { get; init; } = 60;
}

/// <summary>
/// One tool's settings. Every field is optional: what is left out falls back to the built-in default, and a
/// request.json override (enabled, maxParallel, timeoutMinutes) wins over what is here for that run.
/// </summary>
public sealed record ToolSettings
{
    public bool? Enabled { get; init; }
    public int? MaxParallel { get; init; }
    public int? TimeoutMinutes { get; init; }

    /// <summary>Pinned values. Both are part of the skip key.</summary>
    public string? Version { get; init; }
    public string? RulesVersion { get; init; }

    /// <summary>Tool mode: TruffleHog "filesystem", Snyk "code" or "dependencies". Snyk dependencies is never skipped.</summary>
    public string? Mode { get; init; }

    public bool? Verify { get; init; }
    public List<string>? Packs { get; init; }
    public bool? LongPaths { get; init; }

    /// <summary>Rapid7 and CheckMarx.</summary>
    public string? BaseUrl { get; init; }
    public string? TokenSecret { get; init; }

    /// <summary>
    /// Rapid7 only. How the token is sent: "api_key" (an X-Api-Key header, for InsightAppSec) or "console_token"
    /// (HTTP Basic with the token as the user name). Defaults to "api_key".
    /// </summary>
    public string? AuthMode { get; init; }

    /// <summary>Rapid7 and CheckMarx. Records per request. Defaults to 100.</summary>
    public int? PageSize { get; init; }

    /// <summary>Rapid7 and CheckMarx. Stop after this many pages of each area, for a trial run. 0 or left out means all pages.</summary>
    public int? MaxPages { get; init; }

    /// <summary>
    /// Rapid7: which of applications, vulnerabilities, scans and reports to export. Left out means applications and
    /// vulnerabilities. Scans and reports, if listed, are added to each application alongside its vulnerabilities.
    /// CheckMarx: which of applications, projects and results. Left out means all three. Results need the other two.
    /// </summary>
    public List<string>? Areas { get; init; }

    /// <summary>RepoClone only. Where git is, if it is not on the PATH of the account the engine runs as. Defaults to "git".</summary>
    public string? GitPath { get; init; }

    /// <summary>RepoClone only. Where the Azure CLI is, if it is not on the PATH. Defaults to "az.cmd". It is how Azure DevOps access tokens are obtained.</summary>
    public string? AzPath { get; init; }

    /// <summary>
    /// RepoClone only. The Entra tenants to try for Azure DevOps. Left out means every tenant the signed-in Azure CLI
    /// account knows (az account list). For each organization the first tenant whose token can list it is used.
    /// </summary>
    public List<string>? AdoTenantIds { get; init; }

    /// <summary>CheckMarx only. The tenant (realm) in the token address, for example in https://.../realms/{tenant}/.... Required unless tokenUrl has no {tenant} in it.</summary>
    public string? Tenant { get; init; }

    /// <summary>CheckMarx only. The address that swaps the refresh token for an access token. May contain {tenant}. Left out means the US IAM address.</summary>
    public string? TokenUrl { get; init; }

    /// <summary>CheckMarx only. The OAuth client id. Defaults to "ast-app".</summary>
    public string? ClientId { get; init; }

    /// <summary>CheckMarx only. Which scans to read findings from: "latest-per-project" (the default) or "all", which is one call for every scan there has ever been.</summary>
    public string? ResultsScope { get; init; }

    /// <summary>CheckMarx only. Read findings only from scans that ran this engine. Defaults to "sast". An empty string turns the filter off.</summary>
    public string? ResultsEngine { get; init; }

    /// <summary>CheckMarx only. Read findings only from scans with this status. Defaults to "Completed". An empty string turns the filter off.</summary>
    public string? ResultsScanStatus { get; init; }

    /// <summary>CheckMarx only. Read findings from at most this many scans, for a cheap trial run. 0 or left out means no limit.</summary>
    public int? ResultsMaxScans { get; init; }

    /// <summary>
    /// Scanners. The program to run, if it is not on the PATH of the account the engine runs as: trufflehog,
    /// semgrep, snyk, sonar-scanner (SonarStandard) or dotnet-sonarscanner (SonarDotNet).
    /// </summary>
    public string? ExePath { get; init; }

    /// <summary>SonarDotNet only. The dotnet program used for SDK-style builds. Defaults to "dotnet".</summary>
    public string? DotnetPath { get; init; }

    /// <summary>SonarDotNet only. Optional Visual Studio MSBuild.exe path for legacy project builds; otherwise discovered with vswhere.</summary>
    public string? MsbuildPath { get; init; }

    /// <summary>SonarDotNet only. Framework SonarScanner executable used with Visual Studio MSBuild.</summary>
    public string? MsbuildScannerPath { get; init; }

    /// <summary>SonarDotNet only. Version of the Framework SonarScanner, for the scan skip key.</summary>
    public string? MsbuildScannerVersion { get; init; }

    /// <summary>SonarDotNet only. Run SonarScanner CLI without a build if the .NET build fails. Defaults to true.</summary>
    public bool? FallbackToStandardScanner { get; init; }

    /// <summary>Snyk only. The Snyk organization the results are reported to (--org). Left out means the account's default.</summary>
    public string? Org { get; init; }

    /// <summary>SonarQube.</summary>
    public string? ServerUrl { get; init; }

    /// <summary>Environment variable name to secret name. Only these variables are set for the tool.</summary>
    public Dictionary<string, string>? Env { get; init; }

    /// <summary>Run the tool under a locked-down account (Snyk dependency mode, SonarDotNet).</summary>
    public RunAsSettings? RunAs { get; init; }
}

public sealed record RunAsSettings
{
    public string Account { get; init; } = string.Empty;
    public string PasswordSecret { get; init; } = string.Empty;
}
