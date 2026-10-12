using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.RepoClone;
using Vizient.AutomatedScanner.Core.Status;
using Vizient.AutomatedScanner.Core.Tools;
using Vizient.AutomatedScanner.Core.Work;

namespace Vizient.AutomatedScanner.Core.Scan;

/// <summary>
/// Phase 2: the five scanners, taken repo by repo. For each repo that at least one tool will scan, one working copy
/// is made from the mirror, every tool that applies runs on it in parallel, and the copy is deleted. Several repos are
/// in flight at once (<see cref="ScanSettings.MaxParallelRepos"/>), each tool still keeps to its own parallel limit,
/// and a slow tool such as Sonar keeps the others' repos waiting on disk, not the other way round.
/// <para>
/// The orchestrator still sees five tools, each with its own step in the status. <see cref="Tools"/> gives it one
/// member per scanner. They all hand over to one driver, which does the work and then releases them together.
/// Skip policy, canaries, markers, scope, resume, and the commands (pause, stop, abort, skip a repo, disable a
/// tool, change a tool's parallelism, retry failed) behave as they do in <see cref="RepoWorkLoop"/>.
/// </para>
/// </summary>
public sealed class ScanPhase
{
    private readonly IReadOnlyList<ScanToolBase> _scanners;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _joinWait;

    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource> _members = new(StringComparer.Ordinal);
    private Task? _driver;
    private bool _membersFrozen;

    /// <param name="pollInterval">How often the driver looks again for commands and changed limits when nothing else wakes it.</param>
    /// <param name="joinWait">How long the driver waits for every enabled scanner to report in before it starts.</param>
    public ScanPhase(IReadOnlyList<ScanToolBase> scanners, TimeSpan? pollInterval = null, TimeSpan? joinWait = null)
    {
        _scanners = scanners;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        _joinWait = joinWait ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>One tool per scanner, for the orchestrator. Each finishes when the whole phase does.</summary>
    public IReadOnlyList<ITool> Tools => _scanners.Select(s => (ITool)new Member(s, this)).ToList();

    private sealed class Member(ScanToolBase scanner, ScanPhase phase) : ITool
    {
        public string Name => scanner.Name;

        public Task RunAsync(RunContext context, CancellationToken cancellationToken = default) =>
            phase.JoinAsync(scanner, context, cancellationToken);
    }

    private Task JoinAsync(ScanToolBase scanner, RunContext context, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            if (_membersFrozen)
            {
                completion.SetException(new InvalidOperationException(
                    $"{scanner.Name} reported in after the scan phase had already started without it."));
                return completion.Task;
            }

            _members[scanner.Name] = completion;
            _driver ??= Task.Run(() => DriveAsync(context, cancellationToken), CancellationToken.None);
            return completion.Task;
        }
    }

    // ---- the driver ----

    private async Task DriveAsync(RunContext context, CancellationToken cancellationToken)
    {
        var runs = new List<ToolRun>();
        var finished = false;
        try
        {
            var participants = await WaitForMembersAsync(context, cancellationToken);
            await context.FoundState.EnsureLoadedAsync(cancellationToken);
            AddReposFromState(context);

            // A tool that cannot work fails alone and at once, with one message, before any repo is opened.
            var prepared = await Task.WhenAll(participants.Select(async scanner =>
            {
                try
                {
                    await scanner.PrepareForRunAsync(context, cancellationToken);
                    return (scanner, (Exception?)null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return (scanner, ex);
                }
            }));

            foreach (var (scanner, error) in prepared)
            {
                if (error is not null)
                {
                    Complete(scanner.Name, error);
                }
                else
                {
                    runs.Add(new ToolRun(scanner, context, _pollInterval));
                }
            }

            if (runs.Count > 0)
            {
                await RunRepoPassAsync(context, runs, cancellationToken);
            }

            finished = true;
        }
        catch (OperationCanceledException)
        {
            foreach (var name in _members.Keys.ToList())
            {
                Cancel(name);
            }

            return;
        }
        catch (Exception ex)
        {
            // A fault in the driver itself. Every tool that is still waiting hears about it.
            foreach (var name in _members.Keys.ToList())
            {
                Complete(name, ex);
            }

            return;
        }
        finally
        {
            // Even if the run was stopped or aborted, what finished cleanly must not be scanned again next time.
            if (runs.Count > 0)
            {
                await context.FoundState.SaveAsync(CancellationToken.None);
            }
        }

        if (finished)
        {
            foreach (var run in runs)
            {
                Complete(run.Name, null);
            }
        }
    }

    /// <summary>
    /// The scan does not depend on RepoClone having run. Every repo in FoundOrgAppRepo.json that this run's RepoClone did
    /// not report (it was off, or did not cover that source or org) is added from the file, as the last run left it. The
    /// mirror is checked on disk, so one that is not there is skipped as NoLocalClone. Nothing about it was confirmed with
    /// its source this run, so it is not marked as existing remotely.
    /// </summary>
    private static void AddReposFromState(RunContext context)
    {
        var reported = context.Repos.Snapshot().Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var repo in context.FoundState.Repos().Where(r => !reported.Contains(r.Key)))
        {
            context.Repos.Record(repo with
            {
                ExistedLocally = RepoSynchronizer.IsMirror(repo.LocalPath),
                ExistedRemotely = false,
                UpdatedLocally = false
            });
        }
    }

