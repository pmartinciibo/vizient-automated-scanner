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
    private readonly ConcurrentDictionary<string, (string Source, string Org, string Message)> _orgs = new(StringComparer.OrdinalIgnoreCase);
    private volatile FoundOrgAppRepoIndex _prior = FoundOrgAppRepoIndex.Empty;

    /// <summary>What the last scan found, so results.json can show what is new. Empty until RepoClone loads it.</summary>
    public FoundOrgAppRepoIndex Prior => _prior;

    public void SetPrior(FoundOrgAppRepoDocument lastSnapshot) =>
        _prior = new FoundOrgAppRepoIndex(lastSnapshot);

    /// <summary>
    /// Says how the listing of an org went. A later call for the same org replaces the earlier. An org given a message
    /// is in <see cref="Orgs"/> whether or not it has any repos.
    /// </summary>
    public void SetOrgStatus(string source, string org, string message) =>
        _orgs[$"{source.Trim()}|{org.Trim()}"] = (source.Trim(), org.Trim(), message);

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
        var grouped = RepoGrouping.Group(Snapshot());

        // Orgs that were named or asked about but have no repos to show: still listed, with why.
        var present = grouped.Select(g => $"{g.Source}|{g.Org}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var empty = _orgs
            .Where(o => !present.Contains(o.Key))
            .Select(o => new OrgOutcome
            {
                Source = o.Value.Source,
                Name = o.Value.Org,
                ExistedLocally = false,
                ExistedRemotely = false,
                ExistedPriorToScan = prior.HasOrg(o.Value.Source, o.Value.Org),
                StatusMessage = o.Value.Message
            });

        return grouped
            .Select(org => new OrgOutcome
            {
                Source = org.Source,
                Name = org.Org,
                StatusMessage = _orgs.TryGetValue($"{org.Source}|{org.Org}", out var said) ? said.Message : null,
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
            .Concat(empty)
            .OrderBy(o => o.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Name == RepoOutcome.Unassigned)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
