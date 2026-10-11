namespace Vizient.AutomatedScanner.Tests;

using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.RepoClone;

public class RepoExpanderTests : IDisposable
{
    private readonly RepoCloneRun _run = new();

    public void Dispose() => _run.Dispose();

    private async Task<(RunContext Context, RepoOutcome Repo, FakeOrigin Origin)> MirroredAsync(params (string File, string Content)[] files)
    {
        var origin = _run.Origin("one", files.Length == 0 ? [("src/app.cs", "class A {}")] : files);
        var sources = new FakeSources
        {
            Respond = uri => uri.Query.Contains("page=1")
                ? FakeSources.GitHubList(FakeSources.GitHubRepo(1, "one", origin.Path))
                : FakeSources.GitHubList()
        };
        var context = await _run.RunAsync(sources);
        return (context, context.Repos.Snapshot().Single(), origin);
    }

    [Fact]
    public async Task AMirrorIsExpandedIntoAWorkingTreeWithTheDefaultBranchCheckedOut()
    {
        var (context, repo, origin) = await MirroredAsync(("src/app.cs", "class A {}"));

        await using var expanded = await new RepoExpander(context).ExpandAsync(repo, CancellationToken.None);

        Assert.Equal("class A {}", File.ReadAllText(Path.Combine(expanded.Path, "src", "app.cs")));
        Assert.Equal(origin.Head, expanded.HeadSha);
        Assert.StartsWith(Path.Combine(_run.Config.TmpRoot, "expanded"), expanded.Path);
        Assert.NotEqual(repo.LocalPath, expanded.Path);
    }

    [Fact]
    public async Task DisposingTheCopyDeletesItIncludingGitsReadOnlyFilesAndLeavesTheMirror()
    {
        var (context, repo, _) = await MirroredAsync();
        var expanded = await new RepoExpander(context).ExpandAsync(repo, CancellationToken.None);
        var path = expanded.Path;
        Assert.True(Directory.Exists(Path.Combine(path, ".git")));

        await expanded.DisposeAsync();

        Assert.False(Directory.Exists(path));
        Assert.True(File.Exists(Path.Combine(repo.LocalPath!, "HEAD")));
    }

    [Fact]
    public async Task TheCopyIsAnIndependentTreeSoAScannerWritingToItNeverTouchesTheMirror()
    {
        var (context, repo, origin) = await MirroredAsync();
        await using var expanded = await new RepoExpander(context).ExpandAsync(repo, CancellationToken.None);

        File.WriteAllText(Path.Combine(expanded.Path, "build-output.bin"), "junk");
        File.WriteAllText(Path.Combine(expanded.Path, "src", "app.cs"), "changed by a build");

        Assert.Equal(origin.Head, _run.GitOutput(repo.LocalPath!, "rev-parse", "HEAD"));
        Assert.Equal("class A {}", _run.GitOutput(repo.LocalPath!, "show", "HEAD:src/app.cs"));
    }

    [Fact]
    public async Task ACopyLeftByACrashedRunIsReplaced()
    {
        var (context, repo, _) = await MirroredAsync();
        var first = await new RepoExpander(context).ExpandAsync(repo, CancellationToken.None);
        File.WriteAllText(Path.Combine(first.Path, "leftover.txt"), "from the crash");

        await using var second = await new RepoExpander(context).ExpandAsync(repo, CancellationToken.None);

        Assert.Equal(first.Path, second.Path);
        Assert.False(File.Exists(Path.Combine(second.Path, "leftover.txt")));
        Assert.True(File.Exists(Path.Combine(second.Path, "src", "app.cs")));
    }

    [Fact]
    public async Task ExpandingARepoWithNoMirrorFailsClearlyAndMakesNothing()
    {
        var (context, repo, _) = await MirroredAsync();
        var missing = repo with { LocalPath = Path.Combine(_run.ReposRoot, "nowhere") };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new RepoExpander(context).ExpandAsync(missing, CancellationToken.None));

        Assert.Contains("no mirror", error.Message);
        Assert.False(Directory.Exists(Path.Combine(_run.Config.TmpRoot, "expanded", context.Paths.RunId, repo.Key)));
    }

    [Fact]
    public async Task ARepoWithNoCommitsExpandsToAnEmptyTreeWithNoSha()
    {
        var origin = Path.Combine(_run.OriginsRoot, "empty");
        Directory.CreateDirectory(origin);
        using (var init = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "init -q -b main") { WorkingDirectory = origin })!)
        {
            init.WaitForExit();
        }

        var sources = new FakeSources
        {
            Respond = uri => uri.Query.Contains("page=1")
                ? FakeSources.GitHubList(FakeSources.GitHubRepo(1, "empty", origin))
                : FakeSources.GitHubList()
        };
        var context = await _run.RunAsync(sources);
        var repo = context.Repos.Snapshot().Single();

        Assert.True(context.Markers.IsDone("RepoClone", repo.Key));
        Assert.Null(repo.HeadSha);

        await using var expanded = await new RepoExpander(context).ExpandAsync(repo, CancellationToken.None);

        Assert.Null(expanded.HeadSha);
        Assert.True(Directory.Exists(expanded.Path));
    }
}
