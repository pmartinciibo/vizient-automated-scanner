using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Processes;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Core.Work;

public enum RepoWorkKind
{
    Done,
    Failed,
    Skipped
}

/// <summary>What a tool reports for one repo. The loop turns it into a marker and a status count.</summary>
public sealed record RepoWorkResult
{
    public RepoWorkKind Kind { get; private init; }
    public ToolDoneMarker? DoneMarker { get; private init; }
    public ToolFailedMarker? FailedMarker { get; private init; }
    public ToolSkippedMarker? SkippedMarker { get; private init; }

    /// <summary>The tool finished cleanly for this repo.</summary>
    public static RepoWorkResult Done(ToolDoneMarker marker) =>
        new() { Kind = RepoWorkKind.Done, DoneMarker = marker };

    /// <summary>The tool failed for this repo. The message must not contain secrets.</summary>
    public static RepoWorkResult Failed(string category, string message, int attempts = 1, string? sha = null) =>
        new()
        {
            Kind = RepoWorkKind.Failed,
            FailedMarker = new ToolFailedMarker { Category = category, Message = message, Attempts = attempts, Sha = sha }
        };

    /// <summary>The tool decided not to run for this repo.</summary>
    public static RepoWorkResult Skipped(ToolSkippedMarker marker) =>
        new() { Kind = RepoWorkKind.Skipped, SkippedMarker = marker };

    /// <summary>For a process that did not succeed: its category, message and attempts, already free of secrets.</summary>
    public static RepoWorkResult FromFailedProcess(ProcessResult process, string? sha = null) =>
        Failed(process.FailureCategory ?? "ToolError", process.Message, process.Attempts, sha);
}

/// <summary>Everything a tool needs to scan one repo.</summary>
public sealed record RepoWorkContext(string Tool, RepoOutcome Repo, RunContext Run, ToolLimits Limits)
{
    /// <summary>Where this tool's raw output for the repo goes (results\&lt;tool&gt;\&lt;key&gt;.json).</summary>
    public string ResultFile => Run.Paths.ResultFile(Tool, Repo.Key);
}

/// <summary>The tool-specific part of the loop.</summary>
/// <param name="Scan">Runs the tool on one repo.</param>
/// <param name="SkipReason">
/// Called before a repo is queued. A marker means this tool does not run for it (not applicable, unchanged,
/// archived and so on). Null means run it.
/// </param>
/// <param name="OnDone">
/// Called after a repo's done marker has been written, and only then. This is where the scan state for the
/// repo is recorded, so state is never ahead of the markers.
/// </param>
public sealed record RepoWork(
    Func<RepoWorkContext, CancellationToken, Task<RepoWorkResult>> Scan,
    Func<RepoOutcome, ToolSkippedMarker?>? SkipReason = null,
    Func<RepoOutcome, ToolDoneMarker, CancellationToken, Task>? OnDone = null);

/// <summary>How a pass over the repos ended.</summary>
/// <param name="Total">Repos the tool was to scan (not counting skipped ones).</param>
public sealed record RepoLoopSummary(
    int Total,
    int Done,
    int Failed,
    int Skipped,
    int NotStarted,
    bool StoppedEarly,
    string? DisabledReason);
