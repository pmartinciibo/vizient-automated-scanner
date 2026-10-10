using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.Tools;

public sealed class RepoCloneTool : ITool
{
    public string Name => "RepoClone";

    public async Task RunAsync(RunContext context, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"I am {Name}: discovers, tracks and clones repos.");

        // What the last scan found, so each org, application and repo can be told apart as new or existing.
        await context.FoundState.EnsureLoadedAsync(cancellationToken);
        var lastSnapshot = context.FoundState.LoadedSnapshot;
        context.Repos.SetPrior(lastSnapshot);

        // TODO: discover the orgs and repos at each source, clone or update them, and report each one with
        // context.Repos.Record(...), including the HEAD SHA that is checked out. Nothing is discovered yet.

        await SaveSnapshotAsync(context, lastSnapshot, incompleteOrgs: [], cancellationToken);
    }

    /// <summary>
    /// Writes back what this scan found. A scan that found no repos at all, when the last snapshot had some,
    /// is treated as a failed discovery and leaves the file alone. An error never means "everything was
    /// deleted".
    /// </summary>
    private static async Task SaveSnapshotAsync(
        RunContext context,
        FoundOrgAppRepoDocument lastSnapshot,
        IReadOnlyList<(string Source, string Org)> incompleteOrgs,
        CancellationToken cancellationToken)
    {
        var found = context.Repos.Snapshot();

        if (!found.Any(r => r.ExistedRemotely) && lastSnapshot.HasAnyRepo)
        {
            context.Status.AddAlert(new StatusAlert
            {
                Type = "SnapshotNotUpdated",
                Tool = "RepoClone",
                Name = State.FoundOrgAppRepoStore.FileName,
                At = DateTimeOffset.UtcNow
            });
            return;
        }

        context.FoundState.ReplaceSnapshot(found, incompleteOrgs, DateTimeOffset.UtcNow);
        await context.FoundState.SaveAsync(cancellationToken);
    }
}
