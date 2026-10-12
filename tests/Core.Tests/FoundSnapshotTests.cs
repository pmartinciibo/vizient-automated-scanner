namespace Vizient.AutomatedScanner.Tests;

using System.Text.Json;
using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.State;
using Vizient.AutomatedScanner.Core.Tools;

public class FoundSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
    private readonly string _stateRoot;
    private readonly EngineConfig _config;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-10T12:00:00Z");

    public FoundSnapshotTests()
    {
        _stateRoot = Path.Combine(_root, "state");
        _config = new EngineConfig(
            Path.Combine(_root, "engine.json"),
            Path.Combine(_root, "repos"),
            Path.Combine(_root, "runs"),
            _stateRoot,
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

    private static RepoOutcome Gh(string key, string org = "RnD-Vizient", bool remote = true) => new()
    {
        Key = key, Source = "GitHub", Org = org, Name = key, ExistedRemotely = remote, ExistedLocally = true
    };

    private static RepoOutcome Ado(string key, string project, string org = "vizientinc", bool remote = true) => new()
    {
        Key = key, Source = "ADO", Org = org, Project = project, Name = key, ExistedRemotely = remote, ExistedLocally = true
    };

    private static FoundOrgAppRepoDocument Snapshot(params RepoOutcome[] repos) =>
        FoundSnapshotBuilder.Build("run-0", Now, repos, new FoundOrgAppRepoDocument(), []);

    private async Task<RunContext> StartRunAsync(string runId)
    {
        var requestFile = Path.Combine(_root, "requests", $"{runId}.json");
        await AtomicJsonFile.WriteAsync(requestFile, new RunRequestDocument { RunId = runId });
        return await RunContext.StartAsync(_config, requestFile);
    }

    // ---- store ----

    [Fact]
    public async Task LoadWithNoFileGivesAnEmptySnapshotAndCreatesNothing()
    {
        var store = new FoundOrgAppRepoStore(_stateRoot);

        var snapshot = await store.LoadAsync();

        Assert.Empty(snapshot.Orgs);
        Assert.False(snapshot.HasAnyRepo);
        Assert.False(File.Exists(store.Path));
    }

    [Fact]
    public async Task SaveThenLoadRoundTripsTheHierarchy()
    {
        var store = new FoundOrgAppRepoStore(_stateRoot);
        await store.SaveAsync(Snapshot(Gh("gh-1"), Ado("ado-1", "Claro"), Ado("ado-2", "Claro")));

        var loaded = await store.LoadAsync();

        Assert.Equal(["ADO", "GitHub"], loaded.Orgs.Select(o => o.Source));
        var claro = Assert.Single(loaded.Orgs[0].Applications);
        Assert.Equal("Claro", claro.Name);
        Assert.Equal(["ado-1", "ado-2"], claro.Repos.Select(r => r.Key));
        Assert.Equal("RnD-Vizient", loaded.Orgs[1].Applications.Single().Name);
        Assert.Equal("run-0", loaded.RunId);
        Assert.Equal(Now, loaded.UpdatedAt);
        Assert.True(loaded.HasAnyRepo);
    }

    [Fact]
    public async Task TheFileHoldsOnlyTheSnapshotNotThePerRunFlags()
    {
        var store = new FoundOrgAppRepoStore(_stateRoot);
        await store.SaveAsync(Snapshot(Gh("gh-1") with { UpdatedLocally = true, RemoteStatus = RepositoryStatus.Archived }));

        var text = await File.ReadAllTextAsync(store.Path);

        Assert.Equal("FoundOrgAppRepo.json", Path.GetFileName(store.Path));
        Assert.DoesNotContain("updatedLocally", text);
        Assert.DoesNotContain("existedLocally", text);
        Assert.DoesNotContain("archivedOrRetired", text);
        Assert.DoesNotContain("existedPriorToScan", text);
        Assert.DoesNotContain("hasAnyRepo", text);
        Assert.Contains("\"remoteStatus\": \"Archived\"", text);
    }

    [Fact]
    public async Task SaveKeepsThePreviousVersionAsBackup()
    {
        var store = new FoundOrgAppRepoStore(_stateRoot);
        await store.SaveAsync(Snapshot(Gh("gh-1")));
        var first = await File.ReadAllBytesAsync(store.Path);

        await store.SaveAsync(Snapshot(Gh("gh-1"), Gh("gh-2")));

        Assert.Equal(first, await File.ReadAllBytesAsync(store.Path + ".bak"));
        Assert.Empty(Directory.GetFiles(_stateRoot, "*.tmp"));
    }

    [Fact]
    public async Task UnreadableSnapshotFallsBackToTheBackup()
    {
        var store = new FoundOrgAppRepoStore(_stateRoot);
        await store.SaveAsync(Snapshot(Gh("gh-1")));
        await store.SaveAsync(Snapshot(Gh("gh-1"), Gh("gh-2")));
        await File.WriteAllTextAsync(store.Path, "{ not json");

        var loaded = await store.LoadAsync();

        Assert.Equal(["gh-1"], loaded.Orgs.Single().Applications.Single().Repos.Select(r => r.Key));
    }

    [Fact]
    public async Task UnreadableSnapshotWithNoUsableBackupThrowsInsteadOfLookingEmpty()
    {
        var store = new FoundOrgAppRepoStore(_stateRoot);
        Directory.CreateDirectory(_stateRoot);
        await File.WriteAllTextAsync(store.Path, "{ not json");

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());

        await File.WriteAllTextAsync(store.Path + ".bak", "also broken");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    }

    // ---- index ----

    [Fact]
    public void IndexAnswersWhetherAnOrgApplicationOrRepoWasInTheSnapshot()
    {
        var index = new FoundOrgAppRepoIndex(Snapshot(Gh("gh-1"), Ado("ado-1", "Claro")));

        Assert.True(index.HasOrg("ADO", "vizientinc"));
        Assert.True(index.HasOrg("ado", "VIZIENTINC"));
        Assert.False(index.HasOrg("GitHub", "vizientinc"));
        Assert.True(index.HasApplication("ADO", "vizientinc", "Claro"));
        Assert.False(index.HasApplication("ADO", "vizientinc", "Other"));
        Assert.True(index.HasApplication("GitHub", "RnD-Vizient", "RnD-Vizient"));
        Assert.True(index.HasRepo("gh-1"));
        Assert.False(index.HasRepo("gh-2"));
        Assert.False(FoundOrgAppRepoIndex.Empty.HasRepo("gh-1"));
    }

    // ---- builder ----

    [Fact]
    public void SnapshotHoldsOnlyWhatTheSourceListedThisScan()
    {
        var snapshot = Snapshot(Gh("gh-1"), Gh("gh-2", remote: false));

        var keys = snapshot.Orgs.Single().Applications.Single().Repos.Select(r => r.Key);

        Assert.Equal(["gh-1"], keys);
    }

    [Fact]
    public void ARepoThatDisappearedFromACompletedOrgIsKeptAndMarkedDeleted()
    {
        var previous = Snapshot(Gh("gh-1"), Gh("gh-2"));

        var next = FoundSnapshotBuilder.Build("run-1", Now, [Gh("gh-1")], previous, incompleteOrgs: []);

        var repos = next.Orgs.Single().Applications.Single().Repos;
        Assert.Equal(["gh-1", "gh-2"], repos.Select(r => r.Key));
        Assert.Equal(RepositoryStatus.Active, repos[0].RemoteStatus);
        Assert.Equal(RepositoryStatus.Deleted, repos[1].RemoteStatus);
        Assert.Equal("run-1", next.RunId);
    }

    [Fact]
    public void AnIncompleteOrgKeepsWhatTheLastSnapshotHeldForIt()
    {
        var previous = Snapshot(Gh("gh-1", org: "Broken"), Gh("gh-2", org: "Broken"), Gh("gh-3", org: "Fine"), Gh("gh-4", org: "Fine"));

        // Broken only partly listed (gh-1 seen again), Fine listed fully but gh-4 is gone.
        var next = FoundSnapshotBuilder.Build(
            "run-1", Now,
            [Gh("gh-1", org: "Broken"), Gh("gh-3", org: "Fine")],
            previous,
            incompleteOrgs: [("GitHub", "broken")]);

        var byOrg = next.Orgs.ToDictionary(o => o.Name, o => o.Applications.Single().Repos.Select(r => r.Key).ToList());
        Assert.Equal(["gh-1", "gh-2"], byOrg["Broken"]);
        Assert.Equal(["gh-3", "gh-4"], byOrg["Fine"]);
    }

    [Fact]
    public void AnIncompleteOrgWithNothingFoundIsStillKeptWholeIncludingAdoProjects()
    {
        var previous = Snapshot(Ado("ado-1", "Claro"), Ado("ado-2", "Other"), Gh("gh-1"));

        var next = FoundSnapshotBuilder.Build(
            "run-1", Now, [Gh("gh-1")], previous, incompleteOrgs: [("ADO", "vizientinc")]);

        var ado = next.Orgs.Single(o => o.Source == "ADO");
        Assert.Equal(["Claro", "Other"], ado.Applications.Select(a => a.Name));
    }

    [Fact]
    public void ReposOfAnOrgNotListedThisScanAreKeptAsTheyWereAndOnesOfAListedOrgAreMarkedDeleted()
    {
        var previous = Snapshot(Gh("gh-1", org: "Other"), Gh("gh-2", org: "Listed"), Gh("gh-3", org: "Listed"), Ado("ado-1", "Claro"));

        // Only GitHub/Listed was listed, and it now has just gh-2. ADO and GitHub/Other were not part of this scan.
        var next = FoundSnapshotBuilder.Build(
            "run-1", Now, [Gh("gh-2", org: "Listed")], previous, incompleteOrgs: [], listedOrgs: [("GitHub", "Listed")]);

        var keys = next.ToStoredRepos().Select(s => s.Repo.Key).Order().ToList();
        Assert.Equal(["ado-1", "gh-1", "gh-2", "gh-3"], keys);
        var status = next.ToStoredRepos().ToDictionary(s => s.Repo.Key, s => s.Repo.RemoteStatus);
        Assert.Equal(RepositoryStatus.Deleted, status["gh-3"]);
        Assert.Equal(RepositoryStatus.Active, status["gh-1"]);
        Assert.Equal(RepositoryStatus.Active, status["gh-2"]);
    }

    // ---- RepoClone loads then saves ----

    [Fact]
    public async Task RepoCloneLoadsTheLastSnapshotIntoTheReport()
    {
        await new FoundOrgAppRepoStore(_stateRoot).SaveAsync(Snapshot(Gh("gh-1"), Ado("ado-1", "Claro")));
        var context = await StartRunAsync("run-1");
        context.Repos.Record(Gh("gh-1"));

        await new SnapshotOnlyRepoClone().RunAsync(context);

        Assert.True(context.Repos.Prior.HasRepo("ado-1"));
        Assert.True(context.Repos.Prior.HasOrg("ADO", "vizientinc"));
    }

    [Fact]
    public async Task RepoCloneWritesBackWhatItFound()
    {
        var context = await StartRunAsync("run-1");
        context.Repos.Record(Gh("gh-1"));
        context.Repos.Record(Ado("ado-1", "Claro"));
        context.Repos.Record(Gh("gh-2", remote: false));

        await new SnapshotOnlyRepoClone().RunAsync(context);

        var saved = await new FoundOrgAppRepoStore(_stateRoot).LoadAsync();
        Assert.Equal("run-1", saved.RunId);
        Assert.Equal(
            ["ado-1", "gh-1"],
            saved.ToRepoOutcomes().Select(r => r.Key).Order());
    }

    [Fact]
    public async Task ASecondScanSeesWhatTheFirstFoundAndMarksTheRestNew()
    {
        var first = await StartRunAsync("run-1");
        first.Repos.Record(Gh("gh-1"));
        await new SnapshotOnlyRepoClone().RunAsync(first);

        var second = await StartRunAsync("run-2");
        second.Repos.Record(Gh("gh-1"));
        second.Repos.Record(Gh("gh-2"));
        second.Repos.Record(Ado("ado-1", "Claro"));
        await new SnapshotOnlyRepoClone().RunAsync(second);

        var orgs = second.Repos.Orgs().ToDictionary(o => o.Source);
        Assert.True(orgs["GitHub"].ExistedPriorToScan);
        Assert.True(orgs["GitHub"].Applications.Single().ExistedPriorToScan);
        var repos = orgs["GitHub"].Applications.Single().Repos.ToDictionary(r => r.Key);
        Assert.True(repos["gh-1"].ExistedPriorToScan);
        Assert.False(repos["gh-2"].ExistedPriorToScan);
        Assert.False(orgs["ADO"].ExistedPriorToScan);
        Assert.False(orgs["ADO"].Applications.Single().ExistedPriorToScan);
    }

    [Fact]
    public async Task ResultsShowExistedPriorToScanAtEveryLevelAndLeaveTheSnapshotAlone()
    {
        await new FoundOrgAppRepoStore(_stateRoot).SaveAsync(Snapshot(Gh("gh-1")));
        var context = await StartRunAsync("run-1");
        context.Repos.Record(Gh("gh-1"));
        context.Repos.Record(Gh("gh-2"));
        await new SnapshotOnlyRepoClone().RunAsync(context);
        context.Status.Finish(RunState.Completed);
        await AtomicJsonFile.WriteAsync(context.Invocation.Status, context.Status.Snapshot());

        await RunResultsWriter.WriteFinalAsync(context, RunState.Completed, 0, null);

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(context.Invocation.Results));
        var org = json.RootElement.GetProperty("orgs")[0];
        var app = org.GetProperty("applications")[0];
        Assert.True(org.GetProperty("existedPriorToScan").GetBoolean());
        Assert.True(app.GetProperty("existedPriorToScan").GetBoolean());
        Assert.True(app.GetProperty("repos")[0].GetProperty("existedPriorToScan").GetBoolean());
        Assert.False(app.GetProperty("repos")[1].GetProperty("existedPriorToScan").GetBoolean());

        // The snapshot is not a run output, so it is not copied into the run folder or beside the request.
        Assert.False(File.Exists(Path.Combine(context.Paths.RunFolder, FoundOrgAppRepoStore.FileName)));
        Assert.False(File.Exists(Path.Combine(context.Invocation.Directory, FoundOrgAppRepoStore.FileName)));
    }

    [Fact]
    public async Task ScanThatFoundNothingKeepsTheLastSnapshotAndRaisesAnAlert()
    {
        var store = new FoundOrgAppRepoStore(_stateRoot);
        await store.SaveAsync(Snapshot(Gh("gh-1"), Gh("gh-2")));
        var before = await File.ReadAllBytesAsync(store.Path);
        var context = await StartRunAsync("run-1");

        await new SnapshotOnlyRepoClone().RunAsync(context);

        Assert.Equal(before, await File.ReadAllBytesAsync(store.Path));
        Assert.Contains(context.Status.Snapshot().Alerts, a => a.Type == "SnapshotNotUpdated");
    }

    [Fact]
    public async Task RepoCloneFailsRatherThanTreatEverythingAsNewWhenTheSnapshotIsUnreadable()
    {
        Directory.CreateDirectory(_stateRoot);
        await File.WriteAllTextAsync(Path.Combine(_stateRoot, FoundOrgAppRepoStore.FileName), "{ not json");
        var context = await StartRunAsync("run-1");

        await Assert.ThrowsAsync<InvalidDataException>(() => new SnapshotOnlyRepoClone().RunAsync(context));
    }
}
