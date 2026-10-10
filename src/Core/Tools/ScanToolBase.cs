using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Work;

namespace Vizient.AutomatedScanner.Core.Tools;

/// <summary>
/// The base for the five scanners (TruffleHog, Semgrep, Snyk, SonarStandard, SonarDotNet). It takes the repos
/// RepoClone reported, drops the ones this tool should not touch, and runs the rest through the shared loop.
/// A scanner supplies only what it does for one repo.
/// </summary>
public abstract class ScanToolBase : ITool
{
    public abstract string Name { get; }

    /// <summary>Whether this tool applies to the repo at all (for example, SonarDotNet only for .NET repos).</summary>
    protected virtual bool IsApplicable(RepoOutcome repo) => true;

    /// <summary>Runs the tool on one repo, using <c>work.Run.Processes</c> to start it.</summary>
    protected abstract Task<RepoWorkResult> ScanRepoAsync(RepoWorkContext work, CancellationToken cancellationToken);

    public Task RunAsync(RunContext context, CancellationToken cancellationToken = default)
    {
        var loop = new RepoWorkLoop(context, Name, ToolLimits.For(context, Name));
        var work = new RepoWork(
            ScanRepoAsync,
            repo => RepoEligibility.SkipFor(repo, context.Config.Current.ScanArchived, IsApplicable));

        return loop.RunAsync(context.Repos.Snapshot(), work, cancellationToken);
    }
}
