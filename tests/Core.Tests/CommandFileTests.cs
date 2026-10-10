namespace Vizient.AutomatedScanner.Tests;

using System.Text.Json;
using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Control;
using Vizient.AutomatedScanner.Core.IO;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.Tools;

public class CommandFileTests : IDisposable
{
    private const string RunId = "2026-10-10T12-00-00Z";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
    private readonly string _requestDir;
    private readonly string _requestFile;
    private readonly string _commandFile;
    private readonly EngineConfig _config;

    public CommandFileTests()
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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<RunContext> StartRunAsync(string runId = RunId, ScanMode mode = ScanMode.Normal, int revision = 1)
    {
        await AtomicJsonFile.WriteAsync(_requestFile, new RunRequestDocument { RunId = runId, Mode = mode, Revision = revision });
        return await RunContext.StartAsync(_config, _requestFile);
    }

    private static CommandEntry Command(long seq, string type = "Pause", string? tool = null) => new()
    {
        Seq = seq,
        Id = $"id-{seq}",
        Type = type,
        IssuedBy = "phil",
        Args = tool is null
            ? new()
            : new() { ["tool"] = JsonSerializer.SerializeToElement(tool) }
    };

    private static Task WriteCommandsAsync(string file, string runId, params CommandEntry[] commands) =>
        AtomicJsonFile.WriteAsync(file, new CommandDocument { RunId = runId, Commands = [.. commands] });

    private CommandWatcher WatcherFor(RunContext context) => new(
        context.Invocation.Command, context.Request.RunId, new CommandProcessor(context.Control, context.Status),
        context.Commands, context.Status, TimeSpan.FromSeconds(1));

    // ---- the template beside the request ----

    [Fact]
    public async Task TemplateIsWrittenBesideTheRequestWithCommentsFirstAndNoCommands()
    {
        var context = await StartRunAsync();

        var written = await CommandTemplate.EnsureAsync(context.Invocation, RunId);

        Assert.True(written);
        var text = await File.ReadAllTextAsync(_commandFile);
        Assert.StartsWith("// command.json", text);
        var firstBrace = text.IndexOf('{');
        Assert.DoesNotContain(text[..firstBrace].Split('\n', StringSplitOptions.RemoveEmptyEntries), line => !line.StartsWith("//"));
        var document = await AtomicJsonFile.ReadAsync<CommandDocument>(_commandFile);
        Assert.Equal(RunId, document.RunId);
        Assert.Empty(document.Commands);
    }

    [Fact]
    public async Task TemplateCommentsListEverySupportedCommandAndTheToolNames()
    {
        var context = await StartRunAsync();
        await CommandTemplate.EnsureAsync(context.Invocation, RunId);

        var text = await File.ReadAllTextAsync(_commandFile);

        foreach (var command in CommandCatalog.All)
        {
            Assert.Contains(command.Type, text);
            Assert.Contains(command.Args, text);
        }

        foreach (var tool in ToolNames.All)
        {
            Assert.Contains(tool, text);
        }

        Assert.Contains("seq", text);
        Assert.Contains("commandAcks", text);
    }

    [Fact]
    public void EveryCatalogCommandIsOneTheEngineAccepts()
    {
        // Keeps the template honest: a command it documents must not come back as an unknown type.
        var control = new RunControl();
        var status = RunStatusTrackerFor();
        var processor = new CommandProcessor(control, status);

        foreach (var info in CommandCatalog.All)
        {
            var ack = processor.Apply(new CommandEntry { Seq = 1, Type = info.Type });
            Assert.DoesNotContain("unknown command type", ack.Message ?? string.Empty);
        }
    }

