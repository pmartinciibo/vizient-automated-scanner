using Vizient.AutomatedScanner.Core.IO;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Core.Status;

/// <summary>
/// Thread-safe in-memory picture of the run. Tools and the orchestrator update it; the
/// <see cref="StatusWriter"/> turns snapshots of it into status.json. Nothing else writes that file.
/// </summary>
public sealed class RunStatusTracker
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly RunRequestDocument _request;
    private const int KeepLatest = 20;

    private readonly int _attempt;
    private readonly DateTimeOffset _startedAt;
    private readonly Dictionary<string, ToolProgress> _steps;
    private readonly Dictionary<string, DateTimeOffset> _stepStarts = new();
    private readonly Dictionary<string, DateTimeOffset> _stepEnds = new();
    private readonly Dictionary<string, EffectiveToolSettings> _effective = new();
    private readonly List<StatusAlert> _alerts = new();
    private readonly List<CommandAcknowledgement> _commandAcks = new();
    private long _lastCommandSeq;
    private RunState _state = RunState.Starting;
    private RunPhase _phase = RunPhase.Preflight;
    private DateTimeOffset? _endedAt;
    private ScanProgress _scan = ScanProgress.None;

    private RunStatusTracker(
        RunRequestDocument request,
        int attempt,
        long lastCommandSeq,
        TimeProvider time,
        Func<string, string?> disabledReason)
    {
        _request = request;
        _attempt = attempt;
        _lastCommandSeq = lastCommandSeq;
        _time = time;
        _startedAt = time.GetUtcNow();
        _steps = ToolNames.All.ToDictionary(name => name, name => InitialStep(name, disabledReason(name)));
    }

    /// <summary>
    /// Starts tracking. When an earlier attempt of this run left a status.json (Resume, RetryFailed), the
    /// attempt number continues and lastCommandSeq is kept so old commands are not replayed (design section 8).
    /// The run folder's copy is preferred. If the earlier attempt died before it could copy one, the live
    /// status.json beside the request is used, but only if it belongs to this run.
    /// <paramref name="disabledReason"/> says why a tool is off (null if on); without it only the request is consulted.
    /// </summary>
    public static async Task<RunStatusTracker> CreateAsync(
        RunPaths paths,
        InvocationPaths invocation,
        RunRequestDocument request,
        TimeProvider? time = null,
        CancellationToken cancellationToken = default,
        Func<string, string?>? disabledReason = null)
    {
        var attempt = 1;
        long lastCommandSeq = 0;

        var previous = await ReadPreviousAsync(paths, invocation, request.RunId, cancellationToken);
        if (previous is not null)
        {
            attempt = previous.Attempt + 1;
            lastCommandSeq = previous.LastCommandSeq;
        }

        return new RunStatusTracker(
            request,
            attempt,
            lastCommandSeq,
            time ?? TimeProvider.System,
            disabledReason ?? (tool => ToolEnablement.DisabledReason(request, null, tool)));
    }

    private static async Task<StatusDocument?> ReadPreviousAsync(
        RunPaths paths,
        InvocationPaths invocation,
        string runId,
        CancellationToken cancellationToken)
    {
        if (File.Exists(paths.Status))
        {
            return await AtomicJsonFile.ReadAsync<StatusDocument>(paths.Status, cancellationToken);
        }

        if (File.Exists(invocation.Status))
        {
            try
            {
                var live = await AtomicJsonFile.ReadAsync<StatusDocument>(invocation.Status, cancellationToken);
                return live.RunId == runId ? live : null;
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                // Left by some other or broken run. It is not ours to continue.
                return null;
            }
        }

        return null;
    }

    public RunState State
    {
        get { lock (_gate) { return _state; } }
    }

    public long LastCommandSeq
    {
        get { lock (_gate) { return _lastCommandSeq; } }
    }

    public void SetState(RunState state)
    {
        lock (_gate)
        {
            _state = state;
        }
    }

    /// <summary>Starting becomes Running. A command that already moved the state on (Paused, Stopping) stands.</summary>
    public void MarkRunning()
    {
        lock (_gate)
        {
            if (_state == RunState.Starting)
            {
                _state = RunState.Running;
            }
        }
    }

    /// <summary>
    /// Records the outcome of a command. Every command, including a rejected one, uses up its seq,
    /// so it is never read again.
    /// </summary>
    public void RecordAck(CommandAcknowledgement ack)
    {
        lock (_gate)
        {
            _lastCommandSeq = Math.Max(_lastCommandSeq, ack.Seq);
            _commandAcks.Add(ack);
            Trim(_commandAcks);
        }
    }

    public void AddAlert(StatusAlert alert)
    {
        lock (_gate)
        {
            _alerts.Add(alert);
            Trim(_alerts);
        }
    }

    public void SetEffectiveMaxParallel(string tool, int maxParallel)
    {
        lock (_gate)
        {
            _effective[tool] = new EffectiveToolSettings { MaxParallel = maxParallel };
        }
    }

    // The status file stays small: only the latest entries are kept.
    private static void Trim<T>(List<T> list)
    {
        if (list.Count > KeepLatest)
        {
            list.RemoveRange(0, list.Count - KeepLatest);
        }
    }

    public void SetPhase(RunPhase phase)
    {
        lock (_gate)
        {
            _phase = phase;
        }
    }

    /// <summary>Marks the run finished with a terminal state and stamps endedAt.</summary>
    public void Finish(RunState terminalState)
    {
        if (terminalState is not (RunState.Completed or RunState.CompletedWithFlags or RunState.Failed or RunState.Stopped))
        {
            throw new ArgumentException($"{terminalState} is not a terminal state.", nameof(terminalState));
        }

        lock (_gate)
        {
            _state = terminalState;
            _endedAt = _time.GetUtcNow();
        }
    }

    public void StepRunning(string tool) =>
        UpdateStep(tool, step => step with { Status = ToolRunState.Running });

    public void StepFinished(string tool, ToolRunState status, string? message = null) =>
        UpdateStep(tool, step => step with { Status = status, Message = message ?? step.Message });

    /// <summary>
    /// A tool has returned. It is Completed, or CompletedWithFlags if some of its repos failed. A step already
    /// marked Disabled (by a command, part way through) stays Disabled.
    /// </summary>
    public void CompleteStep(string tool) =>
        UpdateStep(tool, step => step.Status == ToolRunState.Disabled
            ? step with { Running = 0 }
            : step with
            {
                Status = step.Failed > 0 ? ToolRunState.CompletedWithFlags : ToolRunState.Completed,
                Running = 0
            });

    /// <summary>For tools to report counts, the current repo and so on.</summary>
    public void UpdateStep(string tool, Func<ToolProgress, ToolProgress> update)
    {
        lock (_gate)
        {
            if (!_steps.TryGetValue(tool, out var step))
            {
                throw new ArgumentException($"Unknown tool '{tool}'.", nameof(tool));
            }

            var updated = update(step);
            _steps[tool] = updated;

            if (updated.Status == ToolRunState.Running)
            {
                _stepStarts.TryAdd(tool, _time.GetUtcNow());
                _stepEnds.Remove(tool);
            }
            else if (IsFinished(updated) && _stepStarts.ContainsKey(tool))
            {
                _stepEnds.TryAdd(tool, _time.GetUtcNow());
            }
        }
    }

    private readonly record struct StepWindow(DateTimeOffset? Start, DateTimeOffset? End, TimeSpan Elapsed);

    // Caller holds the gate. A step that never started has taken no time. A step of several tools starts with the first
    // and ends with the last, and has no end while any of them is still going.
    private StepWindow WindowFor(IEnumerable<string> tools)
    {
        var now = _time.GetUtcNow();
        var started = tools.Where(_stepStarts.ContainsKey).ToList();
        if (started.Count == 0)
        {
            return new StepWindow(null, null, TimeSpan.Zero);
        }

        var start = started.Min(t => _stepStarts[t]);
        DateTimeOffset? end = started.All(_stepEnds.ContainsKey) ? started.Max(t => _stepEnds[t]) : null;
        return new StepWindow(start, end, (end ?? now) - start);
    }

    /// <summary>
    /// Adds the time fields. Percent is repos (or items) handled out of those to handle; skipped ones are not in the total.
    /// The estimate assumes the rest goes at the pace so far, so it is unknown ("--:--:--") until something has finished.
    /// </summary>
    private static ToolProgress WithTiming(ToolProgress step, StepWindow window)
    {
        var elapsed = window.Elapsed;
        var finished = step.Status is ToolRunState.Completed or ToolRunState.CompletedWithFlags or ToolRunState.Failed;

        // While repos are still being found or planned the total is not the total yet, so there is nothing to estimate from.
        var stillCounting = step.Status == ToolRunState.Running
            && step.Message is { } note
            && (note.StartsWith("discovery:", StringComparison.Ordinal) || note.StartsWith("planning:", StringComparison.Ordinal));

        var fraction = finished ? 1.0
            : stillCounting ? 0.0
            : step.Total > 0 ? Math.Min(1.0, (double)(step.Done + step.Failed) / step.Total)
            : 0.0;

        string remaining;
        if (finished)
        {
            remaining = FormatElapsed(TimeSpan.Zero);
        }
        else if (step.Status == ToolRunState.Running && fraction > 0)
        {
            remaining = FormatElapsed(elapsed * ((1 - fraction) / fraction));
        }
        else
        {
            remaining = step.Status == ToolRunState.Running ? "--:--:--" : FormatElapsed(TimeSpan.Zero);
        }

        return step with
        {
            StartedAt = window.Start,
            EndedAt = window.End,
            ElapsedTime = FormatElapsed(elapsed),
            PercentComplete = (int)(fraction * 100),
            EstimatedRemainingTime = remaining
        };
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
    }

    /// <summary>The scan phase reports how many repos it has to scan and how far it has got.</summary>
    public void UpdateScanning(Func<ScanProgress, ScanProgress> update)
    {
        lock (_gate)
        {
            _scan = update(_scan);
        }
    }

    /// <summary>
    /// What status.json and results.json show: the same as <see cref="Snapshot"/>, except that the five scanners are one
    /// step, "Scanning", counted in repos. The tools keep their own steps inside, which the orchestrator and the tests use.
    /// </summary>
    public StatusDocument PublishedSnapshot()
    {
        ScanProgress scan;
        StepWindow scanElapsed;
        lock (_gate)
        {
            scan = _scan;
            scanElapsed = WindowFor(ToolNames.Scanners);
        }

        var full = Snapshot();
        var scanners = full.Steps.Where(s => ToolNames.Scanners.Contains(s.Name)).ToList();
        var steps = full.Steps.Where(s => !ToolNames.Scanners.Contains(s.Name)).Append(WithTiming(Aggregate(scanners, scan), scanElapsed)).ToList();
        var finished = steps.Count(IsFinished);

        return full with
        {
            Steps = steps,
            Overall = new ProgressSummary
            {
                StepsCompleted = finished,
                StepsTotal = steps.Count,
                Percent = finished * 100 / steps.Count
            }
        };
    }

    private static ToolProgress Aggregate(List<ToolProgress> tools, ScanProgress scan)
    {
        var live = tools.Where(t => t.Status != ToolRunState.Disabled).ToList();
        var disabled = tools.Where(t => t.Status == ToolRunState.Disabled).ToList();

        var status = live.Count == 0 ? ToolRunState.Disabled
            : live.Any(t => t.Status == ToolRunState.Running) ? ToolRunState.Running
            : live.All(t => t.Status == ToolRunState.Pending) ? ToolRunState.Pending
            : live.All(t => t.Status == ToolRunState.Failed) ? ToolRunState.Failed
            : live.Any(t => t.Status is ToolRunState.Failed or ToolRunState.CompletedWithFlags) || scan.Failed > 0 ? ToolRunState.CompletedWithFlags
            : ToolRunState.Completed;

        // Counted in repos once the scan phase reports. Until then, and when the tools are run on their own, add up the tools.
        var counted = scan.Active
            ? (scan.Total, scan.Done, scan.Failed, scan.Skipped, scan.Running)
            : (tools.Sum(t => t.Total), tools.Sum(t => t.Done), tools.Sum(t => t.Failed), tools.Sum(t => t.Skipped), tools.Sum(t => t.Running));

        var notes = new List<string>();
        if (status == ToolRunState.Disabled)
        {
            notes.AddRange(disabled.Select(t => t.Message).Where(m => !string.IsNullOrWhiteSpace(m)).Cast<string>().Distinct());
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(scan.Message))
            {
                notes.Add(scan.Message);
            }

            notes.AddRange(live.Where(t => t.Status == ToolRunState.Failed && !string.IsNullOrWhiteSpace(t.Message)).Select(t => $"{t.Name} failed: {t.Message}"));
            if (disabled.Count > 0)
            {
                notes.Add($"not running: {string.Join(", ", disabled.Select(t => t.Name))}");
            }
        }

        return new ToolProgress
        {
            Name = ToolNames.Scanning,
            Status = status,
            Total = counted.Item1,
            Done = counted.Item2,
            Failed = counted.Item3,
            Skipped = counted.Item4,
            Running = counted.Item5,
            Canaries = tools.Sum(t => t.Canaries),
            Scanners = tools.Select(t => new ScannerProgress { Name = t.Name, Status = t.Status, Severities = t.Severities ?? new SeverityCounts() }).ToList(),
            Message = notes.Count == 0 ? null : string.Join(" | ", notes)
        };
    }

    public StatusDocument Snapshot()
    {
        lock (_gate)
        {
            var steps = ToolNames.All.Select(name => WithTiming(_steps[name], WindowFor(new[] { name }))).ToList();
            var finished = steps.Count(IsFinished);

            return new StatusDocument
            {
                RunId = _request.RunId,
                Revision = _request.Revision,
                Attempt = _attempt,
                State = _state,
                Paused = _state is RunState.Paused,
                Pid = Environment.ProcessId,
                Mode = _request.Mode,
                StartedAt = _startedAt,
                UpdatedAt = _time.GetUtcNow(),
                EndedAt = _endedAt,
                Phase = _phase,
                Overall = new ProgressSummary
                {
                    StepsCompleted = finished,
                    StepsTotal = steps.Count,
                    Percent = finished * 100 / steps.Count
                },
                Steps = steps,
                Effective = new Dictionary<string, EffectiveToolSettings>(_effective),
                Alerts = _alerts.ToList(),
                LastCommandSeq = _lastCommandSeq,
                CommandAcks = _commandAcks.ToList()
            };
        }
    }

    private static bool IsFinished(ToolProgress step) => step.Status is
        ToolRunState.Completed or ToolRunState.CompletedWithFlags or ToolRunState.Failed or ToolRunState.Disabled;

    private static ToolProgress InitialStep(string name, string? disabledReason) =>
        disabledReason is null
            ? new ToolProgress { Name = name }
            : new ToolProgress { Name = name, Status = ToolRunState.Disabled, Message = disabledReason };
}
