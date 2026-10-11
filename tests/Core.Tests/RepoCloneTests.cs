namespace Vizient.AutomatedScanner.Tests;

using System.Net;
using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.RepoClone;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.State;
using Vizient.AutomatedScanner.Core.Tools;

public class RepoCloneTests : IDisposable
{
    private readonly RepoCloneRun _run = new();

    public void Dispose() => _run.Dispose();

    private static FakeSources ListsGitHub(params (long Id, string Name, FakeOrigin Origin)[] repos) =>
        new() { Respond = uri => uri.Host == "api.github.com" && uri.Query.Contains("page=1")
            ? FakeSources.GitHubList(repos.Select(r => FakeSources.GitHubRepo(r.Id, r.Name, r.Origin.Path)).ToArray())
            : FakeSources.GitHubList() };

    private Task<FoundOrgAppRepoDocument> ReadStateAsync() =>
        AtomicJsonFile.ReadAsync<FoundOrgAppRepoDocument>(new FoundOrgAppRepoStore(_run.Config.StateRoot).Path);

    private static RepoOutcome Repo(RunContext context, string key) => context.Repos.Snapshot().Single(r => r.Key == key);

    [Fact]
    public async Task NewReposAreClonedAsBareMirrorsUnderTheReposRoot()
    {
        var one = _run.Origin("one", ("src/app.cs", "class A {}"));
        var two = _run.Origin("two");
        one.Branch("feature", ("f.txt", "on a branch"));

        var context = await _run.RunAsync(ListsGitHub((1, "one", one), (2, "two", two)));

        var repo = Repo(context, "gh-1");
        Assert.Equal(Path.Combine(_run.ReposRoot, "GitHub", "acme", "one"), repo.LocalPath);
        Assert.True(File.Exists(Path.Combine(repo.LocalPath!, "HEAD")));
        Assert.True(Directory.Exists(Path.Combine(repo.LocalPath!, "objects")));
        Assert.False(File.Exists(Path.Combine(repo.LocalPath!, "src", "app.cs"))); // no files, only history
        Assert.False(Directory.Exists(Path.Combine(repo.LocalPath!, ".git")));
        Assert.Contains("refs/heads/feature", _run.GitOutput(repo.LocalPath!, "for-each-ref", "--format=%(refname)"));
        Assert.Equal(one.HeadOf("main"), repo.HeadSha);
        Assert.False(repo.ExistedLocally);
        Assert.True(repo.UpdatedLocally);
        Assert.True(repo.ExistedRemotely);
        Assert.Equal(two.HeadOf("main"), Repo(context, "gh-2").HeadSha);
        Assert.True(context.Markers.IsDone("RepoClone", "gh-1"));
        Assert.True(context.Markers.IsDone("RepoClone", "gh-2"));
    }

    [Fact]
    public async Task TheSnapshotIsSavedWithTheCheckedOutShaSoTheNextRunCanTellWhatChanged()
    {
        var one = _run.Origin("one");

        await _run.RunAsync(ListsGitHub((1, "one", one)));

        var state = await ReadStateAsync();
        var stored = Assert.Single(state.ToStoredRepos());
        Assert.Equal(("gh-1", one.Head), (stored.Repo.Key, stored.Repo.HeadSha));
        Assert.Equal("acme", state.Orgs.Single().Name);
    }

    [Fact]
    public async Task AnUnchangedRepoIsStillUpdatedButIsNotReportedAsUpdated()
    {
        var one = _run.Origin("one");
        await _run.RunAsync(ListsGitHub((1, "one", one)));

        var context = await _run.RunAsync(ListsGitHub((1, "one", one)));

        var repo = Repo(context, "gh-1");
        Assert.True(repo.ExistedLocally);
        Assert.False(repo.UpdatedLocally);
        Assert.Equal(one.Head, repo.HeadSha);
        Assert.True(context.Markers.IsDone("RepoClone", "gh-1"));
    }

    [Fact]
    public async Task AChangeOnAnyBranchCountsAsUpdatedEvenIfTheDefaultBranchDidNotMove()
    {
        var one = _run.Origin("one");
        var first = await _run.RunAsync(ListsGitHub((1, "one", one)));
        one.Branch("feature", ("f.txt", "new work"));

        var context = await _run.RunAsync(ListsGitHub((1, "one", one)));

        var repo = Repo(context, "gh-1");
        Assert.True(repo.UpdatedLocally);
        Assert.Equal(Repo(first, "gh-1").HeadSha, repo.HeadSha);
        Assert.Contains("refs/heads/feature", _run.GitOutput(repo.LocalPath!, "for-each-ref", "--format=%(refname)"));
    }

