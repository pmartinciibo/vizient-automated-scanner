namespace Vizient.AutomatedScanner.Tests;

using System.Net;
using System.Text;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Rapid7;

public class Rapid7ClientTests
{
    private const string Token = Rapid7Run.Token;

    private sealed class Harness
    {
        public FakeRapid7 Api { get; } = new();
        public List<TimeSpan> Delays { get; } = new();
        public List<string> Retries { get; } = new();

        public Rapid7Client Client(
            Rapid7Options? options = null,
            TimeSpan? timeout = null) =>
            new(new HttpClient(Api) { Timeout = Timeout.InfiniteTimeSpan }, options ?? Options(), Token, (wait, _) =>
            {
                Delays.Add(wait);
                return Task.CompletedTask;
            })
            {
                RequestTimeout = timeout ?? TimeSpan.FromSeconds(90),
                OnRetry = Retries.Add
            };
    }

    private static Rapid7Options Options(string baseUrl = "https://rapid7.test", int pageSize = 100, int maxPages = 0, string authMode = "api_key") =>
        Rapid7Options.From(new ToolSettings { BaseUrl = baseUrl, PageSize = pageSize, MaxPages = maxPages, AuthMode = authMode });

    private static async Task<List<string>> ReadAllAsync(Rapid7Client client, string area, CancellationToken ct = default)
    {
        var ids = new List<string>();
        await foreach (var page in client.GetPagesAsync(area, ct))
        {
            using (page)
            {
                ids.AddRange(page.Records.Select(r => r.GetProperty("id").GetString()!));
            }
        }

        return ids;
    }

    private static string[] Apps(int count) => Enumerable.Range(1, count).Select(n => R7.App($"app-{n}")).ToArray();

    // ---- addresses ----

    [Theory]
    [InlineData("https://us.api.insight.rapid7.com", "https://us.api.insight.rapid7.com/ias/v1")]
    [InlineData("https://us.api.insight.rapid7.com/", "https://us.api.insight.rapid7.com/ias/v1")]
    [InlineData("https://host/api", "https://host/api/ias/v1")]
    [InlineData("https://host/ias/v1", "https://host/ias/v1")]
    [InlineData("https://host/ias/v1/", "https://host/ias/v1")]
    [InlineData("https://host/IAS/V1", "https://host/IAS/V1")]
    [InlineData("  https://host  ", "https://host/ias/v1")]
    public void TheApiPathIsAddedOnceAndTrailingSlashesAreDropped(string configured, string expected)
    {
        Assert.Equal(expected, Options(configured).BaseUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ThereIsNoImportWithoutAnAddress(string? baseUrl)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Rapid7Options.From(new ToolSettings { BaseUrl = baseUrl }));

        Assert.Contains("tools.Rapid7.baseUrl", error.Message);
    }

    [Fact]
    public void TheDefaultsAreThoseOfTheScriptExceptForTheAuthModeAndTheAreas()
    {
        var options = Rapid7Options.From(new ToolSettings { BaseUrl = "https://host" });

        Assert.Equal((100, 0, "api_key"), (options.PageSize, options.MaxPages, options.AuthMode));
        // Applications with their vulnerabilities. Scans and reports have to be asked for.
        Assert.Equal(["applications", "vulnerabilities"], options.Areas);
        Assert.False(options.Includes("scans"));
        Assert.False(options.Includes("reports"));
    }

    [Fact]
    public void AreasAreKeptInFetchOrderWhateverOrderTheyAreListedIn()
    {
        var options = Rapid7Options.From(new ToolSettings { BaseUrl = "https://host", Areas = ["vulnerabilities", "applications"] });

        Assert.Equal(["applications", "vulnerabilities"], options.Areas);
        Assert.True(options.Includes("applications"));
        Assert.False(options.Includes("scans"));
    }

    // ---- what is sent ----