    private async Task<List<ScanToolBase>> WaitForMembersAsync(RunContext context, CancellationToken cancellationToken)
    {
        // The orchestrator starts every enabled scanner at once. Wait for them all, so the first one does not start
        // the repos without the others.
        var expected = _scanners
            .Where(s => context.IsEnabled(s.Name) && !context.Control.IsToolDisabled(s.Name, out _))
            .Select(s => s.Name)
            .ToList();

        var deadline = DateTime.UtcNow + _joinWait;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (expected.All(_members.ContainsKey))
                {
                    break;
                }
            }

            await Task.Delay(10, cancellationToken);
        }

        lock (_gate)
        {
            _membersFrozen = true;
            return _scanners.Where(s => _members.ContainsKey(s.Name)).ToList();
        }
    }

    private async Task RunRepoPassAsync(RunContext context, List<ToolRun> runs, CancellationToken cancellationToken)
    {
        var control = context.Control;
        var repos = context.Repos.Snapshot();

        var progress = new ScanReporter(context, runs);

        // 1. What each tool will do with each repo, decided before any repo is opened. So the status can say, before the
        // first scan starts, how many repos need one.
        var pending = new Queue<RepoItem>();
        var ordered = repos.OrderBy(r => r.Key, StringComparer.Ordinal).ToList();
        var planningNote = System.Diagnostics.Stopwatch.StartNew();
        progress.Planning(0, ordered.Count, 0);
        var seen = 0;
        foreach (var repo in ordered)
        {
            if (planningNote.ElapsedMilliseconds >= 1000)
            {
                planningNote.Restart();
                progress.Planning(seen, ordered.Count, pending.Count);
            }

            seen++;
            var item = new RepoItem(repo, []);
            foreach (var run in runs)
            {
                if (await PlanAsync(context, run, repo, cancellationToken))
                {
                    item.Tools.Add(run);
                }
            }

            if (item.Tools.Count > 0)
            {
                pending.Enqueue(item);
            }
        }

        foreach (var run in runs)
        {
            run.Publish();
        }

        progress.Planned(pending.Count, ordered.Count - pending.Count);

        var maxRepos = Math.Max(1, context.Config.Current.Scan.MaxParallelRepos ?? runs.Max(r => r.Limits.MaxParallel));
        var expander = new RepoExpander(context);

        // A failure or an abort must stop the repos already running, so they share one token.
        using var passCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = passCts.Token;
        var inFlight = new List<Task>();
        var stopping = false;

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();

                foreach (var done in inFlight.Where(t => t.IsCompleted).ToList())
                {
                    inFlight.Remove(done);
                    await done; // surfaces a failure to record an outcome
                }

                if (!stopping)
                {
                    foreach (var run in runs)
                    {
                        if (control.RetryRequestedSince(run.Name, ref run.RetryCursor))
                        {
                            foreach (var repo in run.TakeFailedForRetry())
                            {
                                pending.Enqueue(new RepoItem(repo, [run]));
                            }
                        }

                        if (!run.Disabled && control.IsToolDisabled(run.Name, out var reason))
                        {
                            run.Disable(reason);
                        }
                    }

                    if (control.StopRequested)
                    {
                        stopping = true;
                    }
                }

                if (inFlight.Count == 0 && (stopping || pending.Count == 0))
                {
                    break;
                }

                if (!stopping && pending.Count > 0 && inFlight.Count < maxRepos)
                {
                    if (control.IsPaused)
                    {
                        // Start nothing new while paused. Look at everything again once it is resumed.
                        await control.WaitWhilePausedAsync(token);
                        continue;
                    }

                    inFlight.Add(RunRepoAsync(context, expander, pending.Dequeue(), progress, token));
                    continue;
                }

                await Task.WhenAny(inFlight.Append(Task.Delay(_pollInterval, token)));
            }
        }
        catch (Exception)
        {
            await passCts.CancelAsync();
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

        foreach (var run in runs)
        {
            var notStarted = pending.Count(i => i.Tools.Contains(run)) + run.NotStarted;
            run.Finish(notStarted, stopping);
            run.Tool.CompleteFullPassIfDone(context, run.Done, run.Failed, notStarted, stopping, run.Plan.Eligible.Count);
        }

        progress.Completed(stopping, pending.Select(i => i.Repo.Key).Distinct().Count());
    }

    // Mirrors the planning of RepoWorkLoop for one tool and one repo: true if the tool has work to do on it.
    private static async Task<bool> PlanAsync(RunContext context, ToolRun run, RepoOutcome repo, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var markers = context.Markers;

        // Continuing a run: finished repos are not repeated, and a retry pass touches only the failed ones.
        if (request.Mode == ScanMode.RetryFailed && !markers.IsFailed(run.Name, repo.Key))
        {
            return false;
        }

        if (request.Mode == ScanMode.Resume && markers.IsDone(run.Name, repo.Key))
        {
            run.CountAlreadyDone();
            return false;
        }

        var scope = request.Scope;
        var excluded = scope.ExcludeRepos.Contains(repo.Key)
            || (scope.IncludeRepos.Count > 0 && !scope.IncludeRepos.Contains(repo.Key));

        var skip = excluded ? new ToolSkippedMarker { Reason = RepoWorkLoop.ExcludedByRequest }
            : context.Control.IsRepoSkipped(repo.Key) ? new ToolSkippedMarker { Reason = RepoWorkLoop.SkippedByCommand }
            : run.Plan.SkipReasonFor(repo);

        if (skip is not null)
        {
            await markers.WriteSkippedAsync(run.Name, repo.Key, skip, cancellationToken);
            run.CountSkipped();
            return false;
        }

        run.AddRunnable(repo);
        return true;
    }

    private async Task RunRepoAsync(RunContext context, RepoExpander expander, RepoItem item, ScanReporter progress, CancellationToken token)
    {
        var repo = item.Repo;
        var live = item.Tools.Where(t => !t.Disabled).ToList();
        foreach (var disabled in item.Tools.Where(t => t.Disabled))
        {
            disabled.CountNotStarted();
        }

        if (live.Count == 0)
        {
            return;
        }

        // A SkipRepo command arrived while the repo was queued.
        if (context.Control.IsRepoSkipped(repo.Key))
        {
            foreach (var run in live)
            {
                await run.SkipLateAsync(repo, RepoWorkLoop.SkippedByCommand, token);
            }

            return;
        }

        progress.Started(repo);
        ExpandedRepo? expanded = null;
        string? expandError = null;
        if (live.Any(t => t.Tool.UsesWorkingCopy(context)))
        {
            try
            {
                expanded = await expander.ExpandAsync(repo, token);
            }
            catch (InvalidOperationException ex)
            {
                expandError = ex.Message;
            }
        }

        try
        {
            await Task.WhenAll(live.Select(run => run.RunAsync(repo, expanded, expandError, token)));
        }
        finally
        {
            // Before the next repo, whether this one finished, failed or was cancelled.
            if (expanded is not null)
            {
                await expanded.DisposeAsync();
            }

            progress.Finished(repo);
        }
    }

    private void Complete(string tool, Exception? error)
    {
        TaskCompletionSource? completion;
        lock (_gate)
        {
            _members.TryGetValue(tool, out completion);
        }

        if (error is null)
        {
            completion?.TrySetResult();
        }
        else
        {
            completion?.TrySetException(error);
        }
    }

    private void Cancel(string tool)
    {
        TaskCompletionSource? completion;
        lock (_gate)
        {
            _members.TryGetValue(tool, out completion);
        }

        completion?.TrySetCanceled();
    }

    private sealed record RepoItem(RepoOutcome Repo, List<ToolRun> Tools);

    // The scan phase in repos: how many need scanning, how many are done, which are being worked on. This is the one
    // "Scanning" step in status.json.
    private sealed class ScanReporter(RunContext context, List<ToolRun> runs)
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _finished = new(StringComparer.Ordinal);
        private readonly List<string> _running = new();
        private int _total;
        private int _skipped;

        public void Planning(int seen, int of, int needScanning) =>
            context.Status.UpdateScanning(_ => new ScanProgress(true, 0, 0, 0, 0, 0,
                $"planning: {seen} of {of} repos checked, {needScanning} need scanning so far"));

        public void Planned(int total, int skipped)
        {
            lock (_gate)
            {
                (_total, _skipped) = (total, skipped);
            }

            Publish(planned: true);
        }

        public void Started(RepoOutcome repo)
        {
            lock (_gate)
            {
                _running.Add(RepoProgress.Label(repo));
            }

            Publish();
        }

        public void Finished(RepoOutcome repo)
        {
            lock (_gate)
            {
                _running.Remove(RepoProgress.Label(repo));
                _finished.Add(repo.Key);
            }

            Publish();
        }

        public void Completed(bool stopping, int notStarted)
        {
            lock (_gate)
            {
                _running.Clear();
            }

            Publish(final: stopping ? $"stopped; {notStarted} repos not started" : null);
        }

        private void Publish(bool planned = false, string? final = null)
        {
            ScanProgress next;
            lock (_gate)
            {
                // A repo is failed while any tool has a failure for it, so a retry that succeeds takes it off the count.
                var failedKeys = runs.SelectMany(r => r.FailedKeys()).ToHashSet(StringComparer.Ordinal);
                var failed = _finished.Count(failedKeys.Contains);
                var done = _finished.Count - failed;

                var message = final
                    ?? (_running.Count > 0
                        ? RepoProgress.Text(done, failed, _skipped, _total, _running)
                        : planned
                            ? $"{_total} repos need scanning, {_skipped} do not"
                            : $"{_finished.Count} of {_total} repos processed" + (failed > 0 ? $", {failed} failed" : string.Empty) + (_skipped > 0 ? $", {_skipped} skipped" : string.Empty));

                next = new ScanProgress(true, _total, done, failed, _skipped, _running.Count, message);
            }

            context.Status.UpdateScanning(_ => next);
        }
    }

    // ---- one tool's counts and limits ----

    private sealed class ToolRun
    {
        private readonly RunContext _context;
        private readonly TimeSpan _poll;
        private readonly object _gate = new();
        private readonly Dictionary<string, RepoOutcome> _runnable = new(StringComparer.Ordinal);
        private readonly HashSet<string> _failedKeys = new(StringComparer.Ordinal);
        private int _total;
        private int _done;
        private int _failed;
        private int _skipped;
        private int _running;
        private SeverityCounts _severities = new();
        private int _inUse;
        private readonly List<string> _runningLabels = new();
        private int _notStarted;
        private bool _disabled;
        private string? _disabledReason;

        public long RetryCursor;

        public ToolRun(ScanToolBase tool, RunContext context, TimeSpan poll)
        {
            Tool = tool;
            _context = context;
            _poll = poll;
            Limits = ToolLimits.For(context, tool.Name);
            RetryCursor = context.Control.RetryCursor;
            Plan = tool.MakePlan(context, context.Repos.Snapshot());
        }

        public ScanToolBase Tool { get; }
        public string Name => Tool.Name;
        public ToolLimits Limits { get; }
        public ScanToolBase.ToolPlan Plan { get; }

        public bool Disabled
        {
            get { lock (_gate) { return _disabled; } }
        }

        public int Done { get { lock (_gate) { return _done; } } }
        public int Failed { get { lock (_gate) { return _failed; } } }
        public int NotStarted { get { lock (_gate) { return _notStarted; } } }

        /// <summary>The repos this tool has failed on, as things stand: a retry that succeeds takes a repo off.</summary>
        public IReadOnlyCollection<string> FailedKeys()
        {
            lock (_gate)
            {
                return _failedKeys.ToList();
            }
        }

        public void AddRunnable(RepoOutcome repo)
        {
            lock (_gate)
            {
                _total++;
                _runnable[repo.Key] = repo;
            }
        }

        public void CountAlreadyDone()
        {
            lock (_gate)
            {
                _total++;
                _done++;
            }
        }

        public void CountSkipped()
        {
            lock (_gate)
            {
                _skipped++;
            }
        }

        public void CountNotStarted()
        {
            lock (_gate)
            {
                _notStarted++;
            }
        }

        public void Disable(string? reason)
        {
            lock (_gate)
            {
                _disabled = true;
                _disabledReason = reason;
            }
        }

        // RetryFailed: the repos that failed in this run go back in the queue and their failure count is reset.
        public List<RepoOutcome> TakeFailedForRetry()
        {
            lock (_gate)
            {
                var repos = _failedKeys.OrderBy(k => k, StringComparer.Ordinal).Select(k => _runnable[k]).ToList();
                _failed -= repos.Count;
                _failedKeys.Clear();
                Publish();
                return repos;
            }
        }

        public async Task SkipLateAsync(RepoOutcome repo, string reason, CancellationToken cancellationToken)
        {
            await _context.Markers.WriteSkippedAsync(Name, repo.Key, new ToolSkippedMarker { Reason = reason }, cancellationToken);
            lock (_gate)
            {
                _total--;
                _skipped++;
            }

            Publish();
        }

        /// <summary>Waits for one of this tool's slots (its parallel limit, which a command can change), runs it on the repo and records the outcome.</summary>
        public async Task RunAsync(RepoOutcome repo, ExpandedRepo? expanded, string? expandError, CancellationToken token)
        {
            if (Disabled)
            {
                CountNotStarted();
                return;
            }

            await AcquireSlotAsync(token);
            try
            {
                if (Disabled)
                {
                    CountNotStarted();
                    return;
                }

                var label = RepoProgress.Label(repo);
                lock (_gate)
                {
                    _running++;
                    _runningLabels.Add(label);
                }

                Publish();

                var needsCopy = Tool.UsesWorkingCopy(_context);
                RepoWorkResult result;
                try
                {
                    result = needsCopy && expanded is null
                        ? RepoWorkResult.Failed("ExpandFailed", expandError ?? "no working copy was made")
                        : await Tool.ScanWithPolicyAsync(
                            new RepoWorkContext(Name, repo, _context, Limits) { WorkingPath = needsCopy ? expanded!.Path : null },
                            Plan,
                            token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        _running--;
                        _runningLabels.Remove(label);
                    }

                    Publish();
                    throw;
                }
                catch (Exception ex)
                {
                    result = RepoWorkResult.Failed("ToolError", $"{ex.GetType().Name}: {ex.Message}");
                }

                await RecordAsync(repo, result, token);
            }
            finally
            {
                lock (_gate)
                {
                    _inUse--;
                }
            }
        }

        // The marker goes down first. A repo only counts once its outcome is on disk.
        private async Task RecordAsync(RepoOutcome repo, RepoWorkResult result, CancellationToken cancellationToken)
        {
            var markers = _context.Markers;
            switch (result.Kind)
            {
                case RepoWorkKind.Done:
                    await markers.WriteDoneAsync(Name, repo.Key, result.DoneMarker!, cancellationToken);
                    await Tool.RecordCleanScanAsync(_context, repo, result.DoneMarker!, cancellationToken);
                    break;
                case RepoWorkKind.Failed:
                    await markers.WriteFailedAsync(Name, repo.Key, result.FailedMarker!, cancellationToken);
                    break;
                default:
                    await markers.WriteSkippedAsync(Name, repo.Key, result.SkippedMarker!, cancellationToken);
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
                        // The tool decided at the last moment not to run (for example, not a .NET repo). It was counted to scan, so move it.
                        _total--;
                        _skipped++;
                        break;
                }
            }

            Publish();
        }

        private async Task AcquireSlotAsync(CancellationToken token)
        {
            while (true)
            {
                lock (_gate)
                {
                    var limit = Math.Max(1, _context.Control.MaxParallelOverride(Name) ?? Limits.MaxParallel);
                    if (_inUse < limit)
                    {
                        _inUse++;
                        return;
                    }
                }

                await Task.Delay(_poll, token);
            }
        }

        public void Finish(int notStarted, bool stopping)
        {
            string? message = null;
            bool disabled;
            string? reason;
            lock (_gate)
            {
                (disabled, reason) = (_disabled, _disabledReason);
            }

            if (disabled)
            {
                var why = string.IsNullOrWhiteSpace(reason) ? "disabled by command" : $"disabled by command: {reason}";
                _context.Status.UpdateStep(Name, step => step with
                {
                    Status = ToolRunState.Disabled,
                    Message = $"{why}; {notStarted} repos not started"
                });
            }
            else if (stopping)
            {
                message = $"stopped; {notStarted} repos not started";
            }

            Publish(message);
        }

        public void Publish(string? message = null)
        {
            int total, done, failed, skipped, running;
            SeverityCounts severities;
            lock (_gate)
            {
                (total, done, failed, skipped, running) = (_total, _done, _failed, _skipped, _running);
                severities = _severities;

                // Unless a message was given on purpose, say how far along it is and what is being scanned now.
                if (message is null && _running > 0)
                {
                    message = RepoProgress.Text(_done, _failed, _skipped, _total, _runningLabels);
                }
            }

            _context.Status.UpdateStep(Name, step => step with
            {
                Total = total,
                Done = done,
                Failed = failed,
                Skipped = skipped,
                Running = running,
                Severities = severities,
                // A disabled step keeps the message that says why.
                Message = step.Status == ToolRunState.Disabled ? step.Message : message
            });
        }
    }
}
