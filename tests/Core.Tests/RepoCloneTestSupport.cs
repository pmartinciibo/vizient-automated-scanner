namespace Vizient.AutomatedScanner.Tests;

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Secrets;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.Tools;

/// <summary>A local git repo standing in for a remote. A plain folder path is a valid clone URL.</summary>
public sealed class FakeOrigin
{
    public FakeOrigin(string folder, params (string File, string Content)[] files)
    {
        Path = folder;
        Directory.CreateDirectory(folder);
        Git("init", "-q", "-b", "main");
        Commit(files);
    }

    public string Path { get; }

    public string Head => Git("rev-parse", "HEAD");

    public string Commit(params (string File, string Content)[] files)
    {
        foreach (var (file, content) in files)
        {
            var full = System.IO.Path.Combine(Path, file);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        Git("add", "-A");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "change " + Guid.NewGuid().ToString("N")[..6]);
        return Head;
    }

    public string HeadOf(string branch) => Git("rev-parse", branch);

    /// <summary>A new branch with a commit on it, leaving main as the checked-out branch.</summary>
    public void Branch(string name, params (string File, string Content)[] files)
    {
        Git("checkout", "-q", "-b", name);
        Commit(files);
        Git("checkout", "-q", "main");
    }

    public void DeleteBranch(string name) => Git("branch", "-q", "-D", name);

    private string Git(params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = Path, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output.Trim();
    }
}

/// <summary>A stand-in for the GitHub and Azure DevOps APIs. Responses are chosen by a test; every request is recorded.</summary>
public sealed class FakeSources : HttpMessageHandler
{
    private readonly object _gate = new();

    public List<(Uri Uri, string? Authorization)> Requests { get; } = new();

    public Func<Uri, HttpResponseMessage?> Respond { get; set; } = _ => null;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
        }

        return Task.FromResult(Respond(request.RequestUri!) ?? FakeRapid7.Json(HttpStatusCode.NotFound, "{\"message\":\"unknown\"}"));
    }

    public static string GitHubRepo(long id, string name, string cloneUrl, bool archived = false, bool disabled = false) =>
        JsonSerializer.Serialize(new { id, name, clone_url = cloneUrl, archived, disabled });

    public static HttpResponseMessage GitHubList(params string[] repos) =>
        FakeRapid7.Json(HttpStatusCode.OK, $"[{string.Join(",", repos)}]");
}

/// <summary>A run, a config with one GitHub org and its token, and a repos root, for running RepoClone against local git origins.</summary>
public sealed class RepoCloneRun : IDisposable
{
    public const string Token = "gh-TOKEN-VALUE-77aa11-ddcc";
    private static int _counter;

    public RepoCloneRun(Func<EngineSettings, EngineSettings>? settings = null)
    {
        Root = System.IO.Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);

        var store = new SecretStore();
        store.Set("gh-token", Token, null, DateTimeOffset.UtcNow);
        store.SaveAsync(System.IO.Path.Combine(Root, "secrets.dat")).GetAwaiter().GetResult();

        var baseSettings = new EngineSettings
        {
            Sources = [new SourceSettings { Source = "GitHub", Org = "acme", TokenSecret = "gh-token" }],
            Retry = new RetrySettings { MaxAttempts = 2, BackoffSeconds = 0 },
            Tools = new() { ["RepoClone"] = new ToolSettings { MaxParallel = 4, TimeoutMinutes = 2 } }
        };
        Config = new EngineConfig(
            System.IO.Path.Combine(Root, "engine.json"), System.IO.Path.Combine(Root, "repos"), System.IO.Path.Combine(Root, "runs"),
            System.IO.Path.Combine(Root, "state"), System.IO.Path.Combine(Root, "tmp"), System.IO.Path.Combine(Root, "logs"),
            Settings: (settings ?? (s => s))(baseSettings));
    }

    public string Root { get; }
    public EngineConfig Config { get; private set; }
    public RunContext? Context { get; private set; }
    public string OriginsRoot => System.IO.Path.Combine(Root, "origins");
    public string ReposRoot => Config.ReposRoot;

    public FakeOrigin Origin(string name, params (string File, string Content)[] files) =>
        new(System.IO.Path.Combine(OriginsRoot, name), files.Length == 0 ? [("readme.txt", "hello")] : files);

    /// <summary>Starts a new run (a new run folder) over the same state and repos root.</summary>
    public async Task<RunContext> NewRunAsync()
    {
        var requestFile = System.IO.Path.Combine(Root, "requests", $"request-{Interlocked.Increment(ref _counter)}.json");
        await AtomicJsonFile.WriteAsync(requestFile, new RunRequestDocument { RunId = $"run-{Guid.NewGuid():N}"[..12], Mode = ScanMode.Normal });
        Context = await RunContext.StartAsync(Config, requestFile);
        return Context;
    }

    public async Task<RunContext> RunAsync(FakeSources sources, Action<Exception>? onError = null)
    {
        var context = await NewRunAsync();
        await new RepoCloneTool(sources, (_, _) => Task.CompletedTask).RunAsync(context);
        return context;
    }

    public void UseExclusions(string file) => Config = Config with { ExclusionsFile = file };

    public void With(Func<EngineSettings, EngineSettings> change) => Config = Config with { Settings = change(Config.Current) };

    /// <summary>Runs git in a folder and returns what it prints. For looking inside a mirror.</summary>
    public string GitOutput(string folder, params string[] args)
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("safe.directory=*");
        info.ArgumentList.Add("-C");
        info.ArgumentList.Add(folder);
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return output.Trim();
    }

    public static string Local(string path) => path.Replace('\\', '/');

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                }
                catch (IOException)
                {
                }
            }

            Directory.Delete(Root, recursive: true);
        }
    }
}
