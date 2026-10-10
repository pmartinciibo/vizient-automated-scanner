namespace Vizient.AutomatedScanner.Tests;

using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Processes;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.Tools;
using Vizient.AutomatedScanner.Core.Work;

public class RepoWorkLoopTests : IDisposable
{
    private const string Tool = "Semgrep";
    private const string RunId = "2026-10-10T12-00-00Z";
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
    private readonly string _requestFile;
    private readonly EngineConfig _config;

    public RepoWorkLoopTests()
    {
        _requestFile = Path.Combine(_root, "requests", "request.json");
        _config = new EngineConfig(
            Path.Combine(_root, "engine.json"),
            Path.Combine(_root, "repos"),
            Path.Combine(_root, "runs"),
            Path.Combine(_root, "state"),
            Path.Combine(_root, "tmp"),
            Path.Combine(_root, "logs"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- helpers ----

    private async Task<RunContext> StartRunAsync(
        ScanMode mode = ScanMode.Normal,
        int revision = 1,
        RequestScope? scope = null,
        Dictionary<string, ToolRequestOverride>? tools = null)
    {
        await AtomicJsonFile.WriteAsync(_requestFile, new RunRequestDocument
        {
            RunId = RunId,
            Mode = mode,
            Revision = revision,
            Scope = scope ?? new RequestScope(),
            Tools = tools ?? new()
        });
        return await RunContext.StartAsync(_config, _requestFile);
    }

    private static RepoOutcome Repo(int n, RepositoryStatus status = RepositoryStatus.Active, string? project = null) => new()
    {
        Key = $"gh-{n:D3}",
        Source = "GitHub",
        Org = "RnD-Vizient",
        Project = project,
        Name = $"repo{n}",
        LocalPath = $"C:\\repos\\repo{n}",
        ExistedLocally = true,
        ExistedRemotely = true,
        RemoteStatus = status
    };

    private static List<RepoOutcome> Repos(int count) => Enumerable.Range(1, count).Select(n => Repo(n)).ToList();

    private static RepoWorkResult Done(string sha = "a1b2") =>
        RepoWorkResult.Done(new ToolDoneMarker { Sha = sha, FindingCount = 1 });

    private static RepoWork Work(Func<RepoWorkContext, CancellationToken, Task<RepoWorkResult>> scan,
        Func<RepoOutcome, ToolSkippedMarker?>? skip = null) => new(scan, skip);

    private static RepoWork Instant() => Work((_, _) => Task.FromResult(Done()));

    private RepoWorkLoop Loop(RunContext context, int maxParallel = 4) =>
        new(context, Tool, new ToolLimits(maxParallel, TimeSpan.FromMinutes(30), RetryPolicy.None), Poll);

    private static ToolProgress Step(RunContext context) =>
        context.Status.Snapshot().Steps.Single(s => s.Name == Tool);

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.Add(Wait);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), $"Timed out waiting for: {what}");
    }

    /// <summary>A scan that holds each repo open until the test lets it go, and records what ran at once.</summary>
    private sealed class GatedWork
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource> _gates = new();
        public int Running { get; private set; }
        public int MaxRunning { get; private set; }
        public List<string> Started { get; } = new();

        public RepoWork Work => new(Scan);

        public int StartedCount { get { lock (_gate) { return Started.Count; } } }

        private TaskCompletionSource GateFor(string key)
        {
            lock (_gate)
            {
                if (!_gates.TryGetValue(key, out var gate))
                {
                    _gates[key] = gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                return gate;
            }
        }

        public void Release(string key) => GateFor(key).TrySetResult();

        public void ReleaseAll()
        {
            lock (_gate)
            {
                foreach (var gate in _gates.Values)
                {
                    gate.TrySetResult();
                }

                AutoRelease = true;
            }
        }

        private bool AutoRelease { get; set; }

        private async Task<RepoWorkResult> Scan(RepoWorkContext work, CancellationToken ct)
        {
            TaskCompletionSource gate;
            lock (_gate)
            {
                Running++;
                MaxRunning = Math.Max(MaxRunning, Running);
                Started.Add(work.Repo.Key);
                gate = GateFor(work.Repo.Key);
                if (AutoRelease)
                {
                    gate.TrySetResult();
                }
            }

            try
            {
                await gate.Task.WaitAsync(ct);
                return Done();
            }
            finally
            {
                lock (_gate)
                {
                    Running--;
                }
            }
        }
    }

