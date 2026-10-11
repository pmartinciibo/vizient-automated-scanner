namespace Vizient.AutomatedScanner.Tests;

using System.Net;
using System.Text.Json;
using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.CheckMarx;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Secrets;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.Tools;

public class CheckMarxImportTests
{
    private const string Refresh = FakeCheckMarx.RefreshToken;

    /// <summary>
    /// Alpha (a1) holds p1 and p2, Beta (a2) holds p2, and p3 belongs to no application.
    /// p1 has an old scan (s1), the one that should be read (s2), and newer ones that must not be: failed (s3), wrong engine (s4).
    /// Findings are only given for the scans that should be read, plus decoys for the others.
    /// </summary>
    private static FakeCheckMarx TypicalApi() => new FakeCheckMarx()
        .With("/api/applications", Cx.App("a1", "Alpha", "p1", "p2"), Cx.App("a2", "Beta", "p2"))
        .With("/api/projects", Cx.Project("p1", "Proj One", "a1"), Cx.Project("p2", "Proj Two", "a1", "a2"), Cx.Project("p3", "Proj Three"))
        .With("/api/scans",
            Cx.Scan("s1", "p1", "2026-01-01T00:00:00Z"),
            Cx.Scan("s2", "p1", "2026-03-01T00:00:00Z"),
            Cx.Scan("s3", "p1", "2026-05-01T00:00:00Z", status: "Failed"),
            Cx.Scan("s4", "p1", "2026-06-01T00:00:00Z", engines: ["sca"]),
            Cx.Scan("s5", "p2", "2026-02-01T00:00:00Z", engines: ["sast", "sca"]),
            Cx.Scan("s6", "p3", "2026-02-02T00:00:00Z"))
        .WithResults("s1", Cx.Findings("old", 9))
        .WithResults("s2", Cx.Findings("s2", 3))
        .WithResults("s3", Cx.Findings("failed", 9))
        .WithResults("s4", Cx.Findings("sca", 9))
        .WithResults("s5", Cx.Findings("s5", 2))
        .WithResults("s6", Cx.Findings("s6", 1));

    private static string[] LeftOver(CheckMarxRun run) =>
        Directory.Exists(run.ResultsDir) ? Directory.GetFiles(run.ResultsDir, "*.tmp") : [];

    private static async Task<ImportDoneMarker> MarkerAsync(CheckMarxRun run) =>
        await AtomicJsonFile.ReadAsync<ImportDoneMarker>(run.Context.Paths.ImportDoneMarker("CheckMarx"));

    private static IEnumerable<string?> Ids(JsonElement list, string property = "id") =>
        list.EnumerateArray().Select(e => e.GetProperty(property).GetString());

