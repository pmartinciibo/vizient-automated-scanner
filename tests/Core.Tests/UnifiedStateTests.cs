namespace Vizient.AutomatedScanner.Tests;

using System.Text.Json;
using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.State;
using Vizient.AutomatedScanner.Core.Tools;
using Vizient.AutomatedScanner.Core.Work;

// FoundOrgAppRepo.json is the one store of lasting state: the org, application and repo list, and what the scans learned.
public class UnifiedStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
    private readonly string _requestFile;
    private int _runCounter;

    public UnifiedStateTests() => _requestFile = Path.Combine(_root, "requests", "request.json");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string StateFolder => Path.Combine(_root, "state");
    private string StateFile => Path.Combine(StateFolder, "FoundOrgAppRepo.json");

    private static readonly EngineSettings Settings = new()
    {
        Skip = new SkipSettings { CanaryPercent = 0 },
        Tools = new() { ["Semgrep"] = new ToolSettings { Version = "1.x", RulesVersion = "rules-1" } }
    };

    private async Task<RunContext> NewRunAsync(params RepoOutcome[] repos)
    {
        await AtomicJsonFile.WriteAsync(_requestFile, new RunRequestDocument { RunId = $"run-{++_runCounter}" });
        var config = new EngineConfig(
            Path.Combine(_root, "engine.json"), Path.Combine(_root, "repos"), Path.Combine(_root, "runs"),
            StateFolder, Path.Combine(_root, "tmp"), Path.Combine(_root, "logs"), Settings: Settings);
        var context = await RunContext.StartAsync(config, _requestFile);
        foreach (var repo in repos)
        {
            context.Repos.Record(repo);
        }

        return context;
    }

    private static RepoOutcome Gh(int n, string org = "RnD-Vizient", string? sha = null) => new()
    {
        Key = $"gh-{n:D3}", Source = "GitHub", Org = org, Name = $"repo{n}", LocalPath = $"C:\\repos\\repo{n}",
        ExistedLocally = true, ExistedRemotely = true, HeadSha = sha ?? $"sha-{n}"
    };

    private static RepoOutcome Ado(int n, string project, string org = "vizientinc") => new()
    {
        Key = $"ado-{n:D3}", Source = "ADO", Org = org, Project = project, Name = $"repo{n}", LocalPath = $"C:\\repos\\ado{n}",
        ExistedLocally = true, ExistedRemotely = true, HeadSha = $"ado-sha-{n}"
    };

    private sealed class Scanner(string name = "Semgrep") : ScanToolBase
    {
        public override string Name => name;
        public List<string> Scanned { get; } = new();

        protected override Task<RepoWorkResult> ScanRepoAsync(RepoWorkContext work, CancellationToken cancellationToken)
        {
            lock (Scanned) { Scanned.Add(work.Repo.Key); }
            return Task.FromResult(RepoWorkResult.Done(new ToolDoneMarker
            {
                StartedAt = DateTimeOffset.UtcNow, EndedAt = DateTimeOffset.UtcNow, FindingCount = 2
            }));
        }
    }

    private async Task<FoundOrgAppRepoDocument> ReadAsync() => await AtomicJsonFile.ReadAsync<FoundOrgAppRepoDocument>(StateFile);

    // ---- one file, and only that one ----

    [Fact]
    public async Task RepoCloneAndTheScannersKeepTheirStateInFoundOrgAppRepoJsonAndNothingElse()
    {
        var context = await NewRunAsync(Gh(1), Ado(1, "Claro"));

        await new SnapshotOnlyRepoClone().RunAsync(context);
        await new Scanner().RunAsync(context);

        // The .bak is the previous version of the same file.
        Assert.Equal(["FoundOrgAppRepo.json", "FoundOrgAppRepo.json.bak"], Directory.GetFiles(StateFolder).Select(Path.GetFileName).Order());
        Assert.False(File.Exists(Path.Combine(StateFolder, "repos.json")));
        Assert.False(File.Exists(Path.Combine(StateFolder, "ScanState.json")));
        Assert.False(File.Exists(Path.Combine(context.Paths.RunFolder, "repos.json")));
    }

    [Fact]
    public async Task TheFileHoldsTheHierarchyEachReposShaAndTheScanStateUnderIt()
    {
        var context = await NewRunAsync(Gh(1), Ado(1, "Claro"), Ado(2, "Claro"));
        await new SnapshotOnlyRepoClone().RunAsync(context);
        await new Scanner().RunAsync(context);

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(StateFile));
        var ado = json.RootElement.GetProperty("orgs")[0];
        var claro = ado.GetProperty("applications")[0];
        var repo = claro.GetProperty("repos")[0];

        Assert.Equal(("ADO", "vizientinc", "Claro"), (ado.GetProperty("source").GetString(), ado.GetProperty("name").GetString(), claro.GetProperty("name").GetString()));
        Assert.Equal(("ado-001", "ado-sha-1"), (repo.GetProperty("key").GetString(), repo.GetProperty("headSha").GetString()));
        var scan = repo.GetProperty("scan").GetProperty("Semgrep");
        Assert.Equal("ado-sha-1", scan.GetProperty("sha").GetString());
        Assert.Equal(2, scan.GetProperty("findingCount").GetInt32());
        Assert.Equal("1.x", scan.GetProperty("toolVersion").GetString());
        Assert.True(json.RootElement.GetProperty("tools").TryGetProperty("Semgrep", out _));
    }

    [Fact]
    public async Task ARepoNoToolHasScannedYetHasNoScanEntryAndNoNoise()
    {
        var context = await NewRunAsync(Gh(1) with { HeadSha = null });
        await new SnapshotOnlyRepoClone().RunAsync(context);

        var text = await File.ReadAllTextAsync(StateFile);

        Assert.DoesNotContain("\"scan\"", text);
        Assert.DoesNotContain("headSha", text);
    }

    // ---- the scanners never wipe what RepoClone wrote, and the reverse ----

    [Fact]
    public async Task AScannerSavingDoesNotLoseTheOrgsApplicationsAndReposRepoCloneWrote()
    {
        var context = await NewRunAsync(Gh(1), Gh(2), Ado(1, "Claro"), Ado(2, "Other"));
        await new SnapshotOnlyRepoClone().RunAsync(context);
        var afterRepoClone = (await ReadAsync()).ToRepoOutcomes().Select(r => r.Key).Order().ToList();

        await new Scanner().RunAsync(context);

        Assert.Equal(afterRepoClone, (await ReadAsync()).ToRepoOutcomes().Select(r => r.Key).Order());
        Assert.Equal(["ado-001", "ado-002", "gh-001", "gh-002"], afterRepoClone);
        var doc = await ReadAsync();
        Assert.Equal(["Claro", "Other"], doc.Orgs.Single(o => o.Source == "ADO").Applications.Select(a => a.Name));
    }

    [Fact]
    public async Task ReposStillThereKeepTheirScanStateWhenRepoCloneListsThemAgain()
    {
        var first = await NewRunAsync(Gh(1), Gh(2));
        await new SnapshotOnlyRepoClone().RunAsync(first);
        await new Scanner().RunAsync(first);

        var second = await NewRunAsync(Gh(1), Gh(2));
        await new SnapshotOnlyRepoClone().RunAsync(second);

        var doc = await ReadAsync();
        Assert.All(doc.ToStoredRepos(), r => Assert.Equal(2, r.Scan["Semgrep"].FindingCount));
        Assert.Equal("run-2", doc.RunId);
    }

    [Fact]
    public async Task ARepoThatIsGoneIsKeptAsDeletedWithItsScanStateAndComesBackActive()
    {
        var first = await NewRunAsync(Gh(1), Gh(2));
        await new SnapshotOnlyRepoClone().RunAsync(first);
        await new Scanner().RunAsync(first);

        var second = await NewRunAsync(Gh(1));
        await new SnapshotOnlyRepoClone().RunAsync(second);
        await new Scanner().RunAsync(second);
        var gone = (await ReadAsync()).ToStoredRepos().Single(r => r.Repo.Key == "gh-002");
        Assert.Equal(RepositoryStatus.Deleted, gone.Repo.RemoteStatus);
        Assert.NotEmpty(gone.Scan);

        var third = await NewRunAsync(Gh(1), Gh(2));
        await new SnapshotOnlyRepoClone().RunAsync(third);
        var back = (await ReadAsync()).ToStoredRepos().Single(r => r.Repo.Key == "gh-002");
        Assert.Equal(RepositoryStatus.Active, back.Repo.RemoteStatus);
        Assert.NotEmpty(back.Scan);
    }

    [Fact]
    public async Task AnOrgWhoseListingDidNotFinishKeepsEverythingItHeldIncludingScanState()
    {
        var first = await NewRunAsync(Gh(1, org: "Broken"), Gh(2, org: "Broken"), Gh(3, org: "Fine"));
        await new SnapshotOnlyRepoClone().RunAsync(first);
        await new Scanner().RunAsync(first);

        var state = new FoundOrgAppRepoState(new FoundOrgAppRepoStore(StateFolder), "run-x");
        await state.EnsureLoadedAsync();
        state.ReplaceSnapshot([Gh(1, org: "Broken"), Gh(3, org: "Fine")], [("GitHub", "Broken")], DateTimeOffset.UtcNow);
        await state.SaveAsync();

        var doc = await ReadAsync();
        var repos = doc.ToStoredRepos().ToDictionary(r => r.Repo.Key);
        Assert.Equal(["gh-001", "gh-002", "gh-003"], repos.Keys.Order());
        Assert.All(repos.Values, r => Assert.True(r.Scan.ContainsKey("Semgrep")));
        Assert.Equal("sha-2", repos["gh-002"].Repo.HeadSha);
    }

    [Fact]
    public async Task TheToolBookkeepingSurvivesRepoClonesReplacementOfTheRepos()
    {
        var first = await NewRunAsync(Gh(1));
        await new SnapshotOnlyRepoClone().RunAsync(first);
        await new Scanner().RunAsync(first);
        var lastFull = (await ReadAsync()).Tools["Semgrep"].LastFullScanAt;
        Assert.NotNull(lastFull);

        var second = await NewRunAsync(Gh(1), Gh(2));
        await new SnapshotOnlyRepoClone().RunAsync(second);

        Assert.Equal(lastFull, (await ReadAsync()).Tools["Semgrep"].LastFullScanAt);
    }

    // ---- the whole pipeline over two runs ----

    [Fact]
    public async Task OnTheSecondRunRepoCloneKnowsWhatIsNewAndTheScannerSkipsWhatDidNotChange()
    {
        var first = await NewRunAsync(Gh(1), Gh(2));
        await new SnapshotOnlyRepoClone().RunAsync(first);
        await new Scanner().RunAsync(first);

        var second = await NewRunAsync(Gh(1), Gh(2, sha: "sha-2-changed"), Gh(3));
        await new SnapshotOnlyRepoClone().RunAsync(second);
        var scanner = new Scanner();
        await scanner.RunAsync(second);

        Assert.Equal(["gh-002", "gh-003"], scanner.Scanned.Order());
        var repos = second.Repos.Orgs().Single().Applications.Single().Repos.ToDictionary(r => r.Key);
        Assert.True(repos["gh-001"].ExistedPriorToScan);
        Assert.True(repos["gh-002"].ExistedPriorToScan);
        Assert.False(repos["gh-003"].ExistedPriorToScan);
        Assert.Equal("Unchanged", (await AtomicJsonFile.ReadAsync<ToolSkippedMarker>(second.Paths.SkippedMarker("Semgrep", "gh-001"))).Reason);
    }

    [Fact]
    public async Task TheSnapshotNotUpdatedAlertStillProtectsScanStateWhenDiscoveryFoundNothing()
    {
        var first = await NewRunAsync(Gh(1), Gh(2));
        await new SnapshotOnlyRepoClone().RunAsync(first);
        await new Scanner().RunAsync(first);
        var before = (await ReadAsync()).ToStoredRepos().ToDictionary(r => r.Repo.Key, r => r.Scan["Semgrep"]);

        var second = await NewRunAsync(); // discovery found nothing
        await new SnapshotOnlyRepoClone().RunAsync(second);

        Assert.Contains(second.Status.Snapshot().Alerts, a => a.Type == "SnapshotNotUpdated");
        Assert.Equal(before, (await ReadAsync()).ToStoredRepos().ToDictionary(r => r.Repo.Key, r => r.Scan["Semgrep"]));
    }

    [Fact]
    public async Task AScannerAddsARepoItScannedThatTheSnapshotDidNotListSoTheFileNeverHoldsStateForAnUnlistedRepo()
    {
        var context = await NewRunAsync(Gh(1));
        await new Scanner().RunAsync(context); // no RepoClone pass first

        var doc = await ReadAsync();
        Assert.Equal(["gh-001"], doc.ToRepoOutcomes().Select(r => r.Key));
        Assert.Equal(2, doc.ToStoredRepos().Single().Scan["Semgrep"].FindingCount);
    }

    [Fact]
    public async Task AFileThatCannotBeReadStopsRepoCloneToo()
    {
        Directory.CreateDirectory(StateFolder);
        await File.WriteAllTextAsync(StateFile, "{ not json");
        var context = await NewRunAsync(Gh(1));

        await Assert.ThrowsAsync<InvalidDataException>(() => new SnapshotOnlyRepoClone().RunAsync(context));

        Assert.Equal("{ not json", await File.ReadAllTextAsync(StateFile));
    }

    [Fact]
    public async Task TheDocumentRoundTripsThroughTheStoreWithScanStateAndShaIntact()
    {
        var doc = FoundSnapshotBuilder.ToDocument(
            "run-9",
            DateTimeOffset.Parse("2026-10-14T03:00:00Z"),
            new Dictionary<string, ToolInventoryState> { ["Snyk"] = new() { ForceFull = true } },
            [new StoredRepo(Gh(1), new Dictionary<string, RepositoryScanState>
            {
                ["Snyk"] = new() { Sha = "sha-1", ScanMode = "code", ToolVersion = "1", RulesVersion = "n/a", ConfigHash = "h", FindingCount = 4 }
            })]);
        var store = new FoundOrgAppRepoStore(StateFolder);

        await store.SaveAsync(doc);
        var loaded = await store.LoadAsync();

        var repo = loaded.ToStoredRepos().Single();
        Assert.Equal(("gh-001", "sha-1"), (repo.Repo.Key, repo.Repo.HeadSha));
        Assert.Equal(("code", 4), (repo.Scan["Snyk"].ScanMode, repo.Scan["Snyk"].FindingCount));
        Assert.True(loaded.Tools["Snyk"].ForceFull);
        Assert.Equal("run-9", loaded.RunId);
    }
}
