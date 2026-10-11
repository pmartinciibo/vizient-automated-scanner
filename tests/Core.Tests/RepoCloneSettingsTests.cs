namespace Vizient.AutomatedScanner.Tests;

using Vizient.AutomatedScanner.Core.Configuration;

public class RepoCloneSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");

    public RepoCloneSettingsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Task<EngineConfig> LoadAsync(string repoCloneJson)
    {
        var file = Path.Combine(_root, "engine.json");
        File.WriteAllText(file,
            "{ \"paths\": { \"reposRoot\": \"r\", \"runs\": \"u\", \"state\": \"s\", \"tmp\": \"t\", \"logs\": \"l\" }, " +
            $"\"tools\": {{ \"RepoClone\": {repoCloneJson} }} }}");
        return EngineConfig.LoadAsync(file, CancellationToken.None);
    }

    [Fact]
    public async Task TheRepoCloneSettingsAreReadFromEngineJson()
    {
        var tool = (await LoadAsync("""
            { "gitPath": "C:/git/git.exe", "azPath": "C:/az/az.cmd", "adoTenantIds": ["contoso.onmicrosoft.com", "0f1e2d3c"], "longPaths": false }
            """)).Current.Tool("RepoClone");

        Assert.Equal(("C:/git/git.exe", "C:/az/az.cmd", false), (tool.GitPath, tool.AzPath, tool.LongPaths));
        Assert.Equal(["contoso.onmicrosoft.com", "0f1e2d3c"], tool.AdoTenantIds);
    }

    [Fact]
    public async Task EverythingIsOptional()
    {
        var tool = (await LoadAsync("{ }")).Current.Tool("RepoClone");

        Assert.Null(tool.GitPath);
        Assert.Null(tool.AzPath);
        Assert.Null(tool.AdoTenantIds);
    }

    [Theory]
    [InlineData("""{ "gitPath": "  " }""", "gitPath")]
    [InlineData("""{ "azPath": "" }""", "azPath")]
    [InlineData("""{ "adoTenantIds": ["ok", "bad tenant; calc"] }""", "adoTenantIds")]
    [InlineData("""{ "adoTenantIds": ["--help"] }""", "adoTenantIds")]
    public async Task ABadRepoCloneSettingIsRejectedAtLoadWithAMessageNamingIt(string json, string expected)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => LoadAsync(json));

        Assert.StartsWith("engine.json:", error.Message);
        Assert.Contains(expected, error.Message);
    }
}
