namespace Vizient.AutomatedScanner.Tests;

using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Processes;
using Vizient.AutomatedScanner.Core.Scan;
using Vizient.AutomatedScanner.Core.Secrets;
using Vizient.AutomatedScanner.Core.Tools;
using Vizient.AutomatedScanner.Core.Work;

/// <summary>
/// A run in which RepoClone has already mirrored some local git repos, ready to be scanned. The mirrors are real, made
/// by the real RepoClone from local folders, so expanding and cleaning up are the real thing too.
/// </summary>
public sealed class ScanWorld : IDisposable
{
    private readonly RepoCloneRun _run = new();
    private readonly List<FakeOrigin> _origins = new();

    public RunContext Context { get; private set; } = null!;
    public string Root => _run.Root;
    public IReadOnlyList<FakeOrigin> Origins => _origins;

    public string ExpandedFolder => Path.Combine(_run.Config.TmpRoot, "expanded");

    /// <summary>Settings for the scan, applied before the run starts. Call before <see cref="CloneAsync"/>.</summary>
    public ScanWorld With(Func<EngineSettings, EngineSettings> change)
    {
        _run.With(change);
        return this;
    }

    public ScanWorld WithTool(string tool, Func<ToolSettings, ToolSettings> change) =>
        With(s => s with
        {
            Tools = new Dictionary<string, ToolSettings>(s.Tools) { [tool] = change(s.Tools.GetValueOrDefault(tool) ?? new ToolSettings()) }
        });

    /// <summary>Gives the scan a shared exclusions file with these lines. Call before <see cref="CloneAsync"/>.</summary>
    public ScanWorld WithExclusions(params string[] lines)
    {
        var file = Path.Combine(_run.Root, "exclusions.txt");
        File.WriteAllLines(file, lines);
        _run.UseExclusions(file);
        return this;
    }

    /// <summary>
    /// Puts a runner in front of the real one. It sees every program the scan starts. If it returns a result that is
    /// used, otherwise the real program runs (git always does).
    /// </summary>
    public InterceptingRunner Intercept(Func<ProcessSpec, ProcessResult?> intercept)
    {
        var runner = new InterceptingRunner(Context.Processes, intercept);
        Context = Context with { Processes = runner };
        return runner;
    }

    public ScanWorld AddSecret(string name, string value)
    {
        var path = Path.Combine(_run.Root, "secrets.dat");
        var store = SecretStore.LoadAsync(path).GetAwaiter().GetResult();
        store.Set(name, value, null, DateTimeOffset.UtcNow);
        store.SaveAsync(path).GetAwaiter().GetResult();
        return this;
    }

    public FakeOrigin AddOrigin(params (string File, string Content)[] files)
    {
        var origin = _run.Origin($"repo{_origins.Count + 1}", files);
        _origins.Add(origin);
        return origin;
    }

    /// <summary>Mirrors every origin added so far, in a new run. Returns that run.</summary>
    public async Task<RunContext> CloneAsync()
    {
        var sources = new FakeSources
        {
            Respond = uri => uri.Query.Contains("page=1")
                ? FakeSources.GitHubList(_origins.Select((o, i) => FakeSources.GitHubRepo(i + 1, $"repo{i + 1}", o.Path)).ToArray())
                : FakeSources.GitHubList()
        };
        Context = await _run.RunAsync(sources);
        return Context;
    }

    /// <summary>A second run over the same state and mirrors, as a later scan would be. Mirrors are updated.</summary>
    public async Task<RunContext> NextRunAsync()
    {
        var sources = new FakeSources
        {
            Respond = uri => uri.Query.Contains("page=1")
                ? FakeSources.GitHubList(_origins.Select((o, i) => FakeSources.GitHubRepo(i + 1, $"repo{i + 1}", o.Path)).ToArray())
                : FakeSources.GitHubList()
        };
        Context = await _run.RunAsync(sources);
        return Context;
    }

    /// <summary>Runs the five scanners the way the orchestrator does: every member started at once.</summary>
    public async Task<ScanResult[]> ScanAsync(IReadOnlyList<ScanToolBase> tools, ScanPhase? phase = null, CancellationToken cancellationToken = default)
    {
        phase ??= new ScanPhase(tools, pollInterval: TimeSpan.FromMilliseconds(10), joinWait: TimeSpan.FromSeconds(2));
        return await ScanAsync(phase, cancellationToken);
    }