    [Fact]
    public async Task TheFirstRequestAsksForPageZeroAtTheConfiguredSize()
    {
        var h = new Harness();
        h.Api.With("apps", Apps(3));

        await ReadAllAsync(h.Client(Options(pageSize: 25)), "applications");

        var request = Assert.Single(h.Api.Requests);
        Assert.Equal("https://rapid7.test/ias/v1/apps?index=0&size=25", request.Uri.ToString());
    }

    [Fact]
    public async Task EachAreaHasItsOwnEndpoint()
    {
        var h = new Harness();
        h.Api.With("apps", Apps(1)).With("scans", R7.Scan("s1", "app-1")).With("reports", R7.Report("r1", "app-1")).With("vulnerabilities", R7.Vuln("v1", "app-1"));

        foreach (var area in new[] { "applications", "scans", "reports", "vulnerabilities" })
        {
            await ReadAllAsync(h.Client(), area);
        }

        Assert.Equal(["apps", "scans", "reports", "vulnerabilities"], h.Api.Requests.Select(r => r.Endpoint));
    }

    [Fact]
    public async Task ApiKeyModeSendsTheTokenInAnXApiKeyHeader()
    {
        var h = new Harness();
        h.Api.With("apps", Apps(1));

        await ReadAllAsync(h.Client(Options(authMode: "api_key")), "applications");

        var headers = h.Api.Requests.Single().Headers;
        Assert.Equal(Token, headers["x-api-key"]);
        Assert.DoesNotContain("authorization", headers.Keys);
        Assert.Equal("application/json", headers["accept"]);
        Assert.Equal("Rapid7-JSON-export/1.0", headers["user-agent"]);
    }

