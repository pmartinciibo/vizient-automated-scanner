namespace Vizient.AutomatedScanner.Tests;

using Vizient.AutomatedScanner.Core.Configuration;

public class Rapid7SettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");

    public Rapid7SettingsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Task<EngineConfig> LoadAsync(string rapid7Json)
    {
        var file = Path.Combine(_root, "engine.json");
        File.WriteAllText(file,
            "{ \"paths\": { \"reposRoot\": \"r\", \"runs\": \"u\", \"state\": \"s\", \"tmp\": \"t\", \"logs\": \"l\" }, " +
            $"\"tools\": {{ \"Rapid7\": {rapid7Json} }} }}");
        return EngineConfig.LoadAsync(file, CancellationToken.None);
    }

    [Fact]
    public async Task TheRapid7SettingsAreReadFromEngineJson()
    {
        var config = await LoadAsync("""
            { "enabled": true, "tokenSecret": "rapid7-token", "baseUrl": "https://us.api.insight.rapid7.com/ias/v1",
              "authMode": "console_token", "pageSize": 50, "maxPages": 3, "areas": ["vulnerabilities", "applications"] }
            """);

        var rapid7 = config.Current.Tool("Rapid7");

        Assert.Equal(("rapid7-token", "https://us.api.insight.rapid7.com/ias/v1", "console_token"), (rapid7.TokenSecret, rapid7.BaseUrl, rapid7.AuthMode));
        Assert.Equal((50, 3), (rapid7.PageSize, rapid7.MaxPages));
        Assert.Equal(["vulnerabilities", "applications"], rapid7.Areas);
    }

    [Fact]
    public async Task EverythingIsOptionalSoAnEngineJsonWithoutThemStillLoads()
    {
        var config = await LoadAsync("""{ "tokenSecret": "rapid7-token" }""");

        var rapid7 = config.Current.Tool("Rapid7");

        Assert.Null(rapid7.AuthMode);
        Assert.Null(rapid7.PageSize);
        Assert.Null(rapid7.MaxPages);
        Assert.Null(rapid7.Areas);
    }

    [Theory]
    [InlineData("""{ "baseUrl": "rapid7.example.com" }""", "tools.Rapid7.baseUrl")]
    [InlineData("""{ "baseUrl": "ftp://rapid7.example.com" }""", "tools.Rapid7.baseUrl")]
    [InlineData("""{ "authMode": "basic" }""", "tools.Rapid7.authMode")]
    [InlineData("""{ "pageSize": 0 }""", "tools.Rapid7.pageSize")]
    [InlineData("""{ "pageSize": -5 }""", "tools.Rapid7.pageSize")]
    [InlineData("""{ "maxPages": -1 }""", "tools.Rapid7.maxPages")]
    [InlineData("""{ "areas": [] }""", "tools.Rapid7.areas")]
    [InlineData("""{ "areas": ["applications", "everything"] }""", "everything")]
    public async Task ABadRapid7SettingIsRejectedAtLoadWithAMessageNamingIt(string rapid7Json, string expected)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => LoadAsync(rapid7Json));

        Assert.StartsWith("engine.json:", error.Message);
        Assert.Contains(expected, error.Message);
    }

    [Theory]
    [InlineData("""{ "baseUrl": "https://host" }""")]
    [InlineData("""{ "baseUrl": "http://localhost:3780/ias/v1" }""")]
    [InlineData("""{ "authMode": "api_key" }""")]
    [InlineData("""{ "maxPages": 0 }""")]
    [InlineData("""{ "areas": ["scans"] }""")]
    public async Task GoodRapid7SettingsAreAccepted(string rapid7Json)
    {
        await LoadAsync(rapid7Json);
    }
}
