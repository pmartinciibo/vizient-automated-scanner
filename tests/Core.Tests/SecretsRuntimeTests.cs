namespace Vizient.AutomatedScanner.Tests;

using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Processes;
using Vizient.AutomatedScanner.Core.Secrets;
using Vizient.AutomatedScanner.Core.Serialization;

// What the engine does with secrets while it runs: giving each tool only its own, and checking them at the start.
public class SecretsRuntimeTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    // Variable names unique to this test class, so they cannot collide with anything on the machine.
    private const string SnykVar = "SCAN_TEST_SNYK_TOKEN";
    private const string SonarVar = "SCAN_TEST_SONAR_TOKEN";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
    private readonly string _requestFile;

    public SecretsRuntimeTests()
    {
        Directory.CreateDirectory(_root);
        _requestFile = Path.Combine(_root, "requests", "request.json");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string SecretsPath => Path.Combine(_root, "secrets.dat");

    private static EngineSettings Settings(Dictionary<string, ToolSettings>? tools = null, int warnDays = 30) => new()
    {
        Secrets = new SecretsSettings { ExpiryWarnDays = warnDays },
        Tools = tools ?? new()
        {
            ["Snyk"] = new ToolSettings { Env = new() { [SnykVar] = "snyk-token" } },
            ["SonarStandard"] = new ToolSettings { Env = new() { [SonarVar] = "sonar-token" } }
        }
    };

    private EngineConfig Config(EngineSettings settings) => new(
        Path.Combine(_root, "engine.json"), Path.Combine(_root, "repos"), Path.Combine(_root, "runs"),
        Path.Combine(_root, "state"), Path.Combine(_root, "tmp"), Path.Combine(_root, "logs"),
        Settings: settings);

    private async Task WriteSecretsAsync(params (string Name, string Value, DateOnly? Expires)[] secrets)
    {
        var document = new SecretsDocument
        {
            Secrets = secrets.ToDictionary(
                s => s.Name,
                s => new SecretEntry { Value = s.Value, CreatedAt = Now.AddDays(-1), ExpiresAt = s.Expires },
                StringComparer.Ordinal)
        };
        await SecretsFile.WriteAsync(SecretsPath, document);
    }

    private async Task<RunContext> StartRunAsync(EngineSettings settings, Dictionary<string, ToolRequestOverride>? tools = null)
    {
        await AtomicJsonFile.WriteAsync(_requestFile, new RunRequestDocument { RunId = "run-1", Tools = tools ?? new() });
        return await RunContext.StartAsync(Config(settings), _requestFile);
    }

    private static ProcessSpec Echo(string command) => new()
    {
        FileName = "cmd.exe",
        Arguments = ["/c", command],
        Timeout = TimeSpan.FromSeconds(30)
    };

    // ---- the secrets file is found beside engine.json ----

    [Fact]
    public void TheSecretsFileIsBesideEngineJson()
    {
        var config = Config(Settings());

        Assert.Equal(Path.Combine(_root, "secrets.dat"), config.SecretsFile);
    }

    // ---- giving a tool its own secrets ----

    [Fact]
    public async Task AToolGetsItsOwnSecretsAsEnvironmentVariablesAndTheyAreMaskedInItsOutput()
    {
        await WriteSecretsAsync(("snyk-token", "snyk-VALUE-123", null), ("sonar-token", "sonar-VALUE-456", null));
        var secrets = new RunSecrets(SecretsPath, Settings());

        var spec = await secrets.ApplyAsync(Echo($"echo token=%{SnykVar}%"), "Snyk");
        var result = await new ProcessRunner().RunAsync(spec);

        Assert.Equal("snyk-VALUE-123", spec.SecretEnvironment[SnykVar]);
        Assert.Contains("token=***", result.StdOut);
        Assert.DoesNotContain("snyk-VALUE-123", result.StdOut);
    }

    [Fact]
    public async Task AToolIsNotGivenAnotherToolsSecret()
    {
        await WriteSecretsAsync(("snyk-token", "snyk-VALUE-123", null), ("sonar-token", "sonar-VALUE-456", null));
        var secrets = new RunSecrets(SecretsPath, Settings());

        var spec = await secrets.ApplyAsync(Echo($"echo [%{SnykVar}%][%{SonarVar}%]"), "Snyk");
        var result = await new ProcessRunner().RunAsync(spec);

        Assert.DoesNotContain(SonarVar, spec.SecretEnvironment.Keys);
        Assert.Contains($"[***][%{SonarVar}%]", result.StdOut);
    }

    [Fact]
    public async Task AToolWithNoEnvMapGetsNoSecretsAtAll()
    {
        await WriteSecretsAsync(("snyk-token", "snyk-VALUE-123", null));
        var secrets = new RunSecrets(SecretsPath, Settings());

        var spec = await secrets.ApplyAsync(Echo("echo hi"), "Semgrep");

        Assert.Empty(spec.SecretEnvironment);
    }

    [Fact]
    public async Task ASecretVariableThatIsAlreadyInTheEnginesEnvironmentDoesNotReachAnotherTool()
    {
        // A token left in the environment the engine was started from must not leak into a tool that is not meant to have it.
        await WriteSecretsAsync(("snyk-token", "snyk-VALUE-123", null), ("sonar-token", "sonar-VALUE-456", null));
        var secrets = new RunSecrets(SecretsPath, Settings());
        Environment.SetEnvironmentVariable(SonarVar, "leaked-from-the-environment");
        try
        {
            var spec = await secrets.ApplyAsync(Echo($"echo [%{SonarVar}%]"), "Semgrep");
            var result = await new ProcessRunner().RunAsync(spec);

            Assert.Contains(SonarVar, spec.RemoveEnvironment, StringComparer.OrdinalIgnoreCase);
            Assert.Contains($"[%{SonarVar}%]", result.StdOut);
            Assert.DoesNotContain("leaked-from-the-environment", result.StdOut);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SonarVar, null);
        }
    }

    [Fact]
    public async Task AToolsOwnVariableIsSetEvenIfTheEngineHasOneOfTheSameName()
    {
        await WriteSecretsAsync(("snyk-token", "snyk-VALUE-123", null), ("sonar-token", "sonar-VALUE-456", null));
        var secrets = new RunSecrets(SecretsPath, Settings());
        Environment.SetEnvironmentVariable(SnykVar, "stale-value-in-environment");
        try
        {
            var spec = await secrets.ApplyAsync(Echo($"echo token=%{SnykVar}%"), "Snyk");
            var result = await new ProcessRunner().RunAsync(spec);

            Assert.DoesNotContain(SnykVar, spec.RemoveEnvironment, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("token=***", result.StdOut);
            Assert.DoesNotContain("stale-value-in-environment", result.StdOut);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SnykVar, null);
        }
    }

    [Fact]
    public async Task ARunnerCanClearNamedVariablesFromWhatTheChildInherits()
    {
        Environment.SetEnvironmentVariable("SCAN_TEST_INHERITED", "inherited-value");
        try
        {
            var kept = await new ProcessRunner().RunAsync(Echo("echo [%SCAN_TEST_INHERITED%]"));
            var cleared = await new ProcessRunner().RunAsync(Echo("echo [%SCAN_TEST_INHERITED%]") with { RemoveEnvironment = ["scan_test_inherited"] });

            Assert.Contains("[inherited-value]", kept.StdOut);
            Assert.Contains("[%SCAN_TEST_INHERITED%]", cleared.StdOut);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SCAN_TEST_INHERITED", null);
        }
    }

    [Fact]
    public async Task AMissingSecretStopsTheToolAndNamesItWithoutAnyValue()
    {
        await WriteSecretsAsync(("sonar-token", "sonar-VALUE-456", null));
        var secrets = new RunSecrets(SecretsPath, Settings());

        var error = await Assert.ThrowsAsync<SecretUnavailableException>(() => secrets.ApplyAsync(Echo("echo hi"), "Snyk"));

        Assert.Contains("'snyk-token'", error.Message);
        Assert.Contains($"Snyk ({SnykVar})", error.Message);
        Assert.DoesNotContain("VALUE", error.Message);
    }

    [Fact]
    public async Task AnExpiredSecretStopsTheTool()
    {
        await WriteSecretsAsync(("snyk-token", "snyk-VALUE-123", Today.AddDays(-1)));
        var secrets = new RunSecrets(SecretsPath, Settings());

        var error = await Assert.ThrowsAsync<SecretUnavailableException>(() => secrets.ApplyAsync(Echo("echo hi"), "Snyk"));

        Assert.Contains("expired", error.Message);
        Assert.DoesNotContain("snyk-VALUE-123", error.Message);
    }

    [Fact]
    public async Task TheFileIsReadOnceAndHeldInMemory()
    {
        await WriteSecretsAsync(("snyk-token", "snyk-VALUE-123", null));
        var secrets = new RunSecrets(SecretsPath, Settings());
        await secrets.ApplyAsync(Echo("echo hi"), "Snyk");

        File.Delete(SecretsPath);

        var spec = await secrets.ApplyAsync(Echo("echo hi"), "Snyk");
        Assert.Equal("snyk-VALUE-123", spec.SecretEnvironment[SnykVar]);
    }

    [Fact]
    public async Task SeveralToolsAskingAtOnceReadTheFileOnce()
    {
        await WriteSecretsAsync(("snyk-token", "snyk-VALUE-123", null), ("sonar-token", "sonar-VALUE-456", null));
        var secrets = new RunSecrets(SecretsPath, Settings());

        var specs = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(i => secrets.ApplyAsync(Echo("echo hi"), i % 2 == 0 ? "Snyk" : "SonarStandard")));

        Assert.All(specs.Where((_, i) => i % 2 == 0), s => Assert.Equal("snyk-VALUE-123", s.SecretEnvironment[SnykVar]));
        Assert.All(specs.Where((_, i) => i % 2 == 1), s => Assert.Equal("sonar-VALUE-456", s.SecretEnvironment[SonarVar]));
    }

    [Fact]
    public async Task AFileThatCannotBeReadStopsTheToolWithAClearError()
    {
        await File.WriteAllTextAsync(SecretsPath, "garbage");
        var secrets = new RunSecrets(SecretsPath, Settings());

        await Assert.ThrowsAsync<SecretsFileException>(() => secrets.ApplyAsync(Echo("echo hi"), "Snyk"));
    }

    // ---- which secrets are required ----

    [Fact]
    public void RequiredSecretsAreTheSourceTokensAndEachToolsTokenEnvAndRunAsPassword()
    {
        var settings = new EngineSettings
        {
            Sources = [new SourceSettings { Source = "ADO", Org = "vizientinc", TokenSecret = "ado-pat" }],
            Tools = new()
            {
                ["Rapid7"] = new ToolSettings { TokenSecret = "rapid7-token" },
                ["Snyk"] = new ToolSettings
                {
                    Env = new() { ["SNYK_TOKEN"] = "snyk-token" },
                    RunAs = new RunAsSettings { Account = "DOM\\svc-build", PasswordSecret = "build-password" }
                }
            }
        };

        var required = SecretsChecker.RequiredSecrets(settings, _ => true);

        Assert.Equal(["ado-pat", "build-password", "rapid7-token", "snyk-token"], required.Keys.Order());
        Assert.Equal("ADO source vizientinc", required["ado-pat"]);
        Assert.Equal("Snyk (SNYK_TOKEN)", required["snyk-token"]);
        Assert.Equal("Snyk (runAs password)", required["build-password"]);
    }

    [Fact]
    public void ASecretNeededByTwoToolsIsListedOnce()
    {
        var settings = new EngineSettings
        {
            Tools = new()
            {
                ["SonarStandard"] = new ToolSettings { Env = new() { ["SONAR_TOKEN"] = "sonar-token" } },
                ["SonarDotNet"] = new ToolSettings { Env = new() { ["SONAR_TOKEN"] = "sonar-token" } }
            }
        };

        Assert.Single(SecretsChecker.RequiredSecrets(settings, _ => true));
    }

    [Fact]
    public void SecretsOfToolsThatAreOffAreNotRequiredAndSourcesNeedRepoCloneOn()
    {
        var settings = new EngineSettings
        {
            Sources = [new SourceSettings { Source = "GitHub", TokenSecret = "github-token" }],
            Tools = new() { ["Snyk"] = new ToolSettings { Env = new() { ["SNYK_TOKEN"] = "snyk-token" } } }
        };

        Assert.Empty(SecretsChecker.RequiredSecrets(settings, _ => false));
        Assert.Equal(["github-token"], SecretsChecker.RequiredSecrets(settings, tool => tool == "RepoClone").Keys);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void TheCheckResultsExitCodeIs0ForOkAnd1ForWarningsAnd2ForErrors(int kind, int expectedExit)
    {
        var finding = kind switch
        {
            1 => new[] { new SecretFinding("a", SecretIssue.ExpiringSoon, Today, 3, "x") },
            2 => [new SecretFinding("a", SecretIssue.Missing, null, null, "x")],
            _ => Array.Empty<SecretFinding>()
        };

        Assert.Equal(expectedExit, new SecretsCheckResult([], finding, null).ExitCode);
        Assert.Equal(2, new SecretsCheckResult([], [], "cannot be read").ExitCode);
        Assert.Equal(2, new SecretsCheckResult([], [new SecretFinding("a", SecretIssue.ExpiringSoon, Today, 3, "x"), new SecretFinding("b", SecretIssue.Expired, Today, -1, "x")], null).ExitCode);
    }

    // ---- the check at the start of a run ----

    private static ToolProgress Step(RunContext context, string tool) =>
        context.Status.Snapshot().Steps.Single(s => s.Name == tool);

    [Fact]
    public async Task WhenEverythingIsInOrderThePreflightRaisesNothing()
    {
        await WriteSecretsAsync(("snyk-token", "v1", null), ("sonar-token", "v2", null));
        var context = await StartRunAsync(Settings());

        var result = await SecretsPreflight.RunAsync(context);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(context.Status.Snapshot().Alerts);
    }

    [Fact]
    public async Task ASecretExpiringWithinTheWarningPeriodRaisesAnExpiringSecretAlertWithTheDate()
    {
        await WriteSecretsAsync(("snyk-token", "v1", Today.AddDays(10)), ("sonar-token", "v2", null));
        var context = await StartRunAsync(Settings());

        var result = await SecretsPreflight.RunAsync(context);

        Assert.Equal(1, result.ExitCode);
        var alert = Assert.Single(context.Status.Snapshot().Alerts);
        Assert.Equal(("ExpiringSecret", "snyk-token", Today.AddDays(10).ToString("yyyy-MM-dd")), (alert.Type, alert.Name, alert.ExpiresAt));
    }

    [Fact]
    public async Task TheWarningPeriodComesFromEngineJson()
    {
        await WriteSecretsAsync(("snyk-token", "v1", Today.AddDays(10)), ("sonar-token", "v2", null));
        var context = await StartRunAsync(Settings(warnDays: 5));

        await SecretsPreflight.RunAsync(context);

        Assert.Empty(context.Status.Snapshot().Alerts);
    }

    [Fact]
    public async Task MissingAndExpiredSecretsRaiseAlertsByName()
    {
        await WriteSecretsAsync(("snyk-token", "v1", Today.AddDays(-3)));
        var context = await StartRunAsync(Settings());

        var result = await SecretsPreflight.RunAsync(context);

        Assert.Equal(2, result.ExitCode);
        var alerts = context.Status.Snapshot().Alerts.ToDictionary(a => a.Name!);
        Assert.Equal("ExpiredSecret", alerts["snyk-token"].Type);
        Assert.Equal("MissingSecret", alerts["sonar-token"].Type);
    }

    [Fact]
    public async Task NoSecretsFileMeansEveryRequiredSecretIsMissing()
    {
        var context = await StartRunAsync(Settings());

        var result = await SecretsPreflight.RunAsync(context);

        Assert.Equal(2, result.Findings.Count);
        Assert.All(context.Status.Snapshot().Alerts, a => Assert.Equal("MissingSecret", a.Type));
    }

    [Fact]
    public async Task AFileThatCannotBeReadRaisesASecretsUnreadableAlert()
    {
        await File.WriteAllTextAsync(SecretsPath, "garbage");
        var context = await StartRunAsync(Settings());

        var result = await SecretsPreflight.RunAsync(context);

        Assert.NotNull(result.FileError);
        Assert.Contains(context.Status.Snapshot().Alerts, a => a.Type == "SecretsUnreadable" && a.Name == "secrets.dat");
    }

    [Fact]
    public async Task OnlyTheSecretsOfToolsThatWillRunAreChecked()
    {
        await WriteSecretsAsync(("sonar-token", "v2", null));
        var context = await StartRunAsync(Settings(), new() { ["Snyk"] = new ToolRequestOverride { Enabled = false } });

        var result = await SecretsPreflight.RunAsync(context);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task AlertsNeverCarryAValue()
    {
        await WriteSecretsAsync(("snyk-token", "snyk-VALUE-123", Today.AddDays(2)), ("sonar-token", "sonar-VALUE-456", null));
        var context = await StartRunAsync(Settings());

        await SecretsPreflight.RunAsync(context);

        var status = System.Text.Json.JsonSerializer.Serialize(context.Status.Snapshot());
        Assert.DoesNotContain("VALUE", status);
    }

    // ---- the settings in engine.json ----

    [Theory]
    [InlineData("""{ "secrets": { "expiryWarnDays": -1 } }""", "secrets.expiryWarnDays")]
    [InlineData("""{ "secrets": { "requiredAccount": "  " } }""", "secrets.requiredAccount")]
    public async Task BadSecretsSettingsAreRejectedAtLoad(string settingsJson, string expected)
    {
        var configFile = Path.Combine(_root, "engine.json");
        var members = settingsJson.Trim()[1..^1];
        await File.WriteAllTextAsync(configFile,
            "{ \"paths\": { \"reposRoot\": \"r\", \"runs\": \"u\", \"state\": \"s\", \"tmp\": \"t\", \"logs\": \"l\" }, " + members + " }");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => EngineConfig.LoadAsync(configFile, CancellationToken.None));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task TheSecretsSettingsAreReadFromEngineJson()
    {
        var configFile = Path.Combine(_root, "engine.json");
        await File.WriteAllTextAsync(configFile,
            "{ \"paths\": { \"reposRoot\": \"r\", \"runs\": \"u\", \"state\": \"s\", \"tmp\": \"t\", \"logs\": \"l\" }," +
            " \"secrets\": { \"requiredAccount\": \"DOM\\\\svc-scanner\", \"expiryWarnDays\": 14 } }");

        var config = await EngineConfig.LoadAsync(configFile, CancellationToken.None);

        Assert.Equal(("DOM\\svc-scanner", 14), (config.Current.Secrets.RequiredAccount, config.Current.Secrets.ExpiryWarnDays));
    }

    [Fact]
    public async Task WithNoSecretsSectionAnyAccountMayManageSecretsAndTheWarningPeriodIs30Days()
    {
        var configFile = Path.Combine(_root, "engine.json");
        await File.WriteAllTextAsync(configFile,
            "{ \"paths\": { \"reposRoot\": \"r\", \"runs\": \"u\", \"state\": \"s\", \"tmp\": \"t\", \"logs\": \"l\" } }");

        var config = await EngineConfig.LoadAsync(configFile, CancellationToken.None);

        Assert.Null(config.Current.Secrets.RequiredAccount);
        Assert.Equal(30, config.Current.Secrets.ExpiryWarnDays);
    }
}
