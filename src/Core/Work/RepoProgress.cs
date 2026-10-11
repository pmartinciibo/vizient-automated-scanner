using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.Work;

/// <summary>The words a tool's step shows while it works through the repos, so every tool says it the same way.</summary>
public static class RepoProgress
{
    /// <summary>"GitHub acme/repo" or "ADO org/project/repo": the source first.</summary>
    public static string Label(RepoOutcome repo) =>
        string.Join(' ', new[]
        {
            repo.Source,
            string.Join('/', new[] { repo.Org, repo.Project, repo.Name }.Where(part => !string.IsNullOrWhiteSpace(part)))
        }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>
    /// For example "47 of 4439 processed, 2 failed, 4000 skipped; now: ADO org/project/repo (+11 more running)".
    /// Processed counts the repos the tool had work for, which is what the total counts.
    /// </summary>
    public static string Text(int done, int failed, int skipped, int total, IReadOnlyList<string> running)
    {
        var text = $"{done + failed} of {total} processed";
        if (failed > 0)
        {
            text += $", {failed} failed";
        }

        if (skipped > 0)
        {
            text += $", {skipped} skipped";
        }

        if (running.Count > 0)
        {
            text += $"; now: {running[0]}";
            if (running.Count > 1)
            {
                text += $" (+{running.Count - 1} more running)";
            }
        }

        return text;
    }
}
