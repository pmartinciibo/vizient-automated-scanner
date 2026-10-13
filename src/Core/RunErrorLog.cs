using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core;

/// <summary>
/// Collects the errors the run handles and carries on from, so results.json can list them. A repo's failure is not here:
/// it is already a failed marker, and <see cref="RunErrorReport"/> reads those when the run ends.
/// </summary>
public sealed class RunErrorLog(TimeProvider? time = null)
{
    private readonly object _gate = new();
    private readonly List<RunError> _errors = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public void Record(
        string scope,
        string message,
        string? tool = null,
        string? category = null,
        string? source = null,
        string? org = null,
        string? key = null)
    {
        var error = new RunError
        {
            Scope = scope,
            At = _time.GetUtcNow(),
            Tool = tool,
            Category = category,
            Message = message,
            Source = source,
            Org = org,
            Key = key
        };

        lock (_gate)
        {
            _errors.Add(error);
        }
    }

    public IReadOnlyList<RunError> Snapshot()
    {
        lock (_gate)
        {
            return _errors.ToList();
        }
    }
}
