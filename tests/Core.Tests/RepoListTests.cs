namespace Vizient.AutomatedScanner.Tests;

using System.Text.Json;
using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.Status;

public class RepoListTests : IDisposable
{
    private const string RunId = "2026-10-10T12-00-00Z";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
    private readonly string _requestFile;
    private readonly EngineConfig _config;

    public RepoListTests()
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

    private static RepoOutcome Repo(
        string key,
        bool local = true,
        bool remote = true,
        bool updated = false,
        RepositoryStatus status = RepositoryStatus.Active,
        string source = "GitHub",
        string org = "RnD-Vizient",
        string? project = null) => new()
    {
        Key = key,
        Source = source,
        Org = org,
        Project = project,
        Name = key,
        ExistedLocally = local,
        ExistedRemotely = remote,
        UpdatedLocally = updated,
        RemoteStatus = status
    };

    private static RepoOutcome AdoRepo(string key, string project, string org = "vizientinc", bool local = true, bool remote = true) =>
        Repo(key, local, remote, source: "ADO", org: org, project: project);

    private async Task<RunContext> StartRunAsync()
    {
        await AtomicJsonFile.WriteAsync(_requestFile, new RunRequestDocument { RunId = RunId });
        return await RunContext.StartAsync(_config, _requestFile);
    }

    private async Task<JsonDocument> WriteResultsAsync(RunContext context)
    {
        context.Status.Finish(RunState.Completed);
        await AtomicJsonFile.WriteAsync(context.Invocation.Status, context.Status.Snapshot());
        await RunResultsWriter.WriteFinalAsync(context, RunState.Completed, 0, null);
        return JsonDocument.Parse(await File.ReadAllTextAsync(context.Invocation.Results));
    }

    // ---- repo flags ----

    [Theory]
    [InlineData(RepositoryStatus.Active, false)]
    [InlineData(RepositoryStatus.Archived, true)]
    [InlineData(RepositoryStatus.Disabled, true)]
    [InlineData(RepositoryStatus.Deleted, false)]
    [InlineData(RepositoryStatus.Unreachable, false)]
    public void ArchivedOrRetiredFollowsTheRemoteStatus(RepositoryStatus status, bool expected)
    {
        Assert.Equal(expected, Repo("gh-1", status: status).ArchivedOrRetired);
    }

    [Fact]
    public void AGitHubReposApplicationIsItsOrg()
    {
        Assert.Equal("RnD-Vizient", Repo("gh-1", org: "RnD-Vizient").ApplicationName);
        Assert.Equal("RnD-Vizient", Repo("gh-1", org: "RnD-Vizient", project: "ignored").ApplicationName);
        Assert.Equal("RnD-Vizient", Repo("gh-1", source: "github", org: "RnD-Vizient").ApplicationName);
    }

    [Fact]
    public void AnAdoReposApplicationIsItsProject()
    {
        Assert.Equal("Claro", AdoRepo("ado-1", "Claro").ApplicationName);
        Assert.Equal("Unassigned", Repo("ado-2", source: "ADO", org: "vizientinc", project: null).ApplicationName);
        Assert.Equal("Unassigned", Repo("ado-3", source: "ADO", org: "vizientinc", project: "  ").ApplicationName);
    }

    // ---- collector ----

    [Fact]
    public void ReportKeepsTheLatestOutcomePerRepoOrderedByKey()
    {
        var report = new RepoReport();
        report.Record(Repo("gh-2", local: false, updated: false));
        report.Record(Repo("gh-1"));
        report.Record(Repo("gh-2", local: false, updated: true));

        var repos = report.Snapshot();

        Assert.Equal(["gh-1", "gh-2"], repos.Select(r => r.Key));
        Assert.True(repos[1].UpdatedLocally);
    }

    [Fact]
    public void ReportAcceptsManyRepoReportsAtOnce()
    {
        var report = new RepoReport();

        Parallel.For(0, 2000, i => report.Record(Repo($"gh-{i}", updated: i % 2 == 0)));

        Assert.Equal(2000, report.Snapshot().Count);
    }

    [Fact]
    public void ReportRejectsAnUnsafeKey()
    {
        Assert.Throws<ArgumentException>(() => new RepoReport().Record(Repo("..\\evil")));
    }

