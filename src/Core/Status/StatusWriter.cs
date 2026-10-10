using Vizient.AutomatedScanner.Core.Serialization;

namespace Vizient.AutomatedScanner.Core.Status;

/// <summary>
/// The single writer of the live status.json (beside the request). Writes once at start, then every
/// interval, and once more on stop so the terminal state is always on disk.
/// </summary>
public sealed class StatusWriter(string statusFile, RunStatusTracker tracker, TimeSpan interval)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private bool _failureReported;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await WriteAsync(attempts: 1, cancellationToken);
        _loop = Task.Run(LoopAsync, CancellationToken.None);
    }

    /// <summary>Stops the timer and writes the final snapshot. Set the terminal state on the tracker first.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            await _loop;
        }

        // The final write matters most, so retry briefly if a reader has the file open.
        await WriteAsync(attempts: 5, cancellationToken);
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                try
                {
                    await WriteAsync(attempts: 1, _stop.Token);
                    _failureReported = false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Most often a reader (an invoker polling this file) held it, so it could not be replaced.
                    // Whatever the cause, one failed tick must not end the updates: the next tick writes again.
                    if (!_failureReported)
                    {
                        _failureReported = true;
                        Console.Error.WriteLine($"Could not update the status file ({ex.GetType().Name}); will keep trying.");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private async Task WriteAsync(int attempts, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await AtomicJsonFile.WriteAsync(statusFile, tracker.Snapshot(), cancellationToken);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < attempts)
                {
                    await Task.Delay(200, cancellationToken);
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
