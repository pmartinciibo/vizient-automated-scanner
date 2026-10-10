using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Status;

namespace Vizient.AutomatedScanner.Core.State;

public static class FoundSnapshotBuilder
{
    /// <summary>
    /// Builds the snapshot to write after a scan: the repos the source listed this time, laid out org,
    /// application, repo. Per-run facts (existed locally, updated, archived flags) are not kept here.
    /// </summary>
    /// <param name="incompleteOrgs">
    /// Orgs (source, name) whose listing did not finish this scan. What the last snapshot held for them is
    /// kept, so a failed or partial listing does not erase repos that were never checked.
    /// </param>
    public static FoundOrgAppRepoDocument Build(
        string runId,
        DateTimeOffset now,
        IEnumerable<RepoOutcome> reposThisScan,
        FoundOrgAppRepoDocument previous,
        IEnumerable<(string Source, string Org)> incompleteOrgs)
    {
        var found = reposThisScan.Where(r => r.ExistedRemotely).ToList();
        var foundKeys = found.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var incomplete = incompleteOrgs
            .Select(o => OrgKey(o.Source, o.Org))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var carriedForward = previous.ToRepoOutcomes()
            .Where(r => incomplete.Contains(OrgKey(r.Source, r.OrgName)) && !foundKeys.Contains(r.Key));

        var orgs = RepoGrouping.Group(found.Concat(carriedForward))
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
                                RemoteStatus = r.RemoteStatus
                            })
                            .ToList()
                    })
                    .ToList()
            })
            .ToList();

        return new FoundOrgAppRepoDocument { RunId = runId, UpdatedAt = now, Orgs = orgs };
    }

    private static string OrgKey(string source, string org) => $"{source.Trim()}|{org.Trim()}";
}