    // ---- the basic pass ----

    [Fact]
    public async Task EveryRepoIsScannedAndGetsADoneMarkerAndCounts()
    {
        var context = await StartRunAsync();

        var summary = await Loop(context).RunAsync(Repos(5), Instant(), CancellationToken.None);

        Assert.Equal(new RepoLoopSummary(5, 5, 0, 0, 0, false, null), summary);
        Assert.All(Repos(5), r => Assert.True(context.Markers.IsDone(Tool, r.Key)));
        var step = Step(context);
        Assert.Equal((5, 5, 0, 0, 0), (step.Total, step.Done, step.Failed, step.Skipped, step.Running));
        Assert.Null(step.Message);
    }

    [Fact]
    public async Task ReposAreHandedToTheToolInKeyOrderWithTheirContext()
    {
        var context = await StartRunAsync();
        var seen = new List<(string Key, string Tool, string ResultFile)>();
        var work = Work((w, _) =>
        {
            lock (seen) { seen.Add((w.Repo.Key, w.Tool, w.ResultFile)); }
            return Task.FromResult(Done());
        });

        await Loop(context, maxParallel: 1).RunAsync(Repos(3).AsEnumerable().Reverse().ToList(), work, CancellationToken.None);

        Assert.Equal(["gh-001", "gh-002", "gh-003"], seen.Select(s => s.Key));
        Assert.All(seen, s => Assert.Equal(Tool, s.Tool));
        Assert.Equal(context.Paths.ResultFile(Tool, "gh-001"), seen[0].ResultFile);
    }

    [Fact]
    public async Task NoReposIsANoOp()
    {
        var context = await StartRunAsync();

        var summary = await Loop(context).RunAsync([], Instant(), CancellationToken.None);

        Assert.Equal(new RepoLoopSummary(0, 0, 0, 0, 0, false, null), summary);
    }

    // ---- parallelism ----

    [Fact]
    public async Task NoMoreThanTheLimitRunAtOnce()
    {
        var context = await StartRunAsync();
        var gated = new GatedWork();
        var run = Loop(context, maxParallel: 3).RunAsync(Repos(8), gated.Work, CancellationToken.None);

        await UntilAsync(() => gated.StartedCount == 3, "three repos started");
        await Task.Delay(100);
        Assert.Equal(3, gated.StartedCount);
        Assert.Equal(3, Step(context).Running);

        gated.ReleaseAll();
        await run.WaitAsync(Wait);

        Assert.Equal(3, gated.MaxRunning);
        Assert.Equal(8, Step(context).Done);
    }

    [Fact]
    public async Task ALimitRaisedByACommandTakesEffectWhileTheLoopIsRunning()
    {
        var context = await StartRunAsync();
        var gated = new GatedWork();
        var run = Loop(context, maxParallel: 1).RunAsync(Repos(6), gated.Work, CancellationToken.None);
        await UntilAsync(() => gated.StartedCount == 1, "one repo started");

        context.Control.SetMaxParallel(Tool, 4);

        await UntilAsync(() => gated.StartedCount == 4, "four repos started after the limit was raised");
        gated.ReleaseAll();
        await run.WaitAsync(Wait);
        Assert.Equal(4, gated.MaxRunning);
    }

    [Fact]
    public async Task ALimitLoweredByACommandStopsNewRepoStartingUntilEnoughHaveFinished()
    {
        var context = await StartRunAsync();
        var gated = new GatedWork();
        var run = Loop(context, maxParallel: 4).RunAsync(Repos(8), gated.Work, CancellationToken.None);
        await UntilAsync(() => gated.StartedCount == 4, "four repos started");

        context.Control.SetMaxParallel(Tool, 1);
        gated.Release("gh-001");
        gated.Release("gh-002");
        await Task.Delay(150);

        // Two finished, two still running, and the limit is now one, so nothing new has started.
        Assert.Equal(4, gated.StartedCount);

        gated.ReleaseAll();
        await run.WaitAsync(Wait);
    }