    // ---- org > application > repo ----

    [Fact]
    public void GitHubOrgHasOneApplicationNamedAfterItself()
    {
        var report = new RepoReport();
        report.Record(Repo("gh-2", org: "RnD-Vizient"));
        report.Record(Repo("gh-1", org: "RnD-Vizient"));

        var org = Assert.Single(report.Orgs());

        Assert.Equal("RnD-Vizient", org.Name);
        Assert.Equal("GitHub", org.Source);
        var app = Assert.Single(org.Applications);
        Assert.Equal("RnD-Vizient", app.Name);
        Assert.Equal(["gh-1", "gh-2"], app.Repos.Select(r => r.Key));
    }

    [Fact]
    public void AdoOrgHasAnApplicationPerProject()
    {
        var report = new RepoReport();
        report.Record(AdoRepo("ado-1", "Zeta"));
        report.Record(AdoRepo("ado-2", "Claro"));
        report.Record(AdoRepo("ado-3", "Claro"));
        report.Record(Repo("ado-4", source: "ADO", org: "vizientinc", project: null));

        var org = Assert.Single(report.Orgs());

        Assert.Equal(["Claro", "Zeta", "Unassigned"], org.Applications.Select(a => a.Name));
        Assert.Equal(["ado-2", "ado-3"], org.Applications[0].Repos.Select(r => r.Key));
    }

    [Fact]
    public void OrgsAreOrderedBySourceThenNameAndSameNamedOrgsInDifferentSourcesStaySeparate()
    {
        var report = new RepoReport();
        report.Record(Repo("gh-1", org: "Shared"));
        report.Record(AdoRepo("ado-1", "P1", org: "Shared"));
        report.Record(AdoRepo("ado-2", "P1", org: "alpha"));
        report.Record(Repo("gh-2", org: "shared"));

        var orgs = report.Orgs();

        Assert.Equal(
            [("ADO", "alpha"), ("ADO", "Shared"), ("GitHub", "Shared")],
            orgs.Select(o => (o.Source, o.Name)));
        Assert.Equal(["gh-1", "gh-2"], orgs[2].Applications.Single().Repos.Select(r => r.Key));
    }

    [Fact]
    public void ExistenceFlagsAtOrgAndApplicationLevelMeanAnyRepoBelow()
    {
        var report = new RepoReport();
        // Claro: one repo only on disk (gone from the source), one only at the source (not cloned yet).
        report.Record(AdoRepo("ado-1", "Claro", local: true, remote: false));
        report.Record(AdoRepo("ado-2", "Claro", local: false, remote: true));
        // Legacy: nothing at the source any more, and no clone.
        report.Record(AdoRepo("ado-3", "Legacy", local: false, remote: false));
        // Local: only on disk.
        report.Record(AdoRepo("ado-4", "Local", local: true, remote: false));

        var org = Assert.Single(report.Orgs());
        var apps = org.Applications.ToDictionary(a => a.Name);

        Assert.True(apps["Claro"].ExistedLocally);
        Assert.True(apps["Claro"].ExistedRemotely);
        Assert.False(apps["Legacy"].ExistedLocally);
        Assert.False(apps["Legacy"].ExistedRemotely);
        Assert.True(apps["Local"].ExistedLocally);
        Assert.False(apps["Local"].ExistedRemotely);
        Assert.True(org.ExistedLocally);
        Assert.True(org.ExistedRemotely);
    }

    // ---- results.json ----

    [Fact]
    public async Task ResultsShowOrgThenApplicationsThenRepos()
    {
        var context = await StartRunAsync();
        context.Repos.Record(Repo("gh-1", updated: true));
        context.Repos.Record(AdoRepo("ado-1", "Claro"));
        context.Repos.Record(Repo("ado-2", local: false, status: RepositoryStatus.Archived, source: "ADO", org: "vizientinc", project: "Claro"));

        using var json = await WriteResultsAsync(context);

        Assert.False(json.RootElement.TryGetProperty("repos", out _));
        var orgs = json.RootElement.GetProperty("orgs");
        Assert.Equal(2, orgs.GetArrayLength());

        var ado = orgs[0];
        Assert.Equal("ADO", ado.GetProperty("source").GetString());
        Assert.Equal("vizientinc", ado.GetProperty("name").GetString());
        var claro = ado.GetProperty("applications")[0];
        Assert.Equal("Claro", claro.GetProperty("name").GetString());
        Assert.Equal(2, claro.GetProperty("repos").GetArrayLength());

        var gitHub = orgs[1];
        Assert.Equal("RnD-Vizient", gitHub.GetProperty("name").GetString());
        var app = gitHub.GetProperty("applications")[0];
        Assert.Equal("RnD-Vizient", app.GetProperty("name").GetString());
        Assert.Equal("gh-1", app.GetProperty("repos")[0].GetProperty("key").GetString());
    }

