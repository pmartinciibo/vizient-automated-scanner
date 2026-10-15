using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Control;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Secrets;
using Vizient.AutomatedScanner.Core.Status;
using Vizient.AutomatedScanner.Core.Scan;
using Vizient.AutomatedScanner.Core.Tools;
using Vizient.AutomatedScanner.Engine;

var appTimer = System.Diagnostics.Stopwatch.StartNew();

if (args.Length > 0 && args[0] == "secrets")
{
    return await RunSecretsCommandAsync(args[1..]);
}

if (args.Length == 0 || args[0] != "run")
{
    PrintRunUsage();
    Console.Error.WriteLine("       engine secrets set|list|remove|check ...   (engine secrets for details)");
    return 2;
}

string? requestArgument = null;
string? secretsArgument = null;
for (var i = 1; i < args.Length; i++)
{
    if (args[i] == "--request" && i + 1 < args.Length && requestArgument is null)
    {
        requestArgument = args[++i];
    }
    else if (args[i] == "--secrets-file" && i + 1 < args.Length && secretsArgument is null)
    {
        secretsArgument = args[++i];
    }
    else
    {
        PrintRunUsage();
        return 2;
    }
}

if (string.IsNullOrWhiteSpace(requestArgument))
{
    PrintRunUsage();
    return 2;
}

var requestFile = Path.GetFullPath(requestArgument);

// An old results.json beside the request would read as this run's outcome.
RunResultsWriter.ClearStaleResults(requestFile);

RunContext context;
try
{
    var config = await EngineConfig.LoadAsync();
    if (secretsArgument is not null)
    {
        config = config with { SecretsFileOverride = Path.GetFullPath(secretsArgument) };
    }

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

// The secrets file reads and the secrets the tools need are there and in date. For now a problem is a warning
// and an alert, not the end of the run: no tool uses a secret yet. Once they do, a missing or expired secret
// (or a file that will not decrypt) must stop the run with exit code 2, as the design says.
try
{
    var secretsCheck = await SecretsPreflight.RunAsync(context);
    if (secretsCheck.FileError is not null)
    {
        Console.Error.WriteLine($"Warning: {secretsCheck.FileError}");
        context.Errors.Record(RunErrorScope.Secrets, secretsCheck.FileError, category: "SecretsFile");
    }

    foreach (var finding in secretsCheck.Findings)
    {
        var problem = finding.Issue switch
        {
            SecretIssue.Missing => "has not been set",
            SecretIssue.Expired => $"expired on {finding.ExpiresAt:yyyy-MM-dd}",
            _ => $"expires on {finding.ExpiresAt:yyyy-MM-dd}"
        };
        Console.Error.WriteLine($"Warning: secret '{finding.Name}' {problem} (needed by {finding.RequiredBy}).");
        context.Errors.Record(RunErrorScope.Secrets, $"Secret '{finding.Name}' {problem} (needed by {finding.RequiredBy}).", category: finding.Issue.ToString());
    }
}
catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
{
    Console.Error.WriteLine($"Warning: the secrets could not be checked: {ex.Message}");
    context.Errors.Record(RunErrorScope.Secrets, $"The secrets could not be checked: {ex.Message}", category: "SecretsCheck");
}

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

// One line per step, updated in place. Skipped when the output is redirected.
var display = ConsoleStatusDisplay.IsSupported()
    ? new ConsoleStatusDisplay(context.Status, TimeSpan.FromSeconds(1))
    : null;
display?.Start();

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

// The five scanners take the repos together: one working copy per repo, made from its mirror, scanned by every tool
// that applies and then deleted.
var scanPhase = new ScanPhase(
[
    new TruffleHogTool(),
    new SemgrepTool(),
    new SnykTool(),
    new SonarStandardTool(),
    new SonarDotNetTool()
]);

var orchestrator = new RunOrchestrator(
    new Rapid7Import(),
    new CheckMarxImport(),
    new RepoCloneTool(),
    scanPhase.Tools);

RunState finalState;
int exitCode;
string? error = null;
try
{
    var results = await orchestrator.RunAsync(context, cancellation.Token);

    // Two lines, in the tools' usual order: what ran, and what did not.
    string Names(bool disabled) => results.Where(r => r.Disabled == disabled).Select(r => r.Tool)
        .OrderBy(tool => ToolNames.All.ToList().IndexOf(tool)).ToList() is { Count: > 0 } names ? string.Join(", ", names) : "none";
    Console.WriteLine($"Enabled: {Names(disabled: false)}");
    Console.WriteLine($"Disabled: {Names(disabled: true)}");

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
    if (display is not null)
    {
        await display.StopAsync();
    }

    await RunResultsWriter.WriteFinalAsync(context, finalState, exitCode, error);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Could not write the final status and results: {ex.Message}");
    exitCode = 2;
}

// The last row on the console, after the status display has stopped.
var elapsed = appTimer.Elapsed;
Console.WriteLine($"Elapsed: {(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}");

// Then every error condition the run met, as results.json lists them.
try
{
    var runErrors = await RunErrorReport.BuildAsync(context, error);
    if (runErrors.Count > 0)
    {
        const int MaxShown = 50;
        Console.WriteLine($"Errors ({runErrors.Count}):");
        foreach (var e in runErrors.Take(MaxShown))
        {
            var where = string.Join("/", new[] { e.Tool, e.Source, e.Org, e.Repo ?? e.Key }.Where(part => !string.IsNullOrWhiteSpace(part)));
            var message = e.Message.ReplaceLineEndings(" ");
            Console.WriteLine($"  [{e.Scope}{(e.Category is null ? string.Empty : $" {e.Category}")}] {(where.Length > 0 ? where + ": " : string.Empty)}{(message.Length > 200 ? message[..200] + "..." : message)}");
        }

        if (runErrors.Count > MaxShown)
        {
            Console.WriteLine($"  ... and {runErrors.Count - MaxShown} more. All are in results.json.");
        }
    }
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Could not list the errors: {ex.Message}");
}

return exitCode;

static async Task<int> RunSecretsCommandAsync(string[] secretsArgs)
{
    EngineConfig config;
    try
    {
        config = await EngineConfig.LoadAsync();
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
    {
        Console.Error.WriteLine($"Fatal: {ex.Message}");
        return 2;
    }

    return await SecretsCommand.RunAsync(secretsArgs, config, new SystemSecretsConsole());
}

static void PrintRunUsage() =>
    Console.Error.WriteLine("Usage: engine run --request <path to request.json> [--secrets-file <path to secrets.dat>]");
