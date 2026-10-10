namespace Vizient.AutomatedScanner.Tests;

using System.Net;
using System.Text.Json;
using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Rapid7;
using Vizient.AutomatedScanner.Core.Secrets;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.Tools;

public class Rapid7ImportTests
{
    private const string Token = Rapid7Run.Token;

    /// <summary>Three applications. app-1 and app-2 have vulnerabilities, app-3 has none.</summary>
    private static FakeRapid7 TypicalApi(int vulnsPerApp = 250) => new FakeRapid7()
        .With("apps", R7.App("app-1", "Alpha"), R7.App("app-2", "Beta"), R7.App("app-3", "Gamma"))
        .With("scans", R7.Scan("s1", "app-1"), R7.Scan("s2", "app-1"), R7.Scan("s3", "app-2"))
        .With("reports", R7.Report("r1", "app-2"))
        .With("vulnerabilities", [.. R7.Vulns("app-1", vulnsPerApp), .. R7.Vulns("app-2", vulnsPerApp)]);

    private static string[] LeftOver(Rapid7Run run) =>
        Directory.Exists(run.ResultsDir) ? Directory.GetFiles(run.ResultsDir, "*.tmp") : [];

    private static async Task<ImportDoneMarker> MarkerAsync(Rapid7Run run) =>
        await AtomicJsonFile.ReadAsync<ImportDoneMarker>(run.Context.Paths.ImportDoneMarker("Rapid7"));

