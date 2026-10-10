using System.Collections.Concurrent;
using Vizient.AutomatedScanner.Core.IO;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.State;

namespace Vizient.AutomatedScanner.Core.Status;

/// <summary>
/// Collects what RepoClone learns about each repo, for the org, application and repo lists in results.json.
/// Many clone operations report at once, and a later report for the same repo replaces the earlier one.
/// </summary>
public sealed class RepoReport
{
    private readonly ConcurrentDictionary<string, RepoOutcome> _repos = new(StringComparer.Ordinal);
    private volatile FoundOrgAppRepoIndex _prior = FoundOrgAppRepoIndex.Empty;

    /// <summary>What the last scan found, so results.json can show what is new. Empty until RepoClone loads it.</summary>
    public FoundOrgAppRepoIndex Prior => _prior;

    public void SetPrior(FoundOrgAppRepoDocument lastSnapshot) =>
        _prior = new FoundOrgAppRepoIndex(lastSnapshot);

    public void Record(RepoOutcome repo)
    {
        if (!SafeName.IsValid(repo.Key))
        {
            throw new ArgumentException($"'{repo.Key}' is not a valid repo key.", nameof(repo));
        }

        _repos[repo.Key] = repo;
    }

    /// <summary>All repos reported so far, ordered by key so the file reads the same from run to run.</summary>
    public IReadOnlyList<RepoOutcome> Snapshot() =>
        _repos.Values.OrderBy(r => r.Key, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Org, then application, then repo, each marked with whether it was in the last snapshot.
    /// See <see cref="RepoGrouping.Group"/> for the ordering.
    /// </summary>
    public IReadOnlyList<OrgOutcome> Orgs()
    {
        var prior = _prior;

        return RepoGrouping.Group(Snapshot())
            .Select(org => new OrgOutcome
            {
                Source = org.Source,
                Name = org.Org,
                ExistedLocally = org.Repos.Any(r => r.ExistedLocally),
                ExistedRemotely = org.Repos.Any(r => r.ExistedRemotely),
                ExistedPriorToScan = prior.HasOrg(org.Source, org.Org),
                Applications = org.Applications
                    .Select(app => new ApplicationOutcome
                    {
                        Name = app.Name,
                        ExistedLocally = app.Repos.Any(r => r.ExistedLocally),
                        ExistedRemotely = app.Repos.Any(r => r.ExistedRemotely),
                        ExistedPriorToScan = prior.HasApplication(org.Source, org.Org, app.Name),
                        Repos = app.Repos
                            .Select(r => r with { ExistedPriorToScan = prior.HasRepo(r.Key) })
                            .ToList()
                    })
                    .ToList()
            })
            .ToList();
    }
}
