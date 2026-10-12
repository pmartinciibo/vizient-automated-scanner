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
        Assert.Contains("GitHub acme/missing", File.ReadAllText(context.Paths.FailedMarker("RepoClone", "gh-2")));
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
        Assert.Equal(2, sources.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/repos")));
        Assert.All(sources.Requests, r => Assert.Equal("Bearer " + RepoCloneRun.Token, r.Authorization));
    }

    [Fact]
    public async Task AnOrgThatCannotBeListedIsFlaggedAndItsKnownReposAreKeptNotDropped()
    {
        var one = _run.Origin("one");
        var other = _run.Origin("other");
        _run.With(s => s with
        {
            Sources = [new SourceSettings { Name = "GitHub", Source = "GitHub", TokenSecret = "gh-token" }]
        });
        Func<Uri, bool> isGlobex = uri => uri.AbsolutePath.Contains("/orgs/globex/");
        var healthy = new FakeSources
        {
            Orgs = ["acme", "globex"],
            Respond = uri => uri.Query.Contains("page=1")
                ? FakeSources.GitHubList(FakeSources.GitHubRepo(isGlobex(uri) ? 20 : 10, isGlobex(uri) ? "other" : "one", isGlobex(uri) ? other.Path : one.Path))
                : FakeSources.GitHubList()
        };
        await _run.RunAsync(healthy);

        var broken = new FakeSources
        {
            Orgs = ["acme", "globex"],
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
        _run.With(s => s with { Sources = [new SourceSettings { Name = "GitHub", Source = "GitHub", TokenSecret = "nope" }] });

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


    private FakeSources AdoSource(FakeOrigin origin, Func<Uri, string?>? gate = null, string repoId = "aaaa1111") => new()
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
                    $"{{\"value\":[{{\"id\":\"{repoId}\",\"name\":\"svc\",\"remoteUrl\":{System.Text.Json.JsonSerializer.Serialize(origin.Path)},\"isDisabled\":false}},"
                    + "{\"id\":\"dddd4444\",\"name\":\"off\",\"remoteUrl\":\"x\",\"isDisabled\":true}]}");
            }

            return FakeRapid7.Json(HttpStatusCode.OK, "{\"value\":[]}");
        }
    };

    [Fact]
    public async Task AzureDevOpsWithAPatListsProjectsAcrossPagesAndClonesUnderOrgAndProject()
    {
        var origin = _run.Origin("svc");
        _run.With(s => s with { Sources = [new SourceSettings { Name = "ADO", Source = "ADO", Org = "vz", TokenSecret = "gh-token" }] });
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

    // ---- turning a source off for one run (request.json "sources") ----

    private FakeSources BothSources(FakeOrigin gh, FakeOrigin ado) => new()
    {
        Respond = uri => uri.Host == "dev.azure.com" ? AdoSource(ado).Respond(uri)
            : uri.Query.Contains("page=1") ? FakeSources.GitHubList(FakeSources.GitHubRepo(1, "one", gh.Path))
            : FakeSources.GitHubList()
    };

    private void ConfigureBoth() =>
        _run.With(s => s with
        {
            Sources = [new SourceSettings { Name = "GitHub", Source = "GitHub", TokenSecret = "gh-token" },
                       new SourceSettings { Name = "ADO", Source = "ADO", Org = "vz", TokenSecret = "gh-token" }]
        });

    private static RunRequestDocument WithSources(RunRequestDocument r, Dictionary<string, SourceRequestOverride> sources) =>
        r with { Tools = new() { ["RepoClone"] = new ToolRequestOverride { Sources = sources } } };

    private static RunRequestDocument Off(RunRequestDocument r, string source) =>
        WithSources(r, new() { [source] = new SourceRequestOverride { State = SourceState.Disabled } });

    [Fact]
    public async Task ASourceIsOnUnlessTheRequestTurnsItOff()
    {
        var gh = _run.Origin("gh");
        var ado = _run.Origin("svc");
        ConfigureBoth();

        var context = await _run.RunAsync(BothSources(gh, ado));

        Assert.True(context.Markers.IsDone("RepoClone", "gh-1"));
        Assert.True(context.Markers.IsDone("RepoClone", "ado-aaaa1111"));
    }

    [Fact]
    public async Task TurningAdoOffClonesOnlyGitHubAndNeverCallsAzureDevOps()
    {
        var gh = _run.Origin("gh");
        var ado = _run.Origin("svc");
        ConfigureBoth();
        var sources = BothSources(gh, ado);

        var context = await _run.RunAsync(sources, r => Off(r, "ADO"));

        Assert.True(context.Markers.IsDone("RepoClone", "gh-1"));
        Assert.DoesNotContain(context.Repos.Snapshot(), r => r.Key.StartsWith("ado-"));
        Assert.DoesNotContain(sources.Requests, r => r.Uri.Host == "dev.azure.com");
        Assert.DoesNotContain(context.Status.Snapshot().Alerts, a => a.Type == "OrgDiscoveryFailed");
        var note = Assert.Single(context.Status.Snapshot().Alerts, a => a.Type == "SourceLeftOut");
        Assert.Contains("(vz)", note.Name);
    }

    [Fact]
    public async Task TurningGitHubOffClonesOnlyAdo()
    {
        var gh = _run.Origin("gh");
        var ado = _run.Origin("svc");
        ConfigureBoth();
        var sources = BothSources(gh, ado);

        var context = await _run.RunAsync(sources, r => Off(r, "GitHub"));

        Assert.True(context.Markers.IsDone("RepoClone", "ado-aaaa1111"));
        Assert.DoesNotContain(context.Repos.Snapshot(), r => r.Key.StartsWith("gh-"));
        Assert.DoesNotContain(sources.Requests, r => r.Uri.Host == "api.github.com");
    }

    [Fact]
    public async Task WhatIsKnownOfASourceLeftOutIsKeptNotDroppedAndItsReposAreNotScanned()
    {
        var gh = _run.Origin("gh");
        var ado = _run.Origin("svc");
        ConfigureBoth();
        await _run.RunAsync(BothSources(gh, ado));

        var context = await _run.RunAsync(BothSources(gh, ado), r => Off(r, "ADO"));

        var state = await ReadStateAsync();
        Assert.Contains(state.ToStoredRepos(), r => r.Repo.Key == "ado-aaaa1111");
        Assert.Contains(state.ToStoredRepos(), r => r.Repo.Key == "gh-1");
        var left = Repo(context, "ado-aaaa1111");
        Assert.Equal(RepositoryStatus.Unreachable, left.RemoteStatus);
        Assert.False(left.ExistedRemotely);
        Assert.True(left.ExistedLocally);
        Assert.False(context.Markers.IsDone("RepoClone", "ado-aaaa1111"));
    }

    [Fact]
    public async Task TurningEverySourceOffIsAClearErrorPointingAtTheToolSwitch()
    {
        var gh = _run.Origin("gh");
        var ado = _run.Origin("svc");
        ConfigureBoth();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _run.RunAsync(BothSources(gh, ado), r => WithSources(r, new()
        {
            ["ADO"] = new SourceRequestOverride { State = SourceState.Disabled },
            ["GitHub"] = new SourceRequestOverride { State = SourceState.Disabled }
        })));

        Assert.Contains("turn off the RepoClone tool", error.Message);
    }

    [Fact]
    public async Task ASourceNameTheEngineDoesNotKnowIsRejectedWhenTheRunStarts()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => _run.NewRunAsync(r => WithSources(r, new() { ["Gitlab"] = new SourceRequestOverride { State = SourceState.Disabled } })));

        Assert.Contains("unknown sources: Gitlab", error.Message);
    }

    [Fact]
    public async Task SourcesUnderAnyToolButRepoCloneAreRejectedWhenTheRunStarts()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => _run.NewRunAsync(r => r with
        {
            Tools = new() { ["Snyk"] = new ToolRequestOverride { Sources = new() { ["ADO"] = new SourceRequestOverride { State = SourceState.Disabled } } } }
        }));

        Assert.Contains("belongs under the RepoClone tool", error.Message);
    }

    [Fact]
    public async Task ASourceMarkedCloneIsOnLikeOneNotMentioned()
    {
        var gh = _run.Origin("gh");
        var ado = _run.Origin("svc");
        ConfigureBoth();

        var context = await _run.RunAsync(BothSources(gh, ado), r => WithSources(r, new() { ["ADO"] = new SourceRequestOverride { State = SourceState.Clone } }));

        Assert.True(context.Markers.IsDone("RepoClone", "ado-aaaa1111"));
        Assert.True(context.Markers.IsDone("RepoClone", "gh-1"));
    }

    // ---- GitHub orgs are discovered from the token, never named ----

    private static FakeSources OrgsWithRepos(Dictionary<string, FakeOrigin> origins, Func<Uri, HttpResponseMessage?>? orgsRespond = null) => new()
    {
        Orgs = origins.Keys.ToArray(),
        OrgsRespond = orgsRespond,
        Respond = uri =>
        {
            var org = uri.AbsolutePath.Split('/')[2];
            return uri.Query.Contains("page=1") && origins.TryGetValue(org, out var origin)
                ? FakeSources.GitHubList(FakeSources.GitHubRepo(Array.IndexOf(origins.Keys.ToArray(), org) + 1, $"{org}-repo", origin.Path))
                : FakeSources.GitHubList();
        }
    };

    [Fact]
    public async Task EveryOrgTheTokenBelongsToIsDiscoveredAndItsReposCloned()
    {
        var origins = new Dictionary<string, FakeOrigin>
        {
            ["alpha"] = _run.Origin("a"),
            ["beta"] = _run.Origin("b"),
            ["gamma"] = _run.Origin("c")
        };
        var sources = OrgsWithRepos(origins);

        var context = await _run.RunAsync(sources);

        Assert.Equal(["alpha", "beta", "gamma"], context.Repos.Snapshot().Select(r => r.Org).Order());
        Assert.All(context.Repos.Snapshot(), r => Assert.True(context.Markers.IsDone("RepoClone", r.Key)));
        Assert.Contains(sources.Requests, r => r.Uri.AbsolutePath == "/user/orgs");
        Assert.Equal(["alpha", "beta", "gamma"], (await ReadStateAsync()).Orgs.Select(o => o.Name).Order());
    }

    [Fact]
    public async Task MoreThanAHundredOrgsAreAllDiscovered()
    {
        var last = _run.Origin("last");
        var sources = new FakeSources
        {
            Orgs = Enumerable.Range(1, 130).Select(i => $"org{i}").ToArray(),
            Respond = uri => uri.Query.Contains("page=1") && uri.AbsolutePath.EndsWith("/org130/repos")
                ? FakeSources.GitHubList(FakeSources.GitHubRepo(130, "last", last.Path))
                : FakeSources.GitHubList()
        };

        var context = await _run.RunAsync(sources);

        var only = Assert.Single(context.Repos.Snapshot());
        Assert.Equal("org130", only.Org);
        Assert.Equal(2, sources.Requests.Count(r => r.Uri.AbsolutePath == "/user/orgs"));
    }

    [Fact]
    public async Task ATokenThatSeesNoOrganizationsFailsTheStepAndLeavesTheSnapshotAlone()
    {
        var one = _run.Origin("one");
        await _run.RunAsync(OrgsWithRepos(new() { ["alpha"] = one }));
        var before = await File.ReadAllTextAsync(new FoundOrgAppRepoStore(_run.Config.StateRoot).Path);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _run.RunAsync(new FakeSources { Orgs = [] }));

        Assert.Contains("GitHub", error.Message);
        Assert.Contains("reaches no organization's repositories", error.Message);
        Assert.Equal(before, await File.ReadAllTextAsync(new FoundOrgAppRepoStore(_run.Config.StateRoot).Path));
    }

    [Fact]
    public async Task WhenTheOrganizationsOfTheTokenCannotBeListedEveryKnownGitHubOrgIsKeptNotDropped()
    {
        var origins = new Dictionary<string, FakeOrigin> { ["alpha"] = _run.Origin("a"), ["beta"] = _run.Origin("b") };
        await _run.RunAsync(OrgsWithRepos(origins));
        _run.With(s => s with
        {
            Sources = [new SourceSettings { Name = "GitHub", Source = "GitHub", TokenSecret = "gh-token" },
                       new SourceSettings { Name = "ADO", Source = "ADO", Org = "vz", TokenSecret = "gh-token" }]
        });
        var ado = _run.Origin("svc");
        var down = new FakeSources
        {
            OrgsRespond = _ => FakeRapid7.Json(HttpStatusCode.Unauthorized, "{\"message\":\"Bad credentials\"}"),
            Respond = uri => AdoSource(ado).Respond(uri)
        };

        var context = await _run.RunAsync(down);

        Assert.Contains(context.Status.Snapshot().Alerts, a => a.Type == "OrgDiscoveryFailed" && a.Name!.Contains("GitHub (organizations of the token)"));
        var state = await ReadStateAsync();
        Assert.Equal(["alpha", "beta"], state.Orgs.Where(o => o.Source == "GitHub").Select(o => o.Name).Order());
        Assert.Contains(state.ToStoredRepos(), r => r.Repo.Key.StartsWith("ado-"));
        Assert.All(context.Repos.Snapshot().Where(r => r.Source == "GitHub"), r => Assert.Equal(RepositoryStatus.Unreachable, r.RemoteStatus));
    }

    [Fact]
    public async Task AnOrgTheTokenNoLongerListsIsKeptAndFlaggedNotDropped()
    {
        var a = _run.Origin("a");
        var b = _run.Origin("b");
        await _run.RunAsync(OrgsWithRepos(new() { ["alpha"] = a, ["beta"] = b }));

        var context = await _run.RunAsync(OrgsWithRepos(new() { ["alpha"] = a }));

        var note = Assert.Single(context.Status.Snapshot().Alerts, x => x.Type == "OrgNotListed");
        Assert.Contains("GitHub/beta", note.Name);
        Assert.Equal(["alpha", "beta"], (await ReadStateAsync()).Orgs.Select(o => o.Name).Order());
        Assert.All(context.Repos.Snapshot().Where(r => r.Org == "beta"), r => Assert.False(r.ExistedRemotely));
    }

    [Fact]
    public async Task AGitHubSourceTurnedOffKeepsEveryGitHubOrgItKnew()
    {
        var origins = new Dictionary<string, FakeOrigin> { ["alpha"] = _run.Origin("a"), ["beta"] = _run.Origin("b") };
        await _run.RunAsync(OrgsWithRepos(origins));
        _run.With(s => s with
        {
            Sources = [new SourceSettings { Name = "GitHub", Source = "GitHub", TokenSecret = "gh-token" },
                       new SourceSettings { Name = "ADO", Source = "ADO", Org = "vz", TokenSecret = "gh-token" }]
        });
        var ado = _run.Origin("svc");

        var context = await _run.RunAsync(
            new FakeSources { Respond = uri => AdoSource(ado).Respond(uri) },
            r => Off(r, "GitHub"));

        Assert.Equal(["alpha", "beta"], (await ReadStateAsync()).Orgs.Where(o => o.Source == "GitHub").Select(o => o.Name).Order());
        Assert.Contains(context.Status.Snapshot().Alerts, a => a.Type == "SourceLeftOut" && a.Name!.StartsWith("GitHub "));
    }

    // ---- everything RepoClone says names the source ----

    [Fact]
    public async Task AFailedAdoRepoSaysItIsAdoAndNamesItsOrgProjectAndRepo()
    {
        var origin = _run.Origin("svc");
        _run.With(s => s with { Sources = [new SourceSettings { Name = "ADO", Source = "ADO", Org = "vz", TokenSecret = "gh-token" }] });
        var sources = AdoSource(origin);
        Directory.Move(origin.Path, origin.Path + "-gone");

        var context = await _run.RunAsync(sources);

        var text = File.ReadAllText(context.Paths.FailedMarker("RepoClone", "ado-aaaa1111"));
        Assert.Contains("ADO vz/ProjA/svc", text);
    }

    [Fact]
    public async Task ARetryNoteSaysWhichSourceIsBeingSlow()
    {
        var origin = _run.Origin("one");
        var calls = 0;
        var sources = new FakeSources
        {
            Respond = uri =>
            {
                if (!uri.Query.Contains("page=1"))
                {
                    return FakeSources.GitHubList();
                }

                return Interlocked.Increment(ref calls) == 1
                    ? FakeRapid7.Status(503)
                    : FakeSources.GitHubList(FakeSources.GitHubRepo(1, "one", origin.Path));
            }
        };
        var context = await _run.NewRunAsync();
        string? note = null;

        await new RepoCloneTool(sources, (_, _) =>
        {
            note = context.Status.Snapshot().Steps.Single(s => s.Name == "RepoClone").Message;
            return Task.CompletedTask;
        }).RunAsync(context);

        Assert.EndsWith("GitHub: HTTP 503; retrying in 1s", note);
        Assert.StartsWith("discovery: ", note);
    }

    [Fact]
    public async Task AMissingSecretNamesTheSourceItWasNeededFor()
    {
        _run.With(s => s with { Sources = [new SourceSettings { Name = "GitHub", Source = "GitHub", TokenSecret = "nope" }] });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _run.RunAsync(new FakeSources()));
        var alerts = _run.Context?.Status.Snapshot().Alerts ?? [];

        Assert.All(alerts.Where(a => a.Type == "OrgDiscoveryFailed"), a => Assert.StartsWith("GitHub", a.Name));
    }

    [Fact]
    public async Task EveryAlertRepoCloneRaisesStartsWithTheSourceItConcerns()
    {
        var a = _run.Origin("a");
        var b = _run.Origin("b");
        await _run.RunAsync(OrgsWithRepos(new() { ["alpha"] = a, ["beta"] = b }));
        _run.With(s => s with
        {
            Sources = [new SourceSettings { Name = "GitHub", Source = "GitHub", TokenSecret = "gh-token" },
                       new SourceSettings { Name = "ADO", Source = "ADO", Org = "vz", TokenSecret = "gh-token" }]
        });
        var sources = new FakeSources
        {
            Orgs = ["alpha", "gone"],
            Respond = uri => uri.Host == "dev.azure.com" ? FakeRapid7.Json(HttpStatusCode.Unauthorized, "{}") : FakeSources.GitHubList()
        };

        var context = await _run.RunAsync(sources, r => Off(r, "ADO"));

        var alerts = context.Status.Snapshot().Alerts.Where(x => x.Tool == "RepoClone").ToList();
        Assert.NotEmpty(alerts);
        Assert.All(alerts, x => Assert.Matches("^(GitHub|ADO)\\b", x.Name!));
    }

    [Fact]
    public async Task WhenTheOnlySourceLeftOnSeesNothingTheErrorSaysWhichSourceAndWhichWereOff()
    {
        _run.With(s => s with
        {
            Sources = [new SourceSettings { Name = "GitHub", Source = "GitHub", TokenSecret = "gh-token" },
                       new SourceSettings { Name = "ADO", Source = "ADO", Org = "vizientinc", TokenSecret = "ado-pat" }]
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _run.RunAsync(new FakeSources { Orgs = [], UserScopes = "repo, read:org" }, r => Off(r, "ADO")));

        Assert.Contains("GitHub (the token in secret 'gh-token')", error.Message);
        Assert.Contains("reaches no organization's repositories", error.Message);
        Assert.Contains("the account 'tester' (scopes: repo, read:org)", error.Message);
        Assert.Contains("read:org", error.Message);
        Assert.DoesNotContain("resource owner", error.Message); // that advice is for fine-grained tokens
        Assert.Contains("Turned off by the request: ADO/vizientinc", error.Message);
        var alerts = _run.Context!.Status.Snapshot().Alerts.Where(a => a.Type == "OrgDiscoveryFailed").ToList();
        Assert.All(alerts, a => Assert.StartsWith("GitHub", a.Name));
        Assert.NotEmpty(alerts);
    }

    [Fact]
    public async Task EverySourceTurnedOffNamesTheSourcesItHas()
    {
        _run.With(s => s with
        {
            Sources = [new SourceSettings { Name = "GitHub", Source = "GitHub", TokenSecret = "gh-token" },
                       new SourceSettings { Name = "ADO", Source = "ADO", Org = "vz", TokenSecret = "gh-token" }]
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _run.RunAsync(new FakeSources(), r => WithSources(r, new()
        {
            ["ADO"] = new SourceRequestOverride { State = SourceState.Disabled },
            ["GitHub"] = new SourceRequestOverride { State = SourceState.Disabled }
        })));

        Assert.Contains("(GitHub, ADO)", error.Message);
    }

    [Fact]
    public async Task AFineGrainedTokenIsRecognisedByItsMissingScopesHeader()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _run.RunAsync(new FakeSources { Orgs = [] }));

        Assert.Contains("no scopes header, which means a fine-grained token", error.Message);
    }

    [Fact]
    public async Task IfGitHubWillNotSayWhoTheTokenIsThatIsReportedInsteadOfBeingHidden()
    {
        var handler = new UserFails();
        var context = await _run.NewRunAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new RepoCloneTool(handler, (_, _) => Task.CompletedTask).RunAsync(context));

        Assert.Contains("GitHub did not say who the token is for", error.Message);
    }

    private sealed class UserFails : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/user/orgs" or "/user/repos" => FakeRapid7.Json(HttpStatusCode.OK, "[]"),
                _ => FakeRapid7.Json(HttpStatusCode.Unauthorized, "{\"message\":\"Bad credentials\"}")
            });
    }

    [Fact]
    public async Task AFineGrainedTokenWithNoMembershipListFindsItsOrgsFromTheReposItCanReach()
    {
        var origin = _run.Origin("one");
        var sources = new FakeSources
        {
            Orgs = [],
            UserRepos = [("alpha", "Organization"), ("alpha", "Organization"), ("beta", "Organization"), ("pmartin", "User")],
            Respond = uri => uri.Query.Contains("page=1") && uri.AbsolutePath == "/orgs/alpha/repos"
                ? FakeSources.GitHubList(FakeSources.GitHubRepo(1, "one", origin.Path))
                : FakeSources.GitHubList()
        };

        var context = await _run.RunAsync(sources);

        Assert.Equal(["alpha"], context.Repos.Snapshot().Select(r => r.Org));
        Assert.Contains(sources.Requests, r => r.Uri.AbsolutePath == "/orgs/alpha/repos");
        Assert.Contains(sources.Requests, r => r.Uri.AbsolutePath == "/orgs/beta/repos");
        Assert.DoesNotContain(sources.Requests, r => r.Uri.AbsolutePath.Contains("pmartin")); // a person's repos are not scanned
        Assert.True(context.Markers.IsDone("RepoClone", "gh-1"));
    }

    [Fact]
    public async Task ReposOwnedByAPersonAreNotEnoughToBeAnOrganization()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _run.RunAsync(new FakeSources
        {
            Orgs = [],
            UserRepos = [("pmartin", "User")]
        }));

        Assert.Contains("reaches no organization's repositories", error.Message);
        Assert.Contains("one resource owner", error.Message);
    }

    [Fact]
    public async Task TheMembershipListIsUsedWhenThereIsOneAndTheReposAreNotAsked()
    {
        var origin = _run.Origin("one");
        var sources = OrgsWithRepos(new() { ["alpha"] = origin });

        await _run.RunAsync(sources);

        Assert.DoesNotContain(sources.Requests, r => r.Uri.AbsolutePath == "/user/repos");
    }

    private void WithOwnRepos() =>
        _run.With(st => st with { Sources = [new SourceSettings { Name = "GitHub", Source = "GitHub", TokenSecret = "gh-token", IncludeOwnRepos = true }] });

    [Fact]
    public async Task ATokenForAPersonalAccountScansTheAccountsOwnReposWhenAskedTo()
    {
        WithOwnRepos();
        var one = _run.Origin("one");
        var two = _run.Origin("two");
        var sources = new FakeSources
        {
            Orgs = [],
            OwnRepos = [FakeSources.GitHubRepo(1, "one", one.Path), FakeSources.GitHubRepo(2, "two", two.Path, archived: true)]
        };

        var context = await _run.RunAsync(sources);

        var repo = context.Repos.Snapshot().Single(r => r.Key == "gh-1");
        Assert.Equal("tester", repo.Org);
        Assert.Equal(Path.Combine(_run.ReposRoot, "GitHub", "tester", "one"), repo.LocalPath);
        Assert.True(context.Markers.IsDone("RepoClone", "gh-1"));
        Assert.True(context.Repos.Snapshot().Single(r => r.Key == "gh-2").ArchivedOrRetired);
        Assert.Equal(["tester"], (await ReadStateAsync()).Orgs.Select(o => o.Name));
    }

    [Fact]
    public async Task TheAccountsOwnReposAreNotScannedUnlessTheSourceAsksForThem()
    {
        var one = _run.Origin("one");
        var sources = new FakeSources { Orgs = [], OwnRepos = [FakeSources.GitHubRepo(1, "one", one.Path)] };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _run.RunAsync(sources));

        Assert.Contains("includeOwnRepos", error.Message);
        Assert.DoesNotContain(sources.Requests, r => r.Uri.Query.Contains("affiliation=owner&") || r.Uri.Query.EndsWith("affiliation=owner"));
    }

    [Fact]
    public async Task OrgsAndTheAccountsOwnReposAreScannedTogether()
    {
        WithOwnRepos();
        var mine = _run.Origin("mine");
        var theirs = _run.Origin("theirs");
        var sources = new FakeSources
        {
            Orgs = ["alpha"],
            OwnRepos = [FakeSources.GitHubRepo(1, "mine", mine.Path)],
            Respond = uri => uri.Query.Contains("page=1") && uri.AbsolutePath == "/orgs/alpha/repos"
                ? FakeSources.GitHubList(FakeSources.GitHubRepo(2, "theirs", theirs.Path))
                : FakeSources.GitHubList()
        };

        var context = await _run.RunAsync(sources);

        Assert.Equal(["alpha", "tester"], context.Repos.Snapshot().Select(r => r.Org).Order());
    }

    private void WithNamedOrgs(params string[] orgs) =>
        _run.With(st => st with { Sources = [new SourceSettings { Name = "GitHub", Source = "GitHub", TokenSecret = "gh-token", Orgs = [.. orgs] }] });

    [Fact]
    public async Task ANamedOrgIsScannedEvenWhenTheTokenCannotDiscoverIt()
    {
        WithNamedOrgs("preftech");
        var origin = _run.Origin("one");
        var sources = new FakeSources
        {
            Orgs = [],
            Respond = uri => uri.Query.Contains("page=1") && uri.AbsolutePath == "/orgs/preftech/repos"
                ? FakeSources.GitHubList(FakeSources.GitHubRepo(7, "tinydb", origin.Path))
                : FakeSources.GitHubList()
        };

        var context = await _run.RunAsync(sources);

        var repo = Assert.Single(context.Repos.Snapshot());
        Assert.Equal("preftech", repo.Org);
        Assert.True(context.Markers.IsDone("RepoClone", "gh-7"));
        Assert.Equal(["preftech"], (await ReadStateAsync()).Orgs.Select(o => o.Name));
    }

    [Fact]
    public async Task NamedOrgsAreAddedToTheDiscoveredOnesAndAnOrgFoundBothWaysIsListedOnce()
    {
        WithNamedOrgs("alpha", "ALPHA", "beta");
        var a = _run.Origin("a");
        var b = _run.Origin("b");
        var sources = new FakeSources
        {
            Orgs = ["alpha"],
            Respond = uri => uri.Query.Contains("page=1")
                ? uri.AbsolutePath == "/orgs/alpha/repos" ? FakeSources.GitHubList(FakeSources.GitHubRepo(1, "a", a.Path))
                : uri.AbsolutePath == "/orgs/beta/repos" ? FakeSources.GitHubList(FakeSources.GitHubRepo(2, "b", b.Path))
                : FakeSources.GitHubList()
                : FakeSources.GitHubList()
        };

        var context = await _run.RunAsync(sources);

        Assert.Equal(["alpha", "beta"], context.Repos.Snapshot().Select(r => r.Org).Order());
        Assert.Equal(1, sources.Requests.Count(r => r.Uri.AbsolutePath == "/orgs/alpha/repos"));
    }

    [Fact]
    public async Task ANamedOrgThatCannotBeListedIsFlaggedForThatOrgAloneAndTheRestCarryOn()
    {
        WithNamedOrgs("real", "missing");
        var origin = _run.Origin("one");
        var sources = new FakeSources
        {
            Orgs = [],
            Respond = uri => uri.AbsolutePath == "/orgs/missing/repos"
                ? FakeRapid7.Json(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}")
                : uri.Query.Contains("page=1") && uri.AbsolutePath == "/orgs/real/repos"
                    ? FakeSources.GitHubList(FakeSources.GitHubRepo(1, "one", origin.Path))
                    : FakeSources.GitHubList()
        };

        var context = await _run.RunAsync(sources);

        var alert = Assert.Single(context.Status.Snapshot().Alerts, a => a.Type == "OrgDiscoveryFailed");
        Assert.StartsWith("GitHub/missing", alert.Name);
        Assert.True(context.Markers.IsDone("RepoClone", "gh-1"));
    }

    // ---- the state of a source in the request: Disabled, ListOnly or Clone ----

    private static RunRequestDocument ListOnly(RunRequestDocument r, string source) =>
        WithSources(r, new() { [source] = new SourceRequestOverride { State = SourceState.ListOnly } });

    [Fact]
    public async Task ListOnlyListsAndRecordsTheReposButClonesNothing()
    {
        var gh = _run.Origin("gh");
        var ado = _run.Origin("svc");
        ConfigureBoth();

        var context = await _run.RunAsync(BothSources(gh, ado), r => ListOnly(r, "ADO"));

        var listed = Repo(context, "ado-aaaa1111");
        Assert.True(listed.ExistedRemotely);
        Assert.False(listed.ExistedLocally);
        Assert.False(Directory.Exists(listed.LocalPath!));
        Assert.Contains("ListOnly", File.ReadAllText(context.Paths.SkippedMarker("RepoClone", "ado-aaaa1111")));
        Assert.False(context.Markers.IsDone("RepoClone", "ado-aaaa1111"));
        // The other source is cloned as usual.
        Assert.True(context.Markers.IsDone("RepoClone", "gh-1"));
        var note = Assert.Single(context.Status.Snapshot().Alerts, a => a.Type == "SourceListOnly");
        Assert.StartsWith("ADO", note.Name);
        // What was listed is in the snapshot, so a later Clone run knows about it.
        Assert.Contains((await ReadStateAsync()).ToStoredRepos(), r => r.Repo.Key == "ado-aaaa1111");
    }

    [Fact]
    public async Task ListOnlyLeavesAnExistingMirrorExactlyAsItWas()
    {
        var gh = _run.Origin("gh");
        var ado = _run.Origin("svc");
        ConfigureBoth();
        var first = await _run.RunAsync(BothSources(gh, ado));
        var path = Repo(first, "ado-aaaa1111").LocalPath!;
        ado.Commit(("new.txt", "a change at the remote"));
        var before = _run.GitOutput(path, "rev-parse", "HEAD");

        var context = await _run.RunAsync(BothSources(gh, ado), r => ListOnly(r, "ADO"));

        Assert.Equal(before, _run.GitOutput(path, "rev-parse", "HEAD")); // not updated
        var repo = Repo(context, "ado-aaaa1111");
        Assert.True(repo.ExistedLocally);
        Assert.False(repo.UpdatedLocally);
        Assert.Equal(path, repo.LocalPath);
    }

    [Fact]
    public async Task ListOnlyDoesNotMoveARenamedReposFolder()
    {
        var one = _run.Origin("one");
        var first = await _run.RunAsync(ListsGitHub((1, "one", one)));
        var oldPath = Repo(first, "gh-1").LocalPath!;
        var renamed = new FakeSources
        {
            Respond = uri => uri.Query.Contains("page=1")
                ? FakeSources.GitHubList(FakeSources.GitHubRepo(1, "one-renamed", one.Path))
                : FakeSources.GitHubList()
        };

        var context = await _run.RunAsync(renamed, r => ListOnly(r, "GitHub"));

        Assert.True(Directory.Exists(oldPath));
        Assert.False(Directory.Exists(oldPath + "-renamed"));
        Assert.Equal(oldPath, Repo(context, "gh-1").LocalPath);
    }

    [Fact]
    public async Task ASourceWithNoStateIsClonedLikeOneMarkedClone()
    {
        var origin = _run.Origin("one");

        var context = await _run.RunAsync(ListsGitHub((1, "one", origin)), r => WithSources(r, new() { ["GitHub"] = new SourceRequestOverride() }));

        Assert.True(context.Markers.IsDone("RepoClone", "gh-1"));
    }

    [Fact]
    public async Task AStateThatIsNotDisabledListOnlyOrCloneIsRejectedWhenTheRunStarts()
    {
        var file = Path.Combine(_run.Root, "bad-request.json");
        File.WriteAllText(file, """
            { "runId": "r1", "tools": { "RepoClone": { "sources": { "ADO": { "state": "Skip" } } } } }
            """);

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => RunContext.StartAsync(_run.Config, file));
    }

    [Fact]
    public async Task TheOldEnabledMemberIsRefusedNotIgnoredBecauseIgnoringItWouldCloneASourceTheUserTurnedOff()
    {
        var file = Path.Combine(_run.Root, "old-request.json");
        File.WriteAllText(file, """
            { "runId": "r1", "tools": { "RepoClone": { "sources": { "ADO": { "enabled": false } } } } }
            """);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => RunContext.StartAsync(_run.Config, file));

        Assert.Contains("tools.RepoClone.sources.ADO has \"enabled\"", error.Message);
        Assert.Contains("Disabled, ListOnly or Clone", error.Message);
    }

    [Fact]
    public async Task TheStateNamesAreReadFromTheFileAsText()
    {
        ConfigureBoth();
        var file = Path.Combine(_run.Root, "request-states.json");
        File.WriteAllText(file, """
            { "runId": "r2", "tools": { "RepoClone": { "sources": {
                "ADO": { "state": "ListOnly" }, "GitHub": { "state": "Disabled" } } } } }
            """);

        var context = await RunContext.StartAsync(_run.Config, file);

        Assert.Equal(SourceState.ListOnly, context.Request.StateOf("ADO"));
        Assert.Equal(SourceState.Disabled, context.Request.StateOf("GitHub"));
        Assert.False(context.Request.IsSourceEnabled("GitHub"));
        Assert.True(context.Request.IsSourceEnabled("ADO"));
    }

    [Fact]
    public async Task AnAdoEntryWithSeveralOrgsListsEachWithTheOnePat()
    {
        var one = _run.Origin("svc");
        _run.With(st => st with
        {
            Sources = [new SourceSettings { Name = "ADO", Source = "ADO", Org = "vz", Orgs = ["vz", "rnd"], TokenSecret = "gh-token" }]
        });
        var sources = AdoSource(one);

        var context = await _run.RunAsync(sources);

        Assert.Contains(sources.Requests, r => r.Uri.AbsolutePath == "/vz/_apis/projects");
        Assert.Contains(sources.Requests, r => r.Uri.AbsolutePath == "/rnd/_apis/projects");
        var expected = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(":" + RepoCloneRun.Token));
        Assert.All(sources.Requests, r => Assert.Equal(expected, r.Authorization));
        Assert.Contains(context.Repos.Snapshot(), r => r.Org == "vz");
    }

    [Fact]
    public async Task OneAdoOrgThatFailsIsFlaggedAloneAndTheOtherStillRuns()
    {
        var one = _run.Origin("svc");
        _run.With(st => st with
        {
            Sources = [new SourceSettings { Name = "ADO", Source = "ADO", Orgs = ["bad", "vz"], TokenSecret = "gh-token" }]
        });
        var good = AdoSource(one);
        var sources = new FakeSources
        {
            Respond = uri => uri.AbsolutePath.StartsWith("/bad/") ? FakeRapid7.Json(HttpStatusCode.Unauthorized, "{}") : good.Respond(uri)
        };

        var context = await _run.RunAsync(sources);

        var alert = Assert.Single(context.Status.Snapshot().Alerts, a => a.Type == "OrgDiscoveryFailed");
        Assert.StartsWith("ADO/bad", alert.Name);
        Assert.True(context.Markers.IsDone("RepoClone", "ado-aaaa1111"));
    }

    // ---- an entry can be switched on its own by its name ----

    private void TwoAdoEntries() =>
        _run.With(st => st with
        {
            Sources = [new SourceSettings { Name = "ADO", Source = "ADO", Org = "vz", TokenSecret = "gh-token" },
                       new SourceSettings { Source = "ADO", Name = "Claro", Org = "claro", TokenSecret = "gh-token" }]
        });

    [Fact]
    public async Task AnEntryIsSwitchedByItsNameAndTheOtherOneIsLeftAlone()
    {
        TwoAdoEntries();
        var origin = _run.Origin("svc");
        var sources = AdoSource(origin);

        var context = await _run.RunAsync(sources, r => WithSources(r, new()
        {
            ["ADO"] = new SourceRequestOverride { State = SourceState.Clone },
            ["Claro"] = new SourceRequestOverride { State = SourceState.Disabled }
        }));

        Assert.Contains(sources.Requests, r => r.Uri.AbsolutePath.StartsWith("/vz/"));
        Assert.DoesNotContain(sources.Requests, r => r.Uri.AbsolutePath.StartsWith("/claro/"));
        var note = Assert.Single(context.Status.Snapshot().Alerts, a => a.Type == "SourceLeftOut");
        Assert.Contains("(claro)", note.Name);
    }

    [Fact]
    public async Task EachEntryFollowsItsOwnNameInTheRequest()
    {
        TwoAdoEntries();
        var vz = _run.Origin("vzsvc");
        var claro = _run.Origin("clarosvc");
        var vzSource = AdoSource(vz, repoId: "aaaa1111");
        var claroSource = AdoSource(claro, repoId: "bbbb2222");
        var sources = new FakeSources { Respond = uri => uri.AbsolutePath.StartsWith("/claro/") ? claroSource.Respond(uri) : vzSource.Respond(uri) };

        var context = await _run.RunAsync(sources, r => WithSources(r, new()
        {
            ["ADO"] = new SourceRequestOverride { State = SourceState.ListOnly },
            ["Claro"] = new SourceRequestOverride { State = SourceState.Clone }
        }));

        // vz is only listed. Claro is cloned. Each follows the name of its own entry.
        Assert.False(Directory.Exists(Repo(context, "ado-aaaa1111").LocalPath!));
        Assert.True(Directory.Exists(Repo(context, "ado-bbbb2222").LocalPath!));
        Assert.Contains("ListOnly", File.ReadAllText(context.Paths.SkippedMarker("RepoClone", "ado-aaaa1111")));
        Assert.True(context.Markers.IsDone("RepoClone", "ado-bbbb2222"));
    }

    [Fact]
    public async Task ARequestNamingAnEntryThatEngineJsonLacksIsRejectedAndSaysWhatIsAvailable()
    {
        TwoAdoEntries();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => _run.NewRunAsync(r => WithSources(r, new()
        {
            ["Nobody"] = new SourceRequestOverride { State = SourceState.Disabled }
        })));

        Assert.Contains("unknown sources: Nobody", error.Message);
        Assert.Contains("Claro", error.Message);
    }

    // ---- results.json: every org engine.json names is there, with how it went ----

    private static OrgOutcome OrgOf(RunContext context, string source, string org) =>
        context.Repos.Orgs().Single(o => o.Source == source && o.Name == org);

    [Fact]
    public async Task AnOrgThatListedSaysHowManyReposWereFound()
    {
        var origin = _run.Origin("one");

        var context = await _run.RunAsync(OrgsWithRepos(new() { ["alpha"] = origin }));

        Assert.Equal("1 repos found", OrgOf(context, "GitHub", "alpha").StatusMessage);
    }

    [Fact]
    public async Task AnOrgNamedInEngineJsonThatCannotBeListedIsStillInTheResultsWithTheError()
    {
        WithNamedOrgs("real", "missing");
        var origin = _run.Origin("one");
        var sources = new FakeSources
        {
            Orgs = [],
            Respond = uri => uri.AbsolutePath == "/orgs/missing/repos"
                ? FakeRapid7.Json(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}")
                : uri.Query.Contains("page=1") && uri.AbsolutePath == "/orgs/real/repos"
                    ? FakeSources.GitHubList(FakeSources.GitHubRepo(1, "one", origin.Path))
                    : FakeSources.GitHubList()
        };

        var context = await _run.RunAsync(sources);

        var missing = OrgOf(context, "GitHub", "missing");
        Assert.Empty(missing.Applications);
        Assert.False(missing.ExistedRemotely);
        Assert.Contains("HTTP 404", missing.StatusMessage);
        Assert.Equal("1 repos found", OrgOf(context, "GitHub", "real").StatusMessage);
    }

    [Fact]
    public async Task AnOrgWithNoReposIsListedAndSaysZeroFound()
    {
        WithNamedOrgs("empty");
        var sources = new FakeSources { Orgs = [], Respond = _ => FakeSources.GitHubList() };

        var context = await _run.RunAsync(sources);

        var empty = OrgOf(context, "GitHub", "empty");
        Assert.Equal("0 repos found", empty.StatusMessage);
        Assert.Empty(empty.Applications);
    }

    [Fact]
    public async Task AnAdoOrgThatFailsIsInTheResultsWithItsError()
    {
        var one = _run.Origin("svc");
        _run.With(st => st with
        {
            Sources = [new SourceSettings { Name = "Ado", Source = "ADO", Orgs = ["bad", "vz"], TokenSecret = "gh-token" }]
        });
        var good = AdoSource(one);
        var sources = new FakeSources
        {
            Respond = uri => uri.AbsolutePath.StartsWith("/bad/") ? FakeRapid7.Json(HttpStatusCode.Unauthorized, "{}") : good.Respond(uri)
        };

        var context = await _run.RunAsync(sources);

        Assert.Contains("HTTP 401", OrgOf(context, "ADO", "bad").StatusMessage);
        Assert.Equal("2 repos found", OrgOf(context, "ADO", "vz").StatusMessage); // the fake lists one repo and one disabled one
    }

    [Fact]
    public async Task OrgsOfASourceTurnedOffAreInTheResultsSayingSo()
    {
        var gh = _run.Origin("gh");
        var ado = _run.Origin("svc");
        ConfigureBoth();
        await _run.RunAsync(BothSources(gh, ado));

        var context = await _run.RunAsync(BothSources(gh, ado), r => Off(r, "ADO"));

        Assert.Equal("turned off by the request", OrgOf(context, "ADO", "vz").StatusMessage);
    }

    [Fact]
    public async Task ANamedOrgInASourceTurnedOffIsAlsoListedWithTheReason()
    {
        _run.With(st => st with
        {
            Sources = [new SourceSettings { Name = "GitHub", Source = "GitHub", TokenSecret = "gh-token", Orgs = ["preftech"] },
                       new SourceSettings { Name = "ADO", Source = "ADO", Org = "vz", TokenSecret = "gh-token" }]
        });
        var ado = _run.Origin("svc");

        var context = await _run.RunAsync(new FakeSources { Respond = uri => AdoSource(ado).Respond(uri) }, r => Off(r, "GitHub"));

        Assert.Equal("turned off by the request", OrgOf(context, "GitHub", "preftech").StatusMessage);
        Assert.Equal("2 repos found", OrgOf(context, "ADO", "vz").StatusMessage); // the fake lists one repo and one disabled one
    }

    [Fact]
    public async Task WhenTheTokensOrgsCannotBeListedEveryNamedAndKnownOrgSaysWhy()
    {
        var a = _run.Origin("a");
        await _run.RunAsync(OrgsWithRepos(new() { ["alpha"] = a }));
        WithNamedOrgs("preftech");
        var ado = _run.Origin("svc");
        _run.With(st => st with
        {
            Sources = [.. st.Sources, new SourceSettings { Name = "ADO", Source = "ADO", Org = "vz", TokenSecret = "gh-token" }]
        });
        var down = new FakeSources
        {
            OrgsRespond = _ => FakeRapid7.Json(HttpStatusCode.Unauthorized, "{\"message\":\"Bad credentials\"}"),
            Respond = uri => AdoSource(ado).Respond(uri)
        };

        var context = await _run.RunAsync(down);

        Assert.StartsWith("not listed:", OrgOf(context, "GitHub", "preftech").StatusMessage);
        Assert.Contains("Bad credentials", OrgOf(context, "GitHub", "preftech").StatusMessage);
        Assert.StartsWith("kept from the last scan", OrgOf(context, "GitHub", "alpha").StatusMessage);
    }

    [Fact]
    public async Task TheStatusMessageIsWrittenToResultsJson()
    {
        var origin = _run.Origin("one");
        var context = await _run.RunAsync(OrgsWithRepos(new() { ["alpha"] = origin }));

        var json = System.Text.Json.JsonSerializer.Serialize(
            context.Repos.Orgs(),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains("\"statusMessage\":\"1 repos found\"", json);
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
            Sources = [new SourceSettings { Name = "ADO", Source = "ADO", Org = "vz" }],
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
            Sources = [new SourceSettings { Name = "ADO", Source = "ADO", Org = "vz" }, new SourceSettings { Name = "GitHub", Source = "GitHub", TokenSecret = "gh-token" }],
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
