using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Core.Work;

/// <summary>
/// The per-repo loop every scanner shares. It decides which repos to run, runs them under a parallelism limit
/// that commands can change while it works, honours pause, stop, skip, disable and retry-failed, writes the
/// marker for each repo's outcome, and keeps this tool's counts in the status. A failure of one repo is a
/// marker, not the end of the loop.
/// </summary>
public sealed class RepoWorkLoop
{
    public const string SkippedByCommand = "SkippedByCommand";
    public const string ExcludedByRequest = "ExcludedByRequest";

    // How often the loop looks again for a changed limit, a command or a freed slot when nothing else wakes it.
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly RunContext _context;
    private readonly string _tool;
    private readonly ToolLimits _limits;
    private readonly TimeSpan _pollInterval;

    // The counts below are shared by the repos running at once. All access goes through _gate.
    private readonly object _gate = new();
    private readonly Dictionary<string, RepoOutcome> _runnable = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedKeys = new(StringComparer.Ordinal);
    private int _total;
    private int _done;
    private int _failed;
    private int _skipped;
    private int _running;
    private SeverityCounts _severities = new();
    private readonly List<string> _runningLabels = new();
    private string? _message;

    public RepoWorkLoop(RunContext context, string tool, ToolLimits limits, TimeSpan? pollInterval = null)
    {
        _context = context;
        _tool = tool;
        _limits = limits;
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    public async Task<RepoLoopSummary> RunAsync(
        IReadOnlyList<RepoOutcome> repos,
        RepoWork work,
        CancellationToken cancellationToken)
    {
        var control = _context.Control;
        var pending = new Queue<RepoOutcome>();

        await PlanAsync(repos, work, pending, cancellationToken);
        Publish();

        // A failure or an abort must stop the repos already running, so they share one token.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = runCts.Token;
        var inFlight = new List<Task>();
        var retryCursor = control.RetryCursor;
        var stopping = false;
        var disabled = false;
        string? disabledReason = null;

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();

                foreach (var finished in inFlight.Where(t => t.IsCompleted).ToList())
                {
                    inFlight.Remove(finished);
                    await finished; // surfaces a failure to record an outcome
                }

                if (!stopping)
                {
                    if (control.RetryRequestedSince(_tool, ref retryCursor))
                    {
                        RequeueFailed(pending);
                    }

                    if (control.IsToolDisabled(_tool, out var reason))
                    {
                        stopping = disabled = true;
                        disabledReason = reason;
                    }
                    else if (control.StopRequested)
                    {
                        stopping = true;
                    }
                }

                if (inFlight.Count == 0 && (stopping || pending.Count == 0))
                {
                    break;
                }

                if (!stopping && pending.Count > 0 && inFlight.Count < CurrentLimit())
                {
                    if (control.IsPaused)
                    {
                        // Start nothing new while paused. Look at everything again once it is resumed.
                        await control.WaitWhilePausedAsync(token);
                        continue;
                    }

                    var repo = pending.Dequeue();
                    if (control.IsRepoSkipped(repo.Key))
                    {
                        await SkipLateAsync(repo, SkippedByCommand, token);
                        continue;
                    }

                    inFlight.Add(RunOneAsync(repo, work, token));
                    continue;
                }

                // Nothing to start right now: wait for a repo to finish, or look again shortly for commands.
                await Task.WhenAny(inFlight.Append(Task.Delay(_pollInterval, token)));
            }
        }
        catch (Exception)
        {
            await runCts.CancelAsync();
            try
            {
                await Task.WhenAll(inFlight);
            }
            catch (Exception)
            {
                // The first failure is the one being reported.
            }

            throw;
        }

