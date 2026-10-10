using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Control;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Status;
using Vizient.AutomatedScanner.Core.Tools;

if (args.Length != 3 || args[0] != "run" || args[1] != "--request")
{
    Console.Error.WriteLine("Usage: engine run --request <path to request.json>");
    return 2;
}

var requestFile = Path.GetFullPath(args[2]);

// An old results.json beside the request would read as this run's outcome.
RunResultsWriter.ClearStaleResults(requestFile);

RunContext context;
try
{
    var config = await EngineConfig.LoadAsync();
    config.EnsureDirectories();
    context = await RunContext.StartAsync(config, requestFile);
}
catch (Exception ex)
{
    // No run folder yet, so results.json beside the request is the invoker's only way to learn why.
    Console.Error.WriteLine($"Fatal: {ex.Message}");
    await RunResultsWriter.WriteStartupFailureAsync(requestFile, ex.Message);
    return 2;
}

Console.WriteLine($"Config: {context.Config.ConfigFile}");
Console.WriteLine($"Run:    {context.Paths.RunId} ({context.Request.Mode}, requested by {context.Request.RequestedBy})");
Console.WriteLine($"Folder: {context.Paths.RunFolder}");

// Ctrl+C stops the run gracefully so status.json still ends in a terminal state.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

var statusWriter = new StatusWriter(
    context.Invocation.Status,
    context.Status,
    TimeSpan.FromSeconds(context.Config.StatusIntervalSeconds));
await statusWriter.StartAsync();

// command.json beside the request: pause, stop, abort, concurrency and so on, while the run is going.
// Leave a copy with the supported commands described at the top for the invoker to fill in.
try
{
    await CommandTemplate.EnsureAsync(context.Invocation, context.Request.RunId);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    // The run can go on without it. Commands can still be sent if the invoker knows the format.
    Console.Error.WriteLine($"Could not write the command.json template: {ex.Message}");
}

var commandWatcher = new CommandWatcher(
    context.Invocation.Command,
    context.Request.RunId,
    new CommandProcessor(context.Control, context.Status),
    context.Commands,
    context.Status,
    TimeSpan.FromSeconds(context.Config.CommandIntervalSeconds));
commandWatcher.Start();

var orchestrator = new RunOrchestrator(
    new Rapid7Import(),
    new CheckMarxImport(),
    new RepoCloneTool(),
    [
        new TruffleHogTool(),
        new SemgrepTool(),
        new SnykTool(),
        new SonarStandardTool(),
        new SonarDotNetTool()
    ]);

RunState finalState;
int exitCode;
string? error = null;
try
{
    var results = await orchestrator.RunAsync(context, cancellation.Token);

    foreach (var disabled in results.Where(r => r.Disabled))
    {
        Console.WriteLine($"Disabled: {disabled.Tool}");
    }

    var failures = results.Where(r => !r.Succeeded).ToList();
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"FAILED {failure.Tool}: {failure.Error}");
    }

    // A Stop command lets in-flight work finish and then ends the run as Stopped.
    if (context.Control.StopRequested)
    {
        finalState = RunState.Stopped;
        exitCode = 1;
    }
    else
    {
        finalState = failures.Count == 0 ? RunState.Completed : RunState.CompletedWithFlags;
        exitCode = failures.Count == 0 ? 0 : 1;
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Stopped.");
    finalState = RunState.Stopped;
    exitCode = 1;
}
catch (Exception ex)
{
    // TODO: redact before this is written to results.json.
    Console.Error.WriteLine($"Fatal: {ex.Message}");
    finalState = RunState.Failed;
    exitCode = 2;
    error = ex.Message;
}

try
{
    await commandWatcher.StopAsync();
    context.Status.Finish(finalState);
    await statusWriter.StopAsync();
    await RunResultsWriter.WriteFinalAsync(context, finalState, exitCode, error);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Could not write the final status and results: {ex.Message}");
    exitCode = 2;
}

return exitCode;