    [Fact]
    public async Task TheCommandLimitForAnotherToolDoesNotApply()
    {
        var context = await StartRunAsync();
        context.Control.SetMaxParallel("Snyk", 1);
        var gated = new GatedWork();
        var run = Loop(context, maxParallel: 3).RunAsync(Repos(5), gated.Work, CancellationToken.None);

        await UntilAsync(() => gated.StartedCount == 3, "three repos started");
        gated.ReleaseAll();
        await run.WaitAsync(Wait);
    }

    // ---- pause, stop, abort ----

    [Fact]
    public async Task PausedLoopStartsNothingUntilResumed()
    {
        var context = await StartRunAsync();
        context.Control.Pause();
        var gated = new GatedWork();
        var run = Loop(context).RunAsync(Repos(3), gated.Work, CancellationToken.None);

        await Task.Delay(200);
        Assert.Equal(0, gated.StartedCount);
        Assert.False(run.IsCompleted);

        context.Control.Resume();
        await UntilAsync(() => gated.StartedCount == 3, "all three started after resume");
        gated.ReleaseAll();
        await run.WaitAsync(Wait);
    }

    [Fact]
    public async Task PauseLetsReposAlreadyRunningFinishAndStartsNoMore()
    {
        var context = await StartRunAsync();
        var gated = new GatedWork();
        var run = Loop(context, maxParallel: 2).RunAsync(Repos(6), gated.Work, CancellationToken.None);
        await UntilAsync(() => gated.StartedCount == 2, "two started");

        context.Control.Pause();
        gated.Release("gh-001");
        gated.Release("gh-002");
        await UntilAsync(() => Step(context).Done == 2, "the two running repos finished");
        await Task.Delay(150);
        Assert.Equal(2, gated.StartedCount);

        context.Control.Resume();
        await UntilAsync(() => gated.StartedCount >= 3, "work continues after resume");
        gated.ReleaseAll();
        await run.WaitAsync(Wait);
        Assert.Equal(6, Step(context).Done);
    }

    [Fact]
    public async Task StopFinishesWhatIsRunningStartsNothingNewAndReportsWhatWasNotStarted()
    {
        var context = await StartRunAsync();
        var gated = new GatedWork();
        var run = Loop(context, maxParallel: 2).RunAsync(Repos(6), gated.Work, CancellationToken.None);
        await UntilAsync(() => gated.StartedCount == 2, "two started");

        context.Control.RequestStop();
        await Task.Delay(100);
        Assert.False(run.IsCompleted); // still waiting on the two in progress
        gated.ReleaseAll();
        var summary = await run.WaitAsync(Wait);

        Assert.Equal(2, gated.StartedCount);
        Assert.Equal(2, summary.Done);
        Assert.Equal(4, summary.NotStarted);
        Assert.True(summary.StoppedEarly);
        Assert.Equal("stopped; 4 repos not started", Step(context).Message);
    }

    [Fact]
    public async Task StopWhilePausedReleasesTheLoop()
    {
        var context = await StartRunAsync();
        context.Control.Pause();
        var run = Loop(context).RunAsync(Repos(3), Instant(), CancellationToken.None);
        await Task.Delay(100);

        context.Control.RequestStop();
        var summary = await run.WaitAsync(Wait);

        Assert.Equal(3, summary.NotStarted);
    }

    [Fact]
    public async Task AbortCancelsTheReposRunningAndThrows()
    {
        var context = await StartRunAsync();
        var gated = new GatedWork();
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(context.Control.AbortToken);
        var run = Loop(context, maxParallel: 2).RunAsync(Repos(5), gated.Work, abort.Token);
        await UntilAsync(() => gated.StartedCount == 2, "two started");

        context.Control.RequestAbort();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await UntilAsync(() => gated.Running == 0, "the running repos were cancelled");
        Assert.Equal(2, gated.StartedCount);
    }

    // ---- DisableTool and SkipRepo ----