    [Fact]
    public async Task ACommandFileWithCommentsOnTopReadsBackAndWatcherAppliesItsCommands()
    {
        var context = await StartRunAsync();
        await CommandTemplate.EnsureAsync(context.Invocation, RunId);
        // The invoker adds a command to the commented file, keeping the comments.
        var text = await File.ReadAllTextAsync(_commandFile);
        await File.WriteAllTextAsync(_commandFile, text.Replace(
            "\"commands\": []",
            "\"commands\": [ { \"seq\": 1, \"id\": \"a\", \"type\": \"Pause\", \"args\": {} } ]"));

        await WatcherFor(context).PollOnceAsync();

        Assert.True(context.Control.IsPaused);
        Assert.Equal(1, context.Status.LastCommandSeq);
    }

    [Fact]
    public async Task TemplateReplacesACommandFileLeftByAnotherRun()
    {
        var context = await StartRunAsync();
        await WriteCommandsAsync(_commandFile, "some-other-run", Command(1));

        var written = await CommandTemplate.EnsureAsync(context.Invocation, RunId);

        Assert.True(written);
        Assert.Equal(RunId, (await AtomicJsonFile.ReadAsync<CommandDocument>(_commandFile)).RunId);
    }

    [Fact]
    public async Task TemplateReplacesAnUnreadableCommandFile()
    {
        var context = await StartRunAsync();
        Directory.CreateDirectory(_requestDir);
        await File.WriteAllTextAsync(_commandFile, "{ not json");

        Assert.True(await CommandTemplate.EnsureAsync(context.Invocation, RunId));
        Assert.Empty((await AtomicJsonFile.ReadAsync<CommandDocument>(_commandFile)).Commands);
    }

    [Fact]
    public async Task TemplateKeepsCommandsAlreadyQueuedForThisRun()
    {
        var context = await StartRunAsync();
        await WriteCommandsAsync(_commandFile, RunId, Command(1, "Stop"));

        var written = await CommandTemplate.EnsureAsync(context.Invocation, RunId);

        Assert.False(written);
        Assert.Single((await AtomicJsonFile.ReadAsync<CommandDocument>(_commandFile)).Commands);
    }

    // ---- the record in the run folder ----

    [Fact]
    public async Task EveryCommandReceivedIsKeptInMemoryAndWrittenToTheRunFolderAtTheEnd()
    {
        var context = await StartRunAsync();
        var watcher = WatcherFor(context);
        await WriteCommandsAsync(_commandFile, RunId, Command(1, "Pause"));
        await watcher.PollOnceAsync();
        await WriteCommandsAsync(_commandFile, RunId, Command(1, "Pause"), Command(2, "Resume"), Command(3, "Explode"));
        await watcher.PollOnceAsync();

        Assert.Equal([1L, 2L, 3L], context.Commands.Snapshot().Select(c => c.Seq));
        Assert.False(File.Exists(context.Paths.Command)); // nothing is written until the run ends

        context.Status.Finish(RunState.Completed);
        await AtomicJsonFile.WriteAsync(context.Invocation.Status, context.Status.Snapshot());
        await RunResultsWriter.WriteFinalAsync(context, RunState.Completed, 0, null);

        var recorded = await AtomicJsonFile.ReadAsync<CommandDocument>(context.Paths.Command);
        Assert.Equal(RunId, recorded.RunId);
        Assert.Equal(["Pause", "Resume", "Explode"], recorded.Commands.Select(c => c.Type));
    }

    [Fact]
    public async Task RejectedCommandsAreRecordedToo()
    {
        var context = await StartRunAsync();
        await WriteCommandsAsync(_commandFile, RunId, Command(1, "Explode"));

        await WatcherFor(context).PollOnceAsync();

        Assert.Single(context.Commands.Snapshot());
        Assert.Equal("Rejected", context.Status.Snapshot().CommandAcks.Single().Status);
    }

    [Fact]
    public async Task RunFolderGetsACommandFileEvenWhenNoCommandWasReceived()
    {
        var context = await StartRunAsync();
        context.Status.Finish(RunState.Completed);
        await AtomicJsonFile.WriteAsync(context.Invocation.Status, context.Status.Snapshot());

        await RunResultsWriter.WriteFinalAsync(context, RunState.Completed, 0, null);

        var recorded = await AtomicJsonFile.ReadAsync<CommandDocument>(context.Paths.Command);
        Assert.Equal(RunId, recorded.RunId);
        Assert.Empty(recorded.Commands);
    }

