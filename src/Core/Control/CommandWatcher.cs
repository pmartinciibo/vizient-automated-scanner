using System.Text.Json;
using Vizient.AutomatedScanner.Core.IO;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Status;

namespace Vizient.AutomatedScanner.Core.Control;

/// <summary>
/// Polls command.json beside the request, applies each new command in seq order, remembers it in the
/// command log and records the ack in the status. The file belongs to the invoker, which rewrites the
/// whole file atomically; the engine only reads it once the run is going.
/// </summary>
public sealed class CommandWatcher(
    string commandFile,
    string runId,
    CommandProcessor processor,
    CommandLog log,
    RunStatusTracker status,
    TimeSpan interval)
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private bool _unreadableReported;

    public void Start() => _loop = Task.Run(LoopAsync, CancellationToken.None);

    public async Task StopAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            await _loop;
        }
    }

    /// <summary>One look at the file. The loop calls this each tick; tests call it directly.</summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CommandEntry> commands;
        try
        {
            commands = await CommandReader.ReadNewAsync(commandFile, runId, status.LastCommandSeq, cancellationToken);
            _unreadableReported = false;
        }
        catch (IOException)
        {
            // The invoker has it open for the moment. Try again next tick.
            return;
        }
        catch (JsonException)
        {
            // Not valid JSON. Say so once, then wait for the invoker to rewrite it.
            if (!_unreadableReported)
            {
                _unreadableReported = true;
                status.AddAlert(new StatusAlert
                {
                    Type = "CommandFileUnreadable",
                    Name = InvocationPaths.CommandFileName,
                    At = DateTimeOffset.UtcNow
                });
            }

            return;
        }

        foreach (var command in commands)
        {
            // Kept in memory for the record written when the run ends, whatever the outcome.
            log.Add(command);
            status.RecordAck(processor.Apply(command));
        }
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                await PollOnceAsync(_stop.Token);
            }
            while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }
}