    [Fact]
    public async Task DisablingTheToolMidRunStopsNewReposAndMarksTheStepDisabledWithTheReason()
    {
        var context = await StartRunAsync();
        var gated = new GatedWork();
        var run = Loop(context, maxParallel: 2).RunAsync(Repos(6), gated.Work, CancellationToken.None);
        await UntilAsync(() => gated.StartedCount == 2, "two started");

        context.Control.DisableTool(Tool, "vendor outage");
        await Task.Delay(100);
        gated.ReleaseAll();
        var summary = await run.WaitAsync(Wait);

        Assert.Equal(4, summary.NotStarted);
        Assert.Equal("vendor outage", summary.DisabledReason);
        var step = Step(context);
        Assert.Equal(ToolRunState.Disabled, step.Status);
        Assert.Equal("disabled by command: vendor outage; 4 repos not started", step.Message);
    }

    [Fact]
    public async Task ADisabledStepStaysDisabledWhenTheToolReturns()
    {
        var context = await StartRunAsync();
        context.Control.DisableTool(Tool, null);

        await Loop(context).RunAsync(Repos(3), Instant(), CancellationToken.None);
        context.Status.CompleteStep(Tool);

        Assert.Equal(ToolRunState.Disabled, Step(context).Status);
    }

    [Fact]
    public async Task ARepoSkippedByCommandBeforeTheStartIsNotScanned()
    {
        var context = await StartRunAsync();
        context.Control.SkipRepo("gh-002");

        var summary = await Loop(context).RunAsync(Repos(3), Instant(), CancellationToken.None);

        Assert.Equal(2, summary.Done);
        Assert.Equal(1, summary.Skipped);
        Assert.False(context.Markers.IsDone(Tool, "gh-002"));
        var marker = await AtomicJsonFile.ReadAsync<ToolSkippedMarker>(context.Paths.SkippedMarker(Tool, "gh-002"));
        Assert.Equal("SkippedByCommand", marker.Reason);
        Assert.Equal(2, Step(context).Total);
    }

    [Fact]
    public async Task ARepoSkippedByCommandWhileQueuedIsDroppedFromTheWorkThatRemains()
    {
        var context = await StartRunAsync();
        var gated = new GatedWork();
        var run = Loop(context, maxParallel: 1).RunAsync(Repos(3), gated.Work, CancellationToken.None);
        await UntilAsync(() => gated.StartedCount == 1, "first started");

        context.Control.SkipRepo("gh-002");
        gated.ReleaseAll();
        var summary = await run.WaitAsync(Wait);

        Assert.Equal(["gh-001", "gh-003"], gated.Started);
        Assert.Equal((2, 2, 1), (summary.Total, summary.Done, summary.Skipped));
        Assert.True(File.Exists(context.Paths.SkippedMarker(Tool, "gh-002")));
    }

    // ---- scope and tool-specific skips ----

    [Fact]
    public async Task ReposOutsideTheRequestsScopeAreSkippedAsExcludedByRequest()
    {
        var context = await StartRunAsync(scope: new RequestScope { IncludeRepos = ["gh-001", "gh-003"], ExcludeRepos = ["gh-003"] });

        var summary = await Loop(context).RunAsync(Repos(4), Instant(), CancellationToken.None);

        Assert.Equal((1, 1, 3), (summary.Total, summary.Done, summary.Skipped));
        Assert.True(context.Markers.IsDone(Tool, "gh-001"));
        foreach (var key in new[] { "gh-002", "gh-003", "gh-004" })
        {
            var marker = await AtomicJsonFile.ReadAsync<ToolSkippedMarker>(context.Paths.SkippedMarker(Tool, key));
            Assert.Equal("ExcludedByRequest", marker.Reason);
        }
    }

    [Fact]
    public async Task AToolCanSkipReposAndTheReasonIsRecorded()
    {
        var context = await StartRunAsync();
        var work = Work(
            (_, _) => Task.FromResult(Done()),
            repo => repo.Key == "gh-002" ? new ToolSkippedMarker { Reason = "Unchanged", Sha = "abc" } : null);

        var summary = await Loop(context).RunAsync(Repos(3), work, CancellationToken.None);

        Assert.Equal((2, 2, 1), (summary.Total, summary.Done, summary.Skipped));
        var marker = await AtomicJsonFile.ReadAsync<ToolSkippedMarker>(context.Paths.SkippedMarker(Tool, "gh-002"));
        Assert.Equal(("Unchanged", "abc"), (marker.Reason, marker.Sha));
    }

