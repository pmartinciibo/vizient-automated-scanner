using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;

namespace Vizient.AutomatedScanner.Core.IO;

public static class CommandReader
{
    /// <summary>
    /// Returns commands with seq greater than <paramref name="lastCommandSeq"/>, in seq order.
    /// command.json is optional, so a missing file means no commands. The file sits beside the request
    /// and can be left over from an earlier run, so one whose runId is not this run's is ignored.
    /// </summary>
    public static async Task<IReadOnlyList<CommandEntry>> ReadNewAsync(
        string commandFile,
        string runId,
        long lastCommandSeq,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(commandFile))
        {
            return [];
        }

        var document = await AtomicJsonFile.ReadAsync<CommandDocument>(commandFile, cancellationToken);
        if (!string.Equals(document.RunId, runId, StringComparison.Ordinal))
        {
            return [];
        }

        return document.Commands
            .Where(c => c.Seq > lastCommandSeq)
            .OrderBy(c => c.Seq)
            .ToList();
    }
}
