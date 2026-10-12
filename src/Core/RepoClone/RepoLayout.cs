using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.RepoClone;

/// <summary>
/// Where each repo is cloned: <c>ADO\&lt;org&gt;\&lt;project&gt;\&lt;repo&gt;</c> and <c>GitHub\&lt;org&gt;\&lt;repo&gt;</c> under the
/// repos root. Names come from the sources, so they are made safe as folder names and can never leave the root.
/// </summary>
public static class RepoLayout
{
    public static string PathFor(string reposRoot, DiscoveredRepo repo) =>
        PathFor(reposRoot, repo.Source, repo.Org, repo.Project, repo.Name);

    public static string PathFor(string reposRoot, string source, string org, string? project, string name)
    {
        var parts = string.Equals(source, RepoOutcome.GitHub, StringComparison.OrdinalIgnoreCase)
            ? new[] { "GitHub", org, name }
            : new[] { "ADO", org, project ?? RepoOutcome.Unassigned, name };

        return Path.Combine([reposRoot, .. parts.Select(Sanitize)]);
    }

    /// <summary>
    /// Gives every repo its path. Two repos that would share one (names differing only in case or in characters
    /// that are not allowed in a folder name) are told apart by the end of the repo key, so neither overwrites the other.
    /// </summary>
    public static Dictionary<string, string> Assign(string reposRoot, IEnumerable<DiscoveredRepo> repos)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var repo in repos.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            var path = PathFor(reposRoot, repo);
            if (!used.Add(path))
            {
                var tail = repo.Key.Length > 8 ? repo.Key[^8..] : repo.Key;
                path = $"{path}~{tail}";
                used.Add(path);
            }

            paths[repo.Key] = path;
        }

        return paths;
    }

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || c < ' ' ? '_' : c).ToArray()).TrimEnd('.', ' ');
        return cleaned is "" or "." or ".." ? "_" : cleaned;
    }
}