    [Fact]
    public async Task ConsoleTokenModeSendsTheTokenAsTheUserNameOfBasicAuth()
    {
        var h = new Harness();
        h.Api.With("apps", Apps(1));

        await ReadAllAsync(h.Client(Options(authMode: "console_token")), "applications");

        var headers = h.Api.Requests.Single().Headers;
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Token}:"));
        Assert.Equal(expected, headers["authorization"]);
        Assert.DoesNotContain("x-api-key", headers.Keys);
    }

    // ---- paging ----

    [Fact]
    public async Task EveryPageIsReadUntilTheLastOneTheServerAnnounces()
    {
        var h = new Harness();
        h.Api.With("apps", Apps(250));

        var ids = await ReadAllAsync(h.Client(Options(pageSize: 100)), "applications");

        Assert.Equal(250, ids.Count);
        Assert.Equal("app-1", ids[0]);
        Assert.Equal("app-250", ids[^1]);
        Assert.Equal(["0", "1", "2"], h.Api.Requests.Select(r => r.Query["index"]));
    }

    [Fact]
    public async Task APageTokenFromTheServerIsFollowedInsteadOfTheIndex()
    {
        var h = new Harness { Api = { PageTokens = true } };
        h.Api.With("apps", Apps(250));

        var ids = await ReadAllAsync(h.Client(Options(pageSize: 100)), "applications");

        Assert.Equal(250, ids.Count);
        Assert.Equal("0", h.Api.Requests[0].Query["index"]);
        Assert.Equal("tok1", h.Api.Requests[1].Query["page-token"]);
        Assert.Equal("tok2", h.Api.Requests[2].Query["page-token"]);
        Assert.All(h.Api.Requests.Skip(1), r => Assert.DoesNotContain("index", r.Query.Keys));
        Assert.All(h.Api.Requests, r => Assert.Equal("100", r.Query["size"]));
    }

    [Fact]
    public async Task AnEmptyPageEndsTheArea()
    {
        var h = new Harness();
        h.Api.Intercept = (request, _) => Task.FromResult<HttpResponseMessage?>(request.Query["index"] == "0"
            ? FakeRapid7.Json(HttpStatusCode.OK, "{\"data\":[" + R7.App("a1") + "],\"metadata\":{}}")
            : FakeRapid7.Json(HttpStatusCode.OK, "{\"data\":[],\"metadata\":{}}"));

        var ids = await ReadAllAsync(h.Client(), "applications");

        Assert.Equal(["a1"], ids);
        Assert.Equal(2, h.Api.Requests.Count); // no total_pages to say it was the last, so it reads until one is empty
    }

    [Fact]
    public async Task AreaWithNothingInItIsOneRequest()
    {
        var h = new Harness();
        h.Api.With("apps");

        Assert.Empty(await ReadAllAsync(h.Client(), "applications"));
        Assert.Single(h.Api.Requests);
    }

    [Fact]
    public async Task TheMaxPagesCapStopsATrialRunEarly()
    {
        var h = new Harness();
        h.Api.With("apps", Apps(500));

        var ids = await ReadAllAsync(h.Client(Options(pageSize: 100, maxPages: 2)), "applications");

        Assert.Equal(200, ids.Count);
        Assert.Equal(2, h.Api.Requests.Count);
    }

    [Fact]
    public async Task APageTokenThatDoesNotAdvanceIsAnErrorNotAnEndlessLoop()
    {
        var h = new Harness();
        h.Api.Intercept = (_, _) => Task.FromResult<HttpResponseMessage?>(FakeRapid7.Json(HttpStatusCode.OK,
            "{\"data\":[" + R7.App("a1") + "],\"metadata\":{\"total_pages\":9,\"page_token\":\"same\"}}"));

        var error = await Assert.ThrowsAsync<Rapid7ApiException>(() => ReadAllAsync(h.Client(), "applications"));

        Assert.Contains("did not advance", error.Message);
        // The token first appears after page 0; asking with it returns the same token again, which is caught there.
        Assert.Equal(2, h.Api.Requests.Count);
    }

    [Fact]
    public async Task AResponseWithNoDataListHasNoRecords()
    {
        var h = new Harness();
        h.Api.Intercept = (_, _) => Task.FromResult<HttpResponseMessage?>(FakeRapid7.Json(HttpStatusCode.OK, "{\"data\":{\"unexpected\":true}}"));

        Assert.Empty(await ReadAllAsync(h.Client(), "applications"));
    }

    [Fact]
    public async Task AResponseThatIsNotAJsonObjectIsAnError()
    {
        var h = new Harness();
        h.Api.Intercept = (_, _) => Task.FromResult<HttpResponseMessage?>(FakeRapid7.Json(HttpStatusCode.OK, "[1,2,3]"));

        var error = await Assert.ThrowsAsync<Rapid7ApiException>(() => ReadAllAsync(h.Client(), "applications"));

        Assert.Contains("expected a JSON object", error.Message);
    }

    [Fact]
    public async Task PagesReportWhereTheyAre()
    {
        var h = new Harness();
        h.Api.With("apps", Apps(250));
        var seen = new List<(int Index, int? Total, int Count)>();

        await foreach (var page in h.Client(Options(pageSize: 100)).GetPagesAsync("applications"))
        {
            using (page)
            {
                seen.Add((page.PageIndex, page.TotalPages, page.Records.Count));
            }
        }

        Assert.Equal([(0, 3, 100), (1, 3, 100), (2, 3, 50)], seen);
    }

    // ---- retries ----

    [Fact]
    public async Task AConnectionProblemIsRetriedWithBackoffUntilItWorks()
    {
        var h = new Harness();
        h.Api.With("apps", Apps(2));
        var failures = 2;
        h.Api.Intercept = (_, _) => failures-- > 0
            ? throw new HttpRequestException("connection reset")
            : Task.FromResult<HttpResponseMessage?>(null);

        var ids = await ReadAllAsync(h.Client(), "applications");

        Assert.Equal(2, ids.Count);
        Assert.Equal(3, h.Api.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], h.Delays);
        Assert.Equal(2, h.Retries.Count);
        Assert.Contains("connection reset", h.Retries[0]);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task ARetryableStatusIsRetried(int status)
    {
        var h = new Harness();
        h.Api.With("apps", Apps(1));
        var failures = 1;
        h.Api.Intercept = (_, _) => Task.FromResult(failures-- > 0 ? FakeRapid7.Status(status) : null);

        var ids = await ReadAllAsync(h.Client(), "applications");

        Assert.Single(ids);
        Assert.Equal(2, h.Api.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1)], h.Delays);
    }

    [Theory]
    [InlineData("2", 2.0)]
    [InlineData("2.5", 2.5)]
    [InlineData("0", 0.0)]
    public async Task WhenTheServerSaysHowLongToWaitThatIsHowLongTheImportWaits(string retryAfter, double seconds)
    {
        var h = new Harness();
        h.Api.With("apps", Apps(1));
        var failures = 1;
        h.Api.Intercept = (_, _) => Task.FromResult(failures-- > 0 ? FakeRapid7.Status(429, retryAfter: retryAfter) : null);

        await ReadAllAsync(h.Client(), "applications");

        Assert.Equal([TimeSpan.FromSeconds(seconds)], h.Delays);
    }

    [Fact]
    public async Task ABackoffNeverExceedsAMinute()
    {
        var h = new Harness();
        h.Api.With("apps", Apps(1));
        var failures = 7;
        h.Api.Intercept = (_, _) => Task.FromResult(failures-- > 0 ? FakeRapid7.Status(503) : null);

        await ReadAllAsync(h.Client(), "applications");

        Assert.Equal([1, 2, 4, 8, 16, 32, 60], h.Delays.Select(d => (int)d.TotalSeconds));
    }

    [Fact]
    public async Task ConnectionProblemsThatNeverClearAreGivenUpOnAfterEightAttempts()
    {
        var h = new Harness();
        h.Api.Intercept = (_, _) => throw new HttpRequestException("no route to host");

        var error = await Assert.ThrowsAsync<Rapid7ApiException>(() => ReadAllAsync(h.Client(), "applications"));

        Assert.Contains("Giving up", error.Message);
        Assert.Contains("8 attempts", error.Message);
        Assert.Contains("no route to host", error.Message);
        Assert.Equal(8, h.Api.Requests.Count);
        Assert.Equal(7, h.Delays.Count);
    }

    [Fact]
    public async Task ARetryableStatusOnTheLastAttemptIsReportedAsThatStatus()
    {
        var h = new Harness();
        h.Api.Intercept = (_, _) => Task.FromResult<HttpResponseMessage?>(FakeRapid7.Status(503, "{\"message\":\"overloaded\"}"));

        var error = await Assert.ThrowsAsync<Rapid7ApiException>(() => ReadAllAsync(h.Client(), "applications"));

        Assert.Equal(503, error.StatusCode);
        Assert.Contains("HTTP 503", error.Message);
        Assert.Contains("overloaded", error.Message);
        Assert.Equal(8, h.Api.Requests.Count);
    }

    [Fact]
    public async Task AStatusThatRetryingWillNotFixIsReportedAtOnceWithAnExcerptOfTheBody()
    {
        var h = new Harness();
        h.Api.Intercept = (_, _) => Task.FromResult<HttpResponseMessage?>(FakeRapid7.Status(404, "{\"message\":\"no such\nthing\"}"));

        var error = await Assert.ThrowsAsync<Rapid7ApiException>(() => ReadAllAsync(h.Client(), "applications"));

        Assert.Equal(404, error.StatusCode);
        Assert.False(error.IsAuthFailure);
        Assert.Contains("HTTP 404 from https://rapid7.test/ias/v1/apps", error.Message);
        Assert.Contains("no such thing", error.Message); // the line break is flattened, as in the script
        Assert.Single(h.Api.Requests);
        Assert.Empty(h.Delays);
    }

    [Fact]
    public async Task TheBodyExcerptIsAtMost400Characters()
    {
        var h = new Harness();
        h.Api.Intercept = (_, _) => Task.FromResult<HttpResponseMessage?>(FakeRapid7.Status(400, new string('x', 5000)));

        var error = await Assert.ThrowsAsync<Rapid7ApiException>(() => ReadAllAsync(h.Client(), "applications"));

        Assert.True(error.Message.Length < 600);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task ARefusedTokenFailsAtOnceAndIsFlaggedAsAnAuthenticationFailure(int status)
    {
        var h = new Harness();
        h.Api.Intercept = (_, _) => Task.FromResult<HttpResponseMessage?>(FakeRapid7.Status(status, "{\"message\":\"denied\"}"));

        var error = await Assert.ThrowsAsync<Rapid7ApiException>(() => ReadAllAsync(h.Client(), "applications"));

        Assert.True(error.IsAuthFailure);
        Assert.Equal(status, error.StatusCode);
        Assert.Single(h.Api.Requests);
        Assert.Empty(h.Delays);
    }

    [Fact]
    public async Task ASuccessfulResponseThatIsNotJsonIsAnError()
    {
        var h = new Harness();
        h.Api.Intercept = (_, _) => Task.FromResult<HttpResponseMessage?>(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login page</html>") });

        var error = await Assert.ThrowsAsync<Rapid7ApiException>(() => ReadAllAsync(h.Client(), "applications"));

        Assert.Contains("Non-JSON response", error.Message);
        Assert.Contains("login page", error.Message);
        Assert.Single(h.Api.Requests);
    }

    [Fact]
    public async Task ARequestThatTakesTooLongCountsAsAConnectionProblemAndIsRetried()
    {
        var h = new Harness();
        h.Api.With("apps", Apps(1));
        var slow = 1;
        h.Api.Intercept = async (_, ct) =>
        {
            if (slow-- > 0)
            {
                await Task.Delay(Timeout.Infinite, ct); // never answers
            }

            return null;
        };

        var ids = await ReadAllAsync(h.Client(timeout: TimeSpan.FromMilliseconds(60)), "applications");

        Assert.Single(ids);
        Assert.Equal(2, h.Api.Requests.Count);
        Assert.Contains("timed out", h.Retries.Single());
    }

    [Fact]
    public async Task CancellingStopsTheImportInsteadOfRetrying()
    {
        var h = new Harness();
        using var cancel = new CancellationTokenSource();
        h.Api.Intercept = async (_, ct) =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAllAsync(h.Client(), "applications", cancel.Token));

        Assert.Single(h.Api.Requests);
        Assert.Empty(h.Delays);
    }

    // ---- the token ----

    [Fact]
    public async Task TheTokenIsMaskedEvenWhenTheServerEchoesItBack()
    {
        var h = new Harness();
        h.Api.Intercept = (_, _) => Task.FromResult<HttpResponseMessage?>(FakeRapid7.Status(400, $"{{\"message\":\"bad key {Token}\"}}"));

        var error = await Assert.ThrowsAsync<Rapid7ApiException>(() => ReadAllAsync(h.Client(), "applications"));

        Assert.DoesNotContain(Token, error.Message);
        Assert.Contains("***", error.Message);
    }

    [Fact]
    public async Task TheTokenIsMaskedInANonJsonResponseToo()
    {
        var h = new Harness();
        h.Api.Intercept = (_, _) => Task.FromResult<HttpResponseMessage?>(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"your key was {Token}") });

        var error = await Assert.ThrowsAsync<Rapid7ApiException>(() => ReadAllAsync(h.Client(), "applications"));

        Assert.DoesNotContain(Token, error.Message);
    }

    [Fact]
    public async Task TheTokenIsNotInTheRequestAddress()
    {
        var h = new Harness();
        h.Api.With("apps", Apps(1));

        await ReadAllAsync(h.Client(), "applications");

        Assert.DoesNotContain(Token, h.Api.Requests.Single().Uri.ToString());
    }

    [Fact]
    public void AnUnknownAreaIsRefused()
    {
        Assert.Throws<ArgumentException>(() => Rapid7Options.Endpoint("everything"));
    }
}
