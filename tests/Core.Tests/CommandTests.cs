namespace Vizient.AutomatedScanner.Tests;

using System.Text.Json;
using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Control;
using Vizient.AutomatedScanner.Core.IO;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.Status;
using Vizient.AutomatedScanner.Core.Tools;

public class CommandTests : IDisposable
{
    private const string RunId = "2026-10-10T12-00-00Z";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
    private readonly string _requestDir;
    private readonly string _requestFile;
    private readonly string _commandFile;
    private readonly EngineConfig _config;

    public CommandTests()
    {
        _requestDir = Path.Combine(_root, "requests");
        _requestFile = Path.Combine(_requestDir, "request.json");
        _commandFile = Path.Combine(_requestDir, "command.json");
        _config = new EngineConfig(
            Path.Combine(_root, "engine.json"),
            Path.Combine(_root, "repos"),
            Path.Combine(_root, "runs"),
            Path.Combine(_root, "state"),
            Path.Combine(_root, "tmp"),
            Path.Combine(_root, "logs"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task<RunContext> StartRunAsync(RunRequestDocument? request = null)
    {
        await AtomicJsonFile.WriteAsync(_requestFile, request ?? new RunRequestDocument { RunId = RunId });
        var context = await RunContext.StartAsync(_config, _requestFile);
        context.Status.MarkRunning();
        return context;
    }

    private static CommandEntry Command(long seq, string type, object? args = null) => new()
    {
        Seq = seq,
        Id = $"id-{seq}",
        Type = type,
        IssuedBy = "phil",
        Args = args is null
            ? new()
            : JsonSerializer.SerializeToElement(args).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
    };

    private static async Task WriteCommandsAsync(string file, string runId, params CommandEntry[] commands) =>
        await AtomicJsonFile.WriteAsync(file, new CommandDocument { RunId = runId, Commands = [.. commands] });

    // ---- processor ----

    [Fact]
    public async Task PauseAndResumeMoveTheStateAndTheGate()
    {
        var context = await StartRunAsync();
        var processor = new CommandProcessor(context.Control, context.Status);

        Assert.Equal("Applied", processor.Apply(Command(1, "Pause")).Status);
        Assert.Equal(RunState.Paused, context.Status.State);
        Assert.True(context.Control.IsPaused);
        Assert.False(context.Control.WaitWhilePausedAsync(CancellationToken.None).IsCompleted);

        Assert.Equal("Applied", processor.Apply(Command(2, "Resume")).Status);
        Assert.Equal(RunState.Running, context.Status.State);
        Assert.True(context.Control.WaitWhilePausedAsync(CancellationToken.None).IsCompleted);
    }

    [Fact]
    public async Task ResumeWhenNotPausedIsIgnored()
    {
        var context = await StartRunAsync();
        var ack = new CommandProcessor(context.Control, context.Status).Apply(Command(1, "Resume"));

        Assert.Equal("Ignored", ack.Status);
    }

    [Fact]
    public async Task StopRequestsStopAndAbortCancelsTheAbortToken()
    {
        var context = await StartRunAsync();
        var processor = new CommandProcessor(context.Control, context.Status);

        Assert.Equal("Applied", processor.Apply(Command(1, "Stop")).Status);
        Assert.True(context.Control.StopRequested);
        Assert.False(context.Control.AbortToken.IsCancellationRequested);
        Assert.Equal(RunState.Stopping, context.Status.State);
        Assert.Equal("Ignored", processor.Apply(Command(2, "Stop")).Status);

        Assert.Equal("Applied", processor.Apply(Command(3, "Abort")).Status);
        Assert.True(context.Control.AbortToken.IsCancellationRequested);
    }

    [Fact]
    public async Task SetConcurrencyUpdatesTheControlAndTheEffectiveSettings()
    {
        var context = await StartRunAsync();
        var processor = new CommandProcessor(context.Control, context.Status);

        var ack = processor.Apply(Command(1, "SetConcurrency", new { tool = "TruffleHog", maxParallel = 3 }));

        Assert.Equal("Applied", ack.Status);
        Assert.Equal(3, context.Control.MaxParallelOverride("TruffleHog"));
        Assert.Null(context.Control.MaxParallelOverride("Semgrep"));
        Assert.Equal(3, context.Status.Snapshot().Effective["TruffleHog"].MaxParallel);

        processor.Apply(Command(2, "SetConcurrency", new { tool = "all", maxParallel = 2 }));
        Assert.Equal(2, context.Control.MaxParallelOverride("Semgrep"));
        Assert.Equal(ToolNames.All.Count, context.Status.Snapshot().Effective.Count);
    }

    [Theory]
    [InlineData("""{ "tool": "Semgrp", "maxParallel": 3 }""")]
    [InlineData("""{ "tool": "Semgrep", "maxParallel": 0 }""")]
    [InlineData("""{ "tool": "Semgrep", "maxParallel": 100000 }""")]
    [InlineData("""{ "tool": "Semgrep", "maxParallel": "3" }""")]
    [InlineData("""{ "maxParallel": 3 }""")]
    public async Task BadConcurrencyArgumentsAreRejectedAndChangeNothing(string argsJson)
    {
        var context = await StartRunAsync();
        var command = new CommandEntry
        {
            Seq = 1,
            Type = "SetConcurrency",
            Args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson)!
        };

        var ack = new CommandProcessor(context.Control, context.Status).Apply(command);

        Assert.Equal("Rejected", ack.Status);
        Assert.NotNull(ack.Message);
        Assert.Empty(context.Control.MaxParallelOverrides());
    }

    [Fact]
    public async Task UnknownCommandTypeIsRejected()
    {
        var context = await StartRunAsync();

        var ack = new CommandProcessor(context.Control, context.Status).Apply(Command(1, "Explode"));

        Assert.Equal("Rejected", ack.Status);
        Assert.Contains("Explode", ack.Message);
    }

    [Fact]
    public async Task DisableSkipAndRetryAreRecordedForTheTools()
    {
        var context = await StartRunAsync();
        var processor = new CommandProcessor(context.Control, context.Status);

        processor.Apply(Command(1, "DisableTool", new { tool = "Snyk", reason = "vendor outage" }));
        processor.Apply(Command(2, "SkipRepo", new { repoKey = "gh-91234" }));
        processor.Apply(Command(3, "RetryFailed", new { tool = "Semgrep" }));
        var badSkip = processor.Apply(Command(4, "SkipRepo", new { repoKey = "..\\evil" }));

        Assert.True(context.Control.IsToolDisabled("Snyk", out var reason));
        Assert.Equal("vendor outage", reason);
        Assert.True(context.Control.IsRepoSkipped("gh-91234"));
        long semgrepCursor = 0;
        long snykCursor = 0;
        Assert.True(context.Control.RetryRequestedSince("Semgrep", ref semgrepCursor));
        Assert.False(context.Control.RetryRequestedSince("Semgrep", ref semgrepCursor)); // already seen
        Assert.False(context.Control.RetryRequestedSince("Snyk", ref snykCursor));       // asked for Semgrep only
        Assert.Equal("Rejected", badSkip.Status);
        Assert.Contains(context.Status.Snapshot().Alerts, a => a.Type == "ToolDisabled" && a.Tool == "Snyk");
    }

    // ---- watcher ----

    [Fact]
    public async Task WatcherAppliesNewCommandsInOrderAndRecordsAcks()
    {
        var context = await StartRunAsync();
        var watcher = new CommandWatcher(
            context.Invocation.Command, RunId, new CommandProcessor(context.Control, context.Status),
            context.Commands, context.Status, TimeSpan.FromSeconds(1));
        await WriteCommandsAsync(
            _commandFile, RunId,
            Command(2, "Resume"),
            Command(1, "Pause"));

        await watcher.PollOnceAsync();

        var status = context.Status.Snapshot();
        Assert.Equal(2, status.LastCommandSeq);
        Assert.Equal([1L, 2L], status.CommandAcks.Select(a => a.Seq));
        Assert.All(status.CommandAcks, a => Assert.Equal("Applied", a.Status));
        Assert.Equal(RunState.Running, status.State);
    }

    [Fact]
    public async Task WatcherDoesNotReplayCommandsAlreadyAcknowledged()
    {
        var context = await StartRunAsync();
        var watcher = new CommandWatcher(
            context.Invocation.Command, RunId, new CommandProcessor(context.Control, context.Status),
            context.Commands, context.Status, TimeSpan.FromSeconds(1));
        await WriteCommandsAsync(_commandFile, RunId, Command(1, "Pause"));
        await watcher.PollOnceAsync();
        context.Control.Resume();
        context.Status.SetState(RunState.Running);

        await watcher.PollOnceAsync();
        await WriteCommandsAsync(_commandFile, RunId, Command(1, "Pause"), Command(2, "Stop"));
        await watcher.PollOnceAsync();

        Assert.False(context.Control.IsPaused);
        Assert.True(context.Control.StopRequested);
        Assert.Equal([1L, 2L], context.Status.Snapshot().CommandAcks.Select(a => a.Seq));
    }

    [Fact]
    public async Task WatcherIgnoresACommandFileLeftByAnotherRun()
    {
        var context = await StartRunAsync();
        var watcher = new CommandWatcher(
            context.Invocation.Command, RunId, new CommandProcessor(context.Control, context.Status),
            context.Commands, context.Status, TimeSpan.FromSeconds(1));
        await WriteCommandsAsync(_commandFile, "some-other-run", Command(1, "Abort"));

        await watcher.PollOnceAsync();

        Assert.False(context.Control.StopRequested);
        Assert.Empty(context.Status.Snapshot().CommandAcks);
    }

    [Fact]
    public async Task WatcherReportsAnUnreadableFileOnceAndRecoversWhenItIsFixed()
    {
        var context = await StartRunAsync();
        var watcher = new CommandWatcher(
            context.Invocation.Command, RunId, new CommandProcessor(context.Control, context.Status),
            context.Commands, context.Status, TimeSpan.FromSeconds(1));
        await File.WriteAllTextAsync(_commandFile, "{ not json");

        await watcher.PollOnceAsync();
        await watcher.PollOnceAsync();

        Assert.Single(context.Status.Snapshot().Alerts, a => a.Type == "CommandFileUnreadable");

        await WriteCommandsAsync(_commandFile, RunId, Command(1, "Stop"));
        await watcher.PollOnceAsync();
        Assert.True(context.Control.StopRequested);
    }

    [Fact]
    public async Task StartedWatcherPicksUpACommandWrittenLater()
    {
        var context = await StartRunAsync();
        var watcher = new CommandWatcher(
            context.Invocation.Command, RunId, new CommandProcessor(context.Control, context.Status),
            context.Commands, context.Status, TimeSpan.FromMilliseconds(50));
        watcher.Start();

        await WriteCommandsAsync(_commandFile, RunId, Command(1, "Pause"));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!context.Control.IsPaused && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        await watcher.StopAsync();
        Assert.True(context.Control.IsPaused);
        Assert.Equal(1, context.Status.LastCommandSeq);
    }

    // ---- orchestrator honours the control state ----

    [Fact]
    public async Task ToolDisabledByCommandBeforeItStartsShowsAsDisabled()
    {
        var context = await StartRunAsync();
        context.Control.DisableTool("Snyk", "vendor outage");

        var results = await NewOrchestrator().RunAsync(context);

        var snyk = context.Status.Snapshot().Steps.Single(s => s.Name == "Snyk");
        Assert.Equal(ToolRunState.Disabled, snyk.Status);
        Assert.Equal("vendor outage", snyk.Message);
        Assert.Single(results, r => r.Tool == "Snyk" && r.Disabled);
    }

    [Fact]
    public async Task StopBeforeTheScanPhaseLeavesTheScannersPending()
    {
        var context = await StartRunAsync();
        var scannersStarted = new List<string>();
        var orchestrator = new RunOrchestrator(
            new LambdaTool("Rapid7"), new LambdaTool("CheckMarx"),
            new LambdaTool("RepoClone", () => context.Control.RequestStop()),
            [new LambdaTool("Semgrep", () => { lock (scannersStarted) { scannersStarted.Add("Semgrep"); } })]);

        await orchestrator.RunAsync(context);

        Assert.Empty(scannersStarted);
        Assert.Equal(ToolRunState.Pending, context.Status.Snapshot().Steps.Single(s => s.Name == "Semgrep").Status);
        Assert.Equal(ToolRunState.Completed, context.Status.Snapshot().Steps.Single(s => s.Name == "RepoClone").Status);
    }

    [Fact]
    public async Task PausedRunHoldsTheScanPhaseUntilResumed()
    {
        var context = await StartRunAsync();
        var semgrepStarted = false;
        var orchestrator = new RunOrchestrator(
            new LambdaTool("Rapid7"), new LambdaTool("CheckMarx"),
            new LambdaTool("RepoClone", () => context.Control.Pause()),
            [new LambdaTool("Semgrep", () => semgrepStarted = true)]);

        var run = orchestrator.RunAsync(context);
        await Task.Delay(200);
        Assert.False(semgrepStarted);
        Assert.False(run.IsCompleted);

        context.Control.Resume();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(semgrepStarted);
    }

    [Fact]
    public async Task AbortCancelsTheRun()
    {
        var context = await StartRunAsync();
        var orchestrator = new RunOrchestrator(
            new LambdaTool("Rapid7"), new LambdaTool("CheckMarx"),
            new LambdaTool("RepoClone", () => context.Control.RequestAbort(), waitForAbort: true),
            []);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrator.RunAsync(context));
    }


    private static RunOrchestrator NewOrchestrator() => new(
        new LambdaTool("Rapid7"), new LambdaTool("CheckMarx"), new LambdaTool("RepoClone"),
        [new LambdaTool("TruffleHog"), new LambdaTool("Semgrep"), new LambdaTool("Snyk"),
         new LambdaTool("SonarStandard"), new LambdaTool("SonarDotNet")]);

    private sealed class LambdaTool(string name, Action? action = null, bool waitForAbort = false) : ITool
    {
        public string Name => name;

        public async Task RunAsync(RunContext context, CancellationToken cancellationToken = default)
        {
            action?.Invoke();
            if (waitForAbort)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }
    }
}