    [Fact]
    public async Task ABranchDeletedAtTheRemoteIsPrunedFromTheMirror()
    {
        var one = _run.Origin("one");
        one.Branch("old", ("o.txt", "x"));
        var first = await _run.RunAsync(ListsGitHub((1, "one", one)));
        one.DeleteBranch("old");

        await _run.RunAsync(ListsGitHub((1, "one", one)));

        Assert.DoesNotContain("refs/heads/old", _run.GitOutput(Repo(first, "gh-1").LocalPath!, "for-each-ref", "--format=%(refname)"));
    }

    [Fact]
    public async Task ARepoThatMovedOnIsUpdatedToTheNewCommit()
    {
        var one = _run.Origin("one", ("a.txt", "v1"));
        await _run.RunAsync(ListsGitHub((1, "one", one)));

        var newHead = one.Commit(("a.txt", "v2"), ("b.txt", "new"));
        var context = await _run.RunAsync(ListsGitHub((1, "one", one)));

        var repo = Repo(context, "gh-1");
        Assert.True(repo.ExistedLocally);
        Assert.True(repo.UpdatedLocally);
        Assert.Equal(newHead, repo.HeadSha);
        Assert.Equal("v2", _run.GitOutput(repo.LocalPath!, "show", "HEAD:a.txt"));
        Assert.True(context.Markers.IsDone("RepoClone", "gh-1"));
    }

    [Fact]
    public async Task AnUpdateThatFailsFlagsTheRepoButLeavesTheOldCloneAndTheOtherReposAlone()
    {
        var one = _run.Origin("one");
        var two = _run.Origin("two");
        await _run.RunAsync(ListsGitHub((1, "one", one), (2, "two", two)));
        var gone = one.Path + "-moved";
        Directory.Move(one.Path, gone);
        two.Commit(("x.txt", "x"));

        var context = await _run.RunAsync(ListsGitHub((1, "one", one), (2, "two", two)));

        Assert.True(context.Markers.IsFailed("RepoClone", "gh-1"));
        Assert.True(context.Markers.IsDone("RepoClone", "gh-2"));
        var failed = Repo(context, "gh-1");
        Assert.Equal(RepositoryStatus.Unreachable, failed.RemoteStatus);
        Assert.True(failed.ExistedLocally);
        Assert.True(File.Exists(Path.Combine(failed.LocalPath!, "HEAD")));
        Assert.Equal("hello", _run.GitOutput(failed.LocalPath!, "show", "HEAD:readme.txt")); // the old mirror is intact
        Assert.Contains("CloneFailed", File.ReadAllText(context.Paths.FailedMarker("RepoClone", "gh-1")));
        Assert.Equal(2, (await ReadStateAsync()).ToStoredRepos().Count());
    }

    [Fact]
    public async Task ACloneThatFailsLeavesNothingBehindAndIsFlagged()
    {
        var one = _run.Origin("one");
        var sources = new FakeSources
        {
            Respond = uri => uri.Query.Contains("page=1")
                ? FakeSources.GitHubList(
                    FakeSources.GitHubRepo(1, "one", one.Path),
                    FakeSources.GitHubRepo(2, "missing", Path.Combine(_run.OriginsRoot, "does-not-exist")))
                : FakeSources.GitHubList()
        };

        var context = await _run.RunAsync(sources);

        Assert.True(context.Markers.IsDone("RepoClone", "gh-1"));
        Assert.True(context.Markers.IsFailed("RepoClone", "gh-2"));
        var failed = Repo(context, "gh-2");
        Assert.False(failed.ExistedLocally || failed.UpdatedLocally);
        Assert.False(Directory.Exists(failed.LocalPath!));
        Assert.Equal(RepositoryStatus.Unreachable, failed.RemoteStatus);
        Assert.Equal(1, context.Status.Snapshot().Steps.Single(s => s.Name == "RepoClone").Failed);
    }

