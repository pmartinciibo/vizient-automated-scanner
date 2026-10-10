using Vizient.AutomatedScanner.Core.IO;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;

namespace Vizient.AutomatedScanner.Core;

/// <summary>Writes results.json beside the request and keeps a snapshot of the final files in the run folder.</summary>
public static class RunResultsWriter
{
    /// <summary>
    /// Call after the final status.json has been written. Writes results.json next to the request, then copies
    /// both status.json and results.json into the run folder so each run keeps its own record.
    /// </summary>
    public static async Task WriteFinalAsync(
        RunContext context,
        RunState state,
        int exitCode,
        string? error,
        CancellationToken cancellationToken = default)
    {
        var status = context.Status.Snapshot();
        var repos = context.Repos.Snapshot();
        var results = new ResultsDocument
        {
            RunId = context.Request.RunId,
            RequestFile = context.RequestFile,
            RunDirectory = context.Paths.RunFolder,
            State = state,
            ExitCode = exitCode,
            StartedAt = status.StartedAt,
            EndedAt = status.EndedAt ?? DateTimeOffset.UtcNow,
            Error = error,
            Tools = status.Steps
                .Select(s => new ToolOutcome { Name = s.Name, Status = s.Status, Message = s.Message })
                .ToList(),
            RepoTotals = RepoSummary.From(repos),
            Orgs = [.. context.Repos.Orgs()]
        };

        await AtomicJsonFile.WriteAsync(context.Invocation.Results, results, cancellationToken);

        await AtomicJsonFile.CopyAsync(context.Invocation.Status, context.Paths.Status, cancellationToken);
        await AtomicJsonFile.CopyAsync(context.Invocation.Results, context.Paths.ResultsSummary, cancellationToken);

        // Every command received during the run, as the engine saw it. Written even when there were none.
        await context.Commands.WriteAsync(context.Paths, context.Request.RunId, cancellationToken);
    }

    /// <summary>
    /// The engine could not get as far as a run, so there is no run folder and no live status. Leave the
    /// invoker a results.json that says why. Best effort: if even that cannot be written, say so on stderr.
    /// </summary>
    public static async Task WriteStartupFailureAsync(
        string requestFile,
        string error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var invocation = InvocationPaths.For(requestFile);
            if (!Directory.Exists(invocation.Directory))
            {
                return;
            }

            // A status.json from an earlier run would read as this run's state.
            File.Delete(invocation.Status);

            await AtomicJsonFile.WriteAsync(
                invocation.Results,
                new ResultsDocument
                {
                    RequestFile = Path.GetFullPath(requestFile),
                    State = RunState.Failed,
                    ExitCode = 2,
                    EndedAt = DateTimeOffset.UtcNow,
                    Error = error
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not write results.json: {ex.Message}");
        }
    }

    /// <summary>Removes a results.json left by an earlier run so it cannot be mistaken for this one.</summary>
    public static void ClearStaleResults(string requestFile)
    {
        try
        {
            File.Delete(InvocationPaths.For(requestFile).Results);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not clear the old results.json: {ex.Message}");
        }
    }
}
