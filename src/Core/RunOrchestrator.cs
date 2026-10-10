using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Core;

public sealed class RunOrchestrator(
    ITool rapid7,
    ITool checkMarx,
    ITool repoClone,
    IReadOnlyList<ITool> scanners)
{
    public async Task<IReadOnlyList<ToolResult>> RunAsync(RunContext context, CancellationToken cancellationToken = default)
    {
        var control = context.Control;

        // Abort (command) and cancellation (Ctrl+C) both cancel in-flight work.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, control.AbortToken);
        var token = linked.Token;

        context.Status.MarkRunning();
        context.Status.SetPhase(RunPhase.ImportAndClone);
        await control.WaitWhilePausedAsync(token);

        // Phase 1: imports and RepoClone run in parallel.
        var rapid7Task = RunToolAsync(rapid7, context, token);
        var checkMarxTask = RunToolAsync(checkMarx, context, token);
        var repoCloneTask = RunToolAsync(repoClone, context, token);

        // Phase 2 starts as soon as RepoClone finishes; imports may still be running.
        // A RepoClone failure is flagged but does not stop the scanners.
        var repoCloneResult = await repoCloneTask;

        if (control.StopRequested)
        {
            // Stop: let in-flight work finish, start nothing new. The scanners stay Pending.
            var inFlight = await Task.WhenAll(rapid7Task, checkMarxTask);
            context.Status.SetPhase(RunPhase.Finalize);
            return inFlight.Prepend(repoCloneResult).ToList();
        }

        await control.WaitWhilePausedAsync(token);
        context.Status.SetPhase(RunPhase.Scan);
        var scannerTasks = scanners.Select(s => RunToolAsync(s, context, token)).ToList();

        var results = await Task.WhenAll(scannerTasks.Append(rapid7Task).Append(checkMarxTask));
        context.Status.SetPhase(RunPhase.Finalize);
        return results.Prepend(repoCloneResult).ToList();
    }

    private static async Task<ToolResult> RunToolAsync(ITool tool, RunContext context, CancellationToken cancellationToken)
    {
        // A tool the request disables is already shown as Disabled in the status.
        if (!context.IsEnabled(tool.Name))
        {
            return new ToolResult(tool.Name, Succeeded: true, Disabled: true);
        }

        // Disabled by a DisableTool command before it started.
        if (context.Control.IsToolDisabled(tool.Name, out var reason))
        {
            context.Status.StepFinished(tool.Name, ToolRunState.Disabled, reason ?? "disabled by command");
            return new ToolResult(tool.Name, Succeeded: true, Disabled: true);
        }

        context.Status.StepRunning(tool.Name);
        try
        {
            await tool.RunAsync(context, cancellationToken);
            context.Status.CompleteStep(tool.Name);
            return new ToolResult(tool.Name, Succeeded: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // TODO: redact before this is persisted (failed marker / status alert).
            var error = $"{ex.GetType().Name}: {ex.Message}";
            context.Status.StepFinished(tool.Name, ToolRunState.Failed, error);
            return new ToolResult(tool.Name, Succeeded: false, error);
        }
    }
}