    private sealed class Stub(string name) : ITool
    {
        public string Name => name;
        public Task RunAsync(RunContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // ---- the one file ----

    [Fact]
    public async Task TheOutputIsOneJsonFileAndTheMarkerThatSaysItIsComplete()
    {
        using var run = new Rapid7Run();

        await run.Tool(TypicalApi()).RunAsync(run.Context);

        Assert.Equal(["_import.done.json", "rapid7.json"], run.ResultFiles());
        Assert.Equal(Path.Combine(run.Context.Paths.RunFolder, "results", "Rapid7", "rapid7.json"), run.OutputPath);
    }

    [Fact]
    public async Task TheFileIsAListOfAppsEachWithItsOwnFieldsAndItsListOfVulnerabilities()
    {
        using var run = new Rapid7Run();

        await run.Tool(TypicalApi()).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var apps = file.RootElement.GetProperty("apps");
        Assert.Equal(3, apps.GetArrayLength());

        var alpha = apps[0];
        Assert.Equal(["id", "name", "description", "links", "vulnerabilities"], alpha.EnumerateObject().Select(p => p.Name));
        Assert.Equal("app-1", alpha.GetProperty("id").GetString());
        Assert.Equal("Alpha app-1", alpha.GetProperty("name").GetString());
        var vulns = alpha.GetProperty("vulnerabilities");
        Assert.Equal(250, vulns.GetArrayLength());
        Assert.All(vulns.EnumerateArray(), v => Assert.Equal("app-1", v.GetProperty("app").GetProperty("id").GetString()));
    }

    [Fact]
    public async Task EachAppHasOnlyItsOwnVulnerabilities()
    {
        using var run = new Rapid7Run();

        await run.Tool(TypicalApi(vulnsPerApp: 40)).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var apps = run.Apps(file);
        var one = apps["app-1"].GetProperty("vulnerabilities").EnumerateArray().Select(v => v.GetProperty("id").GetString()).ToList();
        var two = apps["app-2"].GetProperty("vulnerabilities").EnumerateArray().Select(v => v.GetProperty("id").GetString()).ToList();
        Assert.Equal(40, one.Count);
        Assert.Equal(40, two.Count);
        Assert.All(one, id => Assert.StartsWith("vapp-1-", id));
        Assert.All(two, id => Assert.StartsWith("vapp-2-", id));
        Assert.Empty(one.Intersect(two));
    }

    [Fact]
    public async Task VulnerabilitiesKeepTheOrderTheyArrivedInWithinAnApp()
    {
        using var run = new Rapid7Run();

        await run.Tool(TypicalApi(vulnsPerApp: 30)).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var ids = run.Apps(file)["app-1"].GetProperty("vulnerabilities").EnumerateArray().Select(v => v.GetProperty("id").GetString());
        Assert.Equal(Enumerable.Range(1, 30).Select(n => $"vapp-1-{n}"), ids);
    }

    [Fact]
    public async Task AnAppWithNoVulnerabilitiesStillAppearsWithAnEmptyList()
    {
        using var run = new Rapid7Run();

        await run.Tool(TypicalApi()).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var gamma = run.Apps(file)["app-3"];
        Assert.Equal("Gamma app-3", gamma.GetProperty("name").GetString());
        Assert.Equal(0, gamma.GetProperty("vulnerabilities").GetArrayLength());
    }

    [Fact]
    public async Task TheFileStartsWithWhenAndWhereItCameFromAndTheAreasItHolds()
    {
        using var run = new Rapid7Run();
        var before = DateTimeOffset.UtcNow.AddSeconds(-2);

        await run.Tool(TypicalApi(vulnsPerApp: 1)).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var root = file.RootElement;
        Assert.Equal(["generatedAt", "baseUrl", "api", "areas", "apps"], root.EnumerateObject().Select(p => p.Name));
        Assert.True(DateTimeOffset.Parse(root.GetProperty("generatedAt").GetString()!) >= before);
        Assert.Equal("https://rapid7.test/ias/v1", root.GetProperty("baseUrl").GetString());
        Assert.Equal("Rapid7 InsightAppSec API v1", root.GetProperty("api").GetString());
        Assert.Equal(["applications", "vulnerabilities"], root.GetProperty("areas").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public async Task AppsAreInOrderOfNameThenId()
    {
        using var run = new Rapid7Run();
        var api = new FakeRapid7()
            .With("apps", R7.App("z-id", "Alpha"), R7.App("b-id", "beta"), R7.App("a-id", "Alpha"), R7.App("c-id", "Beta"))
            .With("vulnerabilities");

        await run.Tool(api).RunAsync(run.Context);

        using var file = run.ReadOutput();
        Assert.Equal(
            ["a-id", "z-id", "b-id", "c-id"],
            file.RootElement.GetProperty("apps").EnumerateArray().Select(a => a.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task TheAppsOwnRecordIsCopiedAsItCameWithoutLosingAnything()
    {
        using var run = new Rapid7Run();
        var api = new FakeRapid7()
            .With("apps", "{\"id\":\"app-1\",\"name\":\"Alpha\",\"extra\":{\"nested\":[1,2,{\"deep\":true}]},\"links\":[{\"rel\":\"self\",\"href\":\"https://x/y?a=1&b=2\"}]}")
            .With("vulnerabilities");

        await run.Tool(api).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var app = run.Apps(file)["app-1"];
        Assert.True(app.GetProperty("extra").GetProperty("nested")[2].GetProperty("deep").GetBoolean());
        Assert.Equal("https://x/y?a=1&b=2", app.GetProperty("links")[0].GetProperty("href").GetString());
    }

    [Fact]
    public async Task AnAppRecordThatAlreadyHasAVulnerabilitiesFieldDoesNotClashWithTheList()
    {
        using var run = new Rapid7Run();
        var api = new FakeRapid7()
            .With("apps", "{\"id\":\"app-1\",\"name\":\"Alpha\",\"vulnerabilities\":\"a summary string\"}")
            .With("vulnerabilities", R7.Vuln("v1", "app-1"));

        await run.Tool(api).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var app = run.Apps(file)["app-1"];
        Assert.Equal(JsonValueKind.Array, app.GetProperty("vulnerabilities").ValueKind);
        Assert.Equal(1, app.EnumerateObject().Count(p => p.Name == "vulnerabilities"));
    }

    [Fact]
    public async Task VulnerabilitiesAreCopiedAsTheyCameValuesIncluded()
    {
        using var run = new Rapid7Run();

        await run.Tool(TypicalApi(vulnsPerApp: 1)).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var vuln = run.Apps(file)["app-1"].GetProperty("vulnerabilities")[0];
        Assert.Equal("HIGH", vuln.GetProperty("severity").GetString());
        Assert.Equal("https://shop.example.test/search?q=café&page=2+3", vuln.GetProperty("root_cause").GetProperty("url").GetString());
        // The server wrote the é as an escape. It is left as the server wrote it, not re-encoded.
        Assert.Contains("caf\\u00e9", await File.ReadAllTextAsync(run.OutputPath));
    }

    [Fact]
    public async Task TheFileIsCompactBecauseThereCanBeHundredsOfThousandsOfRecords()
    {
        using var run = new Rapid7Run();

        await run.Tool(TypicalApi(vulnsPerApp: 3)).RunAsync(run.Context);

        var text = await File.ReadAllTextAsync(run.OutputPath);
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain("  ", text);
    }

    [Fact]
    public async Task TheMarkerCountsAppsAndVulnerabilities()
    {
        using var run = new Rapid7Run();

        await run.Tool(TypicalApi(vulnsPerApp: 120)).RunAsync(run.Context);

        var marker = await MarkerAsync(run);
        Assert.Equal((3, 240L, 0), (marker.Entities, marker.Findings, marker.FailedEntities));
        Assert.True(marker.EndedAt >= marker.StartedAt);
        Assert.Equal(3, marker.SchemaVersion);
    }

    [Fact]
    public async Task TheMarkerIsWrittenOnlyAfterTheFileIsInPlace()
    {
        using var run = new Rapid7Run();
        var api = TypicalApi(vulnsPerApp: 5);
        var sawMarkerEarly = false;
        api.Intercept = (_, _) =>
        {
            sawMarkerEarly |= File.Exists(run.Context.Paths.ImportDoneMarker("Rapid7")) || File.Exists(run.OutputPath);
            return Task.FromResult<HttpResponseMessage?>(null);
        };

        await run.Tool(api).RunAsync(run.Context);

        Assert.False(sawMarkerEarly);
        Assert.True(File.Exists(run.OutputPath));
        Assert.True(File.Exists(run.Context.Paths.ImportDoneMarker("Rapid7")));
    }

    [Fact]
    public async Task TheStepShowsHowManyAppsWereWritten()
    {
        using var run = new Rapid7Run();

        await run.Tool(TypicalApi()).RunAsync(run.Context);

        Assert.Equal((3, 3, 0), (run.Step.Total, run.Step.Done, run.Step.Failed));
        Assert.Null(run.Step.Message);
    }

    [Fact]
    public async Task NoTemporaryFilesOrFoldersAreLeftBehind()
    {
        using var run = new Rapid7Run();

        await run.Tool(TypicalApi()).RunAsync(run.Context);

        Assert.Empty(LeftOver(run));
        Assert.Empty(run.SpoolFolders());
    }

    [Fact]
    public async Task VulnerabilitiesAreSetAsideOnTheTempDriveWhileTheyArriveNotInTheResults()
    {
        using var run = new Rapid7Run();
        var api = TypicalApi();
        string[]? spoolFolders = null;
        string[]? inResults = null;
        api.Intercept = (request, _) =>
        {
            if (request.Endpoint == "vulnerabilities" && request.Query["index"] == "3")
            {
                spoolFolders = run.SpoolFolders();
                inResults = run.ResultFiles().ToArray();
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };

        await run.Tool(api).RunAsync(run.Context);

        Assert.Single(spoolFolders!);
        Assert.All(inResults!, f => Assert.StartsWith(".", f)); // only a temporary file, until the file is complete
        Assert.Empty(run.SpoolFolders());
    }

    [Fact]
    public async Task ManyAppsAndAHugeNumberOfVulnerabilitiesAreHandledAsAStream()
    {
        using var run = new Rapid7Run();
        var api = new FakeRapid7()
            .With("apps", Enumerable.Range(1, 50).Select(n => R7.App($"app-{n:D2}")).ToArray())
            .With("vulnerabilities", Enumerable.Range(0, 5000).Select(n => R7.Vuln($"v{n}", $"app-{n % 50 + 1:D2}")).ToArray());

        await run.Tool(api).RunAsync(run.Context);

        var marker = await MarkerAsync(run);
        Assert.Equal((50, 5000L), (marker.Entities, marker.Findings));
        using var file = run.ReadOutput();
        Assert.Equal(50, file.RootElement.GetProperty("apps").GetArrayLength());
        Assert.All(file.RootElement.GetProperty("apps").EnumerateArray(), a => Assert.Equal(100, a.GetProperty("vulnerabilities").GetArrayLength()));
        Assert.Equal(50, api.Count("vulnerabilities")); // 5,000 records at 100 a page
    }

    [Fact]
    public async Task ARecordLargerThanTheCopyBufferIsStillCopiedWhole()
    {
        using var run = new Rapid7Run();
        var big = "{\"id\":\"v1\",\"app\":{\"id\":\"app-1\"},\"notes\":\"" + new string('x', 300_000) + "\"}";
        var api = new FakeRapid7().With("apps", R7.App("app-1")).With("vulnerabilities", big, R7.Vuln("v2", "app-1"));

        await run.Tool(api).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var vulns = run.Apps(file)["app-1"].GetProperty("vulnerabilities");
        Assert.Equal(300_000, vulns[0].GetProperty("notes").GetString()!.Length);
        Assert.Equal("v2", vulns[1].GetProperty("id").GetString());
    }

    // ---- apps that are not in the list ----

    [Fact]
    public async Task AVulnerabilityForAnAppNotInTheListGetsAnEntryWithJustItsId()
    {
        using var run = new Rapid7Run();
        var api = new FakeRapid7().With("apps", R7.App("app-1", "Alpha")).With("vulnerabilities", R7.Vuln("v1", "ghost-app"));

        await run.Tool(api).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var ghost = run.Apps(file)["ghost-app"];
        Assert.Equal(["id", "vulnerabilities"], ghost.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, ghost.GetProperty("vulnerabilities").GetArrayLength());
    }

    [Theory]
    [InlineData("{\"id\":\"v1\"}")]                              // no app at all
    [InlineData("{\"id\":\"v1\",\"app\":{}}")]                   // no app id
    [InlineData("{\"id\":\"v1\",\"app\":{\"id\":null}}")]
    [InlineData("{\"id\":\"v1\",\"app\":{\"id\":\"  \"}}")]
    [InlineData("{\"id\":\"v1\",\"app\":{\"id\":true}}")]
    public async Task ARecordWithNoApplicationIdGoesInAnEntryWithANullIdAtTheEnd(string record)
    {
        using var run = new Rapid7Run();
        var api = new FakeRapid7().With("apps", R7.App("app-1", "Alpha")).With("vulnerabilities", record);

        await run.Tool(api).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var apps = file.RootElement.GetProperty("apps");
        Assert.Equal(2, apps.GetArrayLength());
        var last = apps[1];
        Assert.Equal(JsonValueKind.Null, last.GetProperty("id").ValueKind);
        Assert.Equal(1, last.GetProperty("vulnerabilities").GetArrayLength());
    }

    [Fact]
    public async Task AnIdLikeAPathIsOnlyDataAndNothingIsWrittenOutsideTheResultsFolder()
    {
        using var run = new Rapid7Run();
        var api = new FakeRapid7().With("vulnerabilities", R7.Vuln("v1", "../../evil"));

        await run.Tool(api).RunAsync(run.Context);

        using var file = run.ReadOutput();
        Assert.Equal("../../evil", file.RootElement.GetProperty("apps")[0].GetProperty("id").GetString());
        Assert.Equal(["_import.done.json", "rapid7.json"], run.ResultFiles());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(run.ResultsDir)!));
        Assert.Empty(Directory.GetFiles(run.Context.Paths.RunFolder, "evil*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ANumericAppIdIsKeptAsItsText()
    {
        using var run = new Rapid7Run();
        var api = new FakeRapid7().With("vulnerabilities", "{\"id\":\"v1\",\"app\":{\"id\":123}}");

        await run.Tool(api).RunAsync(run.Context);

        using var file = run.ReadOutput();
        Assert.Equal("123", file.RootElement.GetProperty("apps")[0].GetProperty("id").GetString());
    }

    // ---- which areas and how much ----

    [Fact]
    public async Task ByDefaultOnlyAppsAndVulnerabilitiesAreFetched()
    {
        using var run = new Rapid7Run();
        var api = TypicalApi(vulnsPerApp: 3);

        await run.Tool(api).RunAsync(run.Context);

        Assert.Equal(["apps", "vulnerabilities"], api.Snapshot().Select(r => r.Endpoint).Distinct());
        Assert.Equal(0, api.Count("scans"));
        Assert.Equal(0, api.Count("reports"));
    }

    [Fact]
    public async Task ScansAndReportsCanBeAskedForAndAreAddedToEachApp()
    {
        using var run = new Rapid7Run(s => s with { Areas = ["applications", "scans", "reports", "vulnerabilities"] });
        var api = TypicalApi(vulnsPerApp: 2);

        await run.Tool(api).RunAsync(run.Context);

        Assert.Equal(["apps", "scans", "reports", "vulnerabilities"], api.Snapshot().Select(r => r.Endpoint).Distinct());
        using var file = run.ReadOutput();
        var apps = run.Apps(file);
        Assert.Equal(["id", "name", "description", "links", "scans", "reports", "vulnerabilities"], apps["app-1"].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["s1", "s2"], apps["app-1"].GetProperty("scans").EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        Assert.Equal(0, apps["app-1"].GetProperty("reports").GetArrayLength());
        Assert.Equal(["r1"], apps["app-2"].GetProperty("reports").EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        Assert.Equal(0, apps["app-3"].GetProperty("scans").GetArrayLength());
    }

    [Fact]
    public async Task WithApplicationsOnlyTheAppsHaveNoVulnerabilitiesList()
    {
        using var run = new Rapid7Run(s => s with { Areas = ["applications"] });
        var api = TypicalApi();

        await run.Tool(api).RunAsync(run.Context);

        Assert.Equal(["apps"], api.Snapshot().Select(r => r.Endpoint).Distinct());
        using var file = run.ReadOutput();
        Assert.Equal(3, file.RootElement.GetProperty("apps").GetArrayLength());
        Assert.All(file.RootElement.GetProperty("apps").EnumerateArray(), a => Assert.False(a.TryGetProperty("vulnerabilities", out _)));
    }

    [Fact]
    public async Task WithVulnerabilitiesOnlyThereAreEntriesJustForTheAppsThatHaveThem()
    {
        using var run = new Rapid7Run(s => s with { Areas = ["vulnerabilities"] });
        var api = TypicalApi(vulnsPerApp: 5);

        await run.Tool(api).RunAsync(run.Context);

        Assert.Equal(["vulnerabilities"], api.Snapshot().Select(r => r.Endpoint).Distinct());
        using var file = run.ReadOutput();
        Assert.Equal(["app-1", "app-2"], run.Apps(file).Keys.Order());
        Assert.Equal(["id", "vulnerabilities"], run.Apps(file)["app-1"].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task APageCapGivesATrialRun()
    {
        using var run = new Rapid7Run(s => s with { PageSize = 10, MaxPages = 1 });
        var api = TypicalApi(vulnsPerApp: 250);

        await run.Tool(api).RunAsync(run.Context);

        Assert.All(new[] { "apps", "vulnerabilities" }, endpoint => Assert.Equal(1, api.Count(endpoint)));
        Assert.Equal(10L, (await MarkerAsync(run)).Findings);
    }

    [Fact]
    public async Task ThePageSizeFromEngineJsonIsUsed()
    {
        using var run = new Rapid7Run(s => s with { PageSize = 25, Areas = ["applications"] });
        var api = TypicalApi();

        await run.Tool(api).RunAsync(run.Context);

        Assert.Equal("25", api.Requests.Single().Query["size"]);
    }

    [Fact]
    public async Task TheConsoleTokenModeInEngineJsonChangesHowTheTokenIsSent()
    {
        using var run = new Rapid7Run(s => s with { AuthMode = "console_token", Areas = ["applications"] });
        var api = TypicalApi();

        await run.Tool(api).RunAsync(run.Context);

        Assert.StartsWith("Basic ", api.Requests.Single().Headers["authorization"]);
    }

    // ---- progress ----

    [Fact]
    public async Task TheStepMessageShowsWhichPageOfWhichAreaIsBeingRead()
    {
        using var run = new Rapid7Run();
        var api = TypicalApi();
        var messages = new List<string?>();
        api.Intercept = (_, _) =>
        {
            messages.Add(run.Step.Message);
            return Task.FromResult<HttpResponseMessage?>(null);
        };

        await run.Tool(api).RunAsync(run.Context);

        // Each message is seen at the next request, so it describes the page just read.
        Assert.Contains(messages, m => m == "applications page 1/1");
        Assert.Contains(messages, m => m is not null && m.StartsWith("vulnerabilities page 1/5: 100 so far"));
        Assert.Contains(messages, m => m is not null && m.StartsWith("vulnerabilities page 2/5: 200 so far"));
    }

    [Fact]
    public async Task ARetryIsShownInTheStepMessage()
    {
        using var run = new Rapid7Run();
        var api = new FakeRapid7().With("apps", R7.App("app-1")).With("vulnerabilities");
        var failures = 1;
        string? seen = null;
        api.Intercept = (_, _) =>
        {
            if (failures-- <= 0)
            {
                seen ??= run.Step.Message; // the message as the first retry begins
                return Task.FromResult<HttpResponseMessage?>(null);
            }

            return Task.FromResult<HttpResponseMessage?>(FakeRapid7.Status(503));
        };

        await run.Tool(api).RunAsync(run.Context);

        Assert.Contains("retrying in 1s", seen);
    }

    [Fact]
    public async Task AnAreaWithNothingInItIsNotShownAsPageOneOfZero()
    {
        using var run = new Rapid7Run(s => s with { Areas = ["applications", "scans", "reports"] });
        var api = new FakeRapid7().With("apps", R7.App("app-1")).With("scans").With("reports");
        var messages = new List<string?>();
        api.Intercept = (_, _) =>
        {
            messages.Add(run.Step.Message);
            return Task.FromResult<HttpResponseMessage?>(null);
        };

        await run.Tool(api).RunAsync(run.Context);

        Assert.DoesNotContain(messages, m => m is not null && m.EndsWith("/0"));
        Assert.Contains(messages, m => m == "scans page 1");
    }

    // ---- settings and secrets ----

    [Fact]
    public async Task WithoutABaseUrlTheImportFailsBeforeAnyRequest()
    {
        using var run = new Rapid7Run(s => s with { BaseUrl = null });
        var api = TypicalApi();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run.Tool(api).RunAsync(run.Context));

        Assert.Contains("tools.Rapid7.baseUrl", error.Message);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task WithoutATokenSecretNameTheImportFailsBeforeAnyRequest()
    {
        using var run = new Rapid7Run(s => s with { TokenSecret = null });
        var api = TypicalApi();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run.Tool(api).RunAsync(run.Context));

        Assert.Contains("tools.Rapid7.tokenSecret", error.Message);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task AMissingSecretFailsTheStepNamingItBeforeAnyRequest()
    {
        using var run = new Rapid7Run(writeSecret: false);
        var api = TypicalApi();

        var error = await Assert.ThrowsAsync<SecretUnavailableException>(() => run.Tool(api).RunAsync(run.Context));

        Assert.Contains("'rapid7-token'", error.Message);
        Assert.Empty(api.Requests);
        Assert.Empty(run.ResultFiles());
    }

    [Fact]
    public async Task AnExpiredSecretFailsTheStepBeforeAnyRequest()
    {
        using var run = new Rapid7Run();
        var expired = new SecretsDocument
        {
            Secrets = new() { ["rapid7-token"] = new SecretEntry { Value = Token, CreatedAt = DateTimeOffset.UtcNow.AddDays(-9), ExpiresAt = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)) } }
        };
        await SecretsFile.WriteAsync(Path.Combine(run.Root, "secrets.dat"), expired);
        var api = TypicalApi();

        var error = await Assert.ThrowsAsync<SecretUnavailableException>(() => run.Tool(api).RunAsync(run.Context));

        Assert.Contains("expired", error.Message);
        Assert.DoesNotContain(Token, error.Message);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task TheTokenIsNotInAnyFileMarkerOrStatusTheImportWrites()
    {
        using var run = new Rapid7Run();

        await run.Tool(TypicalApi()).RunAsync(run.Context);

        foreach (var file in Directory.GetFiles(run.Context.Paths.RunFolder, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(Token, await File.ReadAllTextAsync(file));
        }

        Assert.DoesNotContain(Token, JsonSerializer.Serialize(run.Context.Status.Snapshot()));
    }

    // ---- failures ----

    [Fact]
    public async Task ARefusedTokenFailsTheStepAtOnceAndLeavesNothingBehind()
    {
        using var run = new Rapid7Run();
        var api = TypicalApi();
        api.Intercept = (_, _) => Task.FromResult<HttpResponseMessage?>(FakeRapid7.Status(401, "{\"message\":\"bad key\"}"));

        var error = await Assert.ThrowsAsync<Rapid7ApiException>(() => run.Tool(api).RunAsync(run.Context));

        Assert.True(error.IsAuthFailure);
        Assert.Single(api.Requests);
        Assert.Empty(run.ResultFiles());
        Assert.Empty(run.SpoolFolders());
    }

    [Fact]
    public async Task UnderTheOrchestratorARefusedTokenFailsTheStepWithAMessageThatHasNoToken()
    {
        using var run = new Rapid7Run();
        var api = TypicalApi();
        api.Intercept = (_, _) => Task.FromResult<HttpResponseMessage?>(FakeRapid7.Status(403, $"{{\"message\":\"key {Token} may not read this\"}}"));
        var orchestrator = new RunOrchestrator(run.Tool(api), new Stub("CheckMarx"), new Stub("RepoClone"), []);

        var results = await orchestrator.RunAsync(run.Context);

        var rapid7 = results.Single(r => r.Tool == "Rapid7");
        Assert.False(rapid7.Succeeded);
        Assert.Contains("HTTP 403", rapid7.Error);
        Assert.DoesNotContain(Token, rapid7.Error);
        Assert.Equal(ToolRunState.Failed, run.Step.Status);
        Assert.DoesNotContain(Token, run.Step.Message);
    }

    [Fact]
    public async Task AFailurePartWayThroughTheVulnerabilitiesLeavesNothingKeptAndNothingMarkedComplete()
    {
        using var run = new Rapid7Run();
        var api = TypicalApi();
        api.Intercept = (request, _) => Task.FromResult(
            request.Endpoint == "vulnerabilities" && request.Query["index"] == "3" ? FakeRapid7.Status(404) : null);

        await Assert.ThrowsAsync<Rapid7ApiException>(() => run.Tool(api).RunAsync(run.Context));

        Assert.Empty(run.ResultFiles());
        Assert.Empty(run.SpoolFolders());
    }

    [Fact]
    public async Task AFailureInTheAppsAreaMeansTheVulnerabilitiesAreNeverRequested()
    {
        using var run = new Rapid7Run();
        var api = TypicalApi();
        api.Intercept = (request, _) => Task.FromResult(request.Endpoint == "apps" ? FakeRapid7.Status(404) : null);

        await Assert.ThrowsAsync<Rapid7ApiException>(() => run.Tool(api).RunAsync(run.Context));

        Assert.Empty(run.ResultFiles());
        Assert.Equal(0, api.Count("vulnerabilities"));
    }

    [Fact]
    public async Task ConnectionProblemsMidwayAreRiddenOutAndTheImportCompletes()
    {
        using var run = new Rapid7Run();
        var api = TypicalApi(vulnsPerApp: 150);
        var failures = 3;
        api.Intercept = (request, _) => request.Endpoint == "vulnerabilities" && failures-- > 0
            ? throw new HttpRequestException("reset")
            : Task.FromResult<HttpResponseMessage?>(null);

        await run.Tool(api).RunAsync(run.Context);

        Assert.Equal([1, 2, 4], run.Delays.Select(d => (int)d.TotalSeconds));
        Assert.Equal(300L, (await MarkerAsync(run)).Findings);
    }

    [Fact]
    public async Task IfTheFileCannotBePutInPlaceTheStepFailsAndNothingIsLeftBehind()
    {
        using var run = new Rapid7Run();
        var api = TypicalApi(vulnsPerApp: 5);
        api.Intercept = (request, _) =>
        {
            // Something appears where the output belongs, after the import has started.
            if (request.Endpoint == "vulnerabilities")
            {
                Directory.CreateDirectory(run.OutputPath);
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };

        await Assert.ThrowsAnyAsync<Exception>(() => run.Tool(api).RunAsync(run.Context));

        Assert.False(File.Exists(run.Context.Paths.ImportDoneMarker("Rapid7")));
        Assert.Empty(LeftOver(run));
        Assert.Empty(run.SpoolFolders());
    }

    [Fact]
    public async Task AnOldOutputIsRemovedWhenTheImportStartsSoAFailedRerunLeavesNoStaleFile()
    {
        using var run = new Rapid7Run();
        Directory.CreateDirectory(run.ResultsDir);
        await File.WriteAllTextAsync(run.OutputPath, "{\"old\":true}");
        await AtomicJsonFile.WriteAsync(run.Context.Paths.ImportDoneMarker("Rapid7"), new ImportDoneMarker());
        var api = TypicalApi();
        api.Intercept = (request, _) => Task.FromResult(request.Endpoint == "vulnerabilities" ? FakeRapid7.Status(404) : null);

        await Assert.ThrowsAsync<Rapid7ApiException>(() => run.Tool(api).RunAsync(run.Context));

        Assert.Empty(run.ResultFiles());
    }

    // ---- never skipped ----

    [Fact]
    public async Task EveryRunImportsEverythingAgainBecauseTheImportIsNeverSkipped()
    {
        using var first = new Rapid7Run();
        var apiOne = TypicalApi(vulnsPerApp: 3);
        await first.Tool(apiOne).RunAsync(first.Context);

        using var second = new Rapid7Run(root: first.Root);
        var apiTwo = TypicalApi(vulnsPerApp: 3);
        await second.Tool(apiTwo).RunAsync(second.Context);

        Assert.Equal(apiOne.Requests.Count, apiTwo.Requests.Count);
        Assert.True(apiTwo.Count("vulnerabilities") > 0);
        Assert.True(File.Exists(second.OutputPath));
    }

    // ---- commands ----

    [Fact]
    public async Task AbortingStopsTheImportAndDeletesWhatWasPartlyWritten()
    {
        using var run = new Rapid7Run();
        var api = TypicalApi();
        using var cancel = new CancellationTokenSource();
        api.Intercept = (request, _) =>
        {
            if (request.Endpoint == "vulnerabilities" && request.Query["index"] == "2")
            {
                cancel.Cancel();
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.Tool(api).RunAsync(run.Context, cancel.Token));

        Assert.Empty(run.ResultFiles());
        Assert.Empty(run.SpoolFolders());
    }

    [Fact]
    public async Task AStopCommandEndsTheImportKeepingNothingBecauseHalfAnImportIsNotAnImport()
    {
        using var run = new Rapid7Run();
        var api = TypicalApi();
        api.Intercept = (request, _) =>
        {
            if (request.Endpoint == "vulnerabilities" && request.Query["index"] == "1")
            {
                run.Context.Control.RequestStop();
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };

        await run.Tool(api).RunAsync(run.Context);

        Assert.Empty(run.ResultFiles());
        Assert.Empty(run.SpoolFolders());
        Assert.Contains("nothing was kept", run.Step.Message);
        Assert.Equal(2, api.Count("vulnerabilities")); // page 1 was fetched, then the import noticed and stopped
    }

    [Fact]
    public async Task DisablingTheToolMidImportEndsItTheSameWay()
    {
        using var run = new Rapid7Run();
        var api = TypicalApi();
        api.Intercept = (request, _) =>
        {
            if (request.Endpoint == "apps")
            {
                run.Context.Control.DisableTool("Rapid7", "vendor outage");
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };

        await run.Tool(api).RunAsync(run.Context);

        Assert.Empty(run.ResultFiles());
        Assert.Equal(0, api.Count("vulnerabilities"));
    }

    [Fact]
    public async Task APausedImportMakesNoFurtherRequestsUntilItIsResumed()
    {
        using var run = new Rapid7Run();
        run.Context.Control.Pause();
        var api = TypicalApi(vulnsPerApp: 3);

        var running = run.Tool(api).RunAsync(run.Context);
        await Task.Delay(300);
        // The first page is requested, then the import waits before it uses it.
        Assert.True(api.Requests.Count <= 1);
        Assert.False(running.IsCompleted);

        run.Context.Control.Resume();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(File.Exists(run.OutputPath));
    }

    // ---- resuming ----

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(ScanMode.Resume)]
    [InlineData(ScanMode.RetryFailed)]
    public async Task AContinuedRunDoesNotImportAgainWhenItsImportFinished(ScanMode mode)
    {
        var root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
        try
        {
            using var first = new Rapid7Run(root: root, runId: "resume-me");
            await first.Tool(TypicalApi(vulnsPerApp: 3)).RunAsync(first.Context);

            using var again = new Rapid7Run(mode: mode, root: root, runId: "resume-me");
            var api = TypicalApi();
            await again.Tool(api).RunAsync(again.Context);

            Assert.Empty(api.Requests);
            Assert.Equal("already imported in this run", again.Step.Message);
            Assert.True(File.Exists(again.OutputPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AContinuedRunImportsAgainIfTheFirstAttemptNeverFinished()
    {
        var root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
        try
        {
            using var first = new Rapid7Run(root: root, runId: "unfinished");
            var failing = TypicalApi();
            failing.Intercept = (request, _) => Task.FromResult(request.Endpoint == "vulnerabilities" ? FakeRapid7.Status(404) : null);
            await Assert.ThrowsAsync<Rapid7ApiException>(() => first.Tool(failing).RunAsync(first.Context));

            using var resumed = new Rapid7Run(mode: ScanMode.Resume, root: root, runId: "unfinished");
            var api = TypicalApi(vulnsPerApp: 3);
            await resumed.Tool(api).RunAsync(resumed.Context);

            Assert.True(api.Requests.Count > 0);
            Assert.True(File.Exists(resumed.OutputPath));
            Assert.True(File.Exists(resumed.Context.Paths.ImportDoneMarker("Rapid7")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task AMarkerWithNoOutputFileIsNotTakenAsComplete()
    {
        var root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
        try
        {
            using var first = new Rapid7Run(root: root, runId: "no-file");
            await first.Tool(TypicalApi(vulnsPerApp: 2)).RunAsync(first.Context);
            File.Delete(first.OutputPath);

            using var resumed = new Rapid7Run(mode: ScanMode.Resume, root: root, runId: "no-file");
            var api = TypicalApi(vulnsPerApp: 2);
            await resumed.Tool(api).RunAsync(resumed.Context);

            Assert.True(api.Requests.Count > 0);
            Assert.True(File.Exists(resumed.OutputPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    // ---- the tool as the orchestrator runs it ----

    [Fact]
    public async Task UnderTheOrchestratorACleanImportEndsCompleted()
    {
        using var run = new Rapid7Run();
        var orchestrator = new RunOrchestrator(run.Tool(TypicalApi(vulnsPerApp: 2)), new Stub("CheckMarx"), new Stub("RepoClone"), []);

        var results = await orchestrator.RunAsync(run.Context);

        Assert.True(results.Single(r => r.Tool == "Rapid7").Succeeded);
        Assert.Equal(ToolRunState.Completed, run.Step.Status);
    }

    [Fact]
    public void ItIsTheRapid7ToolAndItNeedsNoConstructorArgumentsInProduction()
    {
        ITool tool = new Rapid7Import();

        Assert.Equal("Rapid7", tool.Name);
    }
}
