namespace Vizient.AutomatedScanner.Tests;

using System.Text.Json;
using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.Skip;
using Vizient.AutomatedScanner.Core.State;
using Vizient.AutomatedScanner.Core.Tools;
using Vizient.AutomatedScanner.Core.Work;

// Runs scanners one after another against the same state folder, the way successive scheduled scans would.
public class SkipIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
    private readonly string _requestFile;
    private int _runCounter;

    public SkipIntegrationTests()
    {
        _requestFile = Path.Combine(_root, "requests", "request.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- helpers ----

    private string StatePath => Path.Combine(_root, "state", FoundOrgAppRepoStore.FileName);

    private static EngineSettings Settings(bool skipEnabled = true, params (string Tool, ToolSettings Settings)[] tools) => new()
    {
        Skip = new SkipSettings { Enabled = skipEnabled, CanaryPercent = 0, FullRescanEveryDays = 30 },
        Tools = tools.ToDictionary(t => t.Tool, t => t.Settings)
    };

    private static (string, ToolSettings) Pinned(string tool = "Semgrep", string? mode = null, bool? verify = null) =>
        (tool, new ToolSettings { Version = "1.x", RulesVersion = "rules-1", Mode = mode, Verify = verify });

    private EngineConfig Config(EngineSettings settings) => new(
        Path.Combine(_root, "engine.json"),
        Path.Combine(_root, "repos"),
        Path.Combine(_root, "runs"),
        Path.Combine(_root, "state"),
        Path.Combine(_root, "tmp"),
        Path.Combine(_root, "logs"),
        Settings: settings);

    private async Task<RunContext> NewRunAsync(
        EngineSettings settings,
        IEnumerable<RepoOutcome> repos,
        ScanMode mode = ScanMode.Normal,
        int? canaryPercent = null)
    {
        await AtomicJsonFile.WriteAsync(_requestFile, new RunRequestDocument
        {
            RunId = $"run-{++_runCounter}",
            Mode = mode,
            Skip = new SkipOverrides { CanaryPercent = canaryPercent }
        });
        var context = await RunContext.StartAsync(Config(settings), _requestFile);
        foreach (var repo in repos)
        {
            context.Repos.Record(repo);
        }

        return context;
    }

    private static RepoOutcome Repo(int n, string? sha = null, RepositoryStatus status = RepositoryStatus.Active) => new()
    {
        Key = $"gh-{n:D3}",
        Source = "GitHub",
        Org = "RnD-Vizient",
        Name = $"repo{n}",
        LocalPath = $"C:\\repos\\repo{n}",
        ExistedLocally = true,
        ExistedRemotely = true,
        RemoteStatus = status,
        HeadSha = sha ?? $"sha-{n}"
    };

    private static List<RepoOutcome> Repos(int count) => Enumerable.Range(1, count).Select(n => Repo(n)).ToList();

    private static ToolProgress Step(RunContext context, string tool = "Semgrep") =>
        context.Status.Snapshot().Steps.Single(s => s.Name == tool);

    private sealed class Probe(string name = "Semgrep", Func<RepoWorkContext, int>? findings = null, Func<RepoWorkContext, bool>? fails = null)
        : ScanToolBase
    {
        private readonly List<string> _scanned = new();
        public override string Name => name;
        public IReadOnlyList<string> Scanned { get { lock (_scanned) { return _scanned.ToList(); } } }
        protected override Random Random => new(1);

        protected override Task<RepoWorkResult> ScanRepoAsync(RepoWorkContext work, CancellationToken cancellationToken)
        {
            lock (_scanned)
            {
                _scanned.Add(work.Repo.Key);
            }

            if (fails?.Invoke(work) == true)
            {
                return Task.FromResult(RepoWorkResult.Failed("ToolError", "boom"));
            }

            return Task.FromResult(RepoWorkResult.Done(new ToolDoneMarker
            {
                StartedAt = DateTimeOffset.UtcNow,
                EndedAt = DateTimeOffset.UtcNow,
                FindingCount = findings?.Invoke(work) ?? 3
            }));
        }
    }

    private static async Task<FoundOrgAppRepoDocument> ReadStateAsync(string path) =>
        await AtomicJsonFile.ReadAsync<FoundOrgAppRepoDocument>(path);

    // ---- the first run ----

    [Fact]
    public async Task TheFirstRunScansEveryRepoAndFillsInTheKeyOnEachDoneMarker()
    {
        var settings = Settings(true, Pinned());
        var context = await NewRunAsync(settings, Repos(3));
        var probe = new Probe();

        await probe.RunAsync(context);

        Assert.Equal(["gh-001", "gh-002", "gh-003"], probe.Scanned.Order());
        var marker = await AtomicJsonFile.ReadAsync<ToolDoneMarker>(context.Paths.DoneMarker("Semgrep", "gh-002"));
        Assert.Equal(("sha-2", "worktree", "1.x", "rules-1", false), (marker.Sha, marker.ScanMode, marker.ToolVersion, marker.RulesVersion, marker.Canary));
        Assert.Equal(64, marker.ConfigHash.Length);
        Assert.Equal(3, marker.FindingCount);
    }

    [Fact]
    public async Task TheScanStateIsWrittenToTheStateFolderWithWhatEachCleanScanFound()
    {
        var settings = Settings(true, Pinned());
        var context = await NewRunAsync(settings, Repos(2));

        await new Probe(findings: w => w.Repo.Key == "gh-001" ? 11 : 4).RunAsync(context);

        var state = await ReadStateAsync(StatePath);
        var first = state.ScanOf("gh-001")!;
        Assert.Equal(("sha-1", "worktree", "1.x", "rules-1", 11), (first.Sha, first.ScanMode, first.ToolVersion, first.RulesVersion, first.FindingCount));
        Assert.Equal(4, state.ScanOf("gh-002")!.FindingCount);
        Assert.NotNull(state.Tools["Semgrep"].LastFullScanAt);
        Assert.False(state.Tools["Semgrep"].ForceFull);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(StatePath)!, "*.tmp"));
    }

    // ---- skipping on the next run ----

    [Fact]
    public async Task ASecondRunSkipsUnchangedReposAndWritesAnUnchangedMarkerForEach()
    {
        var settings = Settings(true, Pinned());
        await new Probe().RunAsync(await NewRunAsync(settings, Repos(3)));

        var second = await NewRunAsync(settings, Repos(3));
        var probe = new Probe();
        await probe.RunAsync(second);

        Assert.Empty(probe.Scanned);
        var marker = await AtomicJsonFile.ReadAsync<ToolSkippedMarker>(second.Paths.SkippedMarker("Semgrep", "gh-002"));
        Assert.Equal(("Unchanged", "sha-2"), (marker.Reason, marker.Sha));
        Assert.Equal(("worktree", "1.x", "rules-1"), (marker.Key!.ScanMode, marker.Key.ToolVersion, marker.Key.RulesVersion));
        Assert.NotNull(marker.LastScannedAt);
        Assert.False(File.Exists(second.Paths.DoneMarker("Semgrep", "gh-002")));
        var step = Step(second);
        Assert.Equal((0, 0, 3), (step.Total, step.Done, step.Skipped));
    }

    [Fact]
    public async Task OnlyTheReposWhoseCommitChangedAreScannedAgain()
    {
        var settings = Settings(true, Pinned());
        await new Probe().RunAsync(await NewRunAsync(settings, Repos(4)));

        var repos = Repos(4);
        repos[2] = Repo(3, sha: "sha-3-new-commit");
        var second = await NewRunAsync(settings, repos);
        var probe = new Probe();
        await probe.RunAsync(second);

        Assert.Equal(["gh-003"], probe.Scanned);
        Assert.Equal("sha-3-new-commit", (await ReadStateAsync(StatePath)).ScanOf("gh-003")!.Sha);
        Assert.Equal("sha-1", (await ReadStateAsync(StatePath)).ScanOf("gh-001")!.Sha);
        var step = Step(second);
        Assert.Equal((1, 1, 3), (step.Total, step.Done, step.Skipped));
    }

    [Fact]
    public async Task ARepoNewSinceTheLastRunIsScanned()
    {
        var settings = Settings(true, Pinned());
        await new Probe().RunAsync(await NewRunAsync(settings, Repos(2)));

        var probe = new Probe();
        await probe.RunAsync(await NewRunAsync(settings, Repos(3)));

        Assert.Equal(["gh-003"], probe.Scanned);
    }

    [Theory]
    [InlineData("toolVersion")]
    [InlineData("rulesVersion")]
    [InlineData("mode")]
    public async Task ChangingAPinnedValueInEngineJsonRescansEverything(string changed)
    {
        await new Probe().RunAsync(await NewRunAsync(Settings(true, Pinned()), Repos(3)));

        var newSettings = changed switch
        {
            "toolVersion" => Settings(true, ("Semgrep", new ToolSettings { Version = "2.x", RulesVersion = "rules-1" })),
            "rulesVersion" => Settings(true, ("Semgrep", new ToolSettings { Version = "1.x", RulesVersion = "rules-2" })),
            _ => Settings(true, ("Semgrep", new ToolSettings { Version = "1.x", RulesVersion = "rules-1", Mode = "git" }))
        };
        var probe = new Probe();
        await probe.RunAsync(await NewRunAsync(newSettings, Repos(3)));

        Assert.Equal(3, probe.Scanned.Count);
    }

    [Fact]
    public async Task ChangingTheRulePacksRescansEverythingButChangingParallelismDoesNot()
    {
        ToolSettings Semgrep(string[] packs, int parallel) => new()
        {
            Version = "1.x", RulesVersion = "rules-1", Packs = [.. packs], MaxParallel = parallel
        };

        await new Probe().RunAsync(await NewRunAsync(Settings(true, ("Semgrep", Semgrep(["p/default"], 8))), Repos(3)));

        var tuned = new Probe();
        await tuned.RunAsync(await NewRunAsync(Settings(true, ("Semgrep", Semgrep(["p/default"], 2))), Repos(3)));
        Assert.Empty(tuned.Scanned);

        var newPacks = new Probe();
        await newPacks.RunAsync(await NewRunAsync(Settings(true, ("Semgrep", Semgrep(["p/default", "p/owasp-top-ten"], 8))), Repos(3)));
        Assert.Equal(3, newPacks.Scanned.Count);
    }

    // ---- when it must not skip ----

    [Fact]
    public async Task FullModeScansEverythingAndNoteThatItWasAFullPass()
    {
        var settings = Settings(true, Pinned());
        await new Probe().RunAsync(await NewRunAsync(settings, Repos(3)));

        var probe = new Probe();
        var full = await NewRunAsync(settings, Repos(3), ScanMode.Full);
        await probe.RunAsync(full);

        Assert.Equal(3, probe.Scanned.Count);
        Assert.Equal(3, Step(full).Done);
    }

    [Fact]
    public async Task TurningSkippingOffInEngineJsonScansEverything()
    {
        await new Probe().RunAsync(await NewRunAsync(Settings(true, Pinned()), Repos(2)));

        var probe = new Probe();
        await probe.RunAsync(await NewRunAsync(Settings(false, Pinned()), Repos(2)));

        Assert.Equal(2, probe.Scanned.Count);
    }

    [Fact]
    public async Task AToolWithNoPinnedVersionsIsNeverSkippedBecauseItsKeyIsUnknown()
    {
        var settings = Settings(true);
        await new Probe().RunAsync(await NewRunAsync(settings, Repos(2)));

        var probe = new Probe();
        await probe.RunAsync(await NewRunAsync(settings, Repos(2)));

        Assert.Equal(2, probe.Scanned.Count);
    }

    [Fact]
    public async Task ARepoWhoseCheckedOutShaIsUnknownIsScannedEvenWhenTheRestAreSkipped()
    {
        var settings = Settings(true, Pinned());
        await new Probe().RunAsync(await NewRunAsync(settings, Repos(3)));

        var repos = Repos(3);
        repos[1] = repos[1] with { HeadSha = null };
        var probe = new Probe();
        await probe.RunAsync(await NewRunAsync(settings, repos));

        Assert.Equal(["gh-002"], probe.Scanned);
    }

    [Fact]
    public async Task SnykInDependencyModeIsNeverSkipped()
    {
        var settings = Settings(true, Pinned("Snyk", mode: "dependencies"));
        await new Probe("Snyk").RunAsync(await NewRunAsync(settings, Repos(3)));

        var probe = new Probe("Snyk");
        await probe.RunAsync(await NewRunAsync(settings, Repos(3)));

        Assert.Equal(3, probe.Scanned.Count);
    }

    [Fact]
    public async Task SnykInCodeModeIsSkippedLikeTheOthers()
    {
        var settings = Settings(true, Pinned("Snyk", mode: "code"));
        await new Probe("Snyk").RunAsync(await NewRunAsync(settings, Repos(3)));

        var probe = new Probe("Snyk");
        await probe.RunAsync(await NewRunAsync(settings, Repos(3)));

        Assert.Empty(probe.Scanned);
    }

    [Fact]
    public async Task TruffleHogWithVerificationOnIsNeverSkipped()
    {
        var settings = Settings(true, Pinned("TruffleHog", mode: "filesystem", verify: true));
        await new Probe("TruffleHog").RunAsync(await NewRunAsync(settings, Repos(2)));

        var probe = new Probe("TruffleHog");
        await probe.RunAsync(await NewRunAsync(settings, Repos(2)));

        Assert.Equal(2, probe.Scanned.Count);
    }

    // ---- scheduled full rescans ----

    private async Task WriteStateAsync(DateTimeOffset? lastFullScanAt, bool forceFull = false, int repos = 3)
    {
        var key = ToolScanKey.For(Config(Settings(true, Pinned())), "Semgrep");
        var stored = Repos(repos).Select(repo => new StoredRepo(
            repo,
            new Dictionary<string, RepositoryScanState>
            {
                ["Semgrep"] = new()
                {
                    Sha = repo.HeadSha!, ScanMode = key.ScanMode, ToolVersion = key.ToolVersion,
                    RulesVersion = key.RulesVersion, ConfigHash = key.ConfigHash,
                    ScannedAt = DateTimeOffset.UtcNow.AddDays(-5), FindingCount = 3
                }
            }));
        var tools = new Dictionary<string, ToolInventoryState>
        {
            ["Semgrep"] = new() { LastFullScanAt = lastFullScanAt, ForceFull = forceFull }
        };

        await new FoundOrgAppRepoStore(Path.Combine(_root, "state")).SaveAsync(
            FoundSnapshotBuilder.ToDocument("earlier-run", DateTimeOffset.UtcNow, tools, stored));
    }

    [Fact]
    public async Task AFullRescanDueByTheCalendarScansEveryRepoAndThenResetsTheClock()
    {
        await WriteStateAsync(DateTimeOffset.UtcNow.AddDays(-31));
        var probe = new Probe();
        await probe.RunAsync(await NewRunAsync(Settings(true, Pinned()), Repos(3)));

        Assert.Equal(3, probe.Scanned.Count);
        Assert.True((await ReadStateAsync(StatePath)).Tools["Semgrep"].LastFullScanAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task ARecentFullPassMeansUnchangedReposAreSkipped()
    {
        await WriteStateAsync(DateTimeOffset.UtcNow.AddDays(-1));
        var probe = new Probe();
        await probe.RunAsync(await NewRunAsync(Settings(true, Pinned()), Repos(3)));

        Assert.Empty(probe.Scanned);
    }

    [Fact]
    public async Task APassThatSkippedReposDoesNotCountAsAFullPass()
    {
        var lastFull = DateTimeOffset.UtcNow.AddDays(-2);
        await WriteStateAsync(lastFull);

        await new Probe().RunAsync(await NewRunAsync(Settings(true, Pinned()), Repos(3)));

        Assert.Equal(lastFull, (await ReadStateAsync(StatePath)).Tools["Semgrep"].LastFullScanAt);
    }

    [Fact]
    public async Task AFlaggedFullRescanScansEverythingThenClearsTheFlag()
    {
        await WriteStateAsync(DateTimeOffset.UtcNow.AddDays(-1), forceFull: true);

        var probe = new Probe();
        await probe.RunAsync(await NewRunAsync(Settings(true, Pinned()), Repos(3)));

        Assert.Equal(3, probe.Scanned.Count);
        Assert.False((await ReadStateAsync(StatePath)).Tools["Semgrep"].ForceFull);
        var next = new Probe();
        await next.RunAsync(await NewRunAsync(Settings(true, Pinned()), Repos(3)));
        Assert.Empty(next.Scanned);
    }

    // ---- canaries ----

    [Fact]
    public async Task CanariesAreScannedAnywayAndMarkedAsSuch()
    {
        var settings = Settings(true, Pinned());
        await new Probe().RunAsync(await NewRunAsync(settings, Repos(4)));

        var second = await NewRunAsync(settings, Repos(4), canaryPercent: 50);
        var probe = new Probe();
        await probe.RunAsync(second);

        Assert.Equal(2, probe.Scanned.Count);
        Assert.All(probe.Scanned, key =>
            Assert.True(AtomicJsonFile.ReadAsync<ToolDoneMarker>(second.Paths.DoneMarker("Semgrep", key)).Result.Canary));
        var step = Step(second);
        Assert.Equal((2, 2, 2, 2), (step.Total, step.Done, step.Skipped, step.Canaries));
    }

    [Fact]
    public async Task ACanaryWhoseFindingCountChangedUnderTheSameCommitRaisesAnAlertAndForcesAFullRescan()
    {
        var settings = Settings(true, Pinned());
        await new Probe(findings: _ => 5).RunAsync(await NewRunAsync(settings, Repos(2)));

        var second = await NewRunAsync(settings, Repos(2), canaryPercent: 100);
        await new Probe(findings: _ => 9).RunAsync(second);

        var alerts = second.Status.Snapshot().Alerts.Where(a => a.Type == "CanaryMismatch").ToList();
        Assert.Equal(2, alerts.Count);
        Assert.All(alerts, a => Assert.Equal(("Semgrep", 5, 9), (a.Tool, a.Was, a.Now)));
        Assert.Equal(["gh-001", "gh-002"], alerts.Select(a => a.Repo).Order());
        // The flag survives this run, even though every repo was scanned in it.
        Assert.True((await ReadStateAsync(StatePath)).Tools["Semgrep"].ForceFull);

        var third = new Probe(findings: _ => 9);
        var thirdRun = await NewRunAsync(settings, Repos(2));
        await third.RunAsync(thirdRun);
        Assert.Equal(2, third.Scanned.Count); // the forced full rescan
        Assert.False((await ReadStateAsync(StatePath)).Tools["Semgrep"].ForceFull);

        var fourth = new Probe();
        await fourth.RunAsync(await NewRunAsync(settings, Repos(2)));
        Assert.Empty(fourth.Scanned); // back to normal
    }

    [Fact]
    public async Task ACanaryWithTheSameFindingCountRaisesNothing()
    {
        var settings = Settings(true, Pinned());
        await new Probe(findings: _ => 5).RunAsync(await NewRunAsync(settings, Repos(2)));

        var second = await NewRunAsync(settings, Repos(2), canaryPercent: 100);
        await new Probe(findings: _ => 5).RunAsync(second);

        Assert.DoesNotContain(second.Status.Snapshot().Alerts, a => a.Type == "CanaryMismatch");
        Assert.False((await ReadStateAsync(StatePath)).Tools["Semgrep"].ForceFull);
    }

    [Fact]
    public async Task AChangedFindingCountOnACommitThatChangedIsNotAMismatch()
    {
        var settings = Settings(true, Pinned());
        await new Probe(findings: _ => 5).RunAsync(await NewRunAsync(settings, Repos(2)));

        var repos = Repos(2);
        repos[0] = Repo(1, sha: "new-commit");
        var second = await NewRunAsync(settings, repos, canaryPercent: 0);
        await new Probe(findings: _ => 9).RunAsync(second);

        Assert.DoesNotContain(second.Status.Snapshot().Alerts, a => a.Type == "CanaryMismatch");
    }

    // ---- state only after a clean finish ----

    [Fact]
    public async Task AFailedRepoHasNoStateSoTheNextRunScansItAgain()
    {
        var settings = Settings(true, Pinned());
        var first = await NewRunAsync(settings, Repos(3));
        await new Probe(fails: w => w.Repo.Key == "gh-002").RunAsync(first);

        var state = await ReadStateAsync(StatePath);
        Assert.False(state.HasRepo("gh-002"));
        Assert.True(state.HasRepo("gh-001"));

        var probe = new Probe();
        await probe.RunAsync(await NewRunAsync(settings, Repos(3)));
        Assert.Equal(["gh-002"], probe.Scanned);
    }

    [Fact]
    public async Task AFailedRescanLeavesTheEarlierStateAsItWas()
    {
        var settings = Settings(true, Pinned());
        await new Probe(findings: _ => 5).RunAsync(await NewRunAsync(settings, Repos(2)));
        var before = (await ReadStateAsync(StatePath)).ScanOf("gh-001")!;

        var repos = Repos(2);
        repos[0] = Repo(1, sha: "new-commit");
        await new Probe(fails: w => w.Repo.Key == "gh-001").RunAsync(await NewRunAsync(settings, repos));

        Assert.Equal(before, (await ReadStateAsync(StatePath)).ScanOf("gh-001")!);
    }

    [Fact]
    public async Task AFailedRunStillCountsAsAFullPassBecauseEveryRepoWasTried()
    {
        var settings = Settings(true, Pinned());
        await new Probe(fails: w => w.Repo.Key == "gh-001").RunAsync(await NewRunAsync(settings, Repos(3)));

        Assert.NotNull((await ReadStateAsync(StatePath)).Tools["Semgrep"].LastFullScanAt);
    }

    private sealed class Blocking : ScanToolBase
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override string Name => "Semgrep";

        protected override async Task<RepoWorkResult> ScanRepoAsync(RepoWorkContext work, CancellationToken cancellationToken)
        {
            if (work.Repo.Key != "gh-001")
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return RepoWorkResult.Done(new ToolDoneMarker { StartedAt = DateTimeOffset.UtcNow, EndedAt = DateTimeOffset.UtcNow, FindingCount = 1 });
        }
    }

    [Fact]
    public async Task WhatFinishedBeforeAnAbortIsSavedAndTheRunIsNotAFullPass()
    {
        var settings = Settings(true, Pinned());
        var context = await NewRunAsync(settings, Repos(3));
        var blocking = new Blocking();
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(context.Control.AbortToken);
        var run = blocking.RunAsync(context, abort.Token);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // gh-001 finished; wait for its state to be recorded, then abort.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (context.FoundState.Get("Semgrep", "gh-001") is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        context.Control.RequestAbort();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var state = await ReadStateAsync(StatePath);
        Assert.True(state.HasRepo("gh-001"));
        Assert.False(state.HasRepo("gh-002"));
        Assert.Null(state.Tools.GetValueOrDefault("Semgrep")?.LastFullScanAt);
    }

    [Fact]
    public async Task AStoppedRunIsNotAFullPass()
    {
        var settings = Settings(true, Pinned());
        var context = await NewRunAsync(settings, Repos(3));
        context.Control.RequestStop();

        await new Probe().RunAsync(context);

        Assert.Null((await ReadStateAsync(StatePath)).Tools.GetValueOrDefault("Semgrep")?.LastFullScanAt);
    }

    // ---- what is not part of skipping ----

    [Fact]
    public async Task ReposThatAreOutOfScopeAreNeverCanariesOrCounted()
    {
        var settings = Settings(true, Pinned());
        var repos = Repos(3);
        repos.Add(Repo(4, status: RepositoryStatus.Archived));
        await new Probe().RunAsync(await NewRunAsync(settings, repos));

        var second = await NewRunAsync(settings, repos, canaryPercent: 100);
        var probe = new Probe();
        await probe.RunAsync(second);

        Assert.Equal(["gh-001", "gh-002", "gh-003"], probe.Scanned.Order());
        Assert.Equal(3, Step(second).Canaries);
        Assert.Equal("StatusArchived", (await AtomicJsonFile.ReadAsync<ToolSkippedMarker>(second.Paths.SkippedMarker("Semgrep", "gh-004"))).Reason);
    }

    [Fact]
    public async Task TwoScannersShareOneStateFileWithoutLosingEachOthersEntries()
    {
        var settings = Settings(true, Pinned("Semgrep"), Pinned("TruffleHog"));
        var context = await NewRunAsync(settings, Repos(50));

        await Task.WhenAll(new Probe("Semgrep").RunAsync(context), new Probe("TruffleHog").RunAsync(context));

        var state = await ReadStateAsync(StatePath);
        Assert.Equal(50, state.ToStoredRepos().Count(r => r.Scan.ContainsKey("Semgrep")));
        Assert.Equal(50, state.ToStoredRepos().Count(r => r.Scan.ContainsKey("TruffleHog")));
    }

    // ---- the state file itself ----

    [Fact]
    public async Task AStateFileThatCannotBeReadStopsTheScannerAndIsLeftAlone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath, "{ not json");
        var context = await NewRunAsync(Settings(true, Pinned()), Repos(2));
        var probe = new Probe();

        // Carrying on as if it were empty would scan everything and then overwrite whatever was there.
        await Assert.ThrowsAsync<InvalidDataException>(() => probe.RunAsync(context));

        Assert.Empty(probe.Scanned);
        Assert.Equal("{ not json", await File.ReadAllTextAsync(StatePath));
    }

    [Fact]
    public async Task AnUnreadableStateFileFallsBackToTheBackup()
    {
        var settings = Settings(true, Pinned());
        await new Probe().RunAsync(await NewRunAsync(settings, Repos(2)));
        await new Probe().RunAsync(await NewRunAsync(settings, Repos(2), ScanMode.Full)); // second save makes a backup
        await File.WriteAllTextAsync(StatePath, "{ not json");

        var probe = new Probe();
        await probe.RunAsync(await NewRunAsync(settings, Repos(2)));

        Assert.Empty(probe.Scanned); // the backup had the state, so nothing changed
    }

    [Fact]
    public async Task SavingNeverReplacesAGoodBackupWithAFileThatCannotBeRead()
    {
        var store = new FoundOrgAppRepoStore(Path.Combine(_root, "state"));
        await store.SaveAsync(new FoundOrgAppRepoDocument { Tools = new() { ["Semgrep"] = new ToolInventoryState { ForceFull = true } } });
        await store.SaveAsync(new FoundOrgAppRepoDocument());
        var goodBackup = await File.ReadAllBytesAsync(store.Path + ".bak");
        await File.WriteAllTextAsync(store.Path, "{ not json");

        await store.SaveAsync(new FoundOrgAppRepoDocument());

        Assert.Equal(goodBackup, await File.ReadAllBytesAsync(store.Path + ".bak"));
    }

    [Fact]
    public async Task LoadingWhenThereIsNoFileGivesAnEmptyStateAndIsNotAnError()
    {
        var document = await new FoundOrgAppRepoStore(Path.Combine(_root, "state")).LoadAsync();

        Assert.Empty(document.Orgs);
        Assert.Empty(document.Tools);
    }

    [Fact]
    public async Task TheServiceIsSafeToUseFromManyThreadsAtOnce()
    {
        var context = await NewRunAsync(Settings(true, Pinned()), []);
        await context.FoundState.EnsureLoadedAsync();

        Parallel.For(0, 2000, i => context.FoundState.Record(
            (i / 100) % 2 == 0 ? "Semgrep" : "Snyk",
            Repo(i % 100 + 1),
            new RepositoryScanState { Sha = $"sha-{i}" }));
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => context.FoundState.SaveAsync()));

        var state = await ReadStateAsync(StatePath);
        Assert.Equal(100, state.ToStoredRepos().Count());
        Assert.All(state.ToStoredRepos(), r => Assert.Equal(["Semgrep", "Snyk"], r.Scan.Keys.Order()));
    }

    [Fact]
    public async Task ACommandFileOrAnythingElseDoesNotLandInTheStateFile()
    {
        var settings = Settings(true, Pinned());
        await new Probe().RunAsync(await NewRunAsync(settings, Repos(1)));

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(StatePath));

        Assert.Equal(["schemaVersion", "runId", "updatedAt", "tools", "orgs"], json.RootElement.EnumerateObject().Select(p => p.Name));
    }
}

// The scan state of a repo, found by its key in the org, application, repo layout of the file.
internal static class FoundDocumentExtensions
{
    public static RepositoryScanState? ScanOf(this FoundOrgAppRepoDocument document, string repoKey, string tool = "Semgrep") =>
        document.ToStoredRepos().SingleOrDefault(r => r.Repo.Key == repoKey)?.Scan.GetValueOrDefault(tool);

    public static bool HasRepo(this FoundOrgAppRepoDocument document, string repoKey) =>
        document.ToStoredRepos().Any(r => r.Repo.Key == repoKey);
}
