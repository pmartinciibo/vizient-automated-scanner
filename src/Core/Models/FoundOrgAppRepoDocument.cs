using System.Text.Json.Serialization;

namespace Vizient.AutomatedScanner.Core.Models;

/// <summary>
/// FoundOrgAppRepo.json: the orgs, applications and repos the last scan found. It is always the last known
/// snapshot and nothing else. What changed since the previous scan goes in results.json, not here.
/// </summary>
public sealed record FoundOrgAppRepoDocument
{
    public int SchemaVersion { get; init; } = 3;

    /// <summary>The run that wrote this snapshot. Null for a snapshot that has never been written.</summary>
    public string? RunId { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
    public List<FoundOrg> Orgs { get; init; } = new();

    [JsonIgnore]
    public bool HasAnyRepo => Orgs.Any(o => o.Applications.Any(a => a.Repos.Count > 0));

    /// <summary>The snapshot as a flat list of repos, in the form RepoClone reports them.</summary>
    public IEnumerable<RepoOutcome> ToRepoOutcomes() =>
        Orgs.SelectMany(org => org.Applications.SelectMany(app => app.Repos.Select(repo => new RepoOutcome
        {
            Key = repo.Key,
            Source = org.Source,
            Org = org.Name,
            // An ADO application is a project. For GitHub the application is the org and there is no project.
            Project = string.Equals(org.Source, RepoOutcome.GitHub, StringComparison.OrdinalIgnoreCase) ? null : app.Name,
            Name = repo.Name,
            LocalPath = repo.LocalPath,
            ExistedRemotely = true,
            RemoteStatus = repo.RemoteStatus
        })));
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
    public RepositoryStatus RemoteStatus { get; init; } = RepositoryStatus.Active;
}
