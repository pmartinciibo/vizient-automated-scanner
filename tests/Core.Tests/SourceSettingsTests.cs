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
            [ { "source": "GitHub", "name": "n8", "tokenSecret": "github-token" },
              { "source": "ADO", "name": "n9", "org": "vizientinc", "tokenSecret": "ado-pat" },
              { "source": "ADO", "name": "n10", "org": "other" } ]
            """);

        Assert.Equal(3, config.Current.Sources.Count);
    }

    [Theory]
    [InlineData("""[ { "source": "GitHub", "name": "n11", "org": "RnD-Vizient", "tokenSecret": "t" } ]""", "must be left out for GitHub")]
    [InlineData("""[ { "source": "GitHub", "name": "n12" } ]""", "tokenSecret is required for GitHub")]
    [InlineData("""[ { "source": "ADO", "name": "n15", "tokenSecret": "t" } ]""", "needs \"org\", or \"orgs\" for several")]
    [InlineData("""[ { "source": "ADO", "name": "n16", "org": "a", "orgs": ["a"] }, { "source": "ADO", "name": "n17", "orgs": ["b", "a"] } ]""", "org \"a\" a second time")]
    [InlineData("""[ { "source": "ADO", "name": "n18", "org": "a" }, { "source": "ADO", "name": "n19", "org": "a" } ]""", "a second time")]
    public async Task ABadSourceIsRejectedAtLoadWithAMessageNamingIt(string json, string expected)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => LoadAsync(json));

        Assert.StartsWith("engine.json:", error.Message);
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task GitHubEntriesMayShareOneTokenSecret()
    {
        var config = await LoadAsync("""[ { "source": "GitHub", "name": "n22", "tokenSecret": "t" }, { "source": "GitHub", "name": "n23", "tokenSecret": "t", "orgs": ["acme"] } ]""");

        Assert.Equal(2, config.Current.Sources.Count);
    }

    [Fact]
    public async Task IncludeOwnReposIsForGitHubOnly()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => LoadAsync("""[ { "source": "ADO", "name": "n20", "org": "a", "includeOwnRepos": true } ]"""));
        Assert.Contains("applies to GitHub only", error.Message);

        var config = await LoadAsync("""[ { "source": "GitHub", "name": "n21", "tokenSecret": "t", "includeOwnRepos": true } ]""");
        Assert.True(config.Current.Sources.Single().IncludeOwnRepos);
    }

    [Fact]
    public async Task OrgsNamesExtraGitHubOrgsAndIsForGitHubOnly()
    {
        var config = await LoadAsync("""[ { "source": "GitHub", "name": "n22", "tokenSecret": "t", "orgs": ["preftech", "aptitude-marketplace"] } ]""");
        Assert.Equal(["preftech", "aptitude-marketplace"], config.Current.Sources.Single().Orgs);

    }

    [Fact]
    public async Task AnAdoEntryNamesOneOrgSeveralOrBoth()
    {
        var config = await LoadAsync("""
            [ { "source": "ADO", "name": "n23", "org": "vizientinc", "tokenSecret": "ado-pat", "orgs": ["RnD-Vizient", "vizientinc"] },
              { "source": "ADO", "name": "n24", "orgs": ["other"] } ]
            """);

        Assert.Equal(["vizientinc", "RnD-Vizient"], config.Current.Sources[0].NamedOrgs());
        Assert.Equal(["other"], config.Current.Sources[1].NamedOrgs());
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("../x")]
    [InlineData("name_with_underscore")]
    [InlineData("dotted.name")]
    [InlineData("-leading")]
    public async Task ANamedOrgThatIsNotAGitHubLoginIsRejected(string name)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => LoadAsync($$"""[ { "source": "GitHub", "name": "n25", "tokenSecret": "t", "orgs": ["{{name}}"] } ]"""));
        Assert.Contains("not a GitHub organization name", error.Message);
    }

    [Fact]
    public async Task EveryEntryHasANameAndTwoAdoEntriesCanEachHaveTheirOwnPat()
    {
        var config = await LoadAsync("""
            [ { "source": "ADO", "name": "VizientInc", "org": "vizientinc", "tokenSecret": "ado-pat" },
              { "source": "ADO", "name": "ClaroHealthCareADO", "org": "clarohealthcare", "tokenSecret": "claro-pat" },
              { "source": "GitHub", "name": "GitHubRepos", "tokenSecret": "github-token" } ]
            """);

        Assert.Equal(["VizientInc", "ClaroHealthCareADO", "GitHubRepos"], config.Current.Sources.Select(s => s.Name));
    }

    [Fact]
    public async Task AnEntryWithoutANameIsRejectedBecauseRequestJsonHasNoOtherWayToSayWhichEntryItMeans()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => LoadAsync("""[ { "source": "ADO", "org": "a", "tokenSecret": "t" } ]"""));

        Assert.Contains("sources[0].name is required", error.Message);
        Assert.Contains("request.json", error.Message);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("../x")]
    [InlineData("-leading")]
    public async Task ANameThatIsNotPlainIsRejected(string name)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => LoadAsync($$"""[ { "source": "ADO", "name": "{{name}}", "org": "a", "tokenSecret": "t" } ]"""));
        Assert.Contains("must be a plain name", error.Message);
    }

    [Fact]
    public async Task TwoEntriesCannotShareAName()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => LoadAsync("""
            [ { "source": "ADO", "name": "X", "org": "a" }, { "source": "ADO", "name": "x", "org": "b" } ]
            """));
        Assert.Contains("is used by another entry", error.Message);
    }

    [Fact]
    public async Task TwoGitHubTokensAreTwoSources()
    {
        var config = await LoadAsync("""[ { "source": "GitHub", "name": "n27", "tokenSecret": "a" }, { "source": "GitHub", "name": "n28", "tokenSecret": "b" } ]""");

        Assert.Equal(2, config.Current.Sources.Count);
    }
}
