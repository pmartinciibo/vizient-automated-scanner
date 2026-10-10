using Vizient.AutomatedScanner.Core.IO;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;

namespace Vizient.AutomatedScanner.Core.Control;

/// <summary>
/// Every command the engine has received for this run, kept in memory and written to command.json in the
/// run folder when the run ends. A resumed run starts from what the earlier attempts recorded.
/// </summary>
public sealed class CommandLog
{
    private readonly object _gate = new();
    private readonly List<CommandEntry> _commands = new();

    public static async Task<CommandLog> CreateAsync(
        RunPaths paths,
        string runId,
        CancellationToken cancellationToken = default)
    {
        var log = new CommandLog();

        if (File.Exists(paths.Command))
        {
            var earlier = await AtomicJsonFile.ReadAsync<CommandDocument>(paths.Command, cancellationToken);
            if (earlier.RunId == runId)
            {
                foreach (var command in earlier.Commands)
                {
                    log.Add(command);
                }
            }
        }

        return log;
    }

    /// <summary>Records a command as received. A seq already recorded is not added again.</summary>
    public void Add(CommandEntry command)
    {
        lock (_gate)
        {
            if (_commands.All(c => c.Seq != command.Seq))
            {
                _commands.Add(command);
            }
        }
    }

    public IReadOnlyList<CommandEntry> Snapshot()
    {
        lock (_gate)
        {
            return _commands.OrderBy(c => c.Seq).ToList();
        }
    }

    /// <summary>Writes command.json into the run folder, even when no command was received.</summary>
    public Task WriteAsync(RunPaths paths, string runId, CancellationToken cancellationToken = default) =>
        AtomicJsonFile.WriteAsync(
            paths.Command,
            new CommandDocument { RunId = runId, Commands = [.. Snapshot()] },
            cancellationToken);
}
