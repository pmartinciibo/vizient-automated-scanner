namespace Vizient.AutomatedScanner.Tests;

using System.Net;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Tools;
using Vizient.AutomatedScanner.Core.Work;

public class ProgressTests : IDisposable
{
    private readonly ScanWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Theory]
    [InlineData(0, 0, 0, 10, new string[0], "0 of 10 processed")]
    [InlineData(47, 0, 0, 4439, new[] { "ADO org/proj/repo" }, "47 of 4439 processed; now: ADO org/proj/repo")]
    [InlineData(40, 2, 4000, 4439, new[] { "GitHub a/b", "GitHub a/c", "GitHub a/d" }, "42 of 4439 processed, 2 failed, 4000 skipped; now: GitHub a/b (+2 more running)")]
    public void TheProgressTextSaysHowFarAndWhatIsRunningNow(int done, int failed, int skipped, int total, string[] running, string expected) =>
        Assert.Equal(expected, RepoProgress.Text(done, failed, skipped, total, running));

    [Fact]
    public void ARepoIsLabelledWithItsSourceFirst()
    {
        Assert.Equal("GitHub acme/one", RepoProgress.Label(new RepoOutcome { Source = "GitHub", Org = "acme", Name = "one" }));
        Assert.Equal("ADO org/proj/svc", RepoProgress.Label(new RepoOutcome { Source = "ADO", Org = "org", Project = "proj", Name = "svc" }));
    }

    [Fact]
    public async Task WhileReposAreProcessedTheStepSaysHowManyAreDoneAndWhichAreRunning()
    {
        for (var i = 0; i < 4; i++)
        {
            _world.AddOrigin(("readme.txt", "x"));
        }

        _world.With(s => s with { Scan = new ScanSettings { MaxParallelRepos = 4 } });
        _world.WithTool(ToolNames.Semgrep, t => t with { MaxParallel = 4 });
        await _world.CloneAsync();
        var messages = new List<string?>();
        var release = new TaskCompletionSource();
        var started = 0;
        var tool = new ProbeTool(ToolNames.Semgrep, decide: w =>
        {
            messages.Add(w.Run.Status.Snapshot().Steps.Single(s => s.Name == "Semgrep").Message);
            if (Interlocked.Increment(ref started) == 4)
            {
                release.SetResult();
            }

            release.Task.Wait(TimeSpan.FromSeconds(20));
            return null;
        });

        await tool.RunAsync(_world.Context);

        // Once all four are running, the note names one of them and counts the other three.
        Assert.Contains(messages, m => m is not null && m.Contains("+3 more running") && m.StartsWith("0 of 4 processed"));
        Assert.Null(_world.Step("Semgrep").Message); // nothing running, nothing to say
    }

    [Fact]
    public async Task WhileTheOrgsAreListedTheStepSaysWhichSourceAndOrgAndHowFar()
    {
        using var run = new RepoCloneRun();
        var origin = run.Origin("one");
        var context = await run.NewRunAsync();
        var seen = new List<string?>();
        var sources = new FakeSources
        {
            Orgs = ["alpha"],
            OrgsRespond = _ =>
            {
                seen.Add(context.Status.Snapshot().Steps.Single(s => s.Name == "RepoClone").Message);
                return null; // the fake's own answer
            },
            Respond = uri =>
            {
                seen.Add(context.Status.Snapshot().Steps.Single(s => s.Name == "RepoClone").Message);
                return uri.AbsolutePath == "/orgs/alpha/repos" && uri.Query.Contains("page=1")
                    ? FakeSources.GitHubList(FakeSources.GitHubRepo(1, "one", origin.Path))
                    : FakeSources.GitHubList();
            }
        };

        await new RepoCloneTool(sources, (_, _) => Task.CompletedTask).RunAsync(context);

        Assert.Contains(seen, m => m is not null && m.EndsWith("GitHub: finding the organizations of the token"));
        Assert.Contains(seen, m => m is not null && m.EndsWith("GitHub: listing the repos of alpha (page 1, 0 so far)"));
    }

    [Fact]
    public async Task WhileAnAdoOrgIsListedTheStepSaysWhichProjectItIsOn()
    {
        using var run = new RepoCloneRun();
        var origin = run.Origin("svc");
        run.With(s => s with { Sources = [new SourceSettings { Name = "Ado", Source = "ADO", Org = "vz", TokenSecret = "gh-token" }] });
        var context = await run.NewRunAsync();
        var seen = new List<string?>();
        var responder = AdoFake(origin);
        var sources = new FakeSources
        {
            Respond = uri =>
            {
                seen.Add(context.Status.Snapshot().Steps.Single(s => s.Name == "RepoClone").Message);
                return responder(uri);
            }
        };

        await new RepoCloneTool(sources, (_, _) => Task.CompletedTask).RunAsync(context);

        Assert.Contains(seen, m => m is not null && m.Contains("ADO: vz: listing the repos of project 1 of 2 (ProjA)"));
        Assert.Contains(seen, m => m is not null && m.Contains("ADO: vz: listing the repos of project 2 of 2 (Proj B)"));
    }

    [Fact]
    public async Task WhileListingTheStatusCountsOrgsAndReposAcrossEverySourceNotJustTheCurrentOrg()
    {
        using var run = new RepoCloneRun();
        var a = run.Origin("a");
        var b = run.Origin("b");
        run.With(s => s with
        {
            Sources = [new SourceSettings { Name = "Ado", Source = "ADO", Org = "vz", TokenSecret = "gh-token" },
                       new SourceSettings { Name = "GitHub", Source = "GitHub", TokenSecret = "gh-token" }]
        });
        var context = await run.NewRunAsync();
        var seen = new List<string?>();
        var ado = AdoFake(a);
        var sources = new FakeSources
        {
            Orgs = ["alpha", "beta"],
            Respond = uri =>
            {
                seen.Add(context.Status.Snapshot().Steps.Single(s => s.Name == "RepoClone").Message);
                return uri.Host == "dev.azure.com"
                    ? ado(uri)
                    : uri.Query.Contains("page=1") && uri.AbsolutePath == "/orgs/alpha/repos"
                        ? FakeSources.GitHubList(FakeSources.GitHubRepo(7, "one", b.Path))
                        : FakeSources.GitHubList();
            }
        };

        await new RepoCloneTool(sources, (_, _) => Task.CompletedTask).RunAsync(context);

        // The ADO org is known from the start, so it is one of 1. Once GitHub says it has two orgs, the whole is 3.
        Assert.Contains(seen, m => m is not null && m.StartsWith("discovery: 0 of 1 orgs listed, 0 repos found; ADO: vz:"));
        Assert.Contains(seen, m => m is not null && m.StartsWith("discovery: 1 of 3 orgs listed, 1 repos found; GitHub:"));
        Assert.Contains(seen, m => m is not null && m.StartsWith("discovery: 2 of 3 orgs listed, 2 repos found; GitHub:"));
    }

    // Two projects, one repo in the first.
    private static Func<Uri, HttpResponseMessage> AdoFake(FakeOrigin origin) => uri =>
    {
        if (uri.AbsolutePath.EndsWith("/_apis/projects"))
        {
            return FakeRapid7.Json(HttpStatusCode.OK, "{\"value\":[{\"id\":\"p1\",\"name\":\"ProjA\"},{\"id\":\"p2\",\"name\":\"Proj B\"}]}");
        }

        return uri.AbsolutePath.Contains("/p1/")
            ? FakeRapid7.Json(HttpStatusCode.OK,
                $"{{\"value\":[{{\"id\":\"aaaa1111\",\"name\":\"svc\",\"remoteUrl\":{System.Text.Json.JsonSerializer.Serialize(origin.Path)},\"isDisabled\":false}}]}}")
            : FakeRapid7.Json(HttpStatusCode.OK, "{\"value\":[]}");
    };
}
