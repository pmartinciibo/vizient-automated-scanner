using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Skip;
using Vizient.AutomatedScanner.Core.Work;

namespace Vizient.AutomatedScanner.Core.Tools;

/// <summary>
/// The base for the five scanners (TruffleHog, Semgrep, Snyk, SonarStandard, SonarDotNet). It takes the repos
/// RepoClone reported, drops the ones this tool should not touch, applies the skip policy (skip what has not
/// changed since the last clean scan, but scan a random few anyway as canaries), and runs the rest through the
/// shared loop. A scanner supplies only what it does for one repo.
/// </summary>
public abstract class ScanToolBase : ITool
{
    public abstract string Name { get; }

    /// <summary>The clock for the scheduled full rescan. Tests replace it.</summary>
    protected virtual TimeProvider Time => TimeProvider.System;

    /// <summary>The source of randomness for picking canaries. Tests replace it.</summary>
    protected virtual Random Random => Random.Shared;

    /// <summary>Whether this tool applies to the repo at all (for example, SonarDotNet only for .NET repos).</summary>
    protected virtual bool IsApplicable(RepoOutcome repo) => true;

    /// <summary>
    /// Runs the tool on one repo, using <c>work.Run.Processes</c> to start it. On success return a done marker with
    /// the start and end time and the finding count. The skip key fields (SHA, mode, versions, config hash) are
    /// filled in from the policy if the tool leaves them blank.
    /// </summary>
    protected abstract Task<RepoWorkResult> ScanRepoAsync(RepoWorkContext work, CancellationToken cancellationToken);

    public async Task RunAsync(RunContext context, CancellationToken cancellationToken = default)
    {
        await context.FoundState.EnsureLoadedAsync(cancellationToken);

        var repos = context.Repos.Snapshot();
        var scanArchived = context.Config.Current.ScanArchived;
        ToolSkippedMarker? NotInScope(RepoOutcome repo) => RepoEligibility.SkipFor(repo, scanArchived, IsApplicable);
        var eligible = repos.Where(r => NotInScope(r) is null).ToList();

        var rules = SkipRules.Create(context, Name, Time.GetUtcNow());
        var plan = SkipPlanner.Plan(eligible, rules, key => context.FoundState.Get(Name, key), Random);
        context.Status.UpdateStep(Name, step => step with { Canaries = plan.CanaryCount });

        var work = new RepoWork(
            (repoWork, token) => ScanWithPolicyAsync(repoWork, plan, rules, token),
            repo => NotInScope(repo) ?? plan.SkipMarkerFor(repo.Key),
            (repo, marker, token) => RecordCleanScanAsync(context, repo, marker, token));

        var loop = new RepoWorkLoop(context, Name, ToolLimits.For(context, Name));
        try
        {
            var summary = await loop.RunAsync(repos, work, cancellationToken);

            if (WasFullPass(summary, eligible.Count))
            {
                context.FoundState.CompleteFullPass(Name, Time.GetUtcNow());
            }
        }
        finally
        {
            // Even if the run was stopped or aborted, what finished cleanly must not be scanned again next time.
            await context.FoundState.SaveAsync(CancellationToken.None);
        }
    }

    // Fills in the skip key on a clean result, so every scanner records it the same way.
    private async Task<RepoWorkResult> ScanWithPolicyAsync(
        RepoWorkContext work,
        SkipPlan plan,
        SkipRules rules,
        CancellationToken cancellationToken)
    {
        var result = await ScanRepoAsync(work, cancellationToken);
        if (result.Kind != RepoWorkKind.Done)
        {
            return result;
        }

        var marker = result.DoneMarker!;
        return RepoWorkResult.Done(marker with
        {
            Sha = FirstNonBlank(marker.Sha, work.Repo.HeadSha),
            ScanMode = FirstNonBlank(marker.ScanMode, rules.Key.ScanMode),
            ToolVersion = FirstNonBlank(marker.ToolVersion, rules.Key.ToolVersion),
            RulesVersion = FirstNonBlank(marker.RulesVersion, rules.Key.RulesVersion),
            ConfigHash = FirstNonBlank(marker.ConfigHash, rules.Key.ConfigHash),
            Canary = plan.DecisionFor(work.Repo.Key).Canary
        });
    }

    // Runs after the done marker is on disk. A failed repo never gets here, so its stored state is left as it was
    // and the next run scans it again.
    private Task RecordCleanScanAsync(RunContext context, RepoOutcome repo, ToolDoneMarker marker, CancellationToken cancellationToken)
    {
        var previous = context.FoundState.Get(Name, repo.Key);

        context.FoundState.Record(Name, repo, new RepositoryScanState
        {
            Sha = marker.Sha,
            ScanMode = marker.ScanMode,
            ToolVersion = marker.ToolVersion,
            RulesVersion = marker.RulesVersion,
            ConfigHash = marker.ConfigHash,
            ScannedAt = marker.EndedAt,
            FindingCount = marker.FindingCount
        });

        // Same code, same rules, different findings: something the key cannot see has changed.
        if (marker.Canary && previous is not null && previous.Sha == marker.Sha && previous.FindingCount != marker.FindingCount)
        {
            context.Status.AddAlert(new StatusAlert
            {
                Type = "CanaryMismatch",
                Tool = Name,
                Repo = repo.Key,
                Was = previous.FindingCount,
                Now = marker.FindingCount,
                At = DateTimeOffset.UtcNow
            });
            context.FoundState.SetForceFull(Name);
        }

        return Task.CompletedTask;
    }

    // Every repo this tool could scan was tried, none left unstarted and the run not cut short. A repo that
    // failed counts as tried: it has no stored state, so the next run scans it again whatever happens here.
    private bool WasFullPass(RepoLoopSummary summary, int eligibleCount) =>
        eligibleCount > 0
        && summary.NotStarted == 0
        && !summary.StoppedEarly
        && summary.Done + summary.Failed == eligibleCount;

    private static string FirstNonBlank(string value, string? fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback ?? string.Empty : value;
}
