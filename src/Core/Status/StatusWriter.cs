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
                }
                catch (IOException)
                {
                    // A reader held the file for a moment. The next tick writes again.
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
                catch (IOException) when (attempt < attempts)
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
