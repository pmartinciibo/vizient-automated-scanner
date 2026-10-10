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

    /// <summary>Keyed by tool name. A tool with no entry uses the built-in defaults.</summary>
    public Dictionary<string, ToolSettings> Tools { get; init; } = new();

    public ToolSettings Tool(string name) => Tools.GetValueOrDefault(name) ?? new ToolSettings();
}

/// <summary>An organization to scan, and the name of the secret that holds its token.</summary>
public sealed record SourceSettings
{
    public const string Ado = "ADO";
    public const string GitHub = "GitHub";

    public string Source { get; init; } = string.Empty;
    public string Org { get; init; } = string.Empty;
    public string? TokenSecret { get; init; }
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

    /// <summary>Rapid7 only. Records per request. Defaults to 100.</summary>
    public int? PageSize { get; init; }

    /// <summary>Rapid7 only. Stop after this many pages of each area, for a trial run. 0 or left out means all pages.</summary>
    public int? MaxPages { get; init; }

    /// <summary>
    /// Rapid7 only. Which of applications, vulnerabilities, scans and reports to export. Left out means applications
    /// and vulnerabilities. Scans and reports, if listed, are added to each application alongside its vulnerabilities.
    /// </summary>
    public List<string>? Areas { get; init; }

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
