using System.Text.Json.Serialization;

namespace Vizient.AutomatedScanner.Core.Models;

/// <summary>
/// FoundOrgAppRepo.json: the engine's one store of lasting state about orgs, applications and repos. It holds the
/// orgs, applications and repos the last scan found, and for each repo what it last learned: the checked-out SHA
/// and the result of the last clean scan by each tool (which the skip policy compares against). What changed
/// since the previous scan is not kept here. That goes in results.json.
/// </summary>
public sealed record FoundOrgAppRepoDocument
{
    public int SchemaVersion { get; init; } = 3;

    /// <summary>The run that last wrote this file. Null for one that has never been written.</summary>
    public string? RunId { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Per tool: when it last tried every repo, and whether the next run must scan them all again.</summary>
    public Dictionary<string, ToolInventoryState> Tools { get; init; } = new();

    public List<FoundOrg> Orgs { get; init; } = new();

    [JsonIgnore]
    public bool HasAnyRepo => Orgs.Any(o => o.Applications.Any(a => a.Repos.Count > 0));

    /// <summary>The file as a flat list of repos, each with the scan state it carries.</summary>
    public IEnumerable<StoredRepo> ToStoredRepos() =>
        Orgs.SelectMany(org => org.Applications.SelectMany(app => app.Repos.Select(repo => new StoredRepo(
            new RepoOutcome
            {
                Key = repo.Key,
                Source = org.Source,
                Org = org.Name,
                // An ADO application is a project. For GitHub the application is the org and there is no project.
                Project = string.Equals(org.Source, RepoOutcome.GitHub, StringComparison.OrdinalIgnoreCase) ? null : app.Name,
                Name = repo.Name,
                LocalPath = repo.LocalPath,
                HeadSha = repo.HeadSha,
                LastCommitAt = repo.LastCommitAt,
                ExistedRemotely = true,
                RemoteStatus = repo.RemoteStatus
            },
            new Dictionary<string, RepositoryScanState>(repo.Scan ?? new(), StringComparer.Ordinal)))));

    /// <summary>The repos in the form RepoClone reports them, without their scan state.</summary>
    public IEnumerable<RepoOutcome> ToRepoOutcomes() => ToStoredRepos().Select(s => s.Repo);
}

public sealed record FoundOrg
{
    public string Source { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public List<FoundApplication> Applications { get; init; } = new();
}

public sealed record FoundApplication
{
    public string Name { get; init; } = string.Empty;
    public List<FoundRepo> Repos { get; init; } = new();
}

public sealed record FoundRepo
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? LocalPath { get; init; }

    /// <summary>The HEAD SHA that was checked out in the local clone when the repo was last seen.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HeadSha { get; init; }

    /// <summary>When the newest commit on any branch was made (UTC), or null if not found.</summary>
    public DateTimeOffset? LastCommitAt { get; init; }

    public RepositoryStatus RemoteStatus { get; init; } = RepositoryStatus.Active;

    /// <summary>By tool name: the last clean scan. Left out of the file for a repo no tool has scanned yet.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, RepositoryScanState>? Scan { get; init; }
}

/// <summary>A repo and its scan state, as the engine holds them while it works.</summary>
public sealed record StoredRepo(RepoOutcome Repo, Dictionary<string, RepositoryScanState> Scan);
