using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Core.Control;

/// <summary>
/// What the invoker has asked of the run while it is going (command.json). The commands change this object;
/// the orchestrator and the tools read it before they start new work. Nothing here kills a process by itself.
/// </summary>
public sealed class RunControl
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _abort = new();
    private readonly Dictionary<string, int> _maxParallel = new();
    private readonly Dictionary<string, string?> _disabled = new();
    private readonly HashSet<string> _skippedRepos = new();
    private readonly List<string?> _retryRequests = new();
    private TaskCompletionSource _unpaused = CompletedGate();
    private bool _paused;
    private bool _stopRequested;

    /// <summary>Cancelled by Abort. Pass it to child processes so their process trees are killed.</summary>
    public CancellationToken AbortToken => _abort.Token;

    /// <summary>Set by Stop and by Abort. Tools start no new repos once it is true.</summary>
    public bool StopRequested
    {
        get { lock (_gate) { return _stopRequested; } }
    }

    public bool IsPaused
    {
        get { lock (_gate) { return _paused; } }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_paused)
            {
                return;
            }

            _paused = true;
            _unpaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_paused)
            {
                return;
            }

            _paused = false;
            _unpaused.TrySetResult();
        }
    }

    /// <summary>Tools call this before starting each repo. Returns at once unless the run is paused.</summary>
    public Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        Task gate;
        lock (_gate)
        {
            gate = _unpaused.Task;
        }

        return gate.WaitAsync(cancellationToken);
    }

    public void RequestStop()
    {
        lock (_gate)
        {
            _stopRequested = true;
        }

        // A paused run that is stopping must not stay held.
        Resume();
    }

    public void RequestAbort()
    {
        RequestStop();
        _abort.Cancel();
    }

    /// <summary>The tool name, or "all" for every tool.</summary>
    public void SetMaxParallel(string tool, int maxParallel)
    {
        lock (_gate)
        {
            foreach (var name in tool == "all" ? ToolNames.All : [tool])
            {
                _maxParallel[name] = maxParallel;
            }
        }
    }

    /// <summary>The limit set by a command, or null when the request and engine.json limits stand.</summary>
    public int? MaxParallelOverride(string tool)
    {
        lock (_gate)
        {
            return _maxParallel.TryGetValue(tool, out var value) ? value : null;
        }
    }

    public IReadOnlyDictionary<string, int> MaxParallelOverrides()
    {
        lock (_gate)
        {
            return new Dictionary<string, int>(_maxParallel);
        }
    }

    public void DisableTool(string tool, string? reason)
    {
        lock (_gate)
        {
            _disabled[tool] = reason;
        }
    }

    public bool IsToolDisabled(string tool, out string? reason)
    {
        lock (_gate)
        {
            return _disabled.TryGetValue(tool, out reason);
        }
    }

    public void SkipRepo(string repoKey)
    {
        lock (_gate)
        {
            _skippedRepos.Add(repoKey);
        }
    }

    public bool IsRepoSkipped(string repoKey)
    {
        lock (_gate)
        {
            return _skippedRepos.Contains(repoKey);
        }
    }

    /// <summary>A null tool means every tool.</summary>
    public void RequestRetryFailed(string? tool)
    {
        lock (_gate)
        {
            _retryRequests.Add(tool);
        }
    }

    /// <summary>Returns the retry requests made since the last call. Tools requeue their failed repos.</summary>
    public IReadOnlyList<string?> TakeRetryRequests()
    {
        lock (_gate)
        {
            var taken = _retryRequests.ToList();
            _retryRequests.Clear();
            return taken;
        }
    }

    private static TaskCompletionSource CompletedGate()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }
}
