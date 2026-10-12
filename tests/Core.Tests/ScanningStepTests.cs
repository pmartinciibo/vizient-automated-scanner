namespace Vizient.AutomatedScanner.Tests;

using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Scan;
using Vizient.AutomatedScanner.Core.Status;
using Vizient.AutomatedScanner.Core.Tools;
using Vizient.AutomatedScanner.Core.Work;

/// <summary>status.json shows the five scanners as one step, "Scanning", counted in repos.</summary>
public class ScanningStepTests : IDisposable
{
    private readonly ScanWorld _world = new();

    public void Dispose() => _world.Dispose();

    private async Task WorldWithReposAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _world.AddOrigin(("readme.txt", $"repo {i + 1}"));
        }

        await _world.CloneAsync();
    }

    private ToolProgress Scanning() =>
        _world.Context.Status.PublishedSnapshot().Steps.Single(s => s.Name == "Scanning");

    private static ProbeTool Probe(string name, Func<RepoWorkContext, RepoWorkResult?>? decide = null, TimeSpan? work = null) =>
        new(name, decide: decide, work: work);

    [Fact]
    public async Task ThePublishedStatusHasOneScanningStepInPlaceOfTheFiveScanners()
    {
        await WorldWithReposAsync(1);

        var names = _world.Context.Status.PublishedSnapshot().Steps.Select(s => s.Name).ToList();

        Assert.Equal(["Rapid7", "CheckMarx", "RepoClone", "Scanning"], names);
        // The tools keep their own steps inside, for the orchestrator.
        Assert.Equal(ToolNames.All, _world.Context.Status.Snapshot().Steps.Select(s => s.Name));
    }

    [Fact]
    public async Task TheOverallProgressIsCountedOverThePublishedSteps()
    {
        await WorldWithReposAsync(1);

        var overall = _world.Context.Status.PublishedSnapshot().Overall;

        Assert.Equal(4, overall.StepsTotal);
    }

    [Fact]
    public async Task EveryRepoIsLookedAtBeforeTheFirstScanSoTheTotalIsKnownWhenItStarts()
    {
        await WorldWithReposAsync(4);
        ToolProgress? atFirstScan = null;
        var tool = Probe(ToolNames.Semgrep, decide: w =>
        {
            atFirstScan ??= w.Run.Status.PublishedSnapshot().Steps.Single(s => s.Name == "Scanning");
            return null;
        });

        await _world.ScanAsync([tool]);

        Assert.NotNull(atFirstScan);
        Assert.Equal(4, atFirstScan.Total);
        Assert.Equal(0, atFirstScan.Skipped);
    }

    [Fact]
    public async Task ReposNoToolNeedsToScanAreCountedAsSkippedNotInTheTotal()
    {
        _world.With(s => s with { Skip = new SkipSettings { CanaryPercent = 0, FullRescanEveryDays = 365 } });
        _world.WithTool(ToolNames.Semgrep, t => t with { Version = "1", RulesVersion = "1" });
        await WorldWithReposAsync(3);
        await _world.ScanAsync([Probe(ToolNames.Semgrep)]);
        _world.Origins[1].Commit(("new.txt", "change"));
        await _world.NextRunAsync();

        await _world.ScanAsync([Probe(ToolNames.Semgrep)]);

        var scanning = Scanning();
        Assert.Equal((1, 1, 0, 2), (scanning.Total, scanning.Done, scanning.Failed, scanning.Skipped));
        Assert.Equal(ToolRunState.Completed, scanning.Status);
    }

    [Fact]
    public async Task WhileScanningTheStepNamesTheRepoAndCountsHowManyAreDone()
    {
        _world.With(s => s with { Scan = new ScanSettings { MaxParallelRepos = 1 } });
        await WorldWithReposAsync(3);
        var seen = new List<string?>();
        var tool = Probe(ToolNames.Semgrep, decide: w =>
        {
            seen.Add(w.Run.Status.PublishedSnapshot().Steps.Single(s => s.Name == "Scanning").Message);
            return null;
        });

        await _world.ScanAsync([tool]);

        Assert.Contains("0 of 3 processed; now: GitHub acme/repo1", seen);
        Assert.Contains("1 of 3 processed; now: GitHub acme/repo2", seen);
        Assert.Contains("2 of 3 processed; now: GitHub acme/repo3", seen);
    }

    [Fact]
    public async Task ARepoWithSeveralToolsRunningCountsOnceAndIsNamedOnce()
    {
        await WorldWithReposAsync(1);
        ToolProgress? during = null;
        var gate = new TaskCompletionSource();
        var started = 0;
        RepoWorkResult? Wait(RepoWorkContext w)
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                during = w.Run.Status.PublishedSnapshot().Steps.Single(s => s.Name == "Scanning");
                gate.SetResult();
            }

            gate.Task.Wait(TimeSpan.FromSeconds(20));
            return null;
        }

        await _world.ScanAsync([Probe(ToolNames.TruffleHog, Wait), Probe(ToolNames.Semgrep, Wait)]);

        Assert.NotNull(during);
        Assert.Equal(1, during.Running);
        Assert.Equal("0 of 1 processed; now: GitHub acme/repo1", during.Message);
    }

    [Fact]
    public async Task ARepoThatFailedInTwoToolsCountsAsOneFailedRepo()
    {
        await WorldWithReposAsync(3);
        RepoWorkResult? Boom(RepoWorkContext w) => w.Repo.Key == "gh-2" ? RepoWorkResult.Failed("ToolError", "boom") : null;

        await _world.ScanAsync([Probe(ToolNames.TruffleHog, Boom), Probe(ToolNames.Semgrep, Boom)]);

        var scanning = Scanning();
        Assert.Equal((3, 2, 1), (scanning.Total, scanning.Done, scanning.Failed));
        Assert.Equal(ToolRunState.CompletedWithFlags, scanning.Status);
        Assert.Equal("3 of 3 repos processed, 1 failed", scanning.Message);
    }

    [Fact]
    public async Task WhenEveryScannerIsDisabledScanningIsDisabledWithTheReason()
    {
        await WorldWithReposAsync(1);

        Assert.Equal(ToolRunState.Pending, Scanning().Status);
    }

    [Fact]
    public async Task AToolThatCouldNotPrepareIsNamedInTheScanningMessage()
    {
        await WorldWithReposAsync(2);
        var broken = new ProbeTool(ToolNames.Snyk, prepare: _ => throw new InvalidOperationException("snyk is not installed"));

        await _world.ScanAsync([broken, Probe(ToolNames.Semgrep)]);

        var scanning = Scanning();
        Assert.Equal(ToolRunState.CompletedWithFlags, scanning.Status);
        Assert.Contains("Snyk failed: snyk is not installed", scanning.Message);
    }

    [Fact]
    public async Task AStopSaysHowManyReposWereNotStarted()
    {
        _world.With(s => s with { Scan = new ScanSettings { MaxParallelRepos = 1 } });
        await WorldWithReposAsync(5);
        var tool = Probe(ToolNames.Semgrep, work: TimeSpan.FromMilliseconds(100));
        var scan = _world.ScanAsync([tool]);

        for (var i = 0; i < 2000 && tool.Calls.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        _world.Context.Control.RequestStop();
        await scan;

        Assert.Matches(@"^stopped; \d+ repos not started$", Scanning().Message);
    }

    [Fact]
    public async Task TheStatusFileHasTheOneScanningStep()
    {
        await WorldWithReposAsync(1);
        var file = Path.Combine(_world.Root, "status-out.json");
        var writer = new StatusWriter(file, _world.Context.Status, TimeSpan.FromMilliseconds(50));

        await writer.StartAsync();
        await writer.StopAsync();

        var written = await Vizient.AutomatedScanner.Core.Serialization.AtomicJsonFile.ReadAsync<StatusDocument>(file);
        Assert.Equal(["Rapid7", "CheckMarx", "RepoClone", "Scanning"], written.Steps.Select(s => s.Name));
    }

    [Fact]
    public async Task WithoutTheScanPhaseTheStepAddsUpTheToolsSoTheToolsRunAloneStillShow()
    {
        await WorldWithReposAsync(2);

        await Probe(ToolNames.Semgrep).RunAsync(_world.Context);

        var scanning = Scanning();
        Assert.Equal((2, 2), (scanning.Total, scanning.Done));
    }
}
