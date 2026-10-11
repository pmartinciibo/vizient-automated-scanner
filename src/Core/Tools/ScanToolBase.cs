using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.RepoClone;
using Vizient.AutomatedScanner.Core.Skip;
using Vizient.AutomatedScanner.Core.Work;

namespace Vizient.AutomatedScanner.Core.Tools;

/// <summary>
/// The base for the five scanners (TruffleHog, Semgrep, Snyk, SonarStandard, SonarDotNet). It takes the repos
/// RepoClone reported, drops the ones this tool should not touch, applies the skip policy (skip what has not
/// changed since the last clean scan, but scan a random few anyway as canaries), and runs the rest. A scanner
/// supplies only what it does for one repo.
/// <para>
/// In a run the scanners do not each walk the repos on their own. <see cref="Scan.ScanPhase"/> takes them repo by
/// repo: one working copy is made from the mirror, every tool that applies scans it, and the copy is deleted.
/// Run alone (<see cref="RunAsync"/>), a tool makes its own working copy for each repo.
/// </para>
/// </summary>
public abstract class ScanToolBase : ITool
{
    public abstract string Name { get; }

    /// <summary>The clock for the scheduled full rescan. Tests replace it.</summary>
    protected virtual TimeProvider Time => TimeProvider.System;

    /// <summary>The source of randomness for picking canaries. Tests replace it.</summary>
    protected virtual Random Random => Random.Shared;

    /// <summary>Whether this tool applies to the repo at all, from what is known before it is opened. A tool that has to look inside the repo says so later with a skipped result.</summary>
    protected virtual bool IsApplicable(RepoOutcome repo) => true;

    /// <summary>
    /// Whether the tool reads files, so needs a working copy made from the mirror. False for a tool that reads the
    /// mirror itself (TruffleHog in git mode) or does not look at the repo at all.
    /// </summary>
    protected virtual bool NeedsWorkingCopy(RunContext context) => false;