        return Finish(pending.Count, stopping, disabled, disabledReason);
    }

    // ---- planning ----

    private async Task PlanAsync(
        IReadOnlyList<RepoOutcome> repos,
        RepoWork work,
        Queue<RepoOutcome> pending,
        CancellationToken ct)
    {
        var request = _context.Request;
        var markers = _context.Markers;

        var ordered = repos.OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
        var checkedCount = 0;
        var lastNote = System.Diagnostics.Stopwatch.StartNew();

        foreach (var repo in ordered)
        {
            if (lastNote.ElapsedMilliseconds >= 1000)
            {
                lastNote.Restart();
                var (seen, skippedSoFar) = (checkedCount, _skipped);
                _context.Status.UpdateStep(_tool, step => step with
                {
                    Message = $"planning: {seen} of {ordered.Count} repos checked, {skippedSoFar} skipped so far"
                });
            }

            checkedCount++;

            // Continuing a run: finished repos are not repeated, and a retry pass touches only the failed ones.
            if (request.Mode == ScanMode.RetryFailed && !markers.IsFailed(_tool, repo.Key))
            {
                continue;
            }

            if (request.Mode == ScanMode.Resume && markers.IsDone(_tool, repo.Key))
            {
                _total++;
                _done++;
                continue;
            }

            var skip = SkipFor(repo, work);
            if (skip is not null)
            {
                await markers.WriteSkippedAsync(_tool, repo.Key, skip, ct);
                _skipped++;
                continue;
            }

            _total++;
            _runnable[repo.Key] = repo;
            pending.Enqueue(repo);
        }
    }

    private ToolSkippedMarker? SkipFor(RepoOutcome repo, RepoWork work)
    {
        var scope = _context.Request.Scope;
        var excluded = scope.ExcludeRepos.Contains(repo.Key)
            || (scope.IncludeRepos.Count > 0 && !scope.IncludeRepos.Contains(repo.Key));

        if (excluded)
        {
            return new ToolSkippedMarker { Reason = ExcludedByRequest };
        }

        if (_context.Control.IsRepoSkipped(repo.Key))
        {
            return new ToolSkippedMarker { Reason = SkippedByCommand };
        }

        return work.SkipReason?.Invoke(repo);
    }

    // ---- running one repo ----

    private async Task RunOneAsync(RepoOutcome repo, RepoWork work, CancellationToken token)
    {
        lock (_gate)
        {
            _running++;
            _runningLabels.Add(RepoProgress.Label(repo));
        }

        Publish();

        RepoWorkResult result;
        try
        {
            result = await work.Scan(new RepoWorkContext(_tool, repo, _context, _limits), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (_gate)
            {
                _running--;
                _runningLabels.Remove(RepoProgress.Label(repo));
            }

            Publish();
            throw;
        }
        catch (Exception ex)
        {
            // TODO: redact before this is written to the marker.
            result = RepoWorkResult.Failed("ToolError", $"{ex.GetType().Name}: {ex.Message}");
        }

        await RecordAsync(repo, result, work, token);
    }

    // The marker goes down first. A repo only counts once its outcome is on disk.
    private async Task RecordAsync(RepoOutcome repo, RepoWorkResult result, RepoWork work, CancellationToken ct)
    {
        var markers = _context.Markers;
        switch (result.Kind)
        {
            case RepoWorkKind.Done:
                await markers.WriteDoneAsync(_tool, repo.Key, result.DoneMarker!, ct);
                if (work.OnDone is not null)
                {
                    await work.OnDone(repo, result.DoneMarker!, ct);
                }

                break;
            case RepoWorkKind.Failed:
                await markers.WriteFailedAsync(_tool, repo.Key, result.FailedMarker!, ct);
                break;
            default:
                await markers.WriteSkippedAsync(_tool, repo.Key, result.SkippedMarker!, ct);
                break;
        }

        lock (_gate)
        {
            _running--;
            _runningLabels.Remove(RepoProgress.Label(repo));
            switch (result.Kind)
            {
                case RepoWorkKind.Done:
                    _done++;
                    _severities = _severities.Plus(result.DoneMarker!.Severities);
                    _failedKeys.Remove(repo.Key);
                    break;
                case RepoWorkKind.Failed:
                    _failed++;
                    _failedKeys.Add(repo.Key);
                    break;
                default:
                    // The tool decided at the last moment not to run. It was counted to scan, so move it.
                    _total--;
                    _skipped++;
                    break;
            }
        }

        Publish();
    }

    // A repo was queued, then a SkipRepo command arrived before it started.
    private async Task SkipLateAsync(RepoOutcome repo, string reason, CancellationToken ct)
    {
        await _context.Markers.WriteSkippedAsync(_tool, repo.Key, new ToolSkippedMarker { Reason = reason }, ct);
        lock (_gate)
        {
            _total--;
            _skipped++;
        }

        Publish();
    }

    // RetryFailed: the repos that failed in this run go back in the queue and their failure count is reset.
    private void RequeueFailed(Queue<RepoOutcome> pending)
    {
        lock (_gate)
        {
            foreach (var key in _failedKeys.OrderBy(k => k, StringComparer.Ordinal))
            {
                pending.Enqueue(_runnable[key]);
                _failed--;
            }

            _failedKeys.Clear();
        }

        Publish();
    }

    private int CurrentLimit() =>
        Math.Max(1, _context.Control.MaxParallelOverride(_tool) ?? _limits.MaxParallel);

    // ---- status ----

    private RepoLoopSummary Finish(int notStarted, bool stopping, bool disabled, string? disabledReason)
    {
        lock (_gate)
        {
            _message = null;
        }

        if (disabled)
        {
            var why = string.IsNullOrWhiteSpace(disabledReason) ? "disabled by command" : $"disabled by command: {disabledReason}";
            _context.Status.UpdateStep(_tool, step => step with
            {
                Status = ToolRunState.Disabled,
                Message = $"{why}; {notStarted} repos not started"
            });
        }
        else if (stopping)
        {
            lock (_gate)
            {
                _message = $"stopped; {notStarted} repos not started";
            }
        }

        Publish();

        lock (_gate)
        {
            return new RepoLoopSummary(_total, _done, _failed, _skipped, notStarted, stopping, disabled ? disabledReason : null);
        }
    }

    private void Publish()
    {
        int total, done, failed, skipped, running;
        string? message;
        SeverityCounts severities;
        lock (_gate)
        {
            (total, done, failed, skipped, running, message) = (_total, _done, _failed, _skipped, _running, _message);
            severities = _severities;

            // A message set on purpose (stopped, disabled) stands. Otherwise say how far along it is and what is running now.
            if (message is null && _running > 0)
            {
                message = RepoProgress.Text(_done, _failed, _skipped, _total, _runningLabels);
            }
        }

        _context.Status.UpdateStep(_tool, step => step with
        {
            Total = total,
            Done = done,
            Failed = failed,
            Skipped = skipped,
            Running = running,
            Severities = ToolNames.Scanners.Contains(_tool) ? severities : null,
            // A disabled step keeps the message that says why.
            Message = step.Status == ToolRunState.Disabled ? step.Message : message
        });
    }


}