    [Fact]
    public async Task RunFolderRecordIsTheEnginesLogNotACopyOfTheInvokersFile()
    {
        var context = await StartRunAsync();
        await CommandTemplate.EnsureAsync(context.Invocation, RunId);
        var watcher = WatcherFor(context);
        var text = await File.ReadAllTextAsync(_commandFile);
        await File.WriteAllTextAsync(_commandFile, text.Replace(
            "\"commands\": []",
            "\"commands\": [ { \"seq\": 1, \"id\": \"a\", \"type\": \"Pause\", \"args\": {} } ]"));
        await watcher.PollOnceAsync();
        context.Status.Finish(RunState.Completed);
        await AtomicJsonFile.WriteAsync(context.Invocation.Status, context.Status.Snapshot());

        await RunResultsWriter.WriteFinalAsync(context, RunState.Completed, 0, null);

        var recordedText = await File.ReadAllTextAsync(context.Paths.Command);
        Assert.DoesNotContain("//", recordedText);
        Assert.Contains("\"Pause\"", recordedText);
    }

    [Fact]
    public async Task ACommandFileFromAnotherRunIsNotRecorded()
    {
        var context = await StartRunAsync();
        await WriteCommandsAsync(_commandFile, "some-other-run", Command(1, "Abort"));
        await WatcherFor(context).PollOnceAsync();
        context.Status.Finish(RunState.Completed);
        await AtomicJsonFile.WriteAsync(context.Invocation.Status, context.Status.Snapshot());

        await RunResultsWriter.WriteFinalAsync(context, RunState.Completed, 0, null);

        Assert.Empty((await AtomicJsonFile.ReadAsync<CommandDocument>(context.Paths.Command)).Commands);
    }

    [Fact]
    public async Task AResumedRunKeepsTheCommandsEarlierAttemptsRecorded()
    {
        var first = await StartRunAsync();
        await WriteCommandsAsync(_commandFile, RunId, Command(1, "Pause"), Command(2, "Resume"));
        await WatcherFor(first).PollOnceAsync();
        first.Status.Finish(RunState.Stopped);
        await AtomicJsonFile.WriteAsync(first.Invocation.Status, first.Status.Snapshot());
        await RunResultsWriter.WriteFinalAsync(first, RunState.Stopped, 1, null);

        var second = await StartRunAsync(mode: ScanMode.Resume, revision: 2);
        await WriteCommandsAsync(_commandFile, RunId, Command(1, "Pause"), Command(2, "Resume"), Command(3, "Stop"));
        await WatcherFor(second).PollOnceAsync();
        second.Status.Finish(RunState.Stopped);
        await AtomicJsonFile.WriteAsync(second.Invocation.Status, second.Status.Snapshot());
        await RunResultsWriter.WriteFinalAsync(second, RunState.Stopped, 1, null);

        var recorded = await AtomicJsonFile.ReadAsync<CommandDocument>(second.Paths.Command);
        Assert.Equal([1L, 2L, 3L], recorded.Commands.Select(c => c.Seq));
        Assert.Equal(["Pause", "Resume", "Stop"], recorded.Commands.Select(c => c.Type));
    }

    [Fact]
    public void LogIgnoresASeqItAlreadyHolds()
    {
        var log = new CommandLog();

        log.Add(Command(1, "Pause"));
        log.Add(Command(1, "Stop"));

        Assert.Equal("Pause", Assert.Single(log.Snapshot()).Type);
    }

    private static Core.Status.RunStatusTracker RunStatusTrackerFor() =>
        Core.Status.RunStatusTracker.CreateAsync(
            new RunPaths(Path.Combine(Path.GetTempPath(), "nowhere", RunId)),
            new InvocationPaths(Path.Combine(Path.GetTempPath(), "nowhere")),
            new RunRequestDocument { RunId = RunId }).GetAwaiter().GetResult();
}
