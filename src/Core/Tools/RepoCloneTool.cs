using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.State;

namespace Vizient.AutomatedScanner.Core.Tools;

public sealed class RepoCloneTool : ITool
{
    public string Name => "RepoClone";

    public async Task RunAsync(RunContext context, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"I am {Name}: discovers, tracks and clones repos.");

        // What the last scan found, so each org, application and repo can be told apart as new or existing.
        var store = new FoundOrgAppRepoStore(context.Config.StateRoot);
        var lastSnapshot = await store.LoadAsync(cancellationToken);
        context.Repos.SetPrior(lastSnapshot);

        // TODO: discover the orgs and repos at each source, clone or update them, and report each one with
        // context.Repos.Record(...). Nothing is discovered yet.

        await SaveSnapshotAsync(context, store, lastSnapshot, incompleteOrgs: [], cancellationToken);
    }

    /// <summary>
    /// Writes back what this scan found. A scan that found no repos at all, when the last snapshot had some,
    /// is treated as a failed discovery and leaves the snapshot alone. An error never means "everything was
    /// deleted".
    /// </summary>
    private static async Task SaveSnapshotAsync(
        RunContext context,
        FoundOrgAppRepoStore store,
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
                Name = FoundOrgAppRepoStore.FileName,
                At = DateTimeOffset.UtcNow
            });
            return;
        }

        var next = FoundSnapshotBuilder.Build(
            context.Request.RunId, DateTimeOffset.UtcNow, found, lastSnapshot, incompleteOrgs);
        await store.SaveAsync(next, cancellationToken);
    }
}