    [Fact]
    public async Task ABrokenCloneIsRemovedAndMadeAgain()
    {
        var one = _run.Origin("one");
        var path = Path.Combine(_run.ReposRoot, "GitHub", "acme", "one");
        Directory.CreateDirectory(Path.Combine(path, "objects"));
        File.WriteAllText(Path.Combine(path, "HEAD"), "garbage, not a ref");
        File.WriteAllText(Path.Combine(path, "leftover.txt"), "half a clone");

        var context = await _run.RunAsync(ListsGitHub((1, "one", one)));

        Assert.True(context.Markers.IsDone("RepoClone", "gh-1"));
        Assert.Equal(one.Head, Repo(context, "gh-1").HeadSha);
        Assert.False(File.Exists(Path.Combine(path, "leftover.txt")));
    }

    [Fact]
    public async Task AFolderThatIsNotACloneIsNeverDeleted()
    {
        var one = _run.Origin("one");
        var path = Path.Combine(_run.ReposRoot, "GitHub", "acme", "one");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "precious.txt"), "someone's work");

        var context = await _run.RunAsync(ListsGitHub((1, "one", one)));

        Assert.True(context.Markers.IsFailed("RepoClone", "gh-1"));
        Assert.Equal("someone's work", File.ReadAllText(Path.Combine(path, "precious.txt")));
    }

    [Fact]
    public async Task AWorkingCloneAtTheRepoPathIsRefusedNotReplaced()
    {
        var one = _run.Origin("one");
        var path = Path.Combine(_run.ReposRoot, "GitHub", "acme", "one");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var clone = new System.Diagnostics.ProcessStartInfo("git", $"clone -q \"{one.Path}\" \"{path}\"") { RedirectStandardError = true };
        using (var process = System.Diagnostics.Process.Start(clone)!)
        {
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }

        File.WriteAllText(Path.Combine(path, "uncommitted.txt"), "work in progress");

        var context = await _run.RunAsync(ListsGitHub((1, "one", one)));

        Assert.True(context.Markers.IsFailed("RepoClone", "gh-1"));
        Assert.True(File.Exists(Path.Combine(path, "uncommitted.txt")));
    }

    [Fact]
    public async Task ArchivedReposAreListedButNotClonedUnlessArchivedScanningIsOn()
    {
        var live = _run.Origin("live");
        var old = _run.Origin("old");
        var sources = new FakeSources
        {
            Respond = uri => uri.Query.Contains("page=1")
                ? FakeSources.GitHubList(
                    FakeSources.GitHubRepo(1, "live", live.Path),
                    FakeSources.GitHubRepo(2, "old", old.Path, archived: true),
                    FakeSources.GitHubRepo(3, "off", old.Path, disabled: true))
                : FakeSources.GitHubList()
        };

        var context = await _run.RunAsync(sources);

        Assert.True(Repo(context, "gh-2").ArchivedOrRetired);
        Assert.True(Repo(context, "gh-3").ArchivedOrRetired);
        Assert.False(Directory.Exists(Repo(context, "gh-2").LocalPath!));
        Assert.False(Directory.Exists(Repo(context, "gh-3").LocalPath!));
        Assert.Contains("StatusArchived", File.ReadAllText(context.Paths.SkippedMarker("RepoClone", "gh-2")));
        Assert.Contains("StatusDisabled", File.ReadAllText(context.Paths.SkippedMarker("RepoClone", "gh-3")));

        _run.With(s => s with { ScanArchived = true });
        var again = await _run.RunAsync(sources);

        Assert.True(Directory.Exists(Repo(again, "gh-2").LocalPath!));
        Assert.False(Directory.Exists(Repo(again, "gh-3").LocalPath!));
    }

    [Fact]
    public async Task GitHubIsPagedUntilAShortPage()
    {
        var origin = _run.Origin("one");
        string[] Page(int from, int count) => Enumerable.Range(from, count)
            .Select(i => FakeSources.GitHubRepo(i, $"repo{i}", origin.Path, archived: i > 1)).ToArray(); // archived ones are listed, not cloned
        var sources = new FakeSources
        {
            Respond = uri => uri.Query.Contains("page=1&") || uri.Query.EndsWith("page=1")
                ? FakeSources.GitHubList(Page(1, 100))
                : uri.Query.Contains("page=2") ? FakeSources.GitHubList(Page(101, 3)) : FakeSources.GitHubList()
        };
        _run.With(s => s with { Tools = new() { ["RepoClone"] = new ToolSettings { MaxParallel = 8, TimeoutMinutes = 2 } } });
        var context = await _run.NewRunAsync();

        await new RepoCloneTool(sources, (_, _) => Task.CompletedTask).RunAsync(context);

        Assert.Equal(103, context.Repos.Snapshot().Count);
        Assert.Equal(2, sources.Requests.Count);
        Assert.All(sources.Requests, r => Assert.Equal("Bearer " + RepoCloneRun.Token, r.Authorization));
    }

    [Fact]
    public async Task AnOrgThatCannotBeListedIsFlaggedAndItsKnownReposAreKeptNotDropped()
    {
        var one = _run.Origin("one");
        var other = _run.Origin("other");
        _run.With(s => s with
        {
            Sources = [new SourceSettings { Source = "GitHub", Org = "acme", TokenSecret = "gh-token" },
                       new SourceSettings { Source = "GitHub", Org = "globex", TokenSecret = "gh-token" }]
        });
        Func<Uri, bool> isGlobex = uri => uri.AbsolutePath.Contains("/orgs/globex/");
        var healthy = new FakeSources
        {
            Respond = uri => uri.Query.Contains("page=1")
                ? FakeSources.GitHubList(FakeSources.GitHubRepo(isGlobex(uri) ? 20 : 10, isGlobex(uri) ? "other" : "one", isGlobex(uri) ? other.Path : one.Path))
                : FakeSources.GitHubList()
        };
        await _run.RunAsync(healthy);

        var broken = new FakeSources
        {
            Respond = uri => isGlobex(uri)
                ? FakeRapid7.Json(HttpStatusCode.Forbidden, $"{{\"message\":\"nope {RepoCloneRun.Token}\"}}")
                : healthy.Respond(uri)
        };
        var context = await _run.RunAsync(broken);

        var alert = Assert.Single(context.Status.Snapshot().Alerts, a => a.Type == "OrgDiscoveryFailed");
        Assert.Contains("GitHub/globex", alert.Name);
        Assert.DoesNotContain(RepoCloneRun.Token, alert.Name);
        var state = await ReadStateAsync();
        Assert.Contains(state.ToStoredRepos(), r => r.Repo.Key == "gh-20");
        Assert.Contains(state.ToStoredRepos(), r => r.Repo.Key == "gh-10");
        Assert.Equal(RepositoryStatus.Unreachable, Repo(context, "gh-20").RemoteStatus);
        Assert.False(Repo(context, "gh-20").ExistedRemotely);
    }

    [Fact]
    public async Task WhenNoOrgCanBeListedTheStepFailsAndTheSnapshotIsLeftAlone()
    {
        var one = _run.Origin("one");
        await _run.RunAsync(ListsGitHub((1, "one", one)));
        var before = await File.ReadAllTextAsync(new FoundOrgAppRepoStore(_run.Config.StateRoot).Path);

        var down = new FakeSources { Respond = _ => FakeRapid7.Json(HttpStatusCode.Unauthorized, "{}") };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _run.RunAsync(down));

        Assert.Contains("No organization could be listed", error.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(new FoundOrgAppRepoStore(_run.Config.StateRoot).Path));
    }

    [Fact]
    public async Task AMissingTokenSecretFailsThatOrgWithoutLeakingAnything()
    {
        _run.With(s => s with { Sources = [new SourceSettings { Source = "GitHub", Org = "acme", TokenSecret = "nope" }] });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _run.RunAsync(new FakeSources()));

        Assert.Contains("No organization could be listed", error.Message);
        Assert.DoesNotContain(RepoCloneRun.Token, error.Message);
    }

    [Fact]
    public async Task ARenamedRepoHasItsFolderMovedInsteadOfBeingClonedAgain()
    {
        var one = _run.Origin("one");
        var first = await _run.RunAsync(ListsGitHub((1, "one", one)));
        var oldPath = Repo(first, "gh-1").LocalPath!;
        File.WriteAllText(Path.Combine(oldPath, "ignored-marker"), "this proves it was moved, not re-cloned");

        var renamed = new FakeSources
        {
            Respond = uri => uri.Query.Contains("page=1")
                ? FakeSources.GitHubList(FakeSources.GitHubRepo(1, "one-renamed", one.Path))
                : FakeSources.GitHubList()
        };
        var context = await _run.RunAsync(renamed);

        var newPath = Repo(context, "gh-1").LocalPath!;
        Assert.EndsWith("one-renamed", newPath);
        Assert.False(Directory.Exists(oldPath));
        Assert.True(Repo(context, "gh-1").ExistedLocally);
    }

    [Fact]
    public async Task NoSourcesIsAClearError()
    {
        _run.With(s => s with { Sources = [] });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _run.RunAsync(new FakeSources()));

        Assert.Contains("sources", error.Message);
    }

    [Fact]
    public async Task TheTokenIsNeverInTheStatusOrTheMarkers()
    {
        var one = _run.Origin("one");
        var context = await _run.RunAsync(ListsGitHub((1, "one", one)));

        var everything = string.Join("\n", Directory.EnumerateFiles(context.Paths.RunFolder, "*.json", SearchOption.AllDirectories)
            .Select(File.ReadAllText));

        Assert.DoesNotContain(RepoCloneRun.Token, everything);
    }


    private FakeSources AdoSource(FakeOrigin origin, Func<Uri, string?>? gate = null) => new()
    {
        Respond = uri =>
        {
            if (gate?.Invoke(uri) is { } denied)
            {
                return FakeRapid7.Json(HttpStatusCode.Unauthorized, denied);
            }

            var path = uri.AbsolutePath;
            if (path.EndsWith("/_apis/projects"))
            {
                var second = uri.Query.Contains("continuationToken=next%20page");
                var response = FakeRapid7.Json(HttpStatusCode.OK, second
                    ? "{\"value\":[{\"id\":\"p2\",\"name\":\"Proj B\"}]}"
                    : "{\"value\":[{\"id\":\"p1\",\"name\":\"ProjA\"}]}");
                if (!second)
                {
                    response.Headers.Add("x-ms-continuationtoken", "next page");
                }

                return response;
            }

            if (path.Contains("/p1/_apis/git/repositories"))
            {
                return FakeRapid7.Json(HttpStatusCode.OK,
                    $"{{\"value\":[{{\"id\":\"aaaa1111\",\"name\":\"svc\",\"remoteUrl\":{System.Text.Json.JsonSerializer.Serialize(origin.Path)},\"isDisabled\":false}},"
                    + "{\"id\":\"dddd4444\",\"name\":\"off\",\"remoteUrl\":\"x\",\"isDisabled\":true}]}");
            }

            return FakeRapid7.Json(HttpStatusCode.OK, "{\"value\":[]}");
        }
    };

    [Fact]
    public async Task AzureDevOpsWithAPatListsProjectsAcrossPagesAndClonesUnderOrgAndProject()
    {
        var origin = _run.Origin("svc");
        _run.With(s => s with { Sources = [new SourceSettings { Source = "ADO", Org = "vz", TokenSecret = "gh-token" }] });
        var sources = AdoSource(origin);

        var context = await _run.RunAsync(sources);

        var repo = Repo(context, "ado-aaaa1111");
        Assert.Equal(Path.Combine(_run.ReposRoot, "ADO", "vz", "ProjA", "svc"), repo.LocalPath);
        Assert.Equal(origin.Head, repo.HeadSha);
        Assert.Equal("ProjA", repo.ApplicationName);
        Assert.Equal(RepositoryStatus.Disabled, Repo(context, "ado-dddd4444").RemoteStatus);
        Assert.Contains("StatusDisabled", File.ReadAllText(context.Paths.SkippedMarker("RepoClone", "ado-dddd4444")));
        Assert.Contains(sources.Requests, r => r.Uri.Query.Contains("continuationToken=next%20page"));
        var expected = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":" + RepoCloneRun.Token));
        Assert.All(sources.Requests, r => Assert.Equal(expected, r.Authorization));
    }

    // A stand-in for the Azure CLI: lists the given tenants, and answers a token request with "tok-<tenant>".
    private static void WriteAz(string path, params string[] tenants) =>
        File.WriteAllLines(path,
        [
            "@echo off",
            "if \"%2\"==\"list\" (",
            .. tenants.Select(t => $"  echo {t}"),
            ") else (",
            "  echo tok-%4",
            ")"
        ]);

    [Fact]
    public async Task AzureDevOpsWithoutASecretUsesTheFirstEntraTenantThatCanReadTheOrg()
    {
        var origin = _run.Origin("svc");
        var az = Path.Combine(_run.Root, "az.cmd");
        WriteAz(az, "tenant-a", "tenant-b");
        _run.With(s => s with
        {
            Sources = [new SourceSettings { Source = "ADO", Org = "vz" }],
            Tools = new() { ["RepoClone"] = new ToolSettings { MaxParallel = 2, TimeoutMinutes = 2, AzPath = az } }
        });
        // tenant-a is refused, tenant-b is accepted
        var real = AdoSource(origin).Respond;
        var seen = new List<string?>();
        var handler = new FakeSources();
        handler.Respond = uri =>
        {
            var auth = handler.Requests[^1].Authorization;
            seen.Add(auth);
            return auth == "Bearer tok-tenant-a" ? FakeRapid7.Json(HttpStatusCode.Unauthorized, "{}") : real(uri);
        };

        var context = await _run.RunAsync(handler);

        Assert.True(context.Markers.IsDone("RepoClone", "ado-aaaa1111"));
        Assert.Contains("Bearer tok-tenant-a", seen);
        Assert.Contains("Bearer tok-tenant-b", seen);
        Assert.DoesNotContain("tok-tenant", System.Text.Json.JsonSerializer.Serialize(context.Status.Snapshot()));
    }

    [Fact]
    public async Task WhenNoEntraTenantCanReadTheOrgItIsFlaggedWithoutAnyToken()
    {
        var az = Path.Combine(_run.Root, "az.cmd");
        WriteAz(az, "tenant-a");
        _run.With(s => s with
        {
            Sources = [new SourceSettings { Source = "ADO", Org = "vz" }, new SourceSettings { Source = "GitHub", Org = "acme", TokenSecret = "gh-token" }],
            Tools = new() { ["RepoClone"] = new ToolSettings { MaxParallel = 2, TimeoutMinutes = 2, AzPath = az } }
        });
        var origin = _run.Origin("one");
        var handler = new FakeSources();
        handler.Respond = uri => uri.Host == "dev.azure.com"
            ? FakeRapid7.Json(HttpStatusCode.Unauthorized, "{}")
            : uri.Query.Contains("page=1") ? FakeSources.GitHubList(FakeSources.GitHubRepo(1, "one", origin.Path)) : FakeSources.GitHubList();

        var context = await _run.RunAsync(handler);

        var alert = Assert.Single(context.Status.Snapshot().Alerts, a => a.Type == "OrgDiscoveryFailed");
        Assert.Contains("ADO/vz", alert.Name);
        Assert.DoesNotContain("tok-tenant", alert.Name);
        Assert.True(context.Markers.IsDone("RepoClone", "gh-1"));
    }

    [Theory]
    [InlineData("Normal", "Normal")]
    [InlineData("My Repo", "My Repo")]
    [InlineData("a:b*c", "a_b_c")]
    [InlineData("trailing. ", "trailing")]
    [InlineData("..", "_")]
    [InlineData("", "_")]
    public void FolderNamesAreMadeSafe(string name, string expected) => Assert.Equal(expected, RepoLayout.Sanitize(name));

    [Fact]
    public void ReposWhoseNamesCollideGetTheirOwnFolders()
    {
        DiscoveredRepo Repo(string key, string name) =>
            new(key, "ADO", "org", "proj", name, "url", RepositoryStatus.Active);

        var paths = RepoLayout.Assign("C:\\r", [Repo("ado-aaaa1111", "Repo"), Repo("ado-bbbb2222", "repo")]);

        Assert.Equal(2, paths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("C:\\r\\ADO\\org\\proj\\Repo", paths["ado-aaaa1111"]);
        Assert.StartsWith("C:\\r\\ADO\\org\\proj\\repo~", paths["ado-bbbb2222"]);
    }

    [Fact]
    public void ADevOpsRepoLivesUnderItsProjectAndAGitHubRepoUnderItsOrg()
    {
        Assert.Equal("C:\\r\\ADO\\org\\proj\\x",
            RepoLayout.PathFor("C:\\r", new DiscoveredRepo("ado-1", "ADO", "org", "proj", "x", "u", RepositoryStatus.Active)));
        Assert.Equal("C:\\r\\GitHub\\org\\x",
            RepoLayout.PathFor("C:\\r", new DiscoveredRepo("gh-1", "GitHub", "org", null, "x", "u", RepositoryStatus.Active)));
    }
}