    [Fact]
    public async Task AToolThatDecidesAtTheLastMomentNotToRunMovesTheRepoFromToScanToSkipped()
    {
        var context = await StartRunAsync();
        var work = Work((w, _) => Task.FromResult(w.Repo.Key == "gh-002"
            ? RepoWorkResult.Skipped(new ToolSkippedMarker { Reason = "NotApplicable" })
            : Done()));

        var summary = await Loop(context).RunAsync(Repos(3), work, CancellationToken.None);

        Assert.Equal((2, 2, 0, 1), (summary.Total, summary.Done, summary.Failed, summary.Skipped));
    }

    // ---- failures ----

    [Fact]
    public async Task AFailedRepoGetsAFailedMarkerAndTheOthersCarryOn()
    {
        var context = await StartRunAsync();
        var work = Work((w, _) => Task.FromResult(w.Repo.Key == "gh-002"
            ? RepoWorkResult.Failed("Timeout", "exceeded 30 min", attempts: 2, sha: "a1b2")
            : Done()));

        var summary = await Loop(context).RunAsync(Repos(4), work, CancellationToken.None);

        Assert.Equal((4, 3, 1), (summary.Total, summary.Done, summary.Failed));
        var marker = await AtomicJsonFile.ReadAsync<ToolFailedMarker>(context.Paths.FailedMarker(Tool, "gh-002"));
        Assert.Equal(("Timeout", "exceeded 30 min", 2, "a1b2"), (marker.Category, marker.Message, marker.Attempts, marker.Sha));
        Assert.False(context.Markers.IsDone(Tool, "gh-002"));
    }

    [Fact]
    public async Task AToolThatThrowsFailsOnlyThatRepo()
    {
        var context = await StartRunAsync();
        var work = Work((w, _) => w.Repo.Key == "gh-002"
            ? throw new InvalidOperationException("scanner crashed")
            : Task.FromResult(Done()));

        var summary = await Loop(context).RunAsync(Repos(3), work, CancellationToken.None);

        Assert.Equal((3, 2, 1), (summary.Total, summary.Done, summary.Failed));
        var marker = await AtomicJsonFile.ReadAsync<ToolFailedMarker>(context.Paths.FailedMarker(Tool, "gh-002"));
        Assert.Equal("ToolError", marker.Category);
        Assert.Contains("scanner crashed", marker.Message);
    }

    [Fact]
    public async Task AFailedProcessResultMapsToACategorisedFailure()
    {
        var process = new ProcessResult { Outcome = ProcessOutcome.TimedOut, Attempts = 2, Message = "exceeded 30 min (attempt 2 of 2)" };

        var result = RepoWorkResult.FromFailedProcess(process, sha: "a1b2");

        Assert.Equal(RepoWorkKind.Failed, result.Kind);
        Assert.Equal(("Timeout", 2, "a1b2"), (result.FailedMarker!.Category, result.FailedMarker.Attempts, result.FailedMarker.Sha));
    }

    [Fact]
    public async Task AnUnrecordableOutcomeStopsTheLoopAndTheReposStillRunning()
    {
        var context = await StartRunAsync();
        var gated = new GatedWork();
        var run = Loop(context, maxParallel: 2).RunAsync(Repos(4), gated.Work, CancellationToken.None);
        await UntilAsync(() => gated.StartedCount == 2, "two started");

        // Make the marker for gh-001 impossible to write: a directory is in the way.
        Directory.CreateDirectory(context.Paths.DoneMarker(Tool, "gh-001"));
        gated.Release("gh-001");

        await Assert.ThrowsAnyAsync<Exception>(() => run);
        await UntilAsync(() => gated.Running == 0, "the other running repo was cancelled");
    }

    // ---- RetryFailed ----

