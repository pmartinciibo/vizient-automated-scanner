namespace Vizient.AutomatedScanner.Tests;

using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.IO;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;

public class InputFileTests : IDisposable
{
    private const string RunId = "2026-10-10T12-00-00Z";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
    private readonly EngineConfig _config;
    private readonly string _runFolder;
    private readonly string _commandFile;

    public InputFileTests()
    {
        _runFolder = Path.Combine(_root, "runs", RunId);
        _commandFile = Path.Combine(_root, "requests", "command.json");
        Directory.CreateDirectory(_root);
        _config = new EngineConfig(
            Path.Combine(_root, "engine.json"),
            Path.Combine(_root, "repos"),
            Path.Combine(_root, "runs"),
            Path.Combine(_root, "state"),
            Path.Combine(_root, "tmp"),
            Path.Combine(_root, "logs"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // The request lives outside the run folder, as an external invoker would leave it.
    private async Task<string> WriteRequestAsync(RunRequestDocument request)
    {
        var path = Path.Combine(_root, "requests", $"{Guid.NewGuid():N}.json");
        await AtomicJsonFile.WriteAsync(path, request);
        return path;
    }

    [Fact]
    public async Task EngineConfigResolvesRelativePathsAgainstTheConfigFile()
    {
        var configFile = Path.Combine(_root, "engine.json");
        await File.WriteAllTextAsync(configFile, """
            { "schemaVersion": 3, "paths": { "reposRoot": "repos", "runs": "..\\runs", "state": "state", "tmp": "tmp", "logs": "logs" } }
            """);

        var config = await EngineConfig.LoadAsync(configFile, CancellationToken.None);

        Assert.Equal(Path.Combine(_root, "repos"), config.ReposRoot);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(_root)!, "runs"), config.RunsRoot);
    }

    [Fact]
    public async Task EngineConfigRejectsMissingPath()
    {
        var configFile = Path.Combine(_root, "engine.json");
        await File.WriteAllTextAsync(configFile, """{ "schemaVersion": 3, "paths": { "runs": "runs" } }""");

        await Assert.ThrowsAsync<InvalidDataException>(() => EngineConfig.LoadAsync(configFile, CancellationToken.None));
    }

    [Fact]
    public async Task RunContextLoadsRequestAndHonorsDisabledTools()
    {
        var requestFile = await WriteRequestAsync(new RunRequestDocument
        {
            RunId = RunId,
            Tools = new() { ["Snyk"] = new() { Enabled = false } }
        });

        var context = await RunContext.StartAsync(_config, requestFile);

        Assert.False(context.IsEnabled("Snyk"));
        Assert.True(context.IsEnabled("Semgrep"));
    }

    [Fact]
    public async Task StartCreatesRunFolderFromRunIdAndCopiesRequest()
    {
        var requestFile = await WriteRequestAsync(new RunRequestDocument { RunId = RunId, RequestedBy = "scheduler" });

        var context = await RunContext.StartAsync(_config, requestFile);

        Assert.Equal(_runFolder, context.Paths.RunFolder);
        Assert.True(Directory.Exists(_runFolder));
        var copy = await AtomicJsonFile.ReadAsync<RunRequestDocument>(context.Paths.Request);
        Assert.Equal("scheduler", copy.RequestedBy);
        Assert.True(File.Exists(requestFile));
    }

    [Fact]
    public async Task NewRunRefusesAnExistingRunFolder()
    {
        Directory.CreateDirectory(_runFolder);
        var requestFile = await WriteRequestAsync(new RunRequestDocument { RunId = RunId });

        await Assert.ThrowsAsync<InvalidDataException>(() => RunContext.StartAsync(_config, requestFile));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    public async Task RunIdMustBeASafeFolderName(string runId)
    {
        var requestFile = await WriteRequestAsync(new RunRequestDocument { RunId = runId });

        await Assert.ThrowsAsync<InvalidDataException>(() => RunContext.StartAsync(_config, requestFile));
    }

    [Fact]
    public async Task RunContextRejectsUnknownToolName()
    {
        var requestFile = await WriteRequestAsync(new RunRequestDocument
        {
            RunId = RunId,
            Tools = new() { ["Semgrp"] = new() { Enabled = false } }
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => RunContext.StartAsync(_config, requestFile));
    }

    [Fact]
    public async Task StartRequiresRequestFile()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => RunContext.StartAsync(_config, Path.Combine(_root, "missing.json")));
    }

    [Fact]
    public async Task ResumeNeedsExistingRunFolder()
    {
        var requestFile = await WriteRequestAsync(new RunRequestDocument { RunId = RunId, Mode = ScanMode.Resume, Revision = 2 });

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => RunContext.StartAsync(_config, requestFile));
    }

    [Fact]
    public async Task ResumeReusesRunFolderAndRequiresHigherRevision()
    {
        await RunContext.StartAsync(_config, await WriteRequestAsync(new RunRequestDocument { RunId = RunId, Revision = 1 }));

        var sameRevision = await WriteRequestAsync(new RunRequestDocument { RunId = RunId, Mode = ScanMode.Resume, Revision = 1 });
        await Assert.ThrowsAsync<InvalidDataException>(() => RunContext.StartAsync(_config, sameRevision));

        var next = await WriteRequestAsync(new RunRequestDocument { RunId = RunId, Mode = ScanMode.RetryFailed, Revision = 2 });
        var context = await RunContext.StartAsync(_config, next);

        Assert.Equal(_runFolder, context.Paths.RunFolder);
        Assert.Equal(2, (await AtomicJsonFile.ReadAsync<RunRequestDocument>(context.Paths.Request)).Revision);
    }

    [Fact]
    public async Task CommandReaderReturnsNothingWhenFileIsMissing()
    {
        var commands = await CommandReader.ReadNewAsync(_commandFile, RunId, lastCommandSeq: 0);

        Assert.Empty(commands);
    }

    [Fact]
    public async Task CommandReaderReturnsOnlyNewCommandsInSeqOrder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "requests"));
        await File.WriteAllTextAsync(_commandFile, $$"""
            { "schemaVersion": 3, "runId": "{{RunId}}", "commands": [
              { "seq": 3, "id": "c", "type": "Resume", "issuedAt": "2026-10-10T05:11:00Z", "issuedBy": "phil", "args": {} },
              { "seq": 1, "id": "a", "type": "SetConcurrency", "issuedAt": "2026-10-10T04:30:00Z", "issuedBy": "phil",
                "args": { "tool": "TruffleHog", "maxParallel": 3 } },
              { "seq": 2, "id": "b", "type": "Pause", "issuedAt": "2026-10-10T04:45:00Z", "issuedBy": "phil", "args": {} }
            ] }
            """);

        var commands = await CommandReader.ReadNewAsync(_commandFile, RunId, lastCommandSeq: 1);

        Assert.Equal([2L, 3L], commands.Select(c => c.Seq));
        Assert.Equal("Pause", commands[0].Type);
    }
}
