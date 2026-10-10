using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Core;

public sealed class RunOrchestrator(
    ITool rapid7,
    ITool checkMarx,
    ITool repoClone,
    IReadOnlyList<ITool> scanners)
{
    public async Task<IReadOnlyList<ToolResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        // Phase 1: imports and RepoClone run in parallel.
        var rapid7Task = RunToolAsync(rapid7, cancellationToken);
        var checkMarxTask = RunToolAsync(checkMarx, cancellationToken);
        var repoCloneTask = RunToolAsync(repoClone, cancellationToken);

        // Phase 2 starts as soon as RepoClone finishes; imports may still be running.
        // A RepoClone failure is flagged but does not stop the scanners.
        var repoCloneResult = await repoCloneTask;
        var scannerTasks = scanners.Select(s => RunToolAsync(s, cancellationToken)).ToList();

        var results = await Task.WhenAll(scannerTasks.Append(rapid7Task).Append(checkMarxTask));
        return results.Prepend(repoCloneResult).ToList();
    }

    private static async Task<ToolResult> RunToolAsync(ITool tool, CancellationToken cancellationToken)
    {
        try
        {
            await tool.RunAsync(cancellationToken);
            return new ToolResult(tool.Name, Succeeded: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // TODO: redact before this is persisted (failed marker / status alert).
            return new ToolResult(tool.Name, Succeeded: false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
