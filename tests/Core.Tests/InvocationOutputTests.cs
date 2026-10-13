namespace Vizient.AutomatedScanner.Tests;

using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.IO;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.Tools;

public class InvocationOutputTests : IDisposable
{
    private const string RunId = "2026-10-10T12-00-00Z";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
    private readonly string _requestDir;
    private readonly string _requestFile;
    private readonly EngineConfig _config;

    public InvocationOutputTests()
    {
        _requestDir = Path.Combine(_root, "requests");
        _requestFile = Path.Combine(_requestDir, "request.json");
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

    private Task WriteRequestAsync(RunRequestDocument? request = null) =>
        AtomicJsonFile.WriteAsync(_requestFile, request ?? new RunRequestDocument { RunId = RunId, RequestedBy = "scheduler" });

    [Fact]
    public async Task RequestIsCopiedByteForByteIntoTheRunFolderAndLeftInPlace()
    {
        await WriteRequestAsync();
        var original = await File.ReadAllBytesAsync(_requestFile);

        var context = await RunContext.StartAsync(_config, _requestFile);

        Assert.Equal(original, await File.ReadAllBytesAsync(context.Paths.Request));
        Assert.Equal(original, await File.ReadAllBytesAsync(_requestFile));
    }

    [Fact]
    public async Task FinalResultsAreWrittenBesideTheRequestAndNameTheRunDirectory()
    {
        await WriteRequestAsync();
        var context = await RunContext.StartAsync(_config, _requestFile);
        context.Status.StepFinished("Rapid7", ToolRunState.Completed);
        context.Status.Finish(RunState.Completed);
        await AtomicJsonFile.WriteAsync(context.Invocation.Status, context.Status.Snapshot());

        await RunResultsWriter.WriteFinalAsync(context, RunState.Completed, exitCode: 0, error: null);

        var results = await AtomicJsonFile.ReadAsync<ResultsDocument>(Path.Combine(_requestDir, "results.json"));
        Assert.Equal(context.Paths.RunFolder, results.RunDirectory);
        Assert.Equal(RunId, results.RunId);
        Assert.Equal(RunState.Completed, results.State);
        Assert.Equal(0, results.ExitCode);
        // Every step has its own entry and its own file; the scanners together are also summed as "Scanning".
        Assert.Equal(ToolNames.All, results.Tools.Select(t => t.Name));
        Assert.Equal(ToolRunState.Completed, results.Tools.Single(t => t.Name == "Rapid7").Status);
        Assert.Equal("Scanning", results.Scanning?.Name);
        foreach (var tool in ToolNames.All)
        {
            Assert.True(File.Exists(Path.Combine(context.Paths.RunFolder, "results", tool + ".json")), tool);
        }
    }

    [Fact]
    public async Task FinalStatusAndResultsAreCopiedIntoTheRunFolder()
    {
        await WriteRequestAsync();
        var context = await RunContext.StartAsync(_config, _requestFile);
        context.Status.Finish(RunState.CompletedWithFlags);
        await AtomicJsonFile.WriteAsync(context.Invocation.Status, context.Status.Snapshot());

        await RunResultsWriter.WriteFinalAsync(context, RunState.CompletedWithFlags, exitCode: 1, error: null);

        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(_requestDir, "status.json")),
            await File.ReadAllBytesAsync(context.Paths.Status));
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(_requestDir, "results.json")),
            await File.ReadAllBytesAsync(context.Paths.ResultsSummary));
        Assert.Empty(Directory.GetFiles(context.Paths.RunFolder, "*.tmp"));
    }

    [Fact]
    public async Task OutputsFromAnEarlierRunAreOverwritten()
    {
        await WriteRequestAsync();
        await File.WriteAllTextAsync(Path.Combine(_requestDir, "results.json"), "old");
        await File.WriteAllTextAsync(Path.Combine(_requestDir, "status.json"), "old");
        var context = await RunContext.StartAsync(_config, _requestFile);
        context.Status.Finish(RunState.Completed);
        await AtomicJsonFile.WriteAsync(context.Invocation.Status, context.Status.Snapshot());

        await RunResultsWriter.WriteFinalAsync(context, RunState.Completed, 0, null);

        Assert.Equal(RunId, (await AtomicJsonFile.ReadAsync<ResultsDocument>(context.Invocation.Results)).RunId);
        Assert.Equal(RunId, (await AtomicJsonFile.ReadAsync<StatusDocument>(context.Invocation.Status)).RunId);
    }

    [Fact]
    public async Task ClearStaleResultsRemovesOldResultsOnly()
    {
        await WriteRequestAsync();
        await File.WriteAllTextAsync(Path.Combine(_requestDir, "results.json"), "old");

        RunResultsWriter.ClearStaleResults(_requestFile);

        Assert.False(File.Exists(Path.Combine(_requestDir, "results.json")));
        Assert.True(File.Exists(_requestFile));
    }

    [Fact]
    public async Task StartupFailureLeavesResultsWithTheReasonAndNoRunDirectory()
    {
        await WriteRequestAsync();
        await File.WriteAllTextAsync(Path.Combine(_requestDir, "status.json"), "stale");

        await RunResultsWriter.WriteStartupFailureAsync(_requestFile, "engine.json not found");

        var results = await AtomicJsonFile.ReadAsync<ResultsDocument>(Path.Combine(_requestDir, "results.json"));
        Assert.Equal(RunState.Failed, results.State);
        Assert.Equal(2, results.ExitCode);
        Assert.Equal("engine.json not found", results.Error);
        Assert.Null(results.RunDirectory);
        Assert.Equal(_requestFile, results.RequestFile);
        Assert.False(File.Exists(Path.Combine(_requestDir, "status.json")));
    }

    [Fact]
    public async Task StartupFailureWithMissingRequestFolderDoesNotThrow()
    {
        await RunResultsWriter.WriteStartupFailureAsync(Path.Combine(_root, "nowhere", "request.json"), "x");

        Assert.False(Directory.Exists(Path.Combine(_root, "nowhere")));
    }

    [Fact]
    public async Task ResumeContinuesAttemptFromLiveStatusWhenTheRunFolderHasNone()
    {
        await WriteRequestAsync(new RunRequestDocument { RunId = RunId, Revision = 1 });
        await RunContext.StartAsync(_config, _requestFile);

        // The first attempt died after writing live status but before copying it to the run folder.
        await AtomicJsonFile.WriteAsync(
            Path.Combine(_requestDir, "status.json"),
            new StatusDocument { RunId = RunId, Attempt = 1, LastCommandSeq = 4 });
        await WriteRequestAsync(new RunRequestDocument { RunId = RunId, Revision = 2, Mode = ScanMode.Resume });

        var context = await RunContext.StartAsync(_config, _requestFile);

        var status = context.Status.Snapshot();
        Assert.Equal(2, status.Attempt);
        Assert.Equal(4, status.LastCommandSeq);
    }

    [Fact]
    public async Task LiveStatusFromADifferentRunIsNotContinued()
    {
        await WriteRequestAsync(new RunRequestDocument { RunId = RunId, Revision = 1 });
        await RunContext.StartAsync(_config, _requestFile);
        await AtomicJsonFile.WriteAsync(
            Path.Combine(_requestDir, "status.json"),
            new StatusDocument { RunId = "some-other-run", Attempt = 5, LastCommandSeq = 9 });
        await WriteRequestAsync(new RunRequestDocument { RunId = RunId, Revision = 2, Mode = ScanMode.Resume });

        var status = (await RunContext.StartAsync(_config, _requestFile)).Status.Snapshot();

        Assert.Equal(1, status.Attempt);
        Assert.Equal(0, status.LastCommandSeq);
    }

    [Fact]
    public async Task AnImportsStepFileListsItsApplicationsWithRecordCounts()
    {
        await WriteRequestAsync();
        var context = await RunContext.StartAsync(_config, _requestFile);
        Directory.CreateDirectory(context.Paths.ResultsDir("Rapid7"));
        // Bigger than the 1 MB read buffer, so the file is read in pieces.
        var padding = new string('x', 1_500_000);
        await File.WriteAllTextAsync(
            context.Paths.ResultFile("Rapid7", "rapid7"),
            "{\"generatedAt\":\"t\",\"apps\":[" +
            "{\"id\":\"a1\",\"name\":\"Alpha\",\"scans\":[{\"id\":1},{\"id\":2}],\"vulnerabilities\":[{\"d\":\"" + padding + "\",\"n\":[1,2]}]}," +
            "{\"id\":null,\"scans\":[],\"vulnerabilities\":[{},{},{}]}]}");
        context.Status.Finish(RunState.Completed);
        await AtomicJsonFile.WriteAsync(context.Invocation.Status, context.Status.Snapshot());

        await RunResultsWriter.WriteFinalAsync(context, RunState.Completed, 0, null);

        var file = await AtomicJsonFile.ReadAsync<ToolResultDocument>(context.Paths.ToolResult("Rapid7"));
        Assert.Equal(2, file.Items.Count);
        Assert.Equal("a1", file.Items[0].Key);
        Assert.Equal("Alpha", file.Items[0].Name);
        Assert.Equal(2, file.Items[0].Counts!["scans"]);
        Assert.Equal(1, file.Items[0].Counts!["vulnerabilities"]);
        Assert.Equal("(unassigned)", file.Items[1].Key);
        Assert.Equal(0, file.Items[1].Counts!["scans"]);
        Assert.Equal(3, file.Items[1].Counts!["vulnerabilities"]);
    }

    [Fact]
    public async Task ResultsListEveryErrorTheRunMet()
    {
        await WriteRequestAsync();
        var context = await RunContext.StartAsync(_config, _requestFile);
        context.Repos.Record(new RepoOutcome { Key = "gh-1", Source = "GitHub", Org = "acme", Name = "widgets" });
        await context.Markers.WriteFailedAsync("RepoClone", "gh-1", new ToolFailedMarker { Category = "CloneFailed", Attempts = 2, Message = "fatal: repository not found" });
        await context.Markers.WriteFailedAsync("Semgrep", "gh-2", new ToolFailedMarker { Category = "Timeout", Attempts = 1, Message = "timed out" });
        await context.Markers.WriteDoneAsync("Semgrep", "gh-2", new ToolDoneMarker { Sha = "abc" });
        context.Errors.Record(RunErrorScope.Org, "401 Unauthorized", "RepoClone", "OrgDiscoveryFailed", "ADO", "vizientinc");
        context.Status.Finish(RunState.Failed);
        await AtomicJsonFile.WriteAsync(context.Invocation.Status, context.Status.Snapshot());

        await RunResultsWriter.WriteFinalAsync(context, RunState.Failed, 2, "boom");

        var results = await AtomicJsonFile.ReadAsync<ResultsDocument>(context.Invocation.Results);
        Assert.Equal(3, results.ErrorCount);
        Assert.Equal(3, results.Errors.Count);

        var repo = Assert.Single(results.Errors, e => e.Scope == "Repo");
        Assert.Equal(("RepoClone", "CloneFailed", "gh-1", "widgets", "acme", 2), (repo.Tool, repo.Category, repo.Key, repo.Repo, repo.Org, repo.Attempts));
        Assert.Equal("fatal: repository not found", repo.Message);

        var org = Assert.Single(results.Errors, e => e.Scope == "Org");
        Assert.Equal(("ADO", "vizientinc"), (org.Source, org.Org));

        // A repo that failed and then succeeded on a retry is not an error any more.
        Assert.DoesNotContain(results.Errors, e => e.Key == "gh-2");
        Assert.Equal("boom", Assert.Single(results.Errors, e => e.Scope == "Run").Message);
    }
}
