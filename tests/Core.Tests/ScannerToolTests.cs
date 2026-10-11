namespace Vizient.AutomatedScanner.Tests;

using System.Net;
using System.Text.Json;
using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Processes;
using Vizient.AutomatedScanner.Core.Tools;

/// <summary>A stand-in for SonarQube: system status, the analysis task, and the issues search with its 10,000 limit.</summary>
public sealed class FakeSonar
{
    public const string Url = "http://sonar.test";
    private int _taskCalls;

    public string TaskStatus { get; set; } = "SUCCESS";

    /// <summary>How many polls say PENDING before the real status.</summary>
    public int PendingPolls { get; set; }

    /// <summary>The issue total for a query's filters (for example "types=BUG"). Null filters is the whole project.</summary>
    public Func<string, int> TotalFor { get; set; } = _ => 3;

    /// <summary>Adds to every page's count, to model a server whose pages disagree with its total.</summary>
    public int MissingIssues { get; set; }

    public List<Uri> Requests { get; } = new();
    public List<string?> Authorizations { get; } = new();

    public FakeSources Handler => new()
    {
        Respond = uri =>
        {
            lock (Requests)
            {
                Requests.Add(uri);
            }

            return uri.AbsolutePath switch
            {
                "/api/system/status" => FakeRapid7.Json(HttpStatusCode.OK, "{\"status\":\"UP\"}"),
                "/api/ce/task" => Task(),
                "/api/issues/search" => Issues(uri),
                _ => FakeRapid7.Json(HttpStatusCode.NotFound, "{}")
            };
        }
    };

    private HttpResponseMessage Task()
    {
        var status = Interlocked.Increment(ref _taskCalls) <= PendingPolls ? "PENDING" : TaskStatus;
        return FakeRapid7.Json(HttpStatusCode.OK, $"{{\"task\":{{\"status\":\"{status}\",\"errorMessage\":\"the server said no\"}}}}");
    }

    private HttpResponseMessage Issues(Uri uri)
    {
        var query = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        var filters = string.Join("&", query.Where(q => q.Key is "types" or "severities").OrderBy(q => q.Key).Select(q => $"{q.Key}={q.Value}"));
        var total = TotalFor(filters);
        var size = int.Parse(query["ps"]);
        var page = int.Parse(query["p"]);

        var available = Math.Max(0, total - MissingIssues);
        var count = Math.Clamp(available - (page - 1) * size, 0, size);
        var issues = Enumerable.Range(0, count).Select(i => $"{{\"key\":\"{filters}-{(page - 1) * size + i}\"}}");
        return FakeRapid7.Json(HttpStatusCode.OK, $"{{\"total\":{total},\"p\":{page},\"ps\":{size},\"paging\":{{\"total\":{total}}},\"issues\":[{string.Join(",", issues)}]}}");
    }
}

public class ScannerToolTests : IDisposable
{
    private const string SonarToken = "sonar-TOKEN-VALUE-91b7-4e0c";
    private const string SnykToken = "snyk-TOKEN-VALUE-55aa-77cc";
    private const string AccountPassword = "P@ssw0rd-of-the-build-account";

    private readonly ScanWorld _world = new();

    public void Dispose() => _world.Dispose();

    private string Program(string name)
    {
        var path = Path.Combine(_world.Root, "bin", name + ".exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not a real program; the runner is replaced in these tests");
        return path;
    }

    private async Task<RunContext> CloneAsync(params (string File, string Content)[] files)
    {
        _world.AddOrigin(files.Length == 0 ? [("readme.txt", "hello"), ("src/app.js", "x")] : files);
        return await _world.CloneAsync();
    }

