using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Control;
using Vizient.AutomatedScanner.Core.IO;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Processes;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.Status;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Core;

/// <summary>
/// Everything a tool needs to know about this run: static config, run folder layout, the request,
/// the live status it reports progress to, and the marker store it writes outcomes to.
/// </summary>
public sealed record RunContext(
    EngineConfig Config,
    RunPaths Paths,
    InvocationPaths Invocation,
    string RequestFile,
    RunRequestDocument Request,
    RunStatusTracker Status,
    MarkerStore Markers,
    RunControl Control,
    RepoReport Repos,
    CommandLog Commands,
    IProcessRunner Processes)
{
    /// <summary>A tool is enabled unless the request says otherwise.</summary>
    public bool IsEnabled(string tool) =>
        !Request.Tools.TryGetValue(tool, out var overrides) || overrides.Enabled != false;

    /// <summary>
    /// Reads the external request file, creates (or reopens) the run folder under the configured runs root,
    /// and copies the request file into it. The request file itself is never modified.
    /// Normal and Full start a new run folder; Resume and RetryFailed continue an existing one.
    /// </summary>
    public static async Task<RunContext> StartAsync(
        EngineConfig config,
        string requestFile,
        CancellationToken cancellationToken = default)
    {
        var requestPath = Path.GetFullPath(requestFile);
        if (!File.Exists(requestPath))
        {
            throw new FileNotFoundException($"Request file not found: '{requestPath}'.");
        }

        var request = await AtomicJsonFile.ReadAsync<RunRequestDocument>(requestPath, cancellationToken);
        ValidateRequest(request);

        var paths = new RunPaths(Path.Combine(config.RunsRoot, request.RunId));
        var continuing = request.Mode is ScanMode.Resume or ScanMode.RetryFailed;

        if (continuing)
        {
            await ValidateContinuationAsync(request, paths, cancellationToken);
        }
        else if (Directory.Exists(paths.RunFolder))
        {
            throw new InvalidDataException(
                $"Run folder for runId '{request.RunId}' already exists. A {request.Mode} run needs a new runId.");
        }

        var invocation = InvocationPaths.For(requestPath);

        // Read any earlier status.json (attempt, lastCommandSeq) before this run starts rewriting it.
        var status = await RunStatusTracker.CreateAsync(paths, invocation, request, cancellationToken: cancellationToken);

        // Commands recorded by earlier attempts of this run, if any.
        var commands = await CommandLog.CreateAsync(paths, request.RunId, cancellationToken);

        Directory.CreateDirectory(paths.RunFolder);
        await AtomicJsonFile.CopyAsync(requestPath, paths.Request, cancellationToken);
        return new RunContext(config, paths, invocation, requestPath, request, status, new MarkerStore(paths), new RunControl(), new RepoReport(), commands, new ProcessRunner());
    }

    private static void ValidateRequest(RunRequestDocument request)
    {
        if (!SafeName.IsValid(request.RunId))
        {
            throw new InvalidDataException(
                $"request.json runId '{request.RunId}' is missing or has characters not allowed in a folder name.");
        }

        var unknown = request.Tools.Keys.Except(ToolNames.All).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidDataException($"request.json names unknown tools: {string.Join(", ", unknown)}.");
        }
    }

    private static async Task ValidateContinuationAsync(
        RunRequestDocument request,
        RunPaths paths,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(paths.RunFolder))
        {
            throw new DirectoryNotFoundException(
                $"A {request.Mode} run needs the existing run folder for runId '{request.RunId}', but it does not exist.");
        }

        if (File.Exists(paths.Request))
        {
            var previous = await AtomicJsonFile.ReadAsync<RunRequestDocument>(paths.Request, cancellationToken);
            if (request.Revision <= previous.Revision)
            {
                throw new InvalidDataException(
                    $"request.json revision {request.Revision} must be greater than the run's current revision {previous.Revision}.");
            }
        }
    }
}
