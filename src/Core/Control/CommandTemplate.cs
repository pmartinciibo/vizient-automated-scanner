using Vizient.AutomatedScanner.Core.IO;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Core.Control;

/// <summary>
/// Leaves a command.json beside the request at the start of each run: no commands yet, and comments at the
/// top that say which commands the engine supports and how to send them.
/// </summary>
public static class CommandTemplate
{
    /// <summary>
    /// Writes the template unless the file already holds commands for this run (an invoker that queued
    /// commands before the engine started). A file for another run, or one that cannot be read, is replaced.
    /// </summary>
    /// <returns>True if the template was written.</returns>
    public static async Task<bool> EnsureAsync(
        InvocationPaths invocation,
        string runId,
        CancellationToken cancellationToken = default)
    {
        if (await HoldsCommandsForRunAsync(invocation.Command, runId, cancellationToken))
        {
            return false;
        }

        await AtomicJsonFile.WriteWithCommentsAsync(
            invocation.Command,
            new CommandDocument { RunId = runId, Commands = [] },
            Header(),
            cancellationToken);
        return true;
    }

    public static IReadOnlyList<string> Header()
    {
        var nameWidth = CommandCatalog.All.Max(c => c.Type.Length) + 2;
        var lines = new List<string>
        {
            "command.json: send commands to the scan engine while a run is going.",
            "The engine reads this file every few seconds and applies each new command.",
            "",
            "How to send a command:",
            "  - Add an entry to \"commands\" and save the file. Write the whole file at once",
            "    (write a temporary file, then rename it over this one).",
            "  - \"seq\" must be higher than every earlier command for this run and is never reused.",
            "    The engine applies only commands with a seq above the last one it has seen.",
            "  - \"runId\" must match the run, or the engine ignores the file.",
            "  - \"id\", \"issuedAt\" (UTC, ISO-8601) and \"issuedBy\" are for the record.",
            "  - Leave earlier commands in the file. Ones already applied are skipped.",
            "  - Lines starting with // are comments and are allowed.",
            "",
            "Supported commands (\"type\", then its \"args\"):"
        };

        foreach (var command in CommandCatalog.All)
        {
            lines.Add($"  {command.Type.PadRight(nameWidth)}{command.Summary}");
            lines.Add($"  {new string(' ', nameWidth)}args: {command.Args}");
        }

        lines.Add("");
        lines.Add($"Tool names: {string.Join(", ", ToolNames.All)}");
        lines.Add("");
        lines.Add("Example entry:");
        lines.Add("  { \"seq\": 1, \"id\": \"b7e1-0001\", \"type\": \"SetConcurrency\", \"issuedAt\": \"2026-10-14T04:30:00Z\",");
        lines.Add("    \"issuedBy\": \"phil\", \"args\": { \"tool\": \"TruffleHog\", \"maxParallel\": 3 } }");
        lines.Add("");
        lines.Add("The outcome of each command (Applied, Rejected or Ignored) is shown in status.json under \"commandAcks\".");
        return lines;
    }

    private static async Task<bool> HoldsCommandsForRunAsync(
        string commandFile,
        string runId,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(commandFile))
        {
            return false;
        }

        try
        {
            var existing = await AtomicJsonFile.ReadAsync<CommandDocument>(commandFile, cancellationToken);
            return existing.RunId == runId && existing.Commands.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