    private static ProcessResult WriteOutput(ProcessSpec spec, string content, int exitCode = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(spec.StdOutFile!)!);
        File.WriteAllText(spec.StdOutFile!, content);
        return new ProcessResult { Outcome = ProcessOutcome.Succeeded, ExitCode = exitCode, Attempts = 1, Message = $"exited with code {exitCode}" };
    }

    private string ResultText(string tool, int repo = 1) => File.ReadAllText(_world.Context.Paths.ResultFile(tool, $"gh-{repo}"));

    // ---------------------------------------------------------------- TruffleHog

    [Fact]
    public async Task TruffleHogScansTheWorkingCopyInFilesystemModeWithVerificationOff()
    {
        var exe = Program("trufflehog");
        _world.WithTool("TruffleHog", t => t with { ExePath = exe, Version = "3", RulesVersion = "3" })
              .WithExclusions(@".*\.dll$", "bin");
        await CloneAsync();
        var runner = _world.Intercept(spec => spec.FileName == exe ? WriteOutput(spec, "{\"a\":1}\n{\"a\":2}\n") : null);

        await new TruffleHogTool().RunAsync(_world.Context);

        var spec = runner.For(exe).Single();
        Assert.Equal("filesystem", spec.Arguments[0]);
        Assert.Equal(spec.WorkingDirectory, spec.Arguments[1]);
        Assert.Contains(spec.Arguments, a => a == "--json");
        Assert.Contains(spec.Arguments, a => a == "--no-verification");
        Assert.DoesNotContain(spec.Arguments, a => a.StartsWith("--results"));
        Assert.Contains(spec.Arguments, a => a.StartsWith("--exclude-paths=") && a.EndsWith("exclusions.txt"));
        Assert.True(_world.Done("TruffleHog", 1));
        Assert.Contains("\"findingCount\": 2", _world.DoneText("TruffleHog", 1));
        Assert.Equal("{\"a\":1}\n{\"a\":2}\n", ResultText("TruffleHog"));
    }

    [Fact]
    public async Task TruffleHogInGitModeReadsTheMirrorItselfAndNeedsNoWorkingCopy()
    {
        var exe = Program("trufflehog");
        _world.WithTool("TruffleHog", t => t with { ExePath = exe, Mode = "git", Verify = true });
        await CloneAsync();
        var runner = _world.Intercept(spec => spec.FileName == exe ? WriteOutput(spec, "") : null);

        await new TruffleHogTool().RunAsync(_world.Context);

        var spec = runner.For(exe).Single();
        Assert.Equal("git", spec.Arguments[0]);
        Assert.Equal("file://" + _world.Repo(1).LocalPath!.Replace('\\', '/'), spec.Arguments[1]);
        Assert.Contains("--results=verified,unknown", spec.Arguments);
        Assert.DoesNotContain("--no-verification", spec.Arguments);
        Assert.Null(spec.WorkingDirectory);
        Assert.Contains("\"findingCount\": 0", _world.DoneText("TruffleHog", 1));
        Assert.Empty(_world.LeftoverCopies());
    }

    [Fact]
    public async Task TruffleHogWithAnUnknownModeFailsBeforeAnyRepo()
    {
        var exe = Program("trufflehog");
        _world.WithTool("TruffleHog", t => t with { ExePath = exe, Mode = "history" });
        await CloneAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new TruffleHogTool().RunAsync(_world.Context));

        Assert.Contains("filesystem", error.Message);
    }

    [Fact]
    public async Task ATruffleHogRunThatFailsIsAFailedMarkerWithItsError()
    {
        var exe = Program("trufflehog");
        _world.WithTool("TruffleHog", t => t with { ExePath = exe });
        await CloneAsync();
        _world.Intercept(spec => spec.FileName == exe ? RecordingRunner.Fail(1, "no such detector") : null);

        await new TruffleHogTool().RunAsync(_world.Context);

        Assert.True(_world.Failed("TruffleHog", 1));
        Assert.Contains("no such detector", _world.FailedText("TruffleHog", 1));
        Assert.Empty(_world.LeftoverCopies());
    }

    // ---------------------------------------------------------------- Semgrep

    [Fact]
    public async Task SemgrepRunsOnceWithEachPinnedPackAndTheExclusionsAsGlobs()
    {
        var exe = Program("semgrep");
        _world.WithTool("Semgrep", t => t with { ExePath = exe, Packs = ["p/default", "p/owasp-top-ten"] })
              .WithExclusions(@".*\.yml$", "node_modules", @".*\.dll$");
        await CloneAsync();
        var runner = _world.Intercept(spec => spec.FileName == exe ? WriteOutput(spec, "{\"results\":[{},{},{}],\"errors\":[]}") : null);

        await new SemgrepTool().RunAsync(_world.Context);

        var spec = runner.For(exe).Single();
        var args = string.Join(' ', spec.Arguments);
        Assert.StartsWith("scan --config p/default --config p/owasp-top-ten --json --metrics off", args);
        Assert.Contains("--exclude *.yml --exclude node_modules --exclude *.dll", args);
        Assert.Equal(".", spec.Arguments[^1]);
        Assert.Equal("1", spec.Environment["PYTHONUTF8"]);
        Assert.Contains("\"findingCount\": 3", _world.DoneText("Semgrep", 1));
    }

    [Fact]
    public async Task SemgrepWithNoPacksNamedUsesTheDefaultPackBecauseAutoCannotRunWithMetricsOff()
    {
        var exe = Program("semgrep");
        _world.WithTool("Semgrep", t => t with { ExePath = exe });
        await CloneAsync();
        var runner = _world.Intercept(spec => spec.FileName == exe ? WriteOutput(spec, "{\"results\":[]}") : null);

        await new SemgrepTool().RunAsync(_world.Context);

        Assert.Equal(["--config", "p/default"], runner.For(exe).Single().Arguments.Skip(1).Take(2));
    }

    [Fact]
    public async Task SemgrepOutputThatIsNotItsJsonIsAParseError()
    {
        var exe = Program("semgrep");
        _world.WithTool("Semgrep", t => t with { ExePath = exe });
        await CloneAsync();
        _world.Intercept(spec => spec.FileName == exe ? WriteOutput(spec, "Traceback (most recent call last)") : null);

        await new SemgrepTool().RunAsync(_world.Context);

        Assert.True(_world.Failed("Semgrep", 1));
        Assert.Contains("ParseError", _world.FailedText("Semgrep", 1));
    }

    // ---------------------------------------------------------------- Snyk

    [Fact]
    public async Task SnykCodeTestsTheWorkingCopyWithTheOrgAndOnlyThePlainNamedExclusions()
    {
        var exe = Program("snyk");
        _world.WithTool("Snyk", t => t with { ExePath = exe, Org = "org-1234", Env = new() { ["SNYK_TOKEN"] = "snyk-token" } })
              .WithExclusions(@".*\.yml$", "bin", "obj", "node_modules")
              .AddSecret("snyk-token", SnykToken);
        await CloneAsync();
        var runner = _world.Intercept(spec => spec.FileName == exe
            ? WriteOutput(spec, "{\"runs\":[{\"results\":[{},{}]},{\"results\":[{}]}]}", exitCode: 1)
            : null);

        await new SnykTool().RunAsync(_world.Context);

        var spec = runner.For(exe).Single();
        Assert.Equal(["code", "test", "--json", "--org=org-1234", "--exclude=bin,obj,node_modules"], spec.Arguments);
        Assert.Equal(SnykToken, spec.SecretEnvironment["SNYK_TOKEN"]);
        Assert.Equal([0, 1, 3], spec.SuccessExitCodes);
        Assert.Contains("\"findingCount\": 3", _world.DoneText("Snyk", 1));
    }

    [Fact]
    public async Task SnykDependenciesRunsOpenSourceTestOnAllProjectsAndIsNeverSkipped()
    {
        var exe = Program("snyk");
        _world.WithTool("Snyk", t => t with { ExePath = exe, Mode = "dependencies", Version = "1", RulesVersion = "1", Env = new() { ["SNYK_TOKEN"] = "snyk-token" } })
              .With(s => s with { Skip = new SkipSettings { CanaryPercent = 0, FullRescanEveryDays = 365 } })
              .AddSecret("snyk-token", SnykToken);
        await CloneAsync();
        var runner = _world.Intercept(spec => spec.FileName == exe ? WriteOutput(spec, "[{\"vulnerabilities\":[{},{}]},{\"vulnerabilities\":[]}]") : null);

        await new SnykTool().RunAsync(_world.Context);
        await _world.NextRunAsync();
        runner = _world.Intercept(spec => spec.FileName == exe ? WriteOutput(spec, "[{\"vulnerabilities\":[{}]}]") : null);
        await new SnykTool().RunAsync(_world.Context);

        Assert.Equal(["test", "--all-projects", "--json"], runner.For(exe).Single().Arguments);
        Assert.False(_world.Skipped("Snyk", 1)); // unchanged code, scanned anyway
        Assert.Contains("\"findingCount\": 1", _world.DoneText("Snyk", 1));
    }

    [Fact]
    public async Task SnykExitThreeMeansNothingToScanAndIsACleanRunWithNoFindings()
    {
        var exe = Program("snyk");
        _world.WithTool("Snyk", t => t with { ExePath = exe, Env = new() { ["SNYK_TOKEN"] = "snyk-token" } }).AddSecret("snyk-token", SnykToken);
        await CloneAsync();
        _world.Intercept(spec => spec.FileName == exe ? WriteOutput(spec, "", exitCode: 3) : null);

        await new SnykTool().RunAsync(_world.Context);

        Assert.True(_world.Done("Snyk", 1));
        Assert.Contains("\"findingCount\": 0", _world.DoneText("Snyk", 1));
    }

    [Fact]
    public async Task SnykExitTwoIsAFailureWithTheErrorOfTheRun()
    {
        var exe = Program("snyk");
        _world.WithTool("Snyk", t => t with { ExePath = exe, Env = new() { ["SNYK_TOKEN"] = "snyk-token" } }).AddSecret("snyk-token", SnykToken);
        await CloneAsync();
        _world.Intercept(spec => spec.FileName == exe ? RecordingRunner.Fail(2, "Authentication failed") : null);

        await new SnykTool().RunAsync(_world.Context);

        Assert.True(_world.Failed("Snyk", 1));
        Assert.Contains("Authentication failed", _world.FailedText("Snyk", 1));
    }

    [Fact]
    public async Task SnykRunsAsTheLockedDownAccountWhenEngineJsonNamesOne()
    {
        var exe = Program("snyk");
        _world.WithTool("Snyk", t => t with
              {
                  ExePath = exe,
                  Env = new() { ["SNYK_TOKEN"] = "snyk-token" },
                  RunAs = new RunAsSettings { Account = @"CORP\svc-build", PasswordSecret = "build-password" }
              })
              .AddSecret("snyk-token", SnykToken)
              .AddSecret("build-password", AccountPassword);
        await CloneAsync();
        var runner = _world.Intercept(spec => spec.FileName == exe ? WriteOutput(spec, "[]") : null);

        await new SnykTool().RunAsync(_world.Context);

        var spec = runner.For(exe).Single();
        Assert.Equal(new RunAsAccount(@"CORP\svc-build", AccountPassword), spec.RunAs);
    }

    [Fact]
    public async Task AToolGetsOnlyItsOwnSecretsAndEveryOtherToolsVariableIsCleared()
    {
        var exe = Program("snyk");
        _world.WithTool("Snyk", t => t with { ExePath = exe, Env = new() { ["SNYK_TOKEN"] = "snyk-token" } })
              .WithTool("SonarStandard", t => t with { Env = new() { ["SONAR_TOKEN"] = "sonar-token" } })
              .AddSecret("snyk-token", SnykToken)
              .AddSecret("sonar-token", SonarToken);
        await CloneAsync();
        var runner = _world.Intercept(spec => spec.FileName == exe ? WriteOutput(spec, "[]") : null);

        await new SnykTool().RunAsync(_world.Context);

        var spec = runner.For(exe).Single();
        Assert.Equal(["SNYK_TOKEN"], spec.SecretEnvironment.Keys);
        Assert.Contains("SONAR_TOKEN", spec.RemoveEnvironment);
    }

    // ---------------------------------------------------------------- preparing

    [Fact]
    public async Task AMissingProgramFailsTheToolOnceWithAMessageThatNamesTheSetting()
    {
        _world.WithTool("Semgrep", t => t with { ExePath = Path.Combine(_world.Root, "nowhere", "semgrep.exe") });
        await CloneAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new SemgrepTool().RunAsync(_world.Context));

        Assert.Contains("tools.Semgrep.exePath", error.Message);
        Assert.False(_world.Failed("Semgrep", 1)); // no marker for any repo: the tool never started
    }

    [Fact]
    public async Task AMissingSecretFailsTheToolOnceAndNamesTheSecret()
    {
        var exe = Program("snyk");
        _world.WithTool("Snyk", t => t with { ExePath = exe, Env = new() { ["SNYK_TOKEN"] = "snyk-token" } });
        await CloneAsync();

        var error = await Assert.ThrowsAsync<Core.Secrets.SecretUnavailableException>(() => new SnykTool().RunAsync(_world.Context));

        Assert.Contains("snyk-token", error.Message);
        Assert.DoesNotContain(SnykToken, error.Message);
    }

    // ---------------------------------------------------------------- Sonar

    private void ConfigureSonar(string tool, FakeSonar sonar, Func<ToolSettings, ToolSettings>? more = null)
    {
        var exe = Program(tool == "SonarDotNet" ? "dotnet-sonarscanner" : "sonar-scanner");
        _world.WithTool(tool, t => (more ?? (x => x))(t with { ExePath = exe, ServerUrl = FakeSonar.Url, Env = new() { ["SONAR_TOKEN"] = "sonar-token" } }))
              .AddSecret("sonar-token", SonarToken);
        _ = sonar;
    }

    private static ProcessResult ScannerOk(ProcessSpec spec, string reportTaskFolder)
    {
        var folder = Path.Combine(spec.WorkingDirectory!, reportTaskFolder);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "report-task.txt"), $"projectKey=x\nceTaskUrl={FakeSonar.Url}/api/ce/task?id=AX1\n");
        return RecordingRunner.Ok();
    }

    [Fact]
    public async Task SonarStandardScansWithTheRepoKeyWaitsForTheAnalysisAndFetchesTheIssues()
    {
        var sonar = new FakeSonar { PendingPolls = 2, TotalFor = _ => 7 };
        ConfigureSonar("SonarStandard", sonar);
        _world.WithExclusions(@".*\.yml$", "bin");
        await CloneAsync();
        var exe = _world.Context.Config.Current.Tool("SonarStandard").ExePath!;
        var runner = _world.Intercept(spec => spec.FileName == exe ? ScannerOk(spec, ".scannerwork") : null);

        await new SonarStandardTool(sonar.Handler, (_, _) => Task.CompletedTask).RunAsync(_world.Context);

        var spec = runner.For(exe).Single();
        Assert.Contains("-Dsonar.projectKey=gh-1", spec.Arguments);
        Assert.Contains($"-Dsonar.host.url={FakeSonar.Url}", spec.Arguments);
        Assert.Contains("-Dsonar.exclusions=**/*.yml,**/bin/**", spec.Arguments);
        Assert.DoesNotContain(spec.Arguments, a => a.Contains(SonarToken));
        Assert.Equal(SonarToken, spec.SecretEnvironment["SONAR_TOKEN"]);

        Assert.True(_world.Done("SonarStandard", 1));
        Assert.Contains("\"findingCount\": 7", _world.DoneText("SonarStandard", 1));
        using var result = JsonDocument.Parse(ResultText("SonarStandard"));
        Assert.Equal("gh-1", result.RootElement.GetProperty("project").GetString());
        Assert.Equal(7, result.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(7, result.RootElement.GetProperty("issues").GetArrayLength());
        Assert.Contains(sonar.Requests, r => r.AbsolutePath == "/api/ce/task");
        Assert.Empty(_world.LeftoverCopies());
    }

    [Fact]
    public async Task SonarStandardLeavesADotNetRepoToTheDotNetScanner()
    {
        var sonar = new FakeSonar();
        ConfigureSonar("SonarStandard", sonar);
        await CloneAsync(("app.sln", "sln"), ("src/a.cs", "x"));
        var exe = _world.Context.Config.Current.Tool("SonarStandard").ExePath!;
        var runner = _world.Intercept(spec => spec.FileName == exe ? ScannerOk(spec, ".scannerwork") : null);

        await new SonarStandardTool(sonar.Handler, (_, _) => Task.CompletedTask).RunAsync(_world.Context);

        Assert.Empty(runner.For(exe));
        Assert.True(_world.Skipped("SonarStandard", 1));
        Assert.Contains("NotApplicable", _world.SkippedReason("SonarStandard", 1));
    }

    [Fact]
    public async Task AFailedAnalysisIsAFailureNotAPartialResult()
    {
        var sonar = new FakeSonar { TaskStatus = "FAILED" };
        ConfigureSonar("SonarStandard", sonar);
        await CloneAsync();
        var exe = _world.Context.Config.Current.Tool("SonarStandard").ExePath!;
        _world.Intercept(spec => spec.FileName == exe ? ScannerOk(spec, ".scannerwork") : null);

        await new SonarStandardTool(sonar.Handler, (_, _) => Task.CompletedTask).RunAsync(_world.Context);

        Assert.True(_world.Failed("SonarStandard", 1));
        Assert.Contains("AnalysisFailed", _world.FailedText("SonarStandard", 1));
        Assert.Contains("the server said no", _world.FailedText("SonarStandard", 1));
        Assert.False(File.Exists(_world.Context.Paths.ResultFile("SonarStandard", "gh-1")));
    }

    [Fact]
    public async Task APageThatStopsShortOfTheServersTotalIsAParseErrorNotASilentPartialResult()
    {
        var sonar = new FakeSonar { TotalFor = _ => 5, MissingIssues = 1 };
        ConfigureSonar("SonarStandard", sonar);
        await CloneAsync();
        var exe = _world.Context.Config.Current.Tool("SonarStandard").ExePath!;
        _world.Intercept(spec => spec.FileName == exe ? ScannerOk(spec, ".scannerwork") : null);

        await new SonarStandardTool(sonar.Handler, (_, _) => Task.CompletedTask).RunAsync(_world.Context);

        Assert.True(_world.Failed("SonarStandard", 1));
        Assert.Contains("ParseError", _world.FailedText("SonarStandard", 1));
        Assert.False(File.Exists(_world.Context.Paths.ResultFile("SonarStandard", "gh-1")));
        Assert.Empty(Directory.GetFiles(_world.Context.Paths.ResultsDir("SonarStandard"), ".*.tmp"));
    }

    [Fact]
    public async Task MoreThanTenThousandIssuesAreFetchedInSlicesAndAddUpToTheServersTotal()
    {
        // 12,000 in all: 6,000 bugs, 4,000 code smells, 2,000 vulnerabilities. Each slice is under the limit.
        var sonar = new FakeSonar
        {
            TotalFor = filters => filters switch
            {
                "" => 12_000,
                "types=CODE_SMELL" => 4_000,
                "types=BUG" => 6_000,
                "types=VULNERABILITY" => 2_000,
                _ => 0
            }
        };
        ConfigureSonar("SonarStandard", sonar);
        await CloneAsync();
        var exe = _world.Context.Config.Current.Tool("SonarStandard").ExePath!;
        _world.Intercept(spec => spec.FileName == exe ? ScannerOk(spec, ".scannerwork") : null);

        await new SonarStandardTool(sonar.Handler, (_, _) => Task.CompletedTask).RunAsync(_world.Context);

        Assert.True(_world.Done("SonarStandard", 1));
        using var result = JsonDocument.Parse(ResultText("SonarStandard"));
        Assert.Equal(12_000, result.RootElement.GetProperty("issues").GetArrayLength());
        Assert.All(sonar.Requests.Where(r => r.AbsolutePath == "/api/issues/search"), r => Assert.DoesNotContain("p=21", r.Query));
    }

    [Fact]
    public async Task ASliceStillOverTheLimitAfterSlicingByTypeAndSeverityIsAnErrorNotATruncation()
    {
        var sonar = new FakeSonar { TotalFor = _ => 12_000 };
        ConfigureSonar("SonarStandard", sonar);
        await CloneAsync();
        var exe = _world.Context.Config.Current.Tool("SonarStandard").ExePath!;
        _world.Intercept(spec => spec.FileName == exe ? ScannerOk(spec, ".scannerwork") : null);

        await new SonarStandardTool(sonar.Handler, (_, _) => Task.CompletedTask).RunAsync(_world.Context);

        Assert.True(_world.Failed("SonarStandard", 1));
        Assert.Contains("ParseError", _world.FailedText("SonarStandard", 1));
    }

    [Fact]
    public async Task ASonarServerThatIsNotUpStopsTheToolBeforeAnyRepo()
    {
        var sonar = new FakeSonar();
        ConfigureSonar("SonarStandard", sonar);
        await CloneAsync();
        var down = new FakeSources { Respond = _ => FakeRapid7.Json(HttpStatusCode.OK, "{\"status\":\"STARTING\"}") };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SonarStandardTool(down, (_, _) => Task.CompletedTask).RunAsync(_world.Context));

        Assert.Contains("not ready", error.Message);
    }

    [Fact]
    public async Task SonarDotNetBeginsBuildsTheSlnNearestTheTopAndEndsThenFetchesTheIssues()
    {
        var sonar = new FakeSonar { TotalFor = _ => 2 };
        var dotnet = Program("dotnet");
        ConfigureSonar("SonarDotNet", sonar, t => t with { DotnetPath = dotnet });
        _world.WithExclusions("bin");
        await CloneAsync(("deep/er/big.sln", "x"), ("top.sln", "x"), ("a.csproj", "x"), ("readme.txt", "x"));
        var scanner = _world.Context.Config.Current.Tool("SonarDotNet").ExePath!;
        var runner = _world.Intercept(spec =>
            spec.FileName == scanner && spec.Arguments[0] == "end" ? ScannerOk(spec, Path.Combine(".sonarqube", "out", ".sonar"))
            : spec.FileName == scanner || spec.FileName == dotnet ? RecordingRunner.Ok()
            : null);

        await new SonarDotNetTool(sonar.Handler, (_, _) => Task.CompletedTask).RunAsync(_world.Context);

        var steps = runner.Specs.Where(s => s.FileName == scanner || s.FileName == dotnet).ToList();
        Assert.Equal(["begin", "build", "end"], steps.Select(s => s.Arguments[0]));

        var begin = steps[0];
        Assert.Contains("/k:gh-1", begin.Arguments);
        Assert.Contains("/n:repo1", begin.Arguments);
        Assert.Contains($"/d:sonar.host.url={FakeSonar.Url}", begin.Arguments);
        Assert.Contains($"/d:sonar.token={SonarToken}", begin.Arguments);
        Assert.Contains("/d:sonar.exclusions=**/bin/**", begin.Arguments);
        Assert.Contains(SonarToken, begin.SecretValues);

        Assert.EndsWith("top.sln", steps[1].Arguments[1]);
        Assert.Contains($"/d:sonar.token={SonarToken}", steps[2].Arguments);
        Assert.All(steps, s => Assert.Equal(RetryPolicy.None, s.Retry));
        Assert.All(steps, s => Assert.Equal(steps[0].WorkingDirectory, s.WorkingDirectory));

        Assert.True(_world.Done("SonarDotNet", 1));
        Assert.Contains("\"findingCount\": 2", _world.DoneText("SonarDotNet", 1));
        Assert.Empty(_world.LeftoverCopies());
    }

    [Fact]
    public async Task SonarDotNetBuildsALoneCsprojWhenThereIsNoSln()
    {
        var sonar = new FakeSonar { TotalFor = _ => 0 };
        var dotnet = Program("dotnet");
        ConfigureSonar("SonarDotNet", sonar, t => t with { DotnetPath = dotnet });
        await CloneAsync(("src/z.csproj", "x"), ("src/a/b.csproj", "x"));
        var scanner = _world.Context.Config.Current.Tool("SonarDotNet").ExePath!;
        var runner = _world.Intercept(spec =>
            spec.FileName == scanner && spec.Arguments[0] == "end" ? ScannerOk(spec, Path.Combine(".sonarqube", "out", ".sonar"))
            : spec.FileName == scanner || spec.FileName == dotnet ? RecordingRunner.Ok()
            : null);

        await new SonarDotNetTool(sonar.Handler, (_, _) => Task.CompletedTask).RunAsync(_world.Context);

        Assert.EndsWith("z.csproj", runner.For(dotnet).Single().Arguments[1]);
    }

    [Fact]
    public async Task ABuildThatFailsIsBuildFailedWithTheCompilersWordsAndTheRepoIsNotSentToTheStandardScanner()
    {
        var sonar = new FakeSonar();
        var dotnet = Program("dotnet");
        ConfigureSonar("SonarDotNet", sonar, t => t with { DotnetPath = dotnet });
        await CloneAsync(("app.sln", "x"));
        var scanner = _world.Context.Config.Current.Tool("SonarDotNet").ExePath!;
        var runner = _world.Intercept(spec =>
            spec.FileName == dotnet
                ? new ProcessResult { Outcome = ProcessOutcome.Failed, ExitCode = 1, Attempts = 1, StdOut = "Program.cs(3,5): error CS1002: ; expected", Message = "exited with code 1" }
                : spec.FileName == scanner ? RecordingRunner.Ok() : null);

        await new SonarDotNetTool(sonar.Handler, (_, _) => Task.CompletedTask).RunAsync(_world.Context);

        Assert.True(_world.Failed("SonarDotNet", 1));
        Assert.Contains("BuildFailed", _world.FailedText("SonarDotNet", 1));
        Assert.Contains("CS1002", _world.FailedText("SonarDotNet", 1));
        Assert.DoesNotContain(runner.Specs, s => s.FileName == scanner && s.Arguments[0] == "end");
    }

    [Fact]
    public async Task SonarDotNetLeavesARepoWithoutDotNetProjectsToTheStandardScanner()
    {
        var sonar = new FakeSonar();
        var dotnet = Program("dotnet");
        ConfigureSonar("SonarDotNet", sonar, t => t with { DotnetPath = dotnet });
        await CloneAsync(("readme.txt", "x"));
        var runner = _world.Intercept(spec => spec.FileName == dotnet ? RecordingRunner.Ok() : null);

        await new SonarDotNetTool(sonar.Handler, (_, _) => Task.CompletedTask).RunAsync(_world.Context);

        Assert.Empty(runner.For(dotnet));
        Assert.Contains("NotApplicable", _world.SkippedReason("SonarDotNet", 1));
    }

    [Fact]
    public async Task SonarDotNetRunsEveryStepAsTheLockedDownAccountAndMasksItsPassword()
    {
        var sonar = new FakeSonar { TotalFor = _ => 0 };
        var dotnet = Program("dotnet");
        ConfigureSonar("SonarDotNet", sonar, t => t with
        {
            DotnetPath = dotnet,
            RunAs = new RunAsSettings { Account = @"CORP\svc-build", PasswordSecret = "build-password" }
        });
        _world.AddSecret("build-password", AccountPassword);
        await CloneAsync(("app.sln", "x"));
        var scanner = _world.Context.Config.Current.Tool("SonarDotNet").ExePath!;
        var runner = _world.Intercept(spec =>
            spec.FileName == scanner && spec.Arguments[0] == "end" ? ScannerOk(spec, Path.Combine(".sonarqube", "out", ".sonar"))
            : spec.FileName == scanner || spec.FileName == dotnet ? RecordingRunner.Ok()
            : null);

        await new SonarDotNetTool(sonar.Handler, (_, _) => Task.CompletedTask).RunAsync(_world.Context);

        var steps = runner.Specs.Where(s => s.FileName == scanner || s.FileName == dotnet).ToList();
        Assert.Equal(3, steps.Count);
        Assert.All(steps, s => Assert.Equal(new RunAsAccount(@"CORP\svc-build", AccountPassword), s.RunAs));
    }

    [Fact]
    public async Task TheSonarTokenNeverReachesAMarkerOrAResultFile()
    {
        var sonar = new FakeSonar { TaskStatus = "FAILED" };
        ConfigureSonar("SonarStandard", sonar);
        await CloneAsync();
        var exe = _world.Context.Config.Current.Tool("SonarStandard").ExePath!;
        _world.Intercept(spec => spec.FileName == exe ? ScannerOk(spec, ".scannerwork") : null);

        await new SonarStandardTool(sonar.Handler, (_, _) => Task.CompletedTask).RunAsync(_world.Context);

        var everything = string.Join("\n", Directory.EnumerateFiles(_world.Context.Paths.RunFolder, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain(SonarToken, everything);
    }

    // ---------------------------------------------------------------- helpers

    [Theory]
    [InlineData(@".*\.yml$", "*.yml")]
    [InlineData("node_modules", "node_modules")]
    [InlineData(@".*\.dll$", "*.dll")]
    public void SemgrepGetsExtensionPatternsAsGlobs(string line, string expected)
    {
        var file = Path.Combine(_world.Root, "x.txt");
        File.WriteAllText(file, line);
        var config = new Core.Configuration.EngineConfig("c", "r", "u", "s", "t", "l", ExclusionsFile: file);

        Assert.Equal([expected], Exclusions.ForSemgrep(config));
    }

    [Fact]
    public void SnykGetsOnlyPlainNamesAndSonarGetsAntPatterns()
    {
        var file = Path.Combine(_world.Root, "x.txt");
        File.WriteAllLines(file, [@".*\.yml$", "bin", "", "  obj  ", ".venv", "node_modules"]);
        var config = new Core.Configuration.EngineConfig("c", "r", "u", "s", "t", "l", ExclusionsFile: file);

        Assert.Equal(["bin", "obj", "node_modules"], Exclusions.ForSnyk(config));
        Assert.Equal(["**/*.yml", "**/bin/**", "**/obj/**", "**/.venv/**", "**/node_modules/**"], Exclusions.ForSonar(config));
    }

    [Fact]
    public void NoExclusionsFileMeansNoExclusionsForAnyTool()
    {
        var config = new Core.Configuration.EngineConfig("c", "r", "u", "s", "t", "l");

        Assert.Empty(Exclusions.ForSemgrep(config));
        Assert.Empty(Exclusions.ForSnyk(config));
        Assert.Empty(Exclusions.ForSonar(config));
    }

    [Theory]
    [InlineData("a.sln,sub/b.sln,c.csproj", "a.sln")]
    [InlineData("deep/er/x.sln,top.sln", "top.sln")]
    [InlineData("z/B.sln,z/a.sln", "z/a.sln")]
    [InlineData("src/z.csproj,src/a/b.csproj", "src/z.csproj")]
    [InlineData("deep/x.csproj,deep/x.sln", "deep/x.sln")]
    public void TheBuildTargetIsTheSlnNearestTheTopOtherwiseTheCsprojNearestTheTop(string files, string expected) =>
        Assert.Equal(expected, DotNetProjects.BuildTarget(files.Split(',')));

    [Fact]
    public void NothingToBuildIsNull() => Assert.Null(DotNetProjects.BuildTarget([]));
}
