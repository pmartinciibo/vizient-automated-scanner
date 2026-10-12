namespace Vizient.AutomatedScanner.Tests;

using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Scan;
using Vizient.AutomatedScanner.Core.Tools;
using Vizient.AutomatedScanner.Core.Work;

public class ScanPhaseTests : IDisposable
{
    private readonly ScanWorld _world = new();

    public void Dispose() => _world.Dispose();

    private async Task<ScanWorld> WorldWithReposAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _world.AddOrigin(("readme.txt", $"repo {i + 1}"));
        }

        await _world.CloneAsync();
        return _world;
    }

    private static ProbeTool Trufflehog(Func<RepoWorkContext, RepoWorkResult?>? decide = null, TimeSpan? work = null, bool needsCopy = true) =>
        new(ToolNames.TruffleHog, needsCopy, decide: decide, work: work);

    private static ProbeTool Semgrep(Func<RepoWorkContext, RepoWorkResult?>? decide = null, TimeSpan? work = null, int findings = 0) =>
        new(ToolNames.Semgrep, decide: decide, work: work, findings: findings);

    [Fact]
    public async Task OneWorkingCopyIsMadePerRepoAndEveryToolScansThatSameCopy()
    {
        await WorldWithReposAsync(3);
        var a = Trufflehog();
        var b = Semgrep();

        var results = await _world.ScanAsync([a, b]);

        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.Equal(["gh-1", "gh-2", "gh-3"], a.Repos);
        Assert.Equal(["gh-1", "gh-2", "gh-3"], b.Repos);
        foreach (var key in a.Repos)
        {
            var first = a.Calls.Single(c => c.Repo == key);
            var second = b.Calls.Single(c => c.Repo == key);
            Assert.Equal(first.WorkingPath, second.WorkingPath);
            Assert.True(first.SawFiles && second.SawFiles);
        }

        Assert.Equal(3, a.Calls.Select(c => c.WorkingPath).Distinct().Count());
    }

    [Fact]
    public async Task EveryWorkingCopyIsDeletedWhenTheScanIsOver()
    {
        await WorldWithReposAsync(3);

        await _world.ScanAsync([Trufflehog(), Semgrep()]);

        Assert.Empty(_world.LeftoverCopies());
    }

    [Fact]
    public async Task OneRepoAtATimeMeansTheCopyIsGoneBeforeTheNextOneIsMade()
    {
        _world.With(s => s with { Scan = new ScanSettings { MaxParallelRepos = 1 } });
        await WorldWithReposAsync(4);
        var tool = Trufflehog(work: TimeSpan.FromMilliseconds(30));

        await _world.ScanAsync([tool]);

        Assert.Equal(4, tool.Calls.Count);
        Assert.All(tool.Calls, call => Assert.Equal(1, call.LiveCopies)); // never the previous repo's copy as well
    }

    [Fact]
    public async Task NoMoreReposAreOnDiskAtOnceThanTheLimitAllows()
    {
        _world.With(s => s with { Scan = new ScanSettings { MaxParallelRepos = 2 } });
        await WorldWithReposAsync(8);
        var slow = Trufflehog(work: TimeSpan.FromMilliseconds(80));
        var fast = Semgrep();

        await _world.ScanAsync([slow, fast]);

        Assert.Equal(8, slow.Calls.Count);
        Assert.All(slow.Calls.Concat(fast.Calls), call => Assert.InRange(call.LiveCopies, 1, 2));
        Assert.Empty(_world.LeftoverCopies());
    }

    [Fact]
    public async Task ASlowToolDoesNotHoldBackTheOtherToolsFromTheirRepos()
    {
        _world.With(s => s with { Scan = new ScanSettings { MaxParallelRepos = 8 } });
        _world.WithTool(ToolNames.Semgrep, t => t with { MaxParallel = 1 });
        await WorldWithReposAsync(4);
        var slow = Semgrep(work: TimeSpan.FromMilliseconds(60));
        var fast = Trufflehog();

        await _world.ScanAsync([slow, fast]);

        Assert.Equal(4, slow.Calls.Count);
        Assert.Equal(1, slow.MaxConcurrent); // the tool's own limit holds
        Assert.Equal(4, fast.Calls.Count);
    }

    [Fact]
    public async Task EachToolKeepsToItsOwnParallelLimitAcrossRepos()
    {
        _world.WithTool(ToolNames.TruffleHog, t => t with { MaxParallel = 2 });
        _world.With(s => s with { Scan = new ScanSettings { MaxParallelRepos = 6 } });
        await WorldWithReposAsync(6);
        var tool = Trufflehog(work: TimeSpan.FromMilliseconds(60));

        await _world.ScanAsync([tool]);

        Assert.Equal(6, tool.Calls.Count);
        Assert.InRange(tool.MaxConcurrent, 1, 2);
    }

    [Fact]
    public async Task AToolThatFailsOnOneRepoIsFlaggedForThatRepoAndTheOthersCarryOn()
    {
        await WorldWithReposAsync(3);
        var bad = Trufflehog(decide: w => w.Repo.Key == "gh-2" ? RepoWorkResult.Failed("ToolError", "boom") : null);
        var good = Semgrep();

        var results = await _world.ScanAsync([bad, good]);

        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.True(_world.Failed("TruffleHog", 2));
        Assert.True(_world.Done("TruffleHog", 1));
        Assert.True(_world.Done("TruffleHog", 3));
        Assert.True(_world.Done("Semgrep", 2));
        Assert.Equal(1, _world.Step("TruffleHog").Failed);
        Assert.Equal(ToolRunState.CompletedWithFlags, _world.Step("TruffleHog").Status);
        Assert.Equal(ToolRunState.Completed, _world.Step("Semgrep").Status);
        Assert.Empty(_world.LeftoverCopies());
    }

    [Fact]
    public async Task AToolThatThrowsIsAFailedMarkerNotAFailedPhase()
    {
        await WorldWithReposAsync(2);
        var bad = Trufflehog(decide: w => throw new InvalidOperationException("kaboom"));

        var results = await _world.ScanAsync([bad, Semgrep()]);

        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.True(_world.Failed("TruffleHog", 1));
        Assert.Contains("kaboom", _world.FailedText("TruffleHog", 1));
        Assert.True(_world.Done("Semgrep", 1));
    }

    [Fact]
    public async Task AToolThatCannotPrepareFailsAloneAndAtOnceWithItsMessage()
    {
        await WorldWithReposAsync(2);
        var broken = new ProbeTool(ToolNames.Snyk, prepare: _ => throw new InvalidOperationException("snyk is not installed"));
        var fine = Semgrep();

        var results = await _world.ScanAsync([broken, fine]);

        Assert.Equal("snyk is not installed", results.Single(r => r.Tool == "Snyk").Error!.Message);
        Assert.True(results.Single(r => r.Tool == "Semgrep").Succeeded);
        Assert.Empty(broken.Calls);
        Assert.Equal(2, fine.Calls.Count);
        Assert.Equal(ToolRunState.Failed, _world.Step("Snyk").Status);
    }


    [Fact]
    public async Task ToolsThatReadTheMirrorDirectlyGetNoWorkingCopy()
    {
        await WorldWithReposAsync(2);
        var tool = Trufflehog(needsCopy: false);

        await _world.ScanAsync([tool]);

        Assert.Equal(2, tool.Calls.Count);
        Assert.All(tool.Calls, c => Assert.Null(c.WorkingPath));
        Assert.All(tool.Calls, c => Assert.Equal(0, c.LiveCopies));
    }

    [Fact]
    public async Task ALateNotApplicableIsCountedAsSkippedNotScanned()
    {
        await WorldWithReposAsync(3);
        var tool = Trufflehog(decide: w => w.Repo.Key == "gh-2"
            ? RepoWorkResult.Skipped(new ToolSkippedMarker { Reason = RepoEligibility.NotApplicable })
            : null);

        await _world.ScanAsync([tool]);

        Assert.True(_world.Skipped("TruffleHog", 2));
        Assert.Equal(2, _world.Step("TruffleHog").Done);
        Assert.Equal(1, _world.Step("TruffleHog").Skipped);
        Assert.Equal(2, _world.Step("TruffleHog").Total);
    }

    [Fact]
    public async Task ReposWhoseCloneFailedAreNotScanned()
    {
        await WorldWithReposAsync(2);
        // The second repo's remote is gone, so its update fails and it is flagged unreachable.
        Directory.Move(_world.Origins[1].Path, _world.Origins[1].Path + "-gone");
        await _world.NextRunAsync();
        var tool = Trufflehog();

        await _world.ScanAsync([tool]);

        Assert.Equal(["gh-1"], tool.Repos);
        Assert.Contains("StatusUnreachable", _world.SkippedReason("TruffleHog", 2));
    }

    [Fact]
    public async Task UnchangedReposAreSkippedWithoutBeingOpenedAndAChangedOneIsScanned()
    {
        _world.With(s => s with { Skip = new SkipSettings { CanaryPercent = 0, FullRescanEveryDays = 365 } });
        _world.WithTool(ToolNames.Semgrep, t => t with { Version = "1", RulesVersion = "1" });
        await WorldWithReposAsync(3);
        await _world.ScanAsync([Semgrep(findings: 1)]);
        // A first pass counts as a full pass, which is what allows skipping afterwards.
        _world.Origins[1].Commit(("new.txt", "change"));
        await _world.NextRunAsync();
        var probe = Semgrep(findings: 1);

        await _world.ScanAsync([probe]);

        Assert.Equal(["gh-2"], probe.Repos);
        Assert.True(_world.Skipped("Semgrep", 1));
        Assert.Contains("Unchanged", _world.SkippedReason("Semgrep", 1));
        Assert.True(_world.Done("Semgrep", 2));
    }

    [Fact]
    public async Task CopiesAreDeletedEvenWhenTheRunIsAborted()
    {
        _world.With(s => s with { Scan = new ScanSettings { MaxParallelRepos = 3 } });
        await WorldWithReposAsync(3);
        using var cancellation = new CancellationTokenSource();
        var tool = Trufflehog(work: TimeSpan.FromSeconds(30), decide: null);
        var phase = new ScanPhase([tool], TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(2));

        var scan = _world.ScanAsync(phase, cancellation.Token);
        await WaitUntilAsync(() => tool.Calls.Count == 3);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        Assert.Empty(_world.LeftoverCopies());
    }

    [Fact]
    public async Task AStopCommandStartsNoNewReposAndLetsTheOnesInFlightFinish()
    {
        _world.With(s => s with { Scan = new ScanSettings { MaxParallelRepos = 1 } });
        await WorldWithReposAsync(5);
        var tool = Trufflehog(work: TimeSpan.FromMilliseconds(100));
        var scan = _world.ScanAsync([tool]);

        await WaitUntilAsync(() => tool.Calls.Count >= 1);
        _world.Context.Control.RequestStop();
        await scan;

        Assert.InRange(tool.Calls.Count, 1, 3);
        Assert.Empty(_world.LeftoverCopies());
        Assert.Contains("not started", _world.Step("TruffleHog").Message);
        Assert.Equal(tool.Calls.Count, _world.Step("TruffleHog").Done);
    }

    [Fact]
    public async Task ADisableToolCommandStopsOnlyThatTool()
    {
        _world.With(s => s with { Scan = new ScanSettings { MaxParallelRepos = 1 } });
        await WorldWithReposAsync(5);
        var disabled = Trufflehog(work: TimeSpan.FromMilliseconds(80));
        var other = Semgrep();
        var scan = _world.ScanAsync([disabled, other]);

        await WaitUntilAsync(() => disabled.Calls.Count >= 1);
        _world.Context.Control.DisableTool("TruffleHog", "vendor down");
        await scan;

        Assert.InRange(disabled.Calls.Count, 1, 3);
        Assert.Equal(5, other.Calls.Count);
        Assert.Equal(ToolRunState.Disabled, _world.Step("TruffleHog").Status);
        Assert.Contains("vendor down", _world.Step("TruffleHog").Message);
        Assert.Equal(ToolRunState.Completed, _world.Step("Semgrep").Status);
    }

    [Fact]
    public async Task APauseStartsNoNewReposUntilResumed()
    {
        _world.With(s => s with { Scan = new ScanSettings { MaxParallelRepos = 1 } });
        await WorldWithReposAsync(4);
        var tool = Trufflehog(work: TimeSpan.FromMilliseconds(50));
        _world.Context.Control.Pause();
        var scan = _world.ScanAsync([tool]);

        await Task.Delay(300);
        Assert.Empty(tool.Calls);
        _world.Context.Control.Resume();
        await scan;

        Assert.Equal(4, tool.Calls.Count);
    }

    private async Task<ScanResult[]> ScanWithMaxReposAsync(int? maxRepos, ScanToolBase tool)
    {
        var limited = _world.Context with { Request = _world.Context.Request with { Scan = new RequestScan { MaxRepos = maxRepos } } };
        var phase = new ScanPhase([tool], TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(2));
        foreach (var member in phase.Tools)
        {
            await member.RunAsync(limited);
        }

        return [];
    }

    [Fact]
    public async Task TheScanStopsAfterMaxReposAndLeavesTheRestUntouched()
    {
        await WorldWithReposAsync(4);
        var tool = Trufflehog();

        await ScanWithMaxReposAsync(2, tool);

        Assert.Equal(["gh-1", "gh-2"], tool.Repos);
        Assert.Equal(2, _world.Step("TruffleHog").Total);
        Assert.Equal(0, _world.Step("TruffleHog").Skipped);
    }

    [Fact]
    public async Task AnEmptyMaxReposScansEveryRepo()
    {
        await WorldWithReposAsync(3);
        var tool = Trufflehog();

        await ScanWithMaxReposAsync(null, tool);

        Assert.Equal(["gh-1", "gh-2", "gh-3"], tool.Repos);
    }

    [Fact]
    public async Task ARepoThatIsSkippedDoesNotUseUpAPlaceUnderMaxRepos()
    {
        await WorldWithReposAsync(4);
        _world.Context.Request.Scope.ExcludeRepos.Add("gh-1");
        var tool = Trufflehog();

        await ScanWithMaxReposAsync(2, tool);

        Assert.Equal(["gh-2", "gh-3"], tool.Repos);
    }

    [Fact]
    public async Task ASkipRepoCommandSkipsThatRepoForEveryTool()
    {
        await WorldWithReposAsync(3);
        _world.Context.Control.SkipRepo("gh-2");
        var a = Trufflehog();
        var b = Semgrep();

        await _world.ScanAsync([a, b]);

        Assert.Equal(["gh-1", "gh-3"], a.Repos);
        Assert.Equal(["gh-1", "gh-3"], b.Repos);
        Assert.Contains("SkippedByCommand", _world.SkippedReason("TruffleHog", 2));
        Assert.Contains("SkippedByCommand", _world.SkippedReason("Semgrep", 2));
    }

    [Fact]
    public async Task ReposOutsideTheRequestScopeAreSkippedForEveryTool()
    {
        await WorldWithReposAsync(3);
        _world.Context.Request.Scope.ExcludeRepos.Add("gh-3");
        var a = Trufflehog();

        await _world.ScanAsync([a]);

        Assert.Equal(["gh-1", "gh-2"], a.Repos);
        Assert.Contains("ExcludedByRequest", _world.SkippedReason("TruffleHog", 3));
    }

    [Fact]
    public async Task ASetConcurrencyCommandChangesAToolsParallelismWhileItRuns()
    {
        _world.With(s => s with { Scan = new ScanSettings { MaxParallelRepos = 6 } });
        _world.WithTool(ToolNames.TruffleHog, t => t with { MaxParallel = 1 });
        await WorldWithReposAsync(6);
        _world.Context.Control.SetMaxParallel("TruffleHog", 3);
        var tool = Trufflehog(work: TimeSpan.FromMilliseconds(600));

        await _world.ScanAsync([tool]);

        Assert.Equal(6, tool.Calls.Count);
        Assert.InRange(tool.MaxConcurrent, 2, 3);
    }

    [Fact]
    public async Task EveryToolsCountsAreInTheStatusAndCleanScansAreRecordedInTheState()
    {
        await WorldWithReposAsync(2);

        await _world.ScanAsync([Trufflehog(), Semgrep(findings: 4)]);

        Assert.Equal((2, 2, 0, 0), (_world.Step("Semgrep").Total, _world.Step("Semgrep").Done, _world.Step("Semgrep").Failed, _world.Step("Semgrep").Skipped));
        var state = _world.Context.FoundState.Get("Semgrep", "gh-1");
        Assert.NotNull(state);
        Assert.Equal(4, state.FindingCount);
        Assert.Equal(_world.Repo(1).HeadSha, state.Sha);
        Assert.Contains("\"findingCount\": 4", _world.DoneText("Semgrep", 1));
    }

    [Fact]
    public async Task ResumeDoesNotRepeatReposThatAreDone()
    {
        await WorldWithReposAsync(3);
        var first = Trufflehog();
        await _world.ScanAsync([first]);
        Assert.Equal(3, first.Calls.Count);

        // Resume in the same run folder: every repo already has its marker.
        var resumed = _world.Context with { Request = _world.Context.Request with { Mode = ScanMode.Resume } };
        var second = Trufflehog();
        var phase = new ScanPhase([second], TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(2));
        foreach (var member in phase.Tools)
        {
            await member.RunAsync(resumed);
        }

        Assert.Empty(second.Calls);
        Assert.Equal(3, _world.Step("TruffleHog").Done);
    }

    [Fact]
    public async Task ARunWithNoEnabledScannersFinishesAtOnceWithoutOpeningAnything()
    {
        await WorldWithReposAsync(2);

        var results = await _world.ScanAsync(new ScanPhase([], TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50)));

        Assert.Empty(results);
        Assert.Empty(_world.LeftoverCopies());
    }

    [Fact]
    public async Task WhileAToolRunsAloneItsStatusNamesTheSourceOfTheRepoItIsOn()
    {
        await WorldWithReposAsync(1);
        string? seen = null;
        var tool = Semgrep(decide: w =>
        {
            seen = w.Run.Status.Snapshot().Steps.Single(s => s.Name == "Semgrep").Message;
            return null;
        });

        await tool.RunAsync(_world.Context);

        Assert.Equal("0 of 1 processed; now: GitHub acme/repo1", seen);
    }

    [Fact]
    public async Task RunningAToolAloneMakesAndRemovesItsOwnWorkingCopyForEachRepo()
    {
        await WorldWithReposAsync(2);
        var tool = Semgrep();

        await tool.RunAsync(_world.Context);

        Assert.Equal(2, tool.Calls.Count);
        Assert.All(tool.Calls, c => Assert.True(c.SawFiles));
        Assert.Empty(_world.LeftoverCopies());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the scan to reach this point");
            await Task.Delay(10);
        }
    }
}
