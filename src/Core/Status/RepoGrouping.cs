using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.Status;

/// <summary>
/// Puts a flat list of repos into org, application, repo order. Used for results.json and for the
/// FoundOrgAppRepo.json snapshot, so both are laid out the same way.
/// </summary>
public static class RepoGrouping
{
    public sealed record OrgGroup(string Source, string Org, IReadOnlyList<ApplicationGroup> Applications)
    {
        public IEnumerable<RepoOutcome> Repos => Applications.SelectMany(a => a.Repos);
    }

    public sealed record ApplicationGroup(string Name, IReadOnlyList<RepoOutcome> Repos);

    /// <summary>
    /// Orgs are ordered by source and name, applications by name with "Unassigned" last, repos by key.
    /// An org name is only unique within its source, so GitHub and ADO orgs with the same name stay separate.
    /// </summary>
    public static IReadOnlyList<OrgGroup> Group(IEnumerable<RepoOutcome> repos) =>
        repos
            .GroupBy(r => (Source: r.Source.Trim(), Org: r.OrgName), OrgKeyComparer.Instance)
            .OrderBy(g => g.Key.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.Org == RepoOutcome.Unassigned)
            .ThenBy(g => g.Key.Org, StringComparer.OrdinalIgnoreCase)
            .Select(org => new OrgGroup(
                org.Key.Source,
                org.Key.Org,
                org.GroupBy(r => r.ApplicationName, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(a => a.Key == RepoOutcome.Unassigned)
                    .ThenBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(a => new ApplicationGroup(
                        a.Key,
                        a.OrderBy(r => r.Key, StringComparer.Ordinal).ToList()))
                    .ToList()))
            .ToList();

    // Source and org names compare without regard to case, as both services treat them.
    private sealed class OrgKeyComparer : IEqualityComparer<(string Source, string Org)>
    {
        public static readonly OrgKeyComparer Instance = new();

        public bool Equals((string Source, string Org) x, (string Source, string Org) y) =>
            string.Equals(x.Source, y.Source, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Org, y.Org, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Source, string Org) key) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Source),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.Org));
    }
}
