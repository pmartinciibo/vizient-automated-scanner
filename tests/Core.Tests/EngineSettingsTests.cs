namespace Vizient.AutomatedScanner.Tests;

using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.Tools;
using Vizient.AutomatedScanner.Core.Work;

public class EngineSettingsTests : IDisposable
{
    private const string RunId = "2026-10-10T12-00-00Z";
    private const string MinimalPaths = """
        "paths": { "reposRoot": "repos", "runs": "runs", "state": "state", "tmp": "tmp", "logs": "logs" }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
    private readonly string _configFile;
    private readonly string _requestFile;

    public EngineSettingsTests()
    {
        Directory.CreateDirectory(_root);
        _configFile = Path.Combine(_root, "engine.json");
        _requestFile = Path.Combine(_root, "requests", "request.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private Task<EngineConfig> LoadAsync(string extraJson = "")
    {
        var body = string.IsNullOrWhiteSpace(extraJson) ? MinimalPaths : $"{MinimalPaths}, {extraJson}";
        File.WriteAllText(_configFile, "{ \"schemaVersion\": 3, " + body + " }");
        return EngineConfig.LoadAsync(_configFile, CancellationToken.None);
    }

    private async Task<RunContext> StartRunAsync(EngineConfig config, Dictionary<string, ToolRequestOverride>? tools = null)
    {
        await AtomicJsonFile.WriteAsync(_requestFile, new RunRequestDocument { RunId = RunId, Tools = tools ?? new() });
        return await RunContext.StartAsync(config, _requestFile);
    }

    // ---- loading ----

    [Fact]
    public async Task AFileWithNoToolSettingsLoadsWithTheBuiltInDefaults()
    {
        var config = await LoadAsync();

        Assert.Empty(config.Current.Tools);
        Assert.Empty(config.Current.Sources);
        Assert.False(config.Current.ScanArchived);
        Assert.Equal((true, 5, 30), (config.Current.Skip.Enabled, config.Current.Skip.CanaryPercent, config.Current.Skip.FullRescanEveryDays));
        Assert.Equal((2, 60), (config.Current.Retry.MaxAttempts, config.Current.Retry.BackoffSeconds));
        Assert.Null(config.Current.Tool("Semgrep").MaxParallel);
    }

    [Fact]
    public async Task EverySettingInTheDesignsExampleIsRead()
    {
        var config = await LoadAsync("""
            "sources": [
              { "source": "ADO", "name": "n1", "org": "vizientinc", "tokenSecret": "ado-pat" },
              { "source": "GitHub", "name": "n2", "tokenSecret": "github-token" }
            ],
            "scanArchived": true,
            "skip": { "enabled": false, "canaryPercent": 10, "fullRescanEveryDays": 14 },
            "retry": { "maxAttempts": 3, "backoffSeconds": 5 },
            "tools": {
              "Rapid7": { "enabled": true, "tokenSecret": "rapid7-token", "baseUrl": "https://example.invalid/api" },
              "RepoClone": { "maxParallel": 12, "timeoutMinutes": 60, "longPaths": true },
              "TruffleHog": { "version": "3.x", "rulesVersion": "detectors-3.x", "mode": "filesystem", "verify": false },
              "Semgrep": { "packs": ["p/default", "p/owasp-top-ten"] },
              "Snyk": {
                "mode": "dependencies",
                "env": { "SNYK_TOKEN": "snyk-token" },
                "runAs": { "account": "DOMAIN\\svc-buildtools", "passwordSecret": "buildtools-account-password" }
              },
              "SonarStandard": { "serverUrl": "http://localhost:9000", "env": { "SONAR_TOKEN": "sonar-token" } }
            }
            """);

        var settings = config.Current;
        Assert.Equal(["ADO|vizientinc|ado-pat", "GitHub||github-token"],
            settings.Sources.Select(s => $"{s.Source}|{s.Org}|{s.TokenSecret}"));
        Assert.True(settings.ScanArchived);
        Assert.Equal((false, 10, 14), (settings.Skip.Enabled, settings.Skip.CanaryPercent, settings.Skip.FullRescanEveryDays));
        Assert.Equal((3, 5), (settings.Retry.MaxAttempts, settings.Retry.BackoffSeconds));
        Assert.Equal("https://example.invalid/api", settings.Tool("Rapid7").BaseUrl);
        Assert.Equal("rapid7-token", settings.Tool("Rapid7").TokenSecret);
        Assert.Equal((12, 60, true), (settings.Tool("RepoClone").MaxParallel, settings.Tool("RepoClone").TimeoutMinutes, settings.Tool("RepoClone").LongPaths));
        Assert.Equal(("3.x", "detectors-3.x", "filesystem", false),
            (settings.Tool("TruffleHog").Version, settings.Tool("TruffleHog").RulesVersion, settings.Tool("TruffleHog").Mode, settings.Tool("TruffleHog").Verify));
        Assert.Equal(["p/default", "p/owasp-top-ten"], settings.Tool("Semgrep").Packs);
        Assert.Equal("dependencies", settings.Tool("Snyk").Mode);
        Assert.Equal("snyk-token", settings.Tool("Snyk").Env!["SNYK_TOKEN"]);
        Assert.Equal(("DOMAIN\\svc-buildtools", "buildtools-account-password"),
            (settings.Tool("Snyk").RunAs!.Account, settings.Tool("Snyk").RunAs!.PasswordSecret));
        Assert.Equal("http://localhost:9000", settings.Tool("SonarStandard").ServerUrl);
    }

    [Fact]
    public async Task TheEngineExampleJsonInTheRepoIsValid()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Vizient.AutomatedScanner.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var config = await EngineConfig.LoadAsync(Path.Combine(dir.FullName, "engine.example.json"), CancellationToken.None);

        Assert.Equal(ToolNames.All.Count, config.Current.Tools.Count);
        Assert.NotEmpty(config.Current.Sources);
        Assert.Equal(8, config.Current.Tool("Semgrep").MaxParallel);
    }

    // ---- validation ----

    [Theory]
    [InlineData("""{ "tools": { "Semgrp": { "enabled": true } } }""", "Semgrp")]
    [InlineData("""{ "tools": { "Semgrep": { "maxParallel": 0 } } }""", "tools.Semgrep.maxParallel")]
    [InlineData("""{ "tools": { "Semgrep": { "maxParallel": 1000 } } }""", "tools.Semgrep.maxParallel")]
    [InlineData("""{ "tools": { "Semgrep": { "timeoutMinutes": 0 } } }""", "tools.Semgrep.timeoutMinutes")]
    [InlineData("""{ "tools": { "Snyk": { "mode": "everything" } } }""", "tools.Snyk.mode")]
    [InlineData("""{ "tools": { "Snyk": { "runAs": { "account": "svc" } } } }""", "tools.Snyk.runAs")]
    [InlineData("""{ "tools": { "Snyk": { "env": { "SNYK_TOKEN": "" } } } }""", "tools.Snyk.env")]
    [InlineData("""{ "retry": { "maxAttempts": 0 } }""", "retry.maxAttempts")]
    [InlineData("""{ "retry": { "backoffSeconds": -1 } }""", "retry.backoffSeconds")]
    [InlineData("""{ "skip": { "canaryPercent": 101 } }""", "skip.canaryPercent")]
    [InlineData("""{ "skip": { "canaryPercent": -1 } }""", "skip.canaryPercent")]
    [InlineData("""{ "skip": { "fullRescanEveryDays": 0 } }""", "skip.fullRescanEveryDays")]
    [InlineData("""{ "sources": [ { "source": "GitLab", "org": "x" } ] }""", "sources[0].source")]
    [InlineData("""{ "sources": [ { "source": "ADO", "name": "n3", "org": "" } ] }""", "sources[0] needs")]
    [InlineData("""{ "sources": [ { "source": "ADO", "name": "n4", "org": "a" }, { "source": "ADO", "name": "n5", "org": "A" } ] }""", "sources[1]")]
    public async Task ABadSettingIsRejectedAtLoadWithAMessageNamingIt(string settingsJson, string expectedInMessage)
    {
        // Each case is one JSON object, so splice its members into the top level of the file.
        var members = settingsJson.Trim()[1..^1];

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => LoadAsync(members));

        Assert.StartsWith("engine.json:", error.Message);
        Assert.Contains(expectedInMessage, error.Message);
    }

    [Fact]
    public async Task AnAdoOrgAndAGitHubSourceAreFine()
    {
        var config = await LoadAsync("""
            "sources": [ { "source": "ADO", "name": "n6", "org": "acme" }, { "source": "GitHub", "name": "n7", "tokenSecret": "t" } ]
            """);

        Assert.Equal(2, config.Current.Sources.Count);
    }

    // ---- limits: request, then engine.json, then the built-in default ----

    [Fact]
    public async Task ToolLimitsComeFromEngineJsonWhenItSetsThem()
    {
        var config = await LoadAsync("""
            "retry": { "maxAttempts": 3, "backoffSeconds": 7 },
            "tools": { "Semgrep": { "maxParallel": 2, "timeoutMinutes": 5 } }
            """);
        var context = await StartRunAsync(config);

        var limits = ToolLimits.For(context, "Semgrep");

        Assert.Equal((2, TimeSpan.FromMinutes(5)), (limits.MaxParallel, limits.Timeout));
        Assert.Equal((3, TimeSpan.FromSeconds(7)), (limits.Retry.MaxAttempts, limits.Retry.Backoff));
    }

    [Fact]
    public async Task ASettingEngineJsonLeavesOutFallsBackToTheBuiltInDefault()
    {
        var config = await LoadAsync("""
            "tools": { "Semgrep": { "maxParallel": 2 } }
            """);
        var context = await StartRunAsync(config);

        Assert.Equal(TimeSpan.FromMinutes(30), ToolLimits.For(context, "Semgrep").Timeout);
        Assert.Equal((4, TimeSpan.FromMinutes(45)), (ToolLimits.For(context, "Snyk").MaxParallel, ToolLimits.For(context, "Snyk").Timeout));
        Assert.Equal(2, ToolLimits.For(context, "Semgrep").Retry.MaxAttempts);
    }

    [Fact]
    public async Task ARequestOverrideBeatsEngineJsonForThatRunOnly()
    {
        var config = await LoadAsync("""
            "tools": { "Semgrep": { "maxParallel": 8, "timeoutMinutes": 30 } }
            """);
        var context = await StartRunAsync(config, new() { ["Semgrep"] = new ToolRequestOverride { MaxParallel = 3 } });

        var limits = ToolLimits.For(context, "Semgrep");

        Assert.Equal(3, limits.MaxParallel);                       // from the request
        Assert.Equal(TimeSpan.FromMinutes(30), limits.Timeout);    // not overridden, so engine.json
    }

    // ---- enabled ----

    [Fact]
    public async Task AToolEngineJsonTurnsOffIsOffAndTheStatusSaysWhy()
    {
        var config = await LoadAsync("""
            "tools": { "Snyk": { "enabled": false } }
            """);

        var context = await StartRunAsync(config);

        Assert.False(context.IsEnabled("Snyk"));
        Assert.True(context.IsEnabled("Semgrep"));
        var step = context.Status.Snapshot().Steps.Single(s => s.Name == "Snyk");
        Assert.Equal((ToolRunState.Disabled, "disabled in engine.json"), (step.Status, step.Message));
    }

    [Fact]
    public async Task ARequestCanTurnOnForOneRunATooEngineJsonHasOff()
    {
        var config = await LoadAsync("""
            "tools": { "Snyk": { "enabled": false } }
            """);

        var context = await StartRunAsync(config, new() { ["Snyk"] = new ToolRequestOverride { Enabled = true } });

        Assert.True(context.IsEnabled("Snyk"));
        Assert.Equal(ToolRunState.Pending, context.Status.Snapshot().Steps.Single(s => s.Name == "Snyk").Status);
    }

    [Fact]
    public async Task ARequestCanTurnOffAToolEngineJsonHasOn()
    {
        var config = await LoadAsync("""
            "tools": { "Snyk": { "enabled": true } }
            """);

        var context = await StartRunAsync(config, new() { ["Snyk"] = new ToolRequestOverride { Enabled = false } });

        Assert.False(context.IsEnabled("Snyk"));
        Assert.Equal("disabled in request.json", context.Status.Snapshot().Steps.Single(s => s.Name == "Snyk").Message);
    }

    // ---- scanArchived ----

    private sealed class Scanner : ScanToolBase
    {
        public override string Name => "Semgrep";

        protected override Task<RepoWorkResult> ScanRepoAsync(RepoWorkContext work, CancellationToken cancellationToken) =>
            Task.FromResult(RepoWorkResult.Done(new ToolDoneMarker { Sha = "a1b2" }));
    }

    private static RepoOutcome ArchivedRepo() => new()
    {
        Key = "gh-001", Source = "GitHub", Org = "RnD-Vizient", Name = "old",
        LocalPath = "C:\\repos\\old", ExistedLocally = true, ExistedRemotely = true,
        RemoteStatus = RepositoryStatus.Archived
    };

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ArchivedReposAreScannedOnlyWhenScanArchivedIsOn(bool scanArchived, bool skipped)
    {
        var config = await LoadAsync($"\"scanArchived\": {scanArchived.ToString().ToLowerInvariant()}");
        var context = await StartRunAsync(config);
        context.Repos.Record(ArchivedRepo());

        await new Scanner().RunAsync(context);

        Assert.Equal(skipped, File.Exists(context.Paths.SkippedMarker("Semgrep", "gh-001")));
        Assert.Equal(!skipped, context.Markers.IsDone("Semgrep", "gh-001"));
    }
}