    [Fact]
    public async Task RetryFailedRequeuesTheReposThatFailedAndTheirFailureCountIsReset()
    {
        var context = await StartRunAsync();
        var calls = new Dictionary<string, int>();
        var firstPassDone = new TaskCompletionSource();
        var work = Work(async (w, _) =>
        {
            int call;
            lock (calls) { call = calls[w.Repo.Key] = calls.GetValueOrDefault(w.Repo.Key) + 1; }

            if (w.Repo.Key == "gh-003")
            {
                // Hold the loop open (as a slow repo would) until the test has asked for the retry.
                await firstPassDone.Task;
            }

            return w.Repo.Key == "gh-002" && call == 1
                ? RepoWorkResult.Failed("ToolError", "boom")
                : Done();
        });
        var run = Loop(context, maxParallel: 2).RunAsync(Repos(3), work, CancellationToken.None);
        await UntilAsync(() => Step(context).Failed == 1, "gh-002 failed");

        context.Control.RequestRetryFailed(Tool);
        await UntilAsync(() => Step(context).Failed == 0, "failure count reset by the retry");
        firstPassDone.SetResult();
        var summary = await run.WaitAsync(Wait);

        Assert.Equal(2, calls["gh-002"]);
        Assert.Equal((3, 3, 0), (summary.Total, summary.Done, summary.Failed));
        Assert.True(context.Markers.IsDone(Tool, "gh-002"));
        Assert.False(context.Markers.IsFailed(Tool, "gh-002")); // the done marker replaced the failed one
    }

    [Fact]
    public async Task ARetryRequestForAnotherToolIsIgnoredButOneForEveryToolIsNot()
    {
        var context = await StartRunAsync();
        var calls = 0;
        var release = new TaskCompletionSource();
        var work = Work(async (w, _) =>
        {
            if (w.Repo.Key == "gh-002")
            {
                await release.Task;
                return Done();
            }

            return Interlocked.Increment(ref calls) == 1 ? RepoWorkResult.Failed("ToolError", "boom") : Done();
        });
        var run = Loop(context, maxParallel: 2).RunAsync(Repos(2), work, CancellationToken.None);
        await UntilAsync(() => Step(context).Failed == 1, "gh-001 failed");

        context.Control.RequestRetryFailed("Snyk");
        await Task.Delay(100);
        Assert.Equal(1, Step(context).Failed);

        context.Control.RequestRetryFailed(null);
        await UntilAsync(() => Step(context).Failed == 0, "retry for every tool applied");
        release.SetResult();
        await run.WaitAsync(Wait);
        Assert.Equal(2, calls);
    }

    // ---- Resume and RetryFailed runs ----

    [Fact]
    public async Task AResumedRunDoesNotRepeatReposThatAreDone()
    {
        var first = await StartRunAsync();
        await first.Markers.WriteDoneAsync(Tool, "gh-001", new ToolDoneMarker { Sha = "old" });
        await first.Markers.WriteFailedAsync(Tool, "gh-002", new ToolFailedMarker { Category = "Timeout", Attempts = 2 });

        var resumed = await StartRunAsync(ScanMode.Resume, revision: 2);
        var scanned = new List<string>();
        var work = Work((w, _) => { lock (scanned) { scanned.Add(w.Repo.Key); } return Task.FromResult(Done()); });

        var summary = await Loop(resumed).RunAsync(Repos(3), work, CancellationToken.None);

        Assert.Equal(["gh-002", "gh-003"], scanned.Order());
        Assert.Equal((3, 3, 0), (summary.Total, summary.Done, summary.Failed));
        Assert.True(resumed.Markers.IsDone(Tool, "gh-002")); // the failed one was repeated and now succeeded
        Assert.Equal("old", (await AtomicJsonFile.ReadAsync<ToolDoneMarker>(resumed.Paths.DoneMarker(Tool, "gh-001"))).Sha);
    }

