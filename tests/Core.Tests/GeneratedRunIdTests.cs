namespace Vizient.AutomatedScanner.Tests;

using System.Text.Json;
using System.Text.RegularExpressions;
using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.IO;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;

public class GeneratedRunIdTests : IDisposable
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-14T03:04:05Z");
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
    private readonly string _requestFile;
    private readonly EngineConfig _config;

    public GeneratedRunIdTests()
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

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private async Task WriteRequestJsonAsync(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_requestFile)!);
        await File.WriteAllTextAsync(_requestFile, json);
    }

    private Task<RunContext> StartAsync(DateTimeOffset? at = null) =>
        RunContext.StartAsync(_config, _requestFile, time: new FixedTime(at ?? At));

    [Theory]
    [InlineData("""{ "schemaVersion": 3, "runId": "", "requestedBy": "scheduler" }""")]
    [InlineData("""{ "schemaVersion": 3, "runId": null, "requestedBy": "scheduler" }""")]
    [InlineData("""{ "schemaVersion": 3, "runId": "   ", "requestedBy": "scheduler" }""")]
    [InlineData("""{ "schemaVersion": 3, "requestedBy": "scheduler" }""")]
    public async Task AMissingEmptyOrNullRunIdIsGeneratedFromTheTime(string requestJson)
    {
        await WriteRequestJsonAsync(requestJson);

        var context = await StartAsync();

        Assert.Equal("2026-10-14T03-04-05Z", context.Request.RunId);
        Assert.Equal(Path.Combine(_config.RunsRoot, "2026-10-14T03-04-05Z"), context.Paths.RunFolder);
        Assert.True(Directory.Exists(context.Paths.RunFolder));
    }

    [Fact]
    public async Task TheGeneratedRunIdIsASafeFolderNameWithTheDateAndTimeToTheSecond()
    {
        await WriteRequestJsonAsync("""{ "runId": "" }""");

        var context = await RunContext.StartAsync(_config, _requestFile);

        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}Z$"), context.Request.RunId);
        Assert.True(SafeName.IsValid(context.Request.RunId));
        Assert.DoesNotContain('/', context.Request.RunId);
        Assert.DoesNotContain(':', context.Request.RunId);
    }

    [Fact]
    public async Task TheTimeIsUtcWhateverTheMachinesZone()
    {
        await WriteRequestJsonAsync("""{ "runId": "" }""");

        var context = await StartAsync(DateTimeOffset.Parse("2026-10-14T23:30:00-05:00"));

        Assert.Equal("2026-10-15T04-30-00Z", context.Request.RunId);
    }

    [Fact]
    public async Task TheRunFoldersCopyOfTheRequestCarriesTheGeneratedRunIdAndTheInvokersFileIsLeftAlone()
    {
        await WriteRequestJsonAsync("""{ "schemaVersion": 3, "runId": "", "requestedBy": "scheduler" }""");
        var original = await File.ReadAllBytesAsync(_requestFile);

        var context = await StartAsync();

        var copy = await AtomicJsonFile.ReadAsync<RunRequestDocument>(context.Paths.Request);
        Assert.Equal("2026-10-14T03-04-05Z", copy.RunId);
        Assert.Equal("scheduler", copy.RequestedBy);
        Assert.Equal(original, await File.ReadAllBytesAsync(_requestFile));
    }

    [Fact]
    public async Task TheGeneratedRunIdReachesTheStatusAndTheResults()
    {
        await WriteRequestJsonAsync("""{ "runId": "" }""");
        var context = await StartAsync();
        context.Status.Finish(RunState.Completed);
        await AtomicJsonFile.WriteAsync(context.Invocation.Status, context.Status.Snapshot());

        await RunResultsWriter.WriteFinalAsync(context, RunState.Completed, 0, null);

        Assert.Equal("2026-10-14T03-04-05Z", (await AtomicJsonFile.ReadAsync<StatusDocument>(context.Invocation.Status)).RunId);
        var results = await AtomicJsonFile.ReadAsync<ResultsDocument>(context.Invocation.Results);
        Assert.Equal("2026-10-14T03-04-05Z", results.RunId);
        Assert.Equal(context.Paths.RunFolder, results.RunDirectory);
    }

    [Fact]
    public async Task TwoRunsStartedInTheSameSecondGetDifferentFolders()
    {
        await WriteRequestJsonAsync("""{ "runId": "" }""");

        var first = await StartAsync();
        var second = await StartAsync();
        var third = await StartAsync();

        Assert.Equal(
            ["2026-10-14T03-04-05Z", "2026-10-14T03-04-05Z-2", "2026-10-14T03-04-05Z-3"],
            new[] { first, second, third }.Select(c => c.Request.RunId));
        Assert.All(new[] { first, second, third }, c => Assert.True(SafeName.IsValid(c.Request.RunId)));
    }

    [Fact]
    public async Task ARunIdThatWasGivenIsUsedAsItIs()
    {
        await WriteRequestJsonAsync("""{ "runId": "nightly-42" }""");

        var context = await StartAsync();

        Assert.Equal("nightly-42", context.Request.RunId);
    }

    [Theory]
    [InlineData("Resume")]
    [InlineData("RetryFailed")]
    public async Task ContinuingARunNeedsItsRunIdBecauseThereIsNothingToGenerate(string mode)
    {
        await WriteRequestJsonAsync($$"""{ "runId": "", "mode": "{{mode}}", "revision": 2 }""");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => StartAsync());

        Assert.Contains("runId", error.Message);
    }

    [Fact]
    public async Task TheCommandTemplateAndStartupUseTheGeneratedRunId()
    {
        await WriteRequestJsonAsync("""{ "runId": "" }""");
        var context = await StartAsync();

        await Core.Control.CommandTemplate.EnsureAsync(context.Invocation, context.Request.RunId);

        var template = await AtomicJsonFile.ReadAsync<CommandDocument>(context.Invocation.Command);
        Assert.Equal("2026-10-14T03-04-05Z", template.RunId);
    }
}
