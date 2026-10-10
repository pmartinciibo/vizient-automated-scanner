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
    private readonly Dictionary<string, EffectiveToolSettings> _effective = new();
    private readonly List<StatusAlert> _alerts = new();
    private readonly List<CommandAcknowledgement> _commandAcks = new();
    private long _lastCommandSeq;
    private RunState _state = RunState.Starting;
    private RunPhase _phase = RunPhase.Preflight;
    private DateTimeOffset? _endedAt;

    private RunStatusTracker(
        RunRequestDocument request,
        int attempt,
        long lastCommandSeq,
        TimeProvider time)
    {
        _request = request;
        _attempt = attempt;
        _lastCommandSeq = lastCommandSeq;
        _time = time;
        _startedAt = time.GetUtcNow();
        _steps = ToolNames.All.ToDictionary(name => name, name => InitialStep(name, request));
    }

    /// <summary>
    /// Starts tracking. When an earlier attempt of this run left a status.json (Resume, RetryFailed), the
    /// attempt number continues and lastCommandSeq is kept so old commands are not replayed (design section 8).
    /// The run folder's copy is preferred. If the earlier attempt died before it could copy one, the live
    /// status.json beside the request is used, but only if it belongs to this run.
    /// </summary>
    public static async Task<RunStatusTracker> CreateAsync(
        RunPaths paths,
        InvocationPaths invocation,
        RunRequestDocument request,
        TimeProvider? time = null,
        CancellationToken cancellationToken = default)
    {
        var attempt = 1;
        long lastCommandSeq = 0;

        var previous = await ReadPreviousAsync(paths, invocation, request.RunId, cancellationToken);
        if (previous is not null)
        {
            attempt = previous.Attempt + 1;
            lastCommandSeq = previous.LastCommandSeq;
        }

        return new RunStatusTracker(request, attempt, lastCommandSeq, time ?? TimeProvider.System);
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

            _steps[tool] = update(step);
        }
    }

    public StatusDocument Snapshot()
    {
        lock (_gate)
        {
            var steps = ToolNames.All.Select(name => _steps[name]).ToList();
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

    private static ToolProgress InitialStep(string name, RunRequestDocument request) =>
        request.Tools.TryGetValue(name, out var overrides) && overrides.Enabled == false
            ? new ToolProgress { Name = name, Status = ToolRunState.Disabled, Message = "disabled in request.json" }
            : new ToolProgress { Name = name };
}