    public async Task<ScanResult[]> ScanAsync(ScanPhase phase, CancellationToken cancellationToken = default)
    {
        var members = phase.Tools;
        var tasks = members.Select(async member =>
        {
            Context.Status.StepRunning(member.Name);
            try
            {
                await member.RunAsync(Context, cancellationToken);
                Context.Status.CompleteStep(member.Name);
                return new ScanResult(member.Name, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Context.Status.StepFinished(member.Name, ToolRunState.Failed, ex.Message);
                return new ScanResult(member.Name, ex);
            }
        }).ToList();

        return await Task.WhenAll(tasks);
    }

    public ToolProgress Step(string tool) => Context.Status.Snapshot().Steps.Single(s => s.Name == tool);

    public RepoOutcome Repo(int number) => Context.Repos.Snapshot().Single(r => r.Key == $"gh-{number}");

    public bool Done(string tool, int repo) => Context.Markers.IsDone(tool, $"gh-{repo}");

    public bool Failed(string tool, int repo) => Context.Markers.IsFailed(tool, $"gh-{repo}");

    public bool Skipped(string tool, int repo) => File.Exists(Context.Paths.SkippedMarker(tool, $"gh-{repo}"));

    public string SkippedReason(string tool, int repo) => File.ReadAllText(Context.Paths.SkippedMarker(tool, $"gh-{repo}"));

    public string FailedText(string tool, int repo) => File.ReadAllText(Context.Paths.FailedMarker(tool, $"gh-{repo}"));

    public string DoneText(string tool, int repo) => File.ReadAllText(Context.Paths.DoneMarker(tool, $"gh-{repo}"));

    /// <summary>Folders left under tmp\expanded: working copies that were not deleted.</summary>
    public string[] LeftoverCopies() =>
        Directory.Exists(ExpandedFolder)
            ? Directory.GetDirectories(ExpandedFolder, "*", SearchOption.AllDirectories).Where(d => Path.GetFileName(d).StartsWith("gh-")).ToArray()
            : [];

    public void Dispose() => _run.Dispose();
}

public sealed record ScanResult(string Tool, Exception? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// A scanner that does no scanning, only notes what it was given, for tests of how the phase drives its tools.
/// Named as one of the real tools because the status only knows those.
/// </summary>
public sealed class ProbeTool(
    string name,
    bool needsCopy = true,
    int findings = 0,
    TimeSpan? work = null,
    Func<RepoWorkContext, RepoWorkResult?>? decide = null,
    Func<RunContext, Task>? prepare = null) : ScanToolBase
{
    private readonly object _gate = new();
    private int _concurrent;

    public override string Name => name;

    protected override bool NeedsWorkingCopy(RunContext context) => needsCopy;

    protected override Random Random => new(1);

    public List<ProbeCall> Calls { get; } = new();

    public int MaxConcurrent { get; private set; }

    protected override Task PrepareAsync(RunContext context, CancellationToken cancellationToken) =>
        prepare?.Invoke(context) ?? Task.CompletedTask;

    protected override async Task<RepoWorkResult> ScanRepoAsync(RepoWorkContext work_, CancellationToken cancellationToken)
    {
        var expanded = Path.Combine(work_.Run.Config.TmpRoot, "expanded");
        var live = Directory.Exists(expanded)
            ? Directory.GetDirectories(expanded, "gh-*", SearchOption.AllDirectories).Length
            : 0;

        lock (_gate)
        {
            _concurrent++;
            MaxConcurrent = Math.Max(MaxConcurrent, _concurrent);
            Calls.Add(new ProbeCall(
                work_.Repo.Key,
                work_.WorkingPath,
                work_.WorkingPath is not null && File.Exists(Path.Combine(work_.WorkingPath, "readme.txt")),
                live));
        }

        try
        {
            if (work is { } delay)
            {
                await Task.Delay(delay, cancellationToken);
            }

            if (decide?.Invoke(work_) is { } custom)
            {
                return custom;
            }

            return RepoWorkResult.Done(new ToolDoneMarker
            {
                StartedAt = DateTimeOffset.UtcNow,
                EndedAt = DateTimeOffset.UtcNow,
                FindingCount = findings
            });
        }
        finally
        {
            lock (_gate)
            {
                _concurrent--;
            }
        }
    }

    public IEnumerable<string> Repos => Calls.Select(c => c.Repo).Order();
}

/// <summary>What a probe saw for one repo.</summary>
/// <param name="LiveCopies">How many working copies existed on disk when the tool started on the repo.</param>
public sealed record ProbeCall(string Repo, string? WorkingPath, bool SawFiles, int LiveCopies);

/// <summary>Lets a test answer for some programs and leaves the rest (git) to the real runner. Notes every spec.</summary>
public sealed class InterceptingRunner(IProcessRunner inner, Func<ProcessSpec, ProcessResult?> intercept) : IProcessRunner
{
    public List<ProcessSpec> Specs { get; } = new();

    public IEnumerable<ProcessSpec> For(string fileName) =>
        Specs.ToList().Where(s => string.Equals(s.FileName, fileName, StringComparison.OrdinalIgnoreCase));

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        lock (Specs)
        {
            Specs.Add(spec);
        }

        return intercept(spec) is { } answer ? Task.FromResult(answer) : inner.RunAsync(spec, cancellationToken);
    }
}

/// <summary>Runs nothing. Returns what a test says, and notes every spec it was asked to run.</summary>
public sealed class RecordingRunner(Func<ProcessSpec, ProcessResult>? respond = null) : IProcessRunner
{
    public List<ProcessSpec> Specs { get; } = new();

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        lock (Specs)
        {
            Specs.Add(spec);
        }

        return Task.FromResult(respond?.Invoke(spec) ?? Ok());
    }

    public static ProcessResult Ok(string stdout = "", string stderr = "") =>
        new() { Outcome = ProcessOutcome.Succeeded, ExitCode = 0, Attempts = 1, StdOut = stdout, StdErr = stderr, Message = "exited with code 0" };

    public static ProcessResult Fail(int code = 2, string stderr = "boom") =>
        new() { Outcome = ProcessOutcome.Failed, ExitCode = code, Attempts = 1, StdErr = stderr, Message = $"exited with code {code}" };
}

public static class FakeProgram
{
    /// <summary>Writes a batch file that logs its arguments and runs the given lines. Returns its path.</summary>
    public static string Write(string folder, string name, string logFile, params string[] lines)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".cmd");
        File.WriteAllLines(path,
        [
            "@echo off",
            $"echo %*>> \"{logFile}\"",
            $"echo CWD=%CD%>> \"{logFile}\"",
            .. lines
        ]);
        return path;
    }
}
