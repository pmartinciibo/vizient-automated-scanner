namespace Vizient.AutomatedScanner.Tests;

using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.State;
using Vizient.AutomatedScanner.Core.Tools;

/// <summary>
/// RepoClone without discovery or cloning, for tests of the snapshot file and the scanners. The test puts the repos
/// in the report itself, and this does what RepoClone does with them: load the last snapshot, save this one.
/// </summary>
public sealed class SnapshotOnlyRepoClone : ITool
{
    public string Name => ToolNames.RepoClone;

    public async Task RunAsync(RunContext context, CancellationToken cancellationToken = default)
    {
        var last = await RepoSnapshotKeeper.LoadAsync(context, cancellationToken);
        await RepoSnapshotKeeper.SaveAsync(context, last, [], TimeProvider.System, cancellationToken);
    }
}
