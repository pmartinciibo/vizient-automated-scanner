using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.State;

/// <summary>Quick "was this in the last snapshot?" answers, so RepoClone can tell what existed before the scan.</summary>
public sealed class FoundOrgAppRepoIndex
{
    public static readonly FoundOrgAppRepoIndex Empty = new(new FoundOrgAppRepoDocument());

    private readonly HashSet<string> _orgs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _applications = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _repos = new(StringComparer.Ordinal);

    public FoundOrgAppRepoIndex(FoundOrgAppRepoDocument snapshot)
    {
        foreach (var org in snapshot.Orgs)
        {
            _orgs.Add(OrgKey(org.Source, org.Name));
            foreach (var app in org.Applications)
            {
                _applications.Add(ApplicationKey(org.Source, org.Name, app.Name));
                foreach (var repo in app.Repos)
                {
                    _repos.Add(repo.Key);
                }
            }
        }
    }

    public bool HasOrg(string source, string org) => _orgs.Contains(OrgKey(source, org));

    public bool HasApplication(string source, string org, string application) =>
        _applications.Contains(ApplicationKey(source, org, application));

    public bool HasRepo(string key) => _repos.Contains(key);

    private static string OrgKey(string source, string org) => $"{source.Trim()}|{org.Trim()}";

    private static string ApplicationKey(string source, string org, string application) =>
        $"{OrgKey(source, org)}|{application.Trim()}";
}
