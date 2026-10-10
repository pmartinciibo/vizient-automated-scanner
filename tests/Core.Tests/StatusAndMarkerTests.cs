namespace Vizient.AutomatedScanner.Tests;

using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.IO;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.Status;
using Vizient.AutomatedScanner.Core.Tools;

public class StatusAndMarkerTests : IDisposable
{
    private const string RunId = "2026-10-10T12-00-00Z";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
    private readonly EngineConfig _config;
    private readonly RunPaths _paths;
    private readonly InvocationPaths _invocation;

    public StatusAndMarkerTests()
    {
        Directory.CreateDirectory(_root);
        _paths = new RunPaths(Path.Combine(_root, "runs", RunId));
        _invocation = new InvocationPaths(Path.Combine(_root, "requests"));
        _config = new EngineConfig(
            Path.Combine(_root, "engine.json"),
            Path.Combine(_root, "repos"),
            Path.Combine(_root, "runs"),
            Path.Combine(_root, "state"),
            Path.Combine(_root, "tmp"),
            Path.Combine(_root, "logs"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static RunRequestDocument Request(params string[] disabledTools) => new()
    {
        RunId = RunId,
        Tools = disabledTools.ToDictionary(t => t, _ => new ToolRequestOverride { Enabled = false })
    };

    private async Task<string> WriteRequestAsync(RunRequestDocument request)
    {
        var path = Path.Combine(_root, "requests", $"{Guid.NewGuid():N}.json");
        await AtomicJsonFile.WriteAsync(path, request);
        return path;
    }

    // ---- status tracker ----

    [Fact]
    public async Task SnapshotAlwaysHasEightStepsAndShowsDisabledTools()
    {
        var tracker = await RunStatusTracker.CreateAsync(_paths, _invocation, Request("Snyk"));

        var status = tracker.Snapshot();

        Assert.Equal(ToolNames.All, status.Steps.Select(s => s.Name));
        Assert.Equal(ToolRunState.Disabled, status.Steps.Single(s => s.Name == "Snyk").Status);
        Assert.Equal(ToolRunState.Pending, status.Steps.Single(s => s.Name == "Semgrep").Status);
        Assert.Equal(RunState.Starting, status.State);
        Assert.Equal(Environment.ProcessId, status.Pid);
        Assert.Equal(1, status.Attempt);
    }

    [Fact]
    public async Task OverallProgressCountsFinishedSteps()
    {
        var tracker = await RunStatusTracker.CreateAsync(_paths, _invocation, Request("Snyk"));
        tracker.StepFinished("Rapid7", ToolRunState.Completed);
        tracker.StepFinished("CheckMarx", ToolRunState.Failed, "boom");
        tracker.StepRunning("Semgrep");

        var overall = tracker.Snapshot().Overall;

        Assert.Equal(3, overall.StepsCompleted); // Rapid7, CheckMarx, and the disabled Snyk
        Assert.Equal(8, overall.StepsTotal);
        Assert.Equal(37, overall.Percent);
    }

    [Fact]
    public async Task FinishSetsTerminalStateAndRejectsNonTerminalState()
    {
        var tracker = await RunStatusTracker.CreateAsync(_paths, _invocation, Request());

        Assert.Throws<ArgumentException>(() => tracker.Finish(RunState.Running));
        tracker.Finish(RunState.CompletedWithFlags);

        var status = tracker.Snapshot();
        Assert.Equal(RunState.CompletedWithFlags, status.State);
        Assert.NotNull(status.EndedAt);
    }

    [Fact]
    public async Task ContinuingARunIncrementsAttemptAndKeepsLastCommandSeq()
    {
        await AtomicJsonFile.WriteAsync(_paths.Status, new StatusDocument { RunId = RunId, Attempt = 2, LastCommandSeq = 7 });

        var status = (await RunStatusTracker.CreateAsync(_paths, _invocation, Request())).Snapshot();

        Assert.Equal(3, status.Attempt);
        Assert.Equal(7, status.LastCommandSeq);
    }

    // ---- status writer ----

    [Fact]
    public async Task WriterWritesAtStartPeriodicallyAndAtStop()
    {
        var tracker = await RunStatusTracker.CreateAsync(_paths, _invocation, Request());
        var writer = new StatusWriter(_invocation.Status, tracker, TimeSpan.FromMilliseconds(50));

        await writer.StartAsync();
        var first = await AtomicJsonFile.ReadAsync<StatusDocument>(_invocation.Status);
        Assert.Equal(RunState.Starting, first.State);

        tracker.SetState(RunState.Running);
        await WaitForAsync(async () => (await AtomicJsonFile.ReadAsync<StatusDocument>(_invocation.Status)).State == RunState.Running);

        tracker.Finish(RunState.Completed);
        await writer.StopAsync();

        var last = await AtomicJsonFile.ReadAsync<StatusDocument>(_invocation.Status);
        Assert.Equal(RunState.Completed, last.State);
        Assert.NotNull(last.EndedAt);
        Assert.Empty(Directory.GetFiles(_invocation.Directory, "*.tmp"));
    }

    // ---- orchestrator updates the status ----

    [Fact]
    public async Task OrchestratorReportsStepsPhasesAndFailuresInStatus()
    {
        var context = await RunContext.StartAsync(_config, await WriteRequestAsync(Request("Snyk")));
        var orchestrator = new RunOrchestrator(
            new FakeTool("Rapid7"),
            new FakeTool("CheckMarx", fail: true),
            new FakeTool("RepoClone"),
            [new FakeTool("TruffleHog"), new FakeTool("Semgrep"), new FakeTool("Snyk"),
             new FakeTool("SonarStandard"), new FakeTool("SonarDotNet")]);

        var results = await orchestrator.RunAsync(context);
        var status = context.Status.Snapshot();

        Assert.Equal(RunPhase.Finalize, status.Phase);
        Assert.Equal(RunState.Running, status.State);
        Assert.Equal(ToolRunState.Failed, status.Steps.Single(s => s.Name == "CheckMarx").Status);
        Assert.Contains("boom", status.Steps.Single(s => s.Name == "CheckMarx").Message);
        Assert.Equal(ToolRunState.Disabled, status.Steps.Single(s => s.Name == "Snyk").Status);
        Assert.Equal(6, status.Steps.Count(s => s.Status == ToolRunState.Completed));
        Assert.Equal(100, status.Overall.Percent);
        Assert.Single(results, r => !r.Succeeded);
    }

    // ---- markers ----

    [Fact]
    public async Task MarkersWriteToTheResultsLayoutWithSchemaVersion()
    {
        var store = new MarkerStore(_paths);

        await store.WriteDoneAsync("Semgrep", "gh-91234", new ToolDoneMarker { Sha = "a1b2", FindingCount = 14 });
        await store.WriteImportDoneAsync("Rapid7", new ImportDoneMarker { Entities = 150 });

        var done = await AtomicJsonFile.ReadAsync<ToolDoneMarker>(
            Path.Combine(_paths.Results, "Semgrep", "gh-91234.done.json"));
        Assert.Equal(14, done.FindingCount);
        Assert.Equal(3, done.SchemaVersion);
        Assert.True(File.Exists(Path.Combine(_paths.Results, "Rapid7", "_import.done.json")));
        Assert.Empty(Directory.GetFiles(_paths.Results, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task WritingAnOutcomeRemovesTheOthersFromAnEarlierAttempt()
    {
        var store = new MarkerStore(_paths);

        await store.WriteFailedAsync("Semgrep", "gh-1", new ToolFailedMarker { Category = "Timeout", Attempts = 2 });
        Assert.True(store.IsFailed("Semgrep", "gh-1"));

        await store.WriteDoneAsync("Semgrep", "gh-1", new ToolDoneMarker { Sha = "a1b2" });

        Assert.True(store.IsDone("Semgrep", "gh-1"));
        Assert.False(store.IsFailed("Semgrep", "gh-1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    public async Task MarkerKeyMustBeASafeFileName(string key)
    {
        var store = new MarkerStore(_paths);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.WriteDoneAsync("Semgrep", key, new ToolDoneMarker()));
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (IOException)
            {
                // Caught the file mid-replace; look again.
            }

            await Task.Delay(25);
        }

        Assert.Fail("Condition not met within 5 seconds.");
    }

    private sealed class FakeTool(string name, bool fail = false) : ITool
    {
        public string Name => name;

        public Task RunAsync(RunContext context, CancellationToken cancellationToken = default) =>
            fail ? throw new InvalidOperationException("boom") : Task.CompletedTask;
    }
}