    [Fact]
    public async Task ARetryFailedRunRepeatsOnlyTheReposThatFailed()
    {
        var first = await StartRunAsync();
        await first.Markers.WriteDoneAsync(Tool, "gh-001", new ToolDoneMarker { Sha = "old" });
        await first.Markers.WriteFailedAsync(Tool, "gh-002", new ToolFailedMarker { Category = "Timeout", Attempts = 2 });

        var retry = await StartRunAsync(ScanMode.RetryFailed, revision: 2);
        var scanned = new List<string>();
        var work = Work((w, _) => { lock (scanned) { scanned.Add(w.Repo.Key); } return Task.FromResult(Done()); });

        var summary = await Loop(retry).RunAsync(Repos(3), work, CancellationToken.None);

        Assert.Equal(["gh-002"], scanned);
        Assert.Equal((1, 1), (summary.Total, summary.Done));
        Assert.False(File.Exists(retry.Paths.DoneMarker(Tool, "gh-003")));
    }

    // ---- eligibility ----

    [Theory]
    [InlineData(RepositoryStatus.Archived, "StatusArchived")]
    [InlineData(RepositoryStatus.Disabled, "StatusDisabled")]
    [InlineData(RepositoryStatus.Deleted, "StatusDeleted")]
    [InlineData(RepositoryStatus.Unreachable, "StatusUnreachable")]
    public void ReposThatAreNotActiveAreSkippedWithTheDesignsReason(RepositoryStatus status, string reason)
    {
        Assert.Equal(reason, RepoEligibility.SkipFor(Repo(1, status))!.Reason);
    }

    [Fact]
    public void AnArchivedRepoIsScannedWhenTheEngineIsToldToScanArchivedRepos()
    {
        Assert.Null(RepoEligibility.SkipFor(Repo(1, RepositoryStatus.Archived), scanArchived: true));
    }

    [Fact]
    public void AnActiveRepoWithACloneIsScanned()
    {
        Assert.Null(RepoEligibility.SkipFor(Repo(1)));
        Assert.Null(RepoEligibility.SkipFor(Repo(1) with { ExistedLocally = false, UpdatedLocally = true }));
    }

    [Fact]
    public void ARepoWithNoCloneOnDiskIsSkipped()
    {
        Assert.Equal("NoLocalClone", RepoEligibility.SkipFor(Repo(1) with { LocalPath = null })!.Reason);
        Assert.Equal("NoLocalClone", RepoEligibility.SkipFor(Repo(1) with { ExistedLocally = false })!.Reason);
    }

    [Fact]
    public void AToolsOwnApplicabilityRuleIsCheckedAfterTheStatus()
    {
        Func<RepoOutcome, bool> neverApplies = _ => false;

        Assert.Equal("NotApplicable", RepoEligibility.SkipFor(Repo(1), isApplicable: neverApplies)!.Reason);
        Assert.Equal("StatusArchived", RepoEligibility.SkipFor(Repo(1, RepositoryStatus.Archived), isApplicable: neverApplies)!.Reason);
    }

    // ---- the base class the scanners use ----

    private sealed class FakeScanner(Func<RepoWorkContext, RepoWorkResult> scan, Func<RepoOutcome, bool>? applies = null)
        : ScanToolBase
    {
        public override string Name => Tool;
        protected override bool IsApplicable(RepoOutcome repo) => applies?.Invoke(repo) ?? true;

        protected override Task<RepoWorkResult> ScanRepoAsync(RepoWorkContext work, CancellationToken cancellationToken) =>
            Task.FromResult(scan(work));
    }

    [Fact]
    public async Task AScannerScansTheEligibleReposRepoCloneReportedAndSkipsTheRest()
    {
        var context = await StartRunAsync();
        context.Repos.Record(Repo(1));
        context.Repos.Record(Repo(2, RepositoryStatus.Archived));
        context.Repos.Record(Repo(3) with { LocalPath = null });
        context.Repos.Record(Repo(4, project: "dotnet"));
        context.Repos.Record(Repo(5));
        var scanner = new FakeScanner(_ => Done(), applies: repo => repo.Project != "dotnet");

        await scanner.RunAsync(context);

        Assert.True(context.Markers.IsDone(Tool, "gh-001"));
        Assert.True(context.Markers.IsDone(Tool, "gh-005"));
        Assert.Equal("StatusArchived", (await AtomicJsonFile.ReadAsync<ToolSkippedMarker>(context.Paths.SkippedMarker(Tool, "gh-002"))).Reason);
        Assert.Equal("NoLocalClone", (await AtomicJsonFile.ReadAsync<ToolSkippedMarker>(context.Paths.SkippedMarker(Tool, "gh-003"))).Reason);
        Assert.Equal("NotApplicable", (await AtomicJsonFile.ReadAsync<ToolSkippedMarker>(context.Paths.SkippedMarker(Tool, "gh-004"))).Reason);
        var step = Step(context);
        Assert.Equal((2, 2, 3), (step.Total, step.Done, step.Skipped));
    }