    private sealed class Stub(string name) : ITool
    {
        public string Name => name;
        public Task RunAsync(RunContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // ---- the one file ----

    [Fact]
    public async Task TheOutputIsOneJsonFileAndTheMarkerThatSaysItIsComplete()
    {
        using var run = new CheckMarxRun();

        await run.Tool(TypicalApi()).RunAsync(run.Context);

        Assert.Equal(["_import.done.json", "checkmarx.json"], run.ResultFiles());
        Assert.Equal(Path.Combine(run.Context.Paths.RunFolder, "results", "CheckMarx", "checkmarx.json"), run.OutputPath);
    }

    [Fact]
    public async Task TheFileStartsWithWhenAndWhereItCameFromAndWhatWasReadAndHowManyScans()
    {
        using var run = new CheckMarxRun();

        await run.Tool(TypicalApi()).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var root = file.RootElement;
        Assert.Equal(
            ["generatedAt", "apiUrl", "api", "areas", "resultsScope", "resultsEngine", "resultsScanStatus",
             "scansAvailable", "scansQueried", "scansFailed", "apps", "errors"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("https://cx.test", root.GetProperty("apiUrl").GetString());
        Assert.Equal("Checkmarx One REST API", root.GetProperty("api").GetString());
        Assert.Equal(["applications", "projects", "results"], root.GetProperty("areas").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(("latest-per-project", "sast", "Completed"),
            (root.GetProperty("resultsScope").GetString(), root.GetProperty("resultsEngine").GetString(), root.GetProperty("resultsScanStatus").GetString()));
        Assert.Equal((6, 3, 0), (root.GetProperty("scansAvailable").GetInt32(), root.GetProperty("scansQueried").GetInt32(), root.GetProperty("scansFailed").GetInt32()));
        Assert.Equal(0, root.GetProperty("errors").GetArrayLength());
    }

    [Fact]
    public async Task TheFileIsAListOfAppsEachWithItsOwnFieldsItsProjectsAndItsFindings()
    {
        using var run = new CheckMarxRun();

        await run.Tool(TypicalApi()).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var alpha = file.RootElement.GetProperty("apps")[0];
        Assert.Equal(
            ["id", "name", "description", "type", "criticality", "projectIds", "tags", "projects", "results"],
            alpha.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("a1", "Alpha"), (alpha.GetProperty("id").GetString(), alpha.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task AppsAreInOrderOfNameThenIdWithTheUnassignedEntryLast()
    {
        using var run = new CheckMarxRun();

        await run.Tool(TypicalApi()).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var apps = file.RootElement.GetProperty("apps");
        Assert.Equal(3, apps.GetArrayLength());
        Assert.Equal(["a1", "a2"], new[] { apps[0], apps[1] }.Select(a => a.GetProperty("id").GetString()));
        Assert.Equal(JsonValueKind.Null, apps[2].GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task ProjectsAreListedUnderTheirApplicationsInOrderOfName()
    {
        using var run = new CheckMarxRun();

        await run.Tool(TypicalApi()).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var apps = run.Apps(file);
        Assert.Equal(["p1", "p2"], Ids(apps["a1"].GetProperty("projects")));
        Assert.Equal(["p2"], Ids(apps["a2"].GetProperty("projects")));
        Assert.Equal(["p3"], Ids(apps["(null)"].GetProperty("projects")));
        var project = apps["a1"].GetProperty("projects")[0];
        Assert.Equal("Proj One", project.GetProperty("name").GetString());
        Assert.Equal("https://git.example.test/Proj One?a=1&b=2", project.GetProperty("repoUrl").GetString());
    }

    // ---- which scans the findings come from ----

    [Fact]
    public async Task OnlyTheLatestCompletedSastScanOfEachProjectIsReadInTheOrderTheProjectsAreMet()
    {
        using var run = new CheckMarxRun();
        var api = TypicalApi();

        await run.Tool(api).RunAsync(run.Context);

        Assert.Equal(["s2", "s5", "s6"], api.ScanIdsQueried());
    }

    [Fact]
    public async Task EachProjectsFindingsAreThoseOfItsChosenScanAndNoOthers()
    {
        using var run = new CheckMarxRun();

        await run.Tool(TypicalApi()).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var apps = run.Apps(file);
        Assert.Equal(["s2-1", "s2-2", "s2-3", "s5-1", "s5-2"], Ids(apps["a1"].GetProperty("results")));
        Assert.Equal(["s5-1", "s5-2"], Ids(apps["a2"].GetProperty("results")));
        Assert.Equal(["s6-1"], Ids(apps["(null)"].GetProperty("results")));
    }

    [Fact]
    public async Task AProjectInSeveralApplicationsHasItsFindingsUnderEachButTheyAreCountedOnce()
    {
        using var run = new CheckMarxRun();

        await run.Tool(TypicalApi()).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var apps = run.Apps(file);
        Assert.Equal(Ids(apps["a2"].GetProperty("results")), Ids(apps["a1"].GetProperty("results")).Skip(3));
        Assert.Equal(6L, (await MarkerAsync(run)).Findings); // 3 + 2 + 1, not 3 + 2 + 2 + 1
    }

    [Fact]
    public async Task EachFindingIsTaggedWithItsScanAndProject()
    {
        using var run = new CheckMarxRun();

        await run.Tool(TypicalApi()).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var finding = run.Apps(file)["a1"].GetProperty("results")[0];
        Assert.Equal(["type", "id", "severity", "state", "data", "_scan_id", "_project_id", "_project_name"], finding.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("s2", "p1", "name-of-p1"),
            (finding.GetProperty("_scan_id").GetString(), finding.GetProperty("_project_id").GetString(), finding.GetProperty("_project_name").GetString()));
        Assert.Equal("/src/a&b.cs", finding.GetProperty("data").GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task ATagTheFindingAlreadyHasIsLeftAloneAndAFindingThatIsNotAnObjectPassesThrough()
    {
        using var run = new CheckMarxRun();
        var api = TypicalApi().WithResults("s6", "{\"id\":\"f1\",\"_scan_id\":\"keep-me\"}", "7", "\"plain\"");

        await run.Tool(api).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var results = run.Apps(file)["(null)"].GetProperty("results");
        Assert.Equal("keep-me", results[0].GetProperty("_scan_id").GetString());
        Assert.Equal("p3", results[0].GetProperty("_project_id").GetString());
        Assert.Equal(7, results[1].GetInt32());
        Assert.Equal("plain", results[2].GetString());
    }

    [Fact]
    public async Task EachProjectShowsTheScanItsFindingsCameFrom()
    {
        using var run = new CheckMarxRun();

        await run.Tool(TypicalApi()).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var project = run.Apps(file)["a1"].GetProperty("projects")[0];
        Assert.Equal("p1", project.GetProperty("id").GetString());
        var scan = project.GetProperty("scan");
        Assert.Equal(("s2", "Completed"), (scan.GetProperty("id").GetString(), scan.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task TheScopeAllReadsEveryCompletedSastScanAndShowsNoSingleScanPerProject()
    {
        using var run = new CheckMarxRun(s => s with { ResultsScope = "all" });
        var api = TypicalApi();

        await run.Tool(api).RunAsync(run.Context);

        Assert.Equal(["s1", "s2", "s5", "s6"], api.ScanIdsQueried());
        using var file = run.ReadOutput();
        var apps = run.Apps(file);
        // p1 gives s1 (9) and s2 (3), and p2 gives s5 (2).
        Assert.Equal(9 + 3 + 2, apps["a1"].GetProperty("results").GetArrayLength());
        Assert.False(apps["a1"].GetProperty("projects")[0].TryGetProperty("scan", out _));
    }

    [Fact]
    public async Task AnEmptyEngineFilterAcceptsAnyEngine()
    {
        using var run = new CheckMarxRun(s => s with { ResultsEngine = "" });
        var api = TypicalApi();

        await run.Tool(api).RunAsync(run.Context);

        Assert.Equal(["s4", "s5", "s6"], api.ScanIdsQueried()); // s4 is p1's newest completed scan, whatever it ran
    }

    [Fact]
    public async Task AnEmptyStatusFilterAcceptsAnyStatus()
    {
        using var run = new CheckMarxRun(s => s with { ResultsScanStatus = "" });
        var api = TypicalApi();

        await run.Tool(api).RunAsync(run.Context);

        Assert.Equal(["s3", "s5", "s6"], api.ScanIdsQueried()); // s3 is p1's newest sast scan, whatever its status
    }

    [Fact]
    public async Task TheEngineIsMatchedWithoutRegardToCase()
    {
        using var run = new CheckMarxRun(s => s with { ResultsEngine = "SAST" });
        var api = TypicalApi();

        await run.Tool(api).RunAsync(run.Context);

        Assert.Equal(["s2", "s5", "s6"], api.ScanIdsQueried());
    }

    [Fact]
    public async Task ADifferentEngineCanBeAskedFor()
    {
        using var run = new CheckMarxRun(s => s with { ResultsEngine = "sca" });
        var api = TypicalApi();

        await run.Tool(api).RunAsync(run.Context);

        Assert.Equal(["s4", "s5"], api.ScanIdsQueried());
    }

    [Fact]
    public async Task AScanLimitReadsFindingsFromJustTheFirstFewSelectedScans()
    {
        using var run = new CheckMarxRun(s => s with { ResultsMaxScans = 2 });
        var api = TypicalApi();

        await run.Tool(api).RunAsync(run.Context);

        Assert.Equal(["s2", "s5"], api.ScanIdsQueried());
        using var file = run.ReadOutput();
        Assert.Equal((6, 2), (file.RootElement.GetProperty("scansAvailable").GetInt32(), file.RootElement.GetProperty("scansQueried").GetInt32()));
        Assert.Equal(0, run.Apps(file)["(null)"].GetProperty("results").GetArrayLength());
        Assert.Equal(5L, (await MarkerAsync(run)).Findings);
    }

    [Fact]
    public async Task ScansWithMissingFieldsAreHandledWithoutFailing()
    {
        using var run = new CheckMarxRun();
        var api = TypicalApi()
            .With("/api/scans",
                Cx.Scan("s2", "p1", "2026-03-01T00:00:00Z"),
                "{\"id\":\"no-project\",\"status\":\"Completed\",\"engines\":[\"sast\"]}",
                "{\"id\":\"no-engines\",\"status\":\"Completed\",\"projectId\":\"p2\"}",
                "{\"id\":\"no-date\",\"status\":\"Completed\",\"projectId\":\"p3\",\"engines\":[\"sast\"]}",
                "12345")
            .WithResults("no-project", Cx.Finding("orphan"))
            .WithResults("no-date", Cx.Finding("nodate"));

        await run.Tool(api).RunAsync(run.Context);

        Assert.Equal(["s2", "no-project", "no-date"], api.ScanIdsQueried());
        using var file = run.ReadOutput();
        Assert.Equal(5, file.RootElement.GetProperty("scansAvailable").GetInt32());
        // A scan with no project has no home, so its findings go in the unassigned entry.
        Assert.Contains("orphan", Ids(run.Apps(file)["(null)"].GetProperty("results")));
    }

    // ---- which application each project belongs to ----

    [Fact]
    public async Task AProjectNamingAnApplicationThatIsNotInTheListGetsThatApplicationAnIdOnlyEntry()
    {
        using var run = new CheckMarxRun();
        var api = new FakeCheckMarx()
            .With("/api/applications", Cx.App("a1", "Alpha"))
            .With("/api/projects", Cx.Project("p1", "P One", "ghost-app"))
            .With("/api/scans");

        await run.Tool(api).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var ghost = run.Apps(file)["ghost-app"];
        Assert.Equal(["id", "projects", "results"], ghost.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["p1"], Ids(ghost.GetProperty("projects")));
    }

    [Fact]
    public async Task AnApplicationThatListsAProjectClaimsItEvenIfTheProjectDoesNotListTheApplication()
    {
        using var run = new CheckMarxRun();
        var api = new FakeCheckMarx()
            .With("/api/applications", Cx.App("a1", "Alpha", "p1"))
            .With("/api/projects", Cx.Project("p1", "P One"))
            .With("/api/scans");

        await run.Tool(api).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var apps = run.Apps(file);
        Assert.Equal(["p1"], Ids(apps["a1"].GetProperty("projects")));
        Assert.False(apps.ContainsKey("(null)"));
    }

    [Fact]
    public async Task FindingsOfAProjectThatIsNotInTheListGoUnderTheUnassignedEntryWithNoProjectEntry()
    {
        using var run = new CheckMarxRun();
        var api = new FakeCheckMarx()
            .With("/api/applications", Cx.App("a1", "Alpha"))
            .With("/api/projects", Cx.Project("p1", "P One", "a1"))
            .With("/api/scans", Cx.Scan("s9", "p9", "2026-01-01T00:00:00Z"))
            .WithResults("s9", Cx.Finding("f9"));

        await run.Tool(api).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var unassigned = run.Apps(file)["(null)"];
        Assert.Equal(["f9"], Ids(unassigned.GetProperty("results")));
        Assert.Equal(0, unassigned.GetProperty("projects").GetArrayLength());
    }

    [Fact]
    public async Task ARecordAlreadyUsingOneOfTheFileListNamesDoesNotClash()
    {
        using var run = new CheckMarxRun();
        var api = new FakeCheckMarx()
            .With("/api/applications", "{\"id\":\"a1\",\"name\":\"Alpha\",\"projects\":\"a summary\",\"results\":42}")
            .With("/api/projects", "{\"id\":\"p1\",\"name\":\"P\",\"applicationIds\":[\"a1\"],\"scan\":\"old\"}")
            .With("/api/scans", Cx.Scan("s1", "p1", "2026-01-01T00:00:00Z"))
            .WithResults("s1", Cx.Finding("f1"));

        await run.Tool(api).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var app = run.Apps(file)["a1"];
        Assert.Equal(JsonValueKind.Array, app.GetProperty("projects").ValueKind);
        Assert.Equal(JsonValueKind.Array, app.GetProperty("results").ValueKind);
        Assert.Equal(1, app.EnumerateObject().Count(p => p.Name == "projects"));
        Assert.Equal(1, app.EnumerateObject().Count(p => p.Name == "results"));

        // The project's own "scan" field is replaced by the scan its findings came from.
        var project = app.GetProperty("projects")[0];
        Assert.Equal(1, project.EnumerateObject().Count(p => p.Name == "scan"));
        Assert.Equal("s1", project.GetProperty("scan").GetProperty("id").GetString());
    }

    // ---- which areas ----

    [Fact]
    public async Task WithApplicationsAndProjectsOnlyNoScansOrFindingsAreFetched()
    {
        using var run = new CheckMarxRun(s => s with { Areas = ["applications", "projects"] });
        var api = TypicalApi();

        await run.Tool(api).RunAsync(run.Context);

        Assert.Equal(0, api.Count("/api/scans"));
        Assert.Equal(0, api.Count("/api/results"));
        using var file = run.ReadOutput();
        Assert.Equal(["a1", "a2", "(null)"], run.Apps(file).Keys);
        Assert.All(file.RootElement.GetProperty("apps").EnumerateArray(), a => Assert.False(a.TryGetProperty("results", out _)));
        Assert.False(file.RootElement.TryGetProperty("scansQueried", out _));
    }

    [Fact]
    public async Task WithApplicationsOnlyTheAppsHaveNoProjectsOrFindings()
    {
        using var run = new CheckMarxRun(s => s with { Areas = ["applications"] });
        var api = TypicalApi();

        await run.Tool(api).RunAsync(run.Context);

        Assert.Equal(["/api/applications"], api.Snapshot().Where(r => r.Method == "GET").Select(r => r.Path).Distinct());
        using var file = run.ReadOutput();
        Assert.Equal(2, file.RootElement.GetProperty("apps").GetArrayLength());
        Assert.All(file.RootElement.GetProperty("apps").EnumerateArray(),
            a => Assert.Equal(["id", "name", "description", "type", "criticality", "projectIds", "tags"], a.EnumerateObject().Select(p => p.Name)));
    }

    [Fact]
    public async Task ThePageCapAppliesToEachCollectionAndToEachScansFindings()
    {
        using var run = new CheckMarxRun(s => s with { PageSize = 2, MaxPages = 1 });
        var api = TypicalApi();

        await run.Tool(api).RunAsync(run.Context);

        Assert.Equal(1, api.Count("/api/applications"));
        Assert.Equal(1, api.Count("/api/projects"));
        Assert.Equal(1, api.Count("/api/scans"));
        // Page 1 of 2 scans holds s1 and s2 only, so just p1 is selected, and only its first two findings are read.
        Assert.Equal(["s2"], api.ScanIdsQueried());
        using var file = run.ReadOutput();
        Assert.Equal(["s2-1", "s2-2"], Ids(run.Apps(file)["a1"].GetProperty("results")));
    }

    [Fact]
    public async Task ThePageSizeFromEngineJsonIsUsed()
    {
        using var run = new CheckMarxRun(s => s with { PageSize = 25, Areas = ["applications", "projects"] });
        var api = TypicalApi();

        await run.Tool(api).RunAsync(run.Context);

        Assert.All(api.Snapshot().Where(r => r.Method == "GET"), r => Assert.Equal("25", r.Query["limit"]));
    }

    // ---- the file itself ----

    [Fact]
    public async Task TheFileIsCompactBecauseThereCanBeHundredsOfThousandsOfFindings()
    {
        using var run = new CheckMarxRun();

        await run.Tool(TypicalApi()).RunAsync(run.Context);

        var text = await File.ReadAllTextAsync(run.OutputPath);
        Assert.DoesNotContain('\n', text);
        Assert.DoesNotContain("  ", text);
    }

    [Fact]
    public async Task TheMarkerCountsAppsFindingsAndFailedScans()
    {
        using var run = new CheckMarxRun();

        await run.Tool(TypicalApi()).RunAsync(run.Context);

        var marker = await MarkerAsync(run);
        Assert.Equal((3, 6L, 0), (marker.Entities, marker.Findings, marker.FailedEntities));
        Assert.True(marker.EndedAt >= marker.StartedAt);
    }

    [Fact]
    public async Task TheMarkerAndTheFileAppearTogetherAndOnlyAtTheEnd()
    {
        using var run = new CheckMarxRun();
        var api = TypicalApi();
        var sawEarly = false;
        api.Intercept = (_, _) =>
        {
            sawEarly |= File.Exists(run.OutputPath) || File.Exists(run.Context.Paths.ImportDoneMarker("CheckMarx"));
            return Task.FromResult<HttpResponseMessage?>(null);
        };

        await run.Tool(api).RunAsync(run.Context);

        Assert.False(sawEarly);
        Assert.True(File.Exists(run.OutputPath));
        Assert.True(File.Exists(run.Context.Paths.ImportDoneMarker("CheckMarx")));
    }

    [Fact]
    public async Task TheStepShowsHowManyAppsWereWrittenAndHowManyScansFailed()
    {
        using var run = new CheckMarxRun();

        await run.Tool(TypicalApi()).RunAsync(run.Context);

        Assert.Equal((3, 3, 0), (run.Step.Total, run.Step.Done, run.Step.Failed));
        Assert.Null(run.Step.Message);
    }

    [Fact]
    public async Task NoTemporaryFilesOrFoldersAreLeftBehind()
    {
        using var run = new CheckMarxRun();

        await run.Tool(TypicalApi()).RunAsync(run.Context);

        Assert.Empty(LeftOver(run));
        Assert.Empty(run.SpoolFolders());
    }

    [Fact]
    public async Task FindingsAreSetAsideOnTheTempDriveWhileTheyArriveNotInTheResults()
    {
        using var run = new CheckMarxRun();
        var api = TypicalApi();
        string[]? spoolFolders = null;
        string[]? inResults = null;
        api.Intercept = (request, _) =>
        {
            if (request.Path == "/api/results" && request.Query["scan-id"] == "s6")
            {
                spoolFolders = run.SpoolFolders();
                inResults = run.ResultFiles().ToArray();
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };

        await run.Tool(api).RunAsync(run.Context);

        Assert.Single(spoolFolders!);
        Assert.All(inResults!, f => Assert.StartsWith(".", f));
        Assert.Empty(run.SpoolFolders());
    }

    [Fact]
    public async Task ManyProjectsAndAHugeNumberOfFindingsAreHandledAsAStream()
    {
        using var run = new CheckMarxRun();
        var api = new FakeCheckMarx()
            .With("/api/applications", Enumerable.Range(1, 20).Select(n => Cx.App($"a{n:D2}", $"App {n:D2}")).ToArray())
            .With("/api/projects", Enumerable.Range(1, 40).Select(n => Cx.Project($"p{n:D2}", $"P {n:D2}", $"a{(n - 1) % 20 + 1:D2}")).ToArray())
            .With("/api/scans", Enumerable.Range(1, 40).Select(n => Cx.Scan($"s{n:D2}", $"p{n:D2}", "2026-01-01T00:00:00Z")).ToArray());
        foreach (var n in Enumerable.Range(1, 40))
        {
            api.WithResults($"s{n:D2}", Cx.Findings($"f{n:D2}", 250));
        }

        await run.Tool(api).RunAsync(run.Context);

        Assert.Equal((20, 10_000L, 0), ((await MarkerAsync(run)).Entities, (await MarkerAsync(run)).Findings, (await MarkerAsync(run)).FailedEntities));
        using var file = run.ReadOutput();
        Assert.All(file.RootElement.GetProperty("apps").EnumerateArray(), a => Assert.Equal(500, a.GetProperty("results").GetArrayLength()));
        Assert.Equal(40 * 3, api.Count("/api/results")); // 250 findings is two full pages and one short one
    }

    [Fact]
    public async Task AFindingLargerThanTheCopyBufferIsStillCopiedWhole()
    {
        using var run = new CheckMarxRun();
        var big = "{\"id\":\"big\",\"description\":\"" + new string('x', 300_000) + "\"}";
        var api = new FakeCheckMarx()
            .With("/api/applications", Cx.App("a1", "Alpha"))
            .With("/api/projects", Cx.Project("p1", "P", "a1"))
            .With("/api/scans", Cx.Scan("s1", "p1", "2026-01-01T00:00:00Z"))
            .WithResults("s1", big, Cx.Finding("small"));

        await run.Tool(api).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var results = run.Apps(file)["a1"].GetProperty("results");
        Assert.Equal(300_000, results[0].GetProperty("description").GetString()!.Length);
        Assert.Equal("small", results[1].GetProperty("id").GetString());
    }

    // ---- scans whose findings cannot be read ----

    [Fact]
    public async Task AScanWhoseFindingsCannotBeReadIsSkippedAndListedAndTheRestCarryOn()
    {
        using var run = new CheckMarxRun();
        var api = TypicalApi();
        api.Intercept = (r, _) => Task.FromResult(r.Path == "/api/results" && r.Query["scan-id"] == "s5"
            ? FakeCheckMarx.Status(404, "{\"code\":404,\"message\":\"scan-id s5 not found\"}")
            : null);

        await run.Tool(api).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var root = file.RootElement;
        var errors = root.GetProperty("errors");
        Assert.Equal(1, errors.GetArrayLength());
        Assert.Equal(("s5", "p2", "name-of-p2"), (errors[0].GetProperty("scanId").GetString(), errors[0].GetProperty("projectId").GetString(), errors[0].GetProperty("projectName").GetString()));
        Assert.Contains("HTTP 404", errors[0].GetProperty("error").GetString());
        Assert.Contains("scan-id s5 not found", errors[0].GetProperty("error").GetString());
        Assert.Equal(1, root.GetProperty("scansFailed").GetInt32());

        var apps = run.Apps(file);
        Assert.Equal(["s2-1", "s2-2", "s2-3"], Ids(apps["a1"].GetProperty("results")));
        Assert.Equal(0, apps["a2"].GetProperty("results").GetArrayLength());
        Assert.Equal(["s6-1"], Ids(apps["(null)"].GetProperty("results")));
    }

    [Fact]
    public async Task AFailedScanIsCountedInTheMarkerAndTheStep()
    {
        using var run = new CheckMarxRun();
        var api = TypicalApi();
        api.Intercept = (r, _) => Task.FromResult(r.Path == "/api/results" && r.Query["scan-id"] == "s5" ? FakeCheckMarx.Status(404) : null);

        await run.Tool(api).RunAsync(run.Context);

        var marker = await MarkerAsync(run);
        Assert.Equal((3, 4L, 1), (marker.Entities, marker.Findings, marker.FailedEntities));
        Assert.Equal((3, 3, 1), (run.Step.Total, run.Step.Done, run.Step.Failed));
    }

    [Fact]
    public async Task AFailedScanFlagsTheStepWithoutFailingIt()
    {
        using var run = new CheckMarxRun();
        var api = TypicalApi();
        api.Intercept = (r, _) => Task.FromResult(r.Path == "/api/results" && r.Query["scan-id"] == "s5" ? FakeCheckMarx.Status(404) : null);
        var orchestrator = new RunOrchestrator(new Stub("Rapid7"), run.Tool(api), new Stub("RepoClone"), []);

        var results = await orchestrator.RunAsync(run.Context);

        Assert.True(results.Single(r => r.Tool == "CheckMarx").Succeeded);
        Assert.Equal(ToolRunState.CompletedWithFlags, run.Step.Status);
    }

    [Fact]
    public async Task WhenAScanFailsPartWayItsEarlierPagesAreTakenBackToo()
    {
        using var run = new CheckMarxRun(s => s with { PageSize = 2 });
        var api = TypicalApi(); // s2 has 3 findings: a page of 2, then a page of 1
        api.Intercept = (r, _) => Task.FromResult(
            r.Path == "/api/results" && r.Query["scan-id"] == "s2" && r.Query["offset"] == "2" ? FakeCheckMarx.Status(404) : null);

        await run.Tool(api).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var apps = run.Apps(file);
        Assert.Equal(["s5-1", "s5-2"], Ids(apps["a1"].GetProperty("results"))); // nothing of s2's, not even its first page
        Assert.Equal("s2", file.RootElement.GetProperty("errors")[0].GetProperty("scanId").GetString());
        Assert.Equal(3L, (await MarkerAsync(run)).Findings); // s5's 2 and s6's 1; none of s2's
    }

    [Fact]
    public async Task AScanThatFailsAfterAnEarlierScanOfTheSameProjectKeepsTheEarlierScansFindings()
    {
        using var run = new CheckMarxRun(s => s with { ResultsScope = "all" });
        var api = TypicalApi();
        api.Intercept = (r, _) => Task.FromResult(r.Path == "/api/results" && r.Query["scan-id"] == "s2" ? FakeCheckMarx.Status(404) : null);

        await run.Tool(api).RunAsync(run.Context);

        using var file = run.ReadOutput();
        var a1 = Ids(run.Apps(file)["a1"].GetProperty("results")).ToList();
        Assert.Equal(9, a1.Count(id => id!.StartsWith("old-")));   // s1, read first, stays
        Assert.Equal(0, a1.Count(id => id!.StartsWith("s2-")));    // s2 failed and is gone
    }

    [Fact]
    public async Task ConnectionProblemsMidwayAreRiddenOutAndNothingIsSkipped()
    {
        using var run = new CheckMarxRun();
        var api = TypicalApi();
        var failures = 3;
        api.Intercept = (r, _) => r.Path == "/api/results" && failures-- > 0
            ? throw new HttpRequestException("reset")
            : Task.FromResult<HttpResponseMessage?>(null);

        await run.Tool(api).RunAsync(run.Context);

        Assert.Equal([1, 2, 4], run.Delays.Select(d => (int)d.TotalSeconds));
        var marker = await MarkerAsync(run);
        Assert.Equal((6L, 0), (marker.Findings, marker.FailedEntities));
    }

    [Fact]
    public async Task ScanFailuresThatNeverClearAreListedAsTooAfterEightAttempts()
    {
        using var run = new CheckMarxRun();
        var api = TypicalApi();
        api.Intercept = (r, _) => r.Path == "/api/results" && r.Query["scan-id"] == "s6"
            ? throw new HttpRequestException("no route")
            : Task.FromResult<HttpResponseMessage?>(null);

        await run.Tool(api).RunAsync(run.Context);

        using var file = run.ReadOutput();
        Assert.Contains("Giving up", file.RootElement.GetProperty("errors")[0].GetProperty("error").GetString());
        Assert.Equal(5L, (await MarkerAsync(run)).Findings);
    }

    // ---- failures that stop the import ----

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task ARefusedTokenOnAFindingsCallStopsTheWholeImportAndKeepsNothing(int status)
    {
        using var run = new CheckMarxRun();
        var api = TypicalApi();
        api.Intercept = (r, _) => Task.FromResult(r.Path == "/api/results" && r.Query["scan-id"] == "s5" ? FakeCheckMarx.Status(status) : null);

        var error = await Assert.ThrowsAsync<CheckMarxApiException>(() => run.Tool(api).RunAsync(run.Context));

        Assert.True(error.IsAuthFailure);
        Assert.Empty(run.ResultFiles());
        Assert.Empty(run.SpoolFolders());
        Assert.DoesNotContain("s6", api.ScanIdsQueried());
    }

    [Fact]
    public async Task ARefusedRefreshTokenFailsTheStepAtOnceBeforeAnythingIsRequested()
    {
        using var run = new CheckMarxRun();
        var api = TypicalApi();
        api.Intercept = (r, _) => Task.FromResult(r.Method == "POST" ? FakeCheckMarx.Status(400, "{\"error\":\"invalid_grant\"}") : null);

        var error = await Assert.ThrowsAsync<CheckMarxApiException>(() => run.Tool(api).RunAsync(run.Context));

        Assert.True(error.IsAuthFailure);
        Assert.Single(api.Requests);
        Assert.Empty(run.ResultFiles());
    }

    [Fact]
    public async Task UnderTheOrchestratorARefusedTokenFailsTheStepWithAMessageThatHasNoToken()
    {
        using var run = new CheckMarxRun();
        var api = TypicalApi();
        api.Intercept = (r, _) => Task.FromResult(r.Method == "GET"
            ? FakeCheckMarx.Status(403, $"{{\"message\":\"{Refresh} {api.LatestAccessToken} may not read this\"}}")
            : null);
        var orchestrator = new RunOrchestrator(new Stub("Rapid7"), run.Tool(api), new Stub("RepoClone"), []);

        var results = await orchestrator.RunAsync(run.Context);

        var checkMarx = results.Single(r => r.Tool == "CheckMarx");
        Assert.False(checkMarx.Succeeded);
        Assert.Contains("HTTP 403", checkMarx.Error);
        Assert.DoesNotContain(Refresh, checkMarx.Error);
        Assert.DoesNotContain("access-token-", checkMarx.Error);
        Assert.Equal(ToolRunState.Failed, run.Step.Status);
    }

    [Theory]
    [InlineData("/api/applications")]
    [InlineData("/api/projects")]
    [InlineData("/api/scans")]
    public async Task AFailureReadingACollectionStopsTheImportAndKeepsNothing(string path)
    {
        using var run = new CheckMarxRun();
        var api = TypicalApi();
        api.Intercept = (r, _) => Task.FromResult(r.Path == path ? FakeCheckMarx.Status(404) : null);

        await Assert.ThrowsAsync<CheckMarxApiException>(() => run.Tool(api).RunAsync(run.Context));

        Assert.Empty(run.ResultFiles());
        Assert.Empty(run.SpoolFolders());
        Assert.Equal(0, api.Count("/api/results"));
    }

    [Fact]
    public async Task IfTheFileCannotBePutInPlaceTheStepFailsAndNothingIsLeftBehind()
    {
        using var run = new CheckMarxRun();
        var api = TypicalApi();
        api.Intercept = (request, _) =>
        {
            if (request.Path == "/api/results" && request.Query["scan-id"] == "s6")
            {
                Directory.CreateDirectory(run.OutputPath); // something appears where the output belongs
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };

        await Assert.ThrowsAnyAsync<Exception>(() => run.Tool(api).RunAsync(run.Context));

        Assert.False(File.Exists(run.Context.Paths.ImportDoneMarker("CheckMarx")));
        Assert.Empty(LeftOver(run));
        Assert.Empty(run.SpoolFolders());
    }

    [Fact]
    public async Task AnOldOutputAndMarkerAreRemovedWhenTheImportStartsSoAFailedRerunLeavesNothingStale()
    {
        using var run = new CheckMarxRun();
        Directory.CreateDirectory(run.ResultsDir);
        await File.WriteAllTextAsync(run.OutputPath, "{\"old\":true}");
        await AtomicJsonFile.WriteAsync(run.Context.Paths.ImportDoneMarker("CheckMarx"), new ImportDoneMarker());
        var api = TypicalApi();
        api.Intercept = (r, _) => Task.FromResult(r.Path == "/api/projects" ? FakeCheckMarx.Status(404) : null);

        await Assert.ThrowsAsync<CheckMarxApiException>(() => run.Tool(api).RunAsync(run.Context));

        Assert.Empty(run.ResultFiles());
    }

    // ---- the tokens ----

    [Fact]
    public async Task ALongImportAsksForNewAccessTokensAsTheyGetOldAndUsesTheNewOnes()
    {
        var clock = new MovableClock(DateTimeOffset.Parse("2026-10-14T03:00:00Z"));
        using var run = new CheckMarxRun();
        var api = TypicalApi();
        var bearerAtEachResultsCall = new List<(string Used, string? Latest)>();
        api.Intercept = (r, _) =>
        {
            if (r.Path == "/api/results")
            {
                bearerAtEachResultsCall.Add((r.Headers["authorization"], api.LatestAccessToken));
                clock.Advance(TimeSpan.FromSeconds(150)); // each scan takes two and a half minutes
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };

        await run.Tool(api, clock).RunAsync(run.Context);

        Assert.True(api.TokensIssued >= 2);
        // Whatever was sent was the newest token that existed at that moment: never a stale one.
        Assert.All(bearerAtEachResultsCall, call => Assert.Equal($"Bearer {call.Latest}", call.Used));
        Assert.Equal(6L, (await MarkerAsync(run)).Findings);
    }

    [Fact]
    public async Task NeitherTheRefreshTokenNorAnAccessTokenIsInAnyFileMarkerOrStatusTheImportWrites()
    {
        using var run = new CheckMarxRun();
        var api = TypicalApi();
        // A failing scan whose error text echoes the tokens, so they would end up in the output if they were not masked.
        api.Intercept = (r, _) => Task.FromResult(r.Path == "/api/results" && r.Query["scan-id"] == "s5"
            ? FakeCheckMarx.Status(400, $"{{\"message\":\"bad {Refresh} {api.LatestAccessToken}\"}}")
            : null);

        await run.Tool(api).RunAsync(run.Context);

        foreach (var file in Directory.GetFiles(run.Context.Paths.RunFolder, "*", SearchOption.AllDirectories))
        {
            var text = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain(Refresh, text);
            Assert.DoesNotContain("access-token-", text);
        }

        var status = JsonSerializer.Serialize(run.Context.Status.Snapshot());
        Assert.DoesNotContain(Refresh, status);
        Assert.DoesNotContain("access-token-", status);
    }

    // ---- settings and secrets ----

    [Fact]
    public async Task WithoutATenantTheImportFailsBeforeAnyRequest()
    {
        using var run = new CheckMarxRun(s => s with { Tenant = null });
        var api = TypicalApi();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run.Tool(api).RunAsync(run.Context));

        Assert.Contains("tools.CheckMarx.tenant", error.Message);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task WithoutATokenSecretNameTheImportFailsBeforeAnyRequest()
    {
        using var run = new CheckMarxRun(s => s with { TokenSecret = null });
        var api = TypicalApi();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run.Tool(api).RunAsync(run.Context));

        Assert.Contains("tools.CheckMarx.tokenSecret", error.Message);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task AMissingSecretFailsTheStepNamingItBeforeAnyRequest()
    {
        using var run = new CheckMarxRun(writeSecret: false);
        var api = TypicalApi();

        var error = await Assert.ThrowsAsync<SecretUnavailableException>(() => run.Tool(api).RunAsync(run.Context));

        Assert.Contains("'checkmarx-token'", error.Message);
        Assert.Empty(api.Requests);
        Assert.Empty(run.ResultFiles());
    }

    [Fact]
    public async Task AnExpiredSecretFailsTheStepBeforeAnyRequest()
    {
        using var run = new CheckMarxRun();
        var expired = new SecretsDocument
        {
            Secrets = new() { ["checkmarx-token"] = new SecretEntry { Value = Refresh, CreatedAt = DateTimeOffset.UtcNow.AddDays(-9), ExpiresAt = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)) } }
        };
        await SecretsFile.WriteAsync(Path.Combine(run.Root, "secrets.dat"), expired);
        var api = TypicalApi();

        var error = await Assert.ThrowsAsync<SecretUnavailableException>(() => run.Tool(api).RunAsync(run.Context));

        Assert.Contains("expired", error.Message);
        Assert.DoesNotContain(Refresh, error.Message);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task ACustomTokenAddressAndApiAddressAreUsed()
    {
        using var run = new CheckMarxRun(s => s with
        {
            BaseUrl = "https://eu.cx.test/",
            Tenant = null,
            TokenUrl = "https://iam.eu.test/custom/token"
        });
        var api = TypicalApi();

        await run.Tool(api).RunAsync(run.Context);

        Assert.Equal("https://iam.eu.test/custom/token", api.Requests[0].Uri.ToString());
        Assert.All(api.Requests.Where(r => r.Method == "GET"), r => Assert.Equal("eu.cx.test", r.Uri.Host));
    }

    // ---- never skipped ----

    [Fact]
    public async Task EveryRunImportsEverythingAgainBecauseTheImportIsNeverSkipped()
    {
        using var first = new CheckMarxRun();
        var apiOne = TypicalApi();
        await first.Tool(apiOne).RunAsync(first.Context);

        using var second = new CheckMarxRun(root: first.Root);
        var apiTwo = TypicalApi();
        await second.Tool(apiTwo).RunAsync(second.Context);

        Assert.Equal(apiOne.Requests.Count, apiTwo.Requests.Count);
        Assert.True(File.Exists(second.OutputPath));
    }

    // ---- commands ----

    [Fact]
    public async Task AbortingStopsTheImportAndDeletesWhatWasPartlyWritten()
    {
        using var run = new CheckMarxRun();
        var api = TypicalApi();
        using var cancel = new CancellationTokenSource();
        api.Intercept = (r, _) =>
        {
            if (r.Path == "/api/results" && r.Query["scan-id"] == "s5")
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
    public async Task AStopCommandEndsTheImportKeepingNothing()
    {
        using var run = new CheckMarxRun();
        var api = TypicalApi();
        api.Intercept = (r, _) =>
        {
            if (r.Path == "/api/results" && r.Query["scan-id"] == "s2")
            {
                run.Context.Control.RequestStop();
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };

        await run.Tool(api).RunAsync(run.Context);

        Assert.Empty(run.ResultFiles());
        Assert.Empty(run.SpoolFolders());
        Assert.Contains("nothing was kept", run.Step.Message);
        Assert.Equal(["s2"], api.ScanIdsQueried()); // the next scan was never started
    }

    [Fact]
    public async Task DisablingTheToolMidImportEndsItTheSameWay()
    {
        using var run = new CheckMarxRun();
        var api = TypicalApi();
        api.Intercept = (r, _) =>
        {
            if (r.Path == "/api/projects")
            {
                run.Context.Control.DisableTool("CheckMarx", "vendor outage");
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };

        await run.Tool(api).RunAsync(run.Context);

        Assert.Empty(run.ResultFiles());
        Assert.Equal(0, api.Count("/api/scans"));
    }

    [Fact]
    public async Task APausedImportMakesNoFurtherRequestsUntilItIsResumed()
    {
        using var run = new CheckMarxRun();
        run.Context.Control.Pause();
        var api = TypicalApi();

        var running = run.Tool(api).RunAsync(run.Context);
        await Task.Delay(300);
        Assert.True(api.Snapshot().Count(r => r.Method == "GET") <= 1);
        Assert.False(running.IsCompleted);

        run.Context.Control.Resume();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(File.Exists(run.OutputPath));
    }

    // ---- progress ----

    [Fact]
    public async Task TheStepMessageShowsWhereTheImportIs()
    {
        using var run = new CheckMarxRun();
        var api = TypicalApi();
        var messages = new List<string?>();
        api.Intercept = (_, _) =>
        {
            messages.Add(run.Step.Message);
            return Task.FromResult<HttpResponseMessage?>(null);
        };

        await run.Tool(api).RunAsync(run.Context);

        // Each message is seen at the next request, so it describes what was just read.
        Assert.Contains(messages, m => m == "applications page 1");
        Assert.Contains(messages, m => m == "projects page 1");
        Assert.Contains(messages, m => m == "scans page 1: 6 seen, 3 selected");
        Assert.Contains(messages, m => m is not null && m.StartsWith("results: scan 1/3 (name-of-p1), 3 so far"));
        Assert.Contains(messages, m => m is not null && m.StartsWith("results: scan 2/3 (name-of-p2), 5 so far"));
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
            using var first = new CheckMarxRun(root: root, runId: "resume-me");
            await first.Tool(TypicalApi()).RunAsync(first.Context);

            using var again = new CheckMarxRun(mode: mode, root: root, runId: "resume-me");
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
            using var first = new CheckMarxRun(root: root, runId: "unfinished");
            var failing = TypicalApi();
            failing.Intercept = (r, _) => Task.FromResult(r.Path == "/api/scans" ? FakeCheckMarx.Status(404) : null);
            await Assert.ThrowsAsync<CheckMarxApiException>(() => first.Tool(failing).RunAsync(first.Context));

            using var resumed = new CheckMarxRun(mode: ScanMode.Resume, root: root, runId: "unfinished");
            var api = TypicalApi();
            await resumed.Tool(api).RunAsync(resumed.Context);

            Assert.True(api.Requests.Count > 0);
            Assert.True(File.Exists(resumed.OutputPath));
            Assert.True(File.Exists(resumed.Context.Paths.ImportDoneMarker("CheckMarx")));
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
        using var run = new CheckMarxRun();
        var orchestrator = new RunOrchestrator(new Stub("Rapid7"), run.Tool(TypicalApi()), new Stub("RepoClone"), []);

        var results = await orchestrator.RunAsync(run.Context);

        Assert.True(results.Single(r => r.Tool == "CheckMarx").Succeeded);
        Assert.Equal(ToolRunState.Completed, run.Step.Status);
    }

    [Fact]
    public void ItIsTheCheckMarxToolAndItNeedsNoConstructorArgumentsInProduction()
    {
        ITool tool = new CheckMarxImport();

        Assert.Equal("CheckMarx", tool.Name);
    }
}