    [Fact]
    public async Task OrgAndApplicationShowOnlyTheTwoExistenceFlagsAndReposShowAllFour()
    {
        var context = await StartRunAsync();
        context.Repos.Record(Repo("gh-1", local: false, remote: true, updated: true, status: RepositoryStatus.Archived));

        using var json = await WriteResultsAsync(context);

        var org = json.RootElement.GetProperty("orgs")[0];
        var app = org.GetProperty("applications")[0];
        var repo = app.GetProperty("repos")[0];

        foreach (var level in new[] { org, app })
        {
            Assert.False(level.GetProperty("existedLocally").GetBoolean());
            Assert.True(level.GetProperty("existedRemotely").GetBoolean());
            Assert.False(level.TryGetProperty("updatedLocally", out _));
            Assert.False(level.TryGetProperty("archivedOrRetired", out _));
        }

        Assert.False(repo.GetProperty("existedLocally").GetBoolean());
        Assert.True(repo.GetProperty("existedRemotely").GetBoolean());
        Assert.True(repo.GetProperty("updatedLocally").GetBoolean());
        Assert.True(repo.GetProperty("archivedOrRetired").GetBoolean());
        Assert.Equal("Archived", repo.GetProperty("remoteStatus").GetString());
        Assert.False(repo.TryGetProperty("applicationName", out _));
    }

    [Fact]
    public async Task ResultsKeepOverallRepoTotalsAndTheRunFolderCopyMatches()
    {
        var context = await StartRunAsync();
        context.Repos.Record(Repo("gh-1", updated: true));
        context.Repos.Record(Repo("gh-2", local: false, status: RepositoryStatus.Archived));
        context.Repos.Record(Repo("gh-3", remote: false, status: RepositoryStatus.Deleted));
        context.Repos.Record(Repo("gh-4", status: RepositoryStatus.Disabled));

        using var json = await WriteResultsAsync(context);

        var totals = json.RootElement.GetProperty("repoTotals");
        Assert.Equal(4, totals.GetProperty("total").GetInt32());
        Assert.Equal(3, totals.GetProperty("existedLocally").GetInt32());
        Assert.Equal(3, totals.GetProperty("existedRemotely").GetInt32());
        Assert.Equal(1, totals.GetProperty("updatedLocally").GetInt32());
        Assert.Equal(2, totals.GetProperty("archivedOrRetired").GetInt32());
        Assert.Equal(
            await File.ReadAllBytesAsync(context.Invocation.Results),
            await File.ReadAllBytesAsync(context.Paths.ResultsSummary));
    }

    [Fact]
    public async Task ResultsReadBackAsADocument()
    {
        var context = await StartRunAsync();
        context.Repos.Record(AdoRepo("ado-1", "Claro"));
        (await WriteResultsAsync(context)).Dispose();

        var results = await AtomicJsonFile.ReadAsync<ResultsDocument>(context.Invocation.Results);

        var org = Assert.Single(results.Orgs);
        Assert.Equal("Claro", Assert.Single(org.Applications).Name);
        Assert.Equal("ado-1", org.Applications[0].Repos.Single().Key);
    }

    [Fact]
    public async Task ResultsHaveNoOrgsWhenNoReposWereReported()
    {
        var context = await StartRunAsync();

        using var json = await WriteResultsAsync(context);

        Assert.Equal(0, json.RootElement.GetProperty("orgs").GetArrayLength());
        Assert.Equal(0, json.RootElement.GetProperty("repoTotals").GetProperty("total").GetInt32());
    }
}