    [Fact]
    public async Task AScannerUnderTheOrchestratorEndsCompletedWithFlagsWhenARepoFailed()
    {
        var context = await StartRunAsync();
        context.Repos.Record(Repo(1));
        context.Repos.Record(Repo(2));
        var scanner = new FakeScanner(w => w.Repo.Key == "gh-002" ? RepoWorkResult.Failed("ToolError", "boom") : Done());
        var orchestrator = new RunOrchestrator(
            new Stub("Rapid7"), new Stub("CheckMarx"), new Stub("RepoClone"), [scanner]);

        var results = await orchestrator.RunAsync(context);

        Assert.Equal(ToolRunState.CompletedWithFlags, Step(context).Status);
        Assert.True(results.Single(r => r.Tool == Tool).Succeeded); // the tool itself ran; one repo failed
    }

    [Fact]
    public async Task AScannerWithNoFailuresEndsCompleted()
    {
        var context = await StartRunAsync();
        context.Repos.Record(Repo(1));
        var orchestrator = new RunOrchestrator(
            new Stub("Rapid7"), new Stub("CheckMarx"), new Stub("RepoClone"), [new FakeScanner(_ => Done())]);

        await orchestrator.RunAsync(context);

        Assert.Equal(ToolRunState.Completed, Step(context).Status);
        Assert.Equal(0, Step(context).Running);
    }

    private sealed class Stub(string name) : ITool
    {
        public string Name => name;
        public Task RunAsync(RunContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // ---- limits ----

    [Fact]
    public async Task ToolLimitsStartFromTheDesignsDefaultsAndTakeTheRequestsOverrides()
    {
        var plain = await StartRunAsync();
        var plainLimits = ToolLimits.For(plain, "Snyk");
        Assert.Equal((4, TimeSpan.FromMinutes(45)), (plainLimits.MaxParallel, plainLimits.Timeout));
        Assert.Equal(8, ToolLimits.For(plain, "TruffleHog").MaxParallel);
        Assert.Equal(12, ToolLimits.For(plain, "RepoClone").MaxParallel);
        Assert.Equal(2, plainLimits.Retry.MaxAttempts);

        Directory.Delete(plain.Paths.RunFolder, recursive: true);
        var overridden = await StartRunAsync(tools: new()
        {
            ["Snyk"] = new ToolRequestOverride { MaxParallel = 2, TimeoutMinutes = 10 },
            ["Semgrep"] = new ToolRequestOverride { Enabled = true }
        });
        var limits = ToolLimits.For(overridden, "Snyk");
        Assert.Equal((2, TimeSpan.FromMinutes(10)), (limits.MaxParallel, limits.Timeout));
        Assert.Equal(8, ToolLimits.For(overridden, "Semgrep").MaxParallel);
    }

    // ---- the retry cursor on its own ----

    [Fact]
    public void EachLoopHasItsOwnRetryCursor()
    {
        var control = new Core.Control.RunControl();
        long semgrep = control.RetryCursor;
        long snyk = control.RetryCursor;

        control.RequestRetryFailed(null);
        control.RequestRetryFailed("Snyk");

        Assert.True(control.RetryRequestedSince("Semgrep", ref semgrep)); // the request for every tool
        Assert.False(control.RetryRequestedSince("Semgrep", ref semgrep));
        Assert.True(control.RetryRequestedSince("Snyk", ref snyk));
        Assert.Equal(2, snyk);
    }
}