    /// <summary>
    /// Checks, once before any repo, that the tool can work: its program runs, its secrets and server are there. A
    /// problem here fails the whole tool at once with one clear message, instead of a failed marker for every repo.
    /// </summary>
    protected virtual Task PrepareAsync(RunContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Runs the tool on one repo, using <c>work.Run.Processes</c> to start it, in <c>work.WorkingPath</c> when
    /// <see cref="NeedsWorkingCopy"/> is true. On success return a done marker with the start and end time and the
    /// finding count. The skip key fields (SHA, mode, versions, config hash) are filled in from the policy if the tool
    /// leaves them blank.
    /// </summary>
    protected abstract Task<RepoWorkResult> ScanRepoAsync(RepoWorkContext work, CancellationToken cancellationToken);

    /// <summary>What this tool will look at in this run and what the skip policy says about each repo.</summary>
    internal sealed record ToolPlan(
        IReadOnlyList<RepoOutcome> Eligible,
        Func<RepoOutcome, ToolSkippedMarker?> NotInScope,
        SkipRules Rules,
        SkipPlan Skip)
    {
        /// <summary>A marker if the tool does not run for the repo (not applicable, archived, unchanged), otherwise null.</summary>
        public ToolSkippedMarker? SkipReasonFor(RepoOutcome repo) => NotInScope(repo) ?? Skip.SkipMarkerFor(repo.Key);
    }

    internal Task PrepareForRunAsync(RunContext context, CancellationToken cancellationToken) =>
        PrepareAsync(context, cancellationToken);

    internal bool UsesWorkingCopy(RunContext context) => NeedsWorkingCopy(context);

    internal ToolPlan MakePlan(RunContext context, IReadOnlyList<RepoOutcome> repos)
    {
        var scanArchived = context.Config.Current.ScanArchived;
        ToolSkippedMarker? NotInScope(RepoOutcome repo) => RepoEligibility.SkipFor(repo, scanArchived, IsApplicable);
        var eligible = repos.Where(r => NotInScope(r) is null).ToList();

        var rules = SkipRules.Create(context, Name, Time.GetUtcNow());
        var plan = SkipPlanner.Plan(eligible, rules, key => context.FoundState.Get(Name, key), Random);
        context.Status.UpdateStep(Name, step => step with { Canaries = plan.CanaryCount });

        return new ToolPlan(eligible, NotInScope, rules, plan);
    }

    public async Task RunAsync(RunContext context, CancellationToken cancellationToken = default)
    {
        await PrepareAsync(context, cancellationToken);
        await context.FoundState.EnsureLoadedAsync(cancellationToken);

        var repos = context.Repos.Snapshot();
        var plan = MakePlan(context, repos);

        var work = new RepoWork(
            (repoWork, token) => ScanWithPolicyAsync(repoWork, plan, token),
            plan.SkipReasonFor,
            (repo, marker, token) => RecordCleanScanAsync(context, repo, marker, token));

        var loop = new RepoWorkLoop(context, Name, ToolLimits.For(context, Name));
        try
        {
            var summary = await loop.RunAsync(repos, work, cancellationToken);

            if (WasFullPass(summary.Done, summary.Failed, summary.NotStarted, summary.StoppedEarly, plan.Eligible.Count))
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

    // Fills in the skip key on a clean result, so every scanner records it the same way. A tool that needs files and
    // was not given a working copy makes its own for this one repo and removes it afterwards.
    internal async Task<RepoWorkResult> ScanWithPolicyAsync(RepoWorkContext work, ToolPlan plan, CancellationToken cancellationToken)
    {
        RepoWorkResult result;
        ExpandedRepo? own = null;
        try
        {
            if (work.WorkingPath is null && NeedsWorkingCopy(work.Run))
            {
                try
                {
                    own = await new RepoExpander(work.Run).ExpandAsync(work.Repo, cancellationToken);
                    work = work with { WorkingPath = own.Path };
                }
                catch (InvalidOperationException ex)
                {
                    return RepoWorkResult.Failed("ExpandFailed", ex.Message);
                }
            }

            result = await ScanRepoAsync(work, cancellationToken);
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync();
            }
        }

        if (result.Kind != RepoWorkKind.Done)
        {
            return result;
        }

        var marker = result.DoneMarker!;
        return RepoWorkResult.Done(marker with
        {
            Sha = FirstNonBlank(marker.Sha, work.Repo.HeadSha),
            ScanMode = FirstNonBlank(marker.ScanMode, plan.Rules.Key.ScanMode),
            ToolVersion = FirstNonBlank(marker.ToolVersion, plan.Rules.Key.ToolVersion),
            RulesVersion = FirstNonBlank(marker.RulesVersion, plan.Rules.Key.RulesVersion),
            ConfigHash = FirstNonBlank(marker.ConfigHash, plan.Rules.Key.ConfigHash),
            Canary = plan.Skip.DecisionFor(work.Repo.Key).Canary
        });
    }

    // Runs after the done marker is on disk. A failed repo never gets here, so its stored state is left as it was
    // and the next run scans it again.
    internal Task RecordCleanScanAsync(RunContext context, RepoOutcome repo, ToolDoneMarker marker, CancellationToken cancellationToken)
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

    internal void CompleteFullPassIfDone(RunContext context, int done, int failed, int notStarted, bool stoppedEarly, int eligibleCount)
    {
        if (WasFullPass(done, failed, notStarted, stoppedEarly, eligibleCount))
        {
            context.FoundState.CompleteFullPass(Name, Time.GetUtcNow());
        }
    }

    // Every repo this tool could scan was tried, none left unstarted and the run not cut short. A repo that
    // failed counts as tried: it has no stored state, so the next run scans it again whatever happens here.
    private static bool WasFullPass(int done, int failed, int notStarted, bool stoppedEarly, int eligibleCount) =>
        eligibleCount > 0
        && notStarted == 0
        && !stoppedEarly
        && done + failed == eligibleCount;

    private static string FirstNonBlank(string value, string? fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback ?? string.Empty : value;
}
