using System.Text.Json.Serialization;

namespace Vizient.AutomatedScanner.Core.Models;

/// <summary>What this run found out about one repo. Repos sit under an application, which sits under an org.</summary>
public sealed record RepoOutcome
{
    public const string GitHub = "GitHub";
    public const string Unassigned = "Unassigned";

    public string Key { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Org { get; init; } = string.Empty;
    public string? Project { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? LocalPath { get; init; }

    /// <summary>
    /// The HEAD SHA that is actually checked out in the local clone, as RepoClone found it. The skip policy
    /// compares it with the SHA of the last clean scan. Null means unknown, which means scan.
    /// </summary>
    public string? HeadSha { get; init; }

    /// <summary>A clone was already on disk when the run looked.</summary>
    public bool ExistedLocally { get; init; }

    /// <summary>The source still listed the repo. A repo seen only on disk or in the inventory is false.</summary>
    public bool ExistedRemotely { get; init; }

    /// <summary>This run cloned it or pulled new changes into it.</summary>
    public bool UpdatedLocally { get; init; }

    /// <summary>
    /// It was in the last snapshot (FoundOrgAppRepo.json). False means new since the previous scan.
    /// Filled in when results.json is built, not by RepoClone.
    /// </summary>
    public bool ExistedPriorToScan { get; init; }

    /// <summary>What the source says about the repo. Detail behind <see cref="ArchivedOrRetired"/>.</summary>
    public RepositoryStatus RemoteStatus { get; init; } = RepositoryStatus.Active;

    /// <summary>The source marks the repo Archived or Disabled. Disabled is taken to be "retired".</summary>
    public bool ArchivedOrRetired => RemoteStatus is RepositoryStatus.Archived or RepositoryStatus.Disabled;

    /// <summary>The org the repo is grouped under.</summary>
    [JsonIgnore]
    public string OrgName => string.IsNullOrWhiteSpace(Org) ? Unassigned : Org.Trim();

    /// <summary>
    /// The application the repo is grouped under. An ADO project is an application. GitHub has no project
    /// level, so the org name stands in for it.
    /// </summary>
    [JsonIgnore]
    public string ApplicationName
    {
        get
        {
            if (string.Equals(Source, GitHub, StringComparison.OrdinalIgnoreCase))
            {
                return OrgName;
            }

            return string.IsNullOrWhiteSpace(Project) ? Unassigned : Project.Trim();
        }
    }
}

public sealed record RepoSummary
{
    public int Total { get; init; }
    public int ExistedLocally { get; init; }
    public int ExistedRemotely { get; init; }
    public int UpdatedLocally { get; init; }
    public int ArchivedOrRetired { get; init; }

    public static RepoSummary From(IReadOnlyCollection<RepoOutcome> repos) => new()
    {
        Total = repos.Count,
        ExistedLocally = repos.Count(r => r.ExistedLocally),
        ExistedRemotely = repos.Count(r => r.ExistedRemotely),
        UpdatedLocally = repos.Count(r => r.UpdatedLocally),
        ArchivedOrRetired = repos.Count(r => r.ArchivedOrRetired)
    };
}

/// <summary>An org (GitHub organization or ADO organization), its applications, and their repos.</summary>
public sealed record OrgOutcome
{
    public string Source { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    /// <summary>At least one of its repos already had a clone on disk.</summary>
    public bool ExistedLocally { get; init; }

    /// <summary>The source still lists at least one of its repos.</summary>
    public bool ExistedRemotely { get; init; }

    /// <summary>The org was in the last snapshot. False means new since the previous scan.</summary>
    public bool ExistedPriorToScan { get; init; }

    /// <summary>
    /// How this run got on with the org: "12 repos found", or why it could not be listed (the error, or that the request
    /// turned its source off). An org engine.json names is always in the results, with this, even if it has no repos.
    /// </summary>
    public string? StatusMessage { get; init; }

    public List<ApplicationOutcome> Applications { get; init; } = new();
}

/// <summary>An application (an ADO project; for GitHub, the org itself) and its repos.</summary>
public sealed record ApplicationOutcome
{
    public string Name { get; init; } = string.Empty;

    /// <summary>At least one of its repos already had a clone on disk.</summary>
    public bool ExistedLocally { get; init; }

    /// <summary>The source still lists at least one of its repos.</summary>
    public bool ExistedRemotely { get; init; }

    /// <summary>The application was in the last snapshot. False means new since the previous scan.</summary>
    public bool ExistedPriorToScan { get; init; }

    public List<RepoOutcome> Repos { get; init; } = new();
}
