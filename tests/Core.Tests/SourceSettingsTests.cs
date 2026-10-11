namespace Vizient.AutomatedScanner.Tests;

using Vizient.AutomatedScanner.Core.Configuration;

public class SourceSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");

    public SourceSettingsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Task<EngineConfig> LoadAsync(string sourcesJson)
    {
        var file = Path.Combine(_root, "engine.json");
        File.WriteAllText(file,
            "{ \"paths\": { \"reposRoot\": \"r\", \"runs\": \"u\", \"state\": \"s\", \"tmp\": \"t\", \"logs\": \"l\" }, " +
            $"\"sources\": {sourcesJson} }}");
        return EngineConfig.LoadAsync(file, CancellationToken.None);
    }

    [Fact]
    public async Task AGitHubSourceNamesOnlyItsTokenAndAnAdoSourceNamesItsOrg()
    {
        var config = await LoadAsync("""
            [ { "source": "GitHub", "tokenSecret": "github-token" },
              { "source": "ADO", "org": "vizientinc", "tokenSecret": "ado-pat" },
              { "source": "ADO", "org": "other" } ]
            """);

        Assert.Equal(3, config.Current.Sources.Count);
    }

    [Theory]
    [InlineData("""[ { "source": "GitHub", "org": "RnD-Vizient", "tokenSecret": "t" } ]""", "must be left out for GitHub")]
    [InlineData("""[ { "source": "GitHub" } ]""", "tokenSecret is required for GitHub")]
    [InlineData("""[ { "source": "GitHub", "tokenSecret": "t" }, { "source": "GitHub", "tokenSecret": "t" } ]""", "a second time")]
    [InlineData("""[ { "source": "ADO", "tokenSecret": "t" } ]""", "org is required for Azure DevOps")]
    [InlineData("""[ { "source": "ADO", "org": "a" }, { "source": "ADO", "org": "a" } ]""", "a second time")]
    public async Task ABadSourceIsRejectedAtLoadWithAMessageNamingIt(string json, string expected)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => LoadAsync(json));

        Assert.StartsWith("engine.json:", error.Message);
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task TwoGitHubTokensAreTwoSources()
    {
        var config = await LoadAsync("""[ { "source": "GitHub", "tokenSecret": "a" }, { "source": "GitHub", "tokenSecret": "b" } ]""");

        Assert.Equal(2, config.Current.Sources.Count);
    }
}
