using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Core.State;

/// <summary>
/// RepoClone's two dealings with FoundOrgAppRepo.json: load what the last scan found before discovery, and write back
/// what this scan found afterwards. Kept apart from discovery and cloning so the rules for the file stand alone.
/// </summary>
public static class RepoSnapshotKeeper
{
    /// <summary>Loads the last snapshot and gives it to the report, so each org, application and repo can be told apart as new or existing.</summary>
    public static async Task<FoundOrgAppRepoDocument> LoadAsync(RunContext context, CancellationToken cancellationToken)
    {
        await context.FoundState.EnsureLoadedAsync(cancellationToken);
        var lastSnapshot = context.FoundState.LoadedSnapshot;
        context.Repos.SetPrior(lastSnapshot);
        return lastSnapshot;
    }

    /// <summary>
    /// Writes back what this scan found. A scan that found no repos at all, when the last snapshot had some,
    /// is treated as a failed discovery and leaves the file alone. An error never means "everything was
    /// deleted".
    /// </summary>
    public static async Task SaveAsync(
        RunContext context,
        FoundOrgAppRepoDocument lastSnapshot,
        IReadOnlyList<(string Source, string Org)> incompleteOrgs,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var found = context.Repos.Snapshot();

        if (!found.Any(r => r.ExistedRemotely) && lastSnapshot.HasAnyRepo)
        {
            context.Status.AddAlert(new StatusAlert
            {
                Type = "SnapshotNotUpdated",
                Tool = ToolNames.RepoClone,
                Name = $"{string.Join(" and ", context.Config.Current.Sources.Select(s => s.Source).Distinct())}: no repos found, so {FoundOrgAppRepoStore.FileName} was left as the last scan wrote it",
                At = clock.GetUtcNow()
            });
            return;
        }

        context.FoundState.ReplaceSnapshot(found, incompleteOrgs, clock.GetUtcNow());
        await context.FoundState.SaveAsync(cancellationToken);
    }
}
