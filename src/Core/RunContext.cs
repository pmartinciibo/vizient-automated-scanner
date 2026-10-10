using System.Globalization;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Control;
using Vizient.AutomatedScanner.Core.IO;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Processes;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.State;
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
    IProcessRunner Processes,
    FoundOrgAppRepoState FoundState)
{
    /// <summary>
    /// A tool is on unless the request or engine.json turns it off. A request can also turn on a tool that
    /// engine.json has off, for one run.
    /// </summary>
    public bool IsEnabled(string tool) => DisabledReason(Config, Request, tool) is null;

    private static string? DisabledReason(EngineConfig config, RunRequestDocument request, string tool) =>
        ToolEnablement.DisabledReason(request, config.Current.Tool(tool), tool);

    /// <summary>
    /// Reads the external request file, creates (or reopens) the run folder under the configured runs root,
    /// and copies the request file into it. The request file itself is never modified.
    /// Normal and Full start a new run folder; Resume and RetryFailed continue an existing one.
    /// If the request has no runId, one is generated from the current time (see <see cref="NewRunId"/>).
    /// </summary>
    public static async Task<RunContext> StartAsync(
        EngineConfig config,
        string requestFile,
        CancellationToken cancellationToken = default,
        TimeProvider? time = null)
    {
        var requestPath = Path.GetFullPath(requestFile);
        if (!File.Exists(requestPath))
        {
            throw new FileNotFoundException($"Request file not found: '{requestPath}'.");
        }

        var request = await AtomicJsonFile.ReadAsync<RunRequestDocument>(requestPath, cancellationToken);

        var runIdGenerated = false;
        if (string.IsNullOrWhiteSpace(request.RunId))
        {
            // Continuing a run means naming it, so there is nothing to generate for those.
            if (request.Mode is ScanMode.Resume or ScanMode.RetryFailed)
            {
                throw new InvalidDataException(
                    $"A {request.Mode} run continues an existing run, so request.json must give its runId.");
            }

            request = request with { RunId = NewRunId(config.RunsRoot, time ?? TimeProvider.System) };
            runIdGenerated = true;
        }

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
        var status = await RunStatusTracker.CreateAsync(
            paths,
            invocation,
            request,
            cancellationToken: cancellationToken,
            disabledReason: tool => DisabledReason(config, request, tool));

        // Commands recorded by earlier attempts of this run, if any.
        var commands = await CommandLog.CreateAsync(paths, request.RunId, cancellationToken);

        Directory.CreateDirectory(paths.RunFolder);
        if (runIdGenerated)
        {
            // The invoker's file has no runId, so the run's copy is written with the one that was generated.
            await AtomicJsonFile.WriteAsync(paths.Request, request, cancellationToken);
        }
        else
        {
            await AtomicJsonFile.CopyAsync(requestPath, paths.Request, cancellationToken);
        }

        return new RunContext(config, paths, invocation, requestPath, request, status, new MarkerStore(paths), new RunControl(), new RepoReport(), commands, new ProcessRunner(), new FoundOrgAppRepoState(new FoundOrgAppRepoStore(config.StateRoot), request.RunId));
    }

    /// <summary>
    /// The current UTC time to the second, as yyyy-MM-ddTHH-mm-ssZ (the design's runId form, for example
    /// 2026-10-14T03-00-00Z). It is the run folder's name, so it has no / or : in it. If a run folder with
    /// that name exists already (two runs in the same second), -2, -3 and so on is added.
    /// </summary>
    public static string NewRunId(string runsRoot, TimeProvider time)
    {
        var stamp = time.GetUtcNow().ToUniversalTime().ToString("yyyy-MM-dd'T'HH-mm-ss'Z'", CultureInfo.InvariantCulture);
        var candidate = stamp;
        for (var suffix = 2; Directory.Exists(Path.Combine(runsRoot, candidate)); suffix++)
        {
            candidate = $"{stamp}-{suffix}";
        }

        return candidate;
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
