namespace Vizient.AutomatedScanner.Tests;

using Vizient.AutomatedScanner.Core.Configuration;

public class CheckMarxSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");

    public CheckMarxSettingsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Task<EngineConfig> LoadAsync(string checkMarxJson)
    {
        var file = Path.Combine(_root, "engine.json");
        File.WriteAllText(file,
            "{ \"paths\": { \"reposRoot\": \"r\", \"runs\": \"u\", \"state\": \"s\", \"tmp\": \"t\", \"logs\": \"l\" }, " +
            $"\"tools\": {{ \"CheckMarx\": {checkMarxJson} }} }}");
        return EngineConfig.LoadAsync(file, CancellationToken.None);
    }

    [Fact]
    public async Task TheCheckMarxSettingsAreReadFromEngineJson()
    {
        var config = await LoadAsync("""
            { "enabled": true, "tokenSecret": "checkmarx-token", "baseUrl": "https://us.ast.checkmarx.net",
              "tenant": "acme", "tokenUrl": "https://iam.example.test/realms/{tenant}/token", "clientId": "ast-app",
              "pageSize": 50, "maxPages": 3, "areas": ["results", "applications", "projects"],
              "resultsScope": "all", "resultsEngine": "sca", "resultsScanStatus": "", "resultsMaxScans": 7 }
            """);

        var cx = config.Current.Tool("CheckMarx");

        Assert.Equal(("checkmarx-token", "https://us.ast.checkmarx.net", "acme", "ast-app"), (cx.TokenSecret, cx.BaseUrl, cx.Tenant, cx.ClientId));
        Assert.Equal("https://iam.example.test/realms/{tenant}/token", cx.TokenUrl);
        Assert.Equal((50, 3, 7), (cx.PageSize, cx.MaxPages, cx.ResultsMaxScans));
        Assert.Equal(["results", "applications", "projects"], cx.Areas);
        Assert.Equal(("all", "sca", ""), (cx.ResultsScope, cx.ResultsEngine, cx.ResultsScanStatus));
    }

    [Fact]
    public async Task EverythingIsOptionalSoAnEngineJsonWithoutThemStillLoads()
    {
        var cx = (await LoadAsync("""{ "tokenSecret": "checkmarx-token" }""")).Current.Tool("CheckMarx");

        Assert.Null(cx.Tenant);
        Assert.Null(cx.ResultsScope);
        Assert.Null(cx.Areas);
    }

    [Theory]
    [InlineData("""{ "baseUrl": "cx.example.com" }""", "tools.CheckMarx.baseUrl")]
    [InlineData("""{ "baseUrl": "ftp://cx.example.com" }""", "tools.CheckMarx.baseUrl")]
    [InlineData("""{ "tokenUrl": "iam.example.com/token" }""", "tools.CheckMarx.tokenUrl")]
    [InlineData("""{ "tenant": "   " }""", "tools.CheckMarx.tenant")]
    [InlineData("""{ "pageSize": 0 }""", "tools.CheckMarx.pageSize")]
    [InlineData("""{ "maxPages": -1 }""", "tools.CheckMarx.maxPages")]
    [InlineData("""{ "resultsMaxScans": -1 }""", "tools.CheckMarx.resultsMaxScans")]
    [InlineData("""{ "resultsScope": "everything" }""", "tools.CheckMarx.resultsScope")]
    [InlineData("""{ "areas": [] }""", "tools.CheckMarx.areas")]
    [InlineData("""{ "areas": ["applications", "scans"] }""", "scans")]
    [InlineData("""{ "areas": ["results"] }""", "needs")]
    [InlineData("""{ "areas": ["results", "applications"] }""", "needs")]
    [InlineData("""{ "areas": ["results", "projects"] }""", "needs")]
    public async Task ABadCheckMarxSettingIsRejectedAtLoadWithAMessageNamingIt(string json, string expected)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => LoadAsync(json));

        Assert.StartsWith("engine.json:", error.Message);
        Assert.Contains(expected, error.Message);
    }

    [Theory]
    [InlineData("""{ "baseUrl": "https://us.ast.checkmarx.net" }""")]
    [InlineData("""{ "baseUrl": "http://localhost:8080" }""")]
    [InlineData("""{ "tokenUrl": "https://iam.example.test/realms/{tenant}/token" }""")]
    [InlineData("""{ "tokenUrl": "https://iam.example.test/fixed/token" }""")]
    [InlineData("""{ "areas": ["applications"] }""")]
    [InlineData("""{ "areas": ["applications", "projects"] }""")]
    [InlineData("""{ "areas": ["applications", "projects", "results"] }""")]
    [InlineData("""{ "resultsScope": "latest-per-project", "resultsEngine": "", "resultsScanStatus": "", "resultsMaxScans": 0 }""")]
    public async Task GoodCheckMarxSettingsAreAccepted(string json)
    {
        await LoadAsync(json);
    }

    [Fact]
    public async Task TheEngineJsonShippedInTheRepoNamesTheCheckMarxSettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Vizient.AutomatedScanner.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var cx = (await EngineConfig.LoadAsync(Path.Combine(dir.FullName, "engine.json"), CancellationToken.None)).Current.Tool("CheckMarx");

        Assert.Equal("checkmarx-token", cx.TokenSecret);
        Assert.False(string.IsNullOrWhiteSpace(cx.BaseUrl));
        Assert.False(string.IsNullOrWhiteSpace(cx.Tenant));
        Assert.Null(cx.MaxPages); // no trial cap left behind in the shipped config
        Assert.Null(cx.ResultsMaxScans);
    }
}
