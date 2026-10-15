using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Status;

namespace Vizient.AutomatedScanner.Core.State;

public static class FoundSnapshotBuilder
{
    /// <summary>
    /// Builds the state to write after RepoClone has listed the orgs: the repos the source listed this time, laid out
    /// org, application, repo. A repo that is still there keeps what the last scans learned about it. One that has
    /// gone from an org that was listed in full is kept, marked Deleted, with its scan state. Repos of orgs not listed this
    /// scan are kept as they were. Per-run facts (existed locally, updated, archived flags) are not kept.
    /// </summary>
    /// <param name="incompleteOrgs">
    /// Orgs (source, name) whose listing did not finish this scan. Everything the previous state held for them is
    /// kept, scan state included, so a failed or partial listing does not erase repos that were never checked.
    /// </param>
    /// <param name="listedOrgs">
    /// Orgs (source, name) whose listing finished this scan. A repo of one of these that was not listed is gone, and is
    /// marked Deleted. A repo of any other org (not named this time, its source turned off, ListOnly) is kept as it was: the
    /// file stands for every repo on disk, not only the ones this run looked at. Null means the orgs that have a repo
    /// in <paramref name="reposThisScan"/>.
    /// </param>
    public static FoundOrgAppRepoDocument Build(
        string runId,
        DateTimeOffset now,
        IEnumerable<RepoOutcome> reposThisScan,
        FoundOrgAppRepoDocument previous,
        IEnumerable<(string Source, string Org)> incompleteOrgs,
        IEnumerable<(string Source, string Org)>? listedOrgs = null)
    {
        var earlier = previous.ToStoredRepos().ToList();
        var earlierByKey = earlier.ToDictionary(s => s.Repo.Key, StringComparer.Ordinal);

        var found = reposThisScan
            .Where(r => r.ExistedRemotely)
            .Select(r => new StoredRepo(
                r,
                earlierByKey.TryGetValue(r.Key, out var before)
                    ? new Dictionary<string, RepositoryScanState>(before.Scan, StringComparer.Ordinal)
                    : new Dictionary<string, RepositoryScanState>(StringComparer.Ordinal)))
            .ToList();
        var foundKeys = found.Select(s => s.Repo.Key).ToHashSet(StringComparer.Ordinal);

        var incomplete = incompleteOrgs
            .Select(o => OrgKey(o.Source, o.Org))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var listed = (listedOrgs ?? found.Select(s => (s.Repo.Source, s.Repo.OrgName)))
            .Select(o => OrgKey(o.Source, o.Org))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Every repo the file held and this scan did not list stays. One of an org that was listed in full is marked Deleted:
        // the source no longer has it, but its mirror and scan state are still ours. Any other is kept as it was.
        var carriedForward = earlier
            .Where(s => !foundKeys.Contains(s.Repo.Key))
            .Select(s =>
            {
                var org = OrgKey(s.Repo.Source, s.Repo.OrgName);
                return listed.Contains(org) && !incomplete.Contains(org)
                    ? s with { Repo = s.Repo with { RemoteStatus = RepositoryStatus.Deleted } }
                    : s;
            });

        return ToDocument(runId, now, previous.Tools, found.Concat(carriedForward));
    }

    /// <summary>Lays a flat list of repos out as the file: org, application, repo.</summary>
    public static FoundOrgAppRepoDocument ToDocument(
        string? runId,
        DateTimeOffset now,
        IReadOnlyDictionary<string, ToolInventoryState> tools,
        IEnumerable<StoredRepo> repos)
    {
        var stored = repos.ToDictionary(s => s.Repo.Key, StringComparer.Ordinal);

        var orgs = RepoGrouping.Group(stored.Values.Select(s => s.Repo))
            .Select(org => new FoundOrg
            {
                Source = org.Source,
                Name = org.Org,
                Applications = org.Applications
                    .Select(app => new FoundApplication
                    {
                        Name = app.Name,
                        Repos = app.Repos
                            .Select(r => new FoundRepo
                            {
                                Key = r.Key,
                                Name = r.Name,
                                LocalPath = r.LocalPath,
                                HeadSha = r.HeadSha,
                                LastCommitAt = r.LastCommitAt,
                                RemoteStatus = r.RemoteStatus,
                                Scan = stored[r.Key].Scan.Count == 0 ? null : new Dictionary<string, RepositoryScanState>(stored[r.Key].Scan)
                            })
                            .ToList()
                    })
                    .ToList()
            })
            .ToList();

        return new FoundOrgAppRepoDocument
        {
            RunId = runId,
            UpdatedAt = now,
            Tools = new Dictionary<string, ToolInventoryState>(tools),
            Orgs = orgs
        };
    }

    private static string OrgKey(string source, string org) => $"{source.Trim()}|{org.Trim()}";
}
