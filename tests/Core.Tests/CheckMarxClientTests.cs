namespace Vizient.AutomatedScanner.Tests;

using System.Net;
using Vizient.AutomatedScanner.Core.CheckMarx;
using Vizient.AutomatedScanner.Core.Models;

public class CheckMarxClientTests
{
    private const string Refresh = FakeCheckMarx.RefreshToken;

    private sealed class Harness
    {
        public FakeCheckMarx Api { get; } = new();
        public List<TimeSpan> Delays { get; } = new();
        public List<string> Retries { get; } = new();
        public MovableClock Clock { get; } = new(DateTimeOffset.Parse("2026-10-14T03:00:00Z"));

        public CheckMarxClient Client(CheckMarxOptions? options = null, TimeSpan? timeout = null) =>
            new(new HttpClient(Api) { Timeout = Timeout.InfiniteTimeSpan }, options ?? Options(), Refresh, (wait, _) =>
            {
                Delays.Add(wait);
                return Task.CompletedTask;
            }, Clock)
            {
                RequestTimeout = timeout ?? TimeSpan.FromSeconds(90),
                OnRetry = Retries.Add
            };
    }

    private static CheckMarxOptions Options(ToolSettings? settings = null) =>
        CheckMarxOptions.From(settings ?? new ToolSettings { BaseUrl = "https://cx.test", Tenant = "acme" });

    private static async Task<List<string>> ReadAllAsync(CheckMarxClient client, string path, Dictionary<string, string>? query = null)
    {
        var ids = new List<string>();
        await foreach (var page in client.GetPagesAsync(path, query))
        {
            using (page)
            {
                ids.AddRange(page.Records.Select(r => r.GetProperty("id").GetString()!));
            }
        }

        return ids;
    }

    private static string[] Apps(int count) => Enumerable.Range(1, count).Select(n => Cx.App($"a{n}", $"App {n}")).ToArray();

    // ---- the settings ----

    [Fact]
    public void TheDefaultsAreThoseOfTheScript()
    {
        var options = CheckMarxOptions.From(new ToolSettings { Tenant = "acme" });

        Assert.Equal("https://us.ast.checkmarx.net", options.ApiUrl);
        Assert.Equal("https://us.iam.checkmarx.net/auth/realms/acme/protocol/openid-connect/token", options.TokenUrl);
        Assert.Equal("ast-app", options.ClientId);
        Assert.Equal((100, 0), (options.PageSize, options.MaxPages));
        Assert.Equal(["applications", "projects", "results"], options.Areas);
        Assert.Equal(("latest-per-project", "sast", "Completed", 0), (options.ResultsScope, options.ResultsEngine, options.ResultsScanStatus, options.ResultsMaxScans));
    }

    [Theory]
    [InlineData("https://cx.test/", "https://cx.test")]
    [InlineData("  https://cx.test  ", "https://cx.test")]
    [InlineData("https://cx.test/base///", "https://cx.test/base")]
    public void TheApiAddressHasItsTrailingSlashesDropped(string configured, string expected)
    {
        Assert.Equal(expected, CheckMarxOptions.From(new ToolSettings { BaseUrl = configured, Tenant = "t" }).ApiUrl);
    }

    [Fact]
    public void TheTokenAddressNeedsATenantUnlessItIsGivenWithoutOne()
    {
        var error = Assert.Throws<InvalidOperationException>(() => CheckMarxOptions.From(new ToolSettings()));
        Assert.Contains("tools.CheckMarx.tenant", error.Message);

        Assert.Throws<InvalidOperationException>(() => CheckMarxOptions.From(new ToolSettings { Tenant = "  " }));
        Assert.Throws<InvalidOperationException>(() =>
            CheckMarxOptions.From(new ToolSettings { TokenUrl = "https://iam.test/{tenant}/token" }));

        var fixedAddress = CheckMarxOptions.From(new ToolSettings { TokenUrl = "https://iam.test/fixed/token" });
        Assert.Equal("https://iam.test/fixed/token", fixedAddress.TokenUrl);
    }

    [Fact]
    public void ATokenAddressWithATenantPlaceholderIsFilledIn()
    {
        var options = CheckMarxOptions.From(new ToolSettings { Tenant = " acme ", TokenUrl = "https://iam.test/realms/{tenant}/token" });

        Assert.Equal("https://iam.test/realms/acme/token", options.TokenUrl);
    }

    [Theory]
    [InlineData(null, "sast")]
    [InlineData("", null)]
    [InlineData("sca", "sca")]
    public void TheEngineFilterDefaultsToSastAndAnEmptyValueTurnsItOff(string? configured, string? expected)
    {
        Assert.Equal(expected, CheckMarxOptions.From(new ToolSettings { Tenant = "t", ResultsEngine = configured }).ResultsEngine);
    }

    [Theory]
    [InlineData(null, "Completed")]
    [InlineData("", null)]
    [InlineData("Failed", "Failed")]
    public void TheScanStatusFilterDefaultsToCompletedAndAnEmptyValueTurnsItOff(string? configured, string? expected)
    {
        Assert.Equal(expected, CheckMarxOptions.From(new ToolSettings { Tenant = "t", ResultsScanStatus = configured }).ResultsScanStatus);
    }

    [Fact]
    public void AreasAreKeptInFetchOrderWhateverOrderTheyAreListedIn()
    {
        var options = CheckMarxOptions.From(new ToolSettings { Tenant = "t", Areas = ["results", "applications", "projects"] });

        Assert.Equal(["applications", "projects", "results"], options.Areas);
        Assert.True(options.Includes("projects"));
        Assert.False(CheckMarxOptions.From(new ToolSettings { Tenant = "t", Areas = ["applications"] }).Includes("projects"));
    }

    // ---- getting the access token ----

    [Fact]
    public async Task TheRefreshTokenIsSwappedForAnAccessTokenWithAFormPost()
    {
        var h = new Harness();
        h.Api.With("/api/applications", Apps(1));

        await ReadAllAsync(h.Client(), "/api/applications");

        var token = h.Api.Requests[0];
        Assert.Equal("POST", token.Method);
        Assert.Equal("https://us.iam.checkmarx.net/auth/realms/acme/protocol/openid-connect/token", token.Uri.ToString());
        Assert.Equal("grant_type=refresh_token&client_id=ast-app&refresh_token=" + Uri.EscapeDataString(Refresh), token.Body);
        Assert.Equal("Checkmarx-JSON-export/1.0", token.Headers["user-agent"]);
        Assert.Equal("application/json", token.Headers["accept"]);
        Assert.DoesNotContain("authorization", token.Headers.Keys);
    }

    [Fact]
    public async Task TheClientIdFromSettingsIsUsed()
    {
        var h = new Harness();
        h.Api.With("/api/applications", Apps(1));

        await ReadAllAsync(h.Client(Options(new ToolSettings { Tenant = "acme", ClientId = "other-app" })), "/api/applications");

        Assert.Contains("client_id=other-app", h.Api.Requests[0].Body);
    }

    [Fact]
    public async Task EveryApiRequestCarriesTheAccessTokenAsABearerToken()
    {
        var h = new Harness();
        h.Api.With("/api/applications", Apps(250));

        await ReadAllAsync(h.Client(), "/api/applications");

        var calls = h.Api.Requests.Where(r => r.Method == "GET").ToList();
        Assert.Equal(3, calls.Count);
        Assert.All(calls, r => Assert.Equal("Bearer access-token-1", r.Headers["authorization"]));
        Assert.Equal("Checkmarx-JSON-export/1.0", calls[0].Headers["user-agent"]);
        Assert.Equal(1, h.Api.TokensIssued);
    }

    [Fact]
    public async Task TheRefreshTokenGoesInThePostBodyAndNeverInAnAddress()
    {
        var h = new Harness();
        h.Api.With("/api/applications", Apps(1));

        await ReadAllAsync(h.Client(), "/api/applications");

        Assert.All(h.Api.Requests, r => Assert.DoesNotContain(Refresh, r.Uri.ToString()));
        Assert.DoesNotContain(Refresh, string.Join(" ", h.Api.Requests.Where(r => r.Method == "GET").Select(r => string.Join(" ", r.Headers.Values))));
    }

    [Fact]
    public async Task AnAccessTokenIsReplacedOnceItIsMoreThanFourMinutesOld()
    {
        var h = new Harness();
        h.Api.With("/api/applications", Apps(3));
        var client = h.Client(Options(new ToolSettings { Tenant = "acme", PageSize = 1 }));
        var tokensSeen = new List<string>();

        // Each page is a full page, so a fourth, empty one is asked for. The clock moves 100s after every page.
        await foreach (var page in client.GetPagesAsync("/api/applications"))
        {
            using (page)
            {
                tokensSeen.Add(h.Api.Requests.Last(r => r.Method == "GET").Headers["authorization"]);
                h.Clock.Advance(TimeSpan.FromSeconds(100));
            }
        }

        // Asked for at 0s, 100s and 200s the first token is good. At 300s it is too old, so a new one is fetched first.
        Assert.Equal(
            ["Bearer access-token-1", "Bearer access-token-1", "Bearer access-token-1", "Bearer access-token-2"],
            tokensSeen);
        Assert.Equal(2, h.Api.TokensIssued);
        Assert.Equal(2, client.TokensIssued);
    }

    [Fact]
    public async Task AnAccessTokenExactlyFourMinutesOldIsStillUsed()
    {
        var h = new Harness();
        h.Api.With("/api/applications", Apps(1));
        var client = h.Client();
        await ReadAllAsync(client, "/api/applications");

        h.Clock.Advance(TimeSpan.FromSeconds(240));
        await ReadAllAsync(client, "/api/applications");
        Assert.Equal(1, h.Api.TokensIssued);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await ReadAllAsync(client, "/api/applications");
        Assert.Equal(2, h.Api.TokensIssued);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task ARefusedRefreshTokenIsAnAuthenticationFailure(int status)
    {
        var h = new Harness();
        h.Api.Intercept = (r, _) => Task.FromResult(r.Method == "POST" ? FakeCheckMarx.Status(status, "{\"error\":\"denied\"}") : null);

        var error = await Assert.ThrowsAsync<CheckMarxApiException>(() => ReadAllAsync(h.Client(), "/api/applications"));

        Assert.True(error.IsAuthFailure);
        Assert.Single(h.Api.Requests);
    }

    [Fact]
    public async Task AnInvalidGrantFromTheTokenAddressIsAnAuthenticationFailureToo()
    {
        var h = new Harness();
        h.Api.Intercept = (r, _) => Task.FromResult(r.Method == "POST" ? FakeCheckMarx.Status(400, "{\"error\":\"invalid_grant\"}") : null);

        var error = await Assert.ThrowsAsync<CheckMarxApiException>(() => ReadAllAsync(h.Client(), "/api/applications"));

        Assert.True(error.IsAuthFailure);
        Assert.Contains("Authentication failed", error.Message);
        Assert.Contains("invalid_grant", error.Message);
        Assert.Single(h.Api.Requests);
    }

    [Fact]
    public async Task ATokenResponseWithNoAccessTokenIsAnAuthenticationFailure()
    {
        var h = new Harness();
        h.Api.Intercept = (r, _) => Task.FromResult(r.Method == "POST" ? FakeCheckMarx.Json(HttpStatusCode.OK, "{\"token_type\":\"Bearer\"}") : null);

        var error = await Assert.ThrowsAsync<CheckMarxApiException>(() => ReadAllAsync(h.Client(), "/api/applications"));

        Assert.True(error.IsAuthFailure);
        Assert.Contains("access_token", error.Message);
    }

    [Fact]
    public async Task ATokenAddressThatKeepsFailingToConnectIsAnAuthenticationFailureAfterTheRetries()
    {
        var h = new Harness();
        h.Api.Intercept = (r, _) => r.Method == "POST" ? throw new HttpRequestException("no route") : Task.FromResult<HttpResponseMessage?>(null);

        var error = await Assert.ThrowsAsync<CheckMarxApiException>(() => ReadAllAsync(h.Client(), "/api/applications"));

        Assert.True(error.IsAuthFailure);
        Assert.Contains("Authentication failed", error.Message);
        Assert.Equal(8, h.Api.Requests.Count);
    }

    [Fact]
    public async Task ABlipOnTheTokenAddressIsRetriedLikeAnyOtherCall()
    {
        var h = new Harness();
        h.Api.With("/api/applications", Apps(1));
        var failures = 2;
        h.Api.Intercept = (r, _) => r.Method == "POST" && failures-- > 0
            ? Task.FromResult<HttpResponseMessage?>(FakeCheckMarx.Status(503))
            : Task.FromResult<HttpResponseMessage?>(null);

        var ids = await ReadAllAsync(h.Client(), "/api/applications");

        Assert.Single(ids);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], h.Delays);
    }

    [Fact]
    public async Task BothTokensAreMaskedEvenWhenTheServerEchoesThemBack()
    {
        var h = new Harness();
        h.Api.Intercept = (r, _) => Task.FromResult(r.Method == "GET"
            ? FakeCheckMarx.Status(400, $"{{\"message\":\"bad: {Refresh} and {h.Api.LatestAccessToken}\"}}")
            : null);

        var error = await Assert.ThrowsAsync<CheckMarxApiException>(() => ReadAllAsync(h.Client(), "/api/applications"));

        Assert.DoesNotContain(Refresh, error.Message);
        Assert.DoesNotContain("access-token-1", error.Message);
        Assert.Contains("***", error.Message);
    }

    [Fact]
    public async Task TheRefreshTokenIsMaskedInATokenAddressError()
    {
        var h = new Harness();
        h.Api.Intercept = (r, _) => Task.FromResult(r.Method == "POST" ? FakeCheckMarx.Status(400, $"{{\"error\":\"bad {Refresh}\"}}") : null);

        var error = await Assert.ThrowsAsync<CheckMarxApiException>(() => ReadAllAsync(h.Client(), "/api/applications"));

        Assert.DoesNotContain(Refresh, error.Message);
    }

    // ---- what is asked for ----

    [Fact]
    public async Task TheFirstPageIsAskedForAtOffsetZeroWithTheConfiguredLimit()
    {
        var h = new Harness();
        h.Api.With("/api/applications", Apps(3));

        await ReadAllAsync(h.Client(Options(new ToolSettings { Tenant = "acme", BaseUrl = "https://cx.test", PageSize = 25 })), "/api/applications");

        var call = h.Api.Requests.Single(r => r.Method == "GET");
        Assert.Equal("https://cx.test/api/applications?offset=0&limit=25", call.Uri.ToString());
    }

    [Fact]
    public async Task ExtraQueryValuesGoAheadOfOffsetAndLimitAndAreEscaped()
    {
        var h = new Harness();
        h.Api.WithResults("scan one&two", Cx.Finding("f1"));

        await ReadAllAsync(h.Client(), "/api/results", new Dictionary<string, string> { ["scan-id"] = "scan one&two" });

        var call = h.Api.Requests.Single(r => r.Method == "GET");
        Assert.EndsWith("/api/results?scan-id=scan%20one%26two&offset=0&limit=100", call.Uri.AbsoluteUri);
        Assert.Equal("scan one&two", call.Query["scan-id"]);
    }

    // ---- paging ----

    [Fact]
    public async Task PagesAreReadByOffsetUntilOneComesBackShort()
    {
        var h = new Harness();
        h.Api.With("/api/applications", Apps(250));

        var ids = await ReadAllAsync(h.Client(), "/api/applications");

        Assert.Equal(250, ids.Count);
        Assert.Equal(["0", "100", "200"], h.Api.Requests.Where(r => r.Method == "GET").Select(r => r.Query["offset"]));
        Assert.Equal("a1", ids[0]);
        Assert.Equal("a250", ids[^1]);
    }

    [Fact]
    public async Task ACollectionThatEndsExactlyOnAPageBoundaryNeedsOneMoreEmptyPage()
    {
        var h = new Harness();
        h.Api.With("/api/applications", Apps(200));

        var ids = await ReadAllAsync(h.Client(), "/api/applications");

        Assert.Equal(200, ids.Count);
        Assert.Equal(3, h.Api.Count("/api/applications"));
    }

    [Fact]
    public async Task AnEmptyCollectionIsOneRequest()
    {
        var h = new Harness();
        h.Api.With("/api/applications");

        Assert.Empty(await ReadAllAsync(h.Client(), "/api/applications"));
        Assert.Equal(1, h.Api.Count("/api/applications"));
    }

    [Fact]
    public async Task TheMaxPagesCapStopsATrialRunEarly()
    {
        var h = new Harness();
        h.Api.With("/api/applications", Apps(500));

        var ids = await ReadAllAsync(
            h.Client(Options(new ToolSettings { Tenant = "acme", BaseUrl = "https://cx.test", MaxPages = 2 })), "/api/applications");

        Assert.Equal(200, ids.Count);
        Assert.Equal(2, h.Api.Count("/api/applications"));
    }

    [Theory]
    [InlineData("applications")]
    [InlineData("projects")]
    [InlineData("scans")]
    [InlineData("results")]
    [InlineData("items")]
    [InlineData("data")]
    public async Task TheListIsFoundUnderAnyOfTheUsualNames(string key)
    {
        var h = new Harness();
        h.Api.Intercept = (_, _) => Task.FromResult<HttpResponseMessage?>(null);
        h.Api.Intercept = (r, _) => Task.FromResult(r.Method == "GET"
            ? FakeCheckMarx.Json(HttpStatusCode.OK, $"{{\"{key}\":[{Cx.App("a1", "App")}],\"totalCount\":1}}")
            : null);

        var ids = await ReadAllAsync(h.Client(), "/api/applications");

        Assert.Equal(["a1"], ids);
    }

    [Fact]
    public async Task ABareListIsAcceptedToo()
    {
        var h = new Harness { Api = { BareLists = true } };
        h.Api.With("/api/applications", Apps(3));

        Assert.Equal(3, (await ReadAllAsync(h.Client(), "/api/applications")).Count);
    }

    [Theory]
    [InlineData("{\"unexpected\":true}")]
    [InlineData("{\"items\":\"not a list\"}")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    public async Task AResponseWithNoListHasNoRecords(string body)
    {
        var h = new Harness();
        h.Api.Intercept = (r, _) => Task.FromResult(r.Method == "GET" ? FakeCheckMarx.Json(HttpStatusCode.OK, body) : null);

        Assert.Empty(await ReadAllAsync(h.Client(), "/api/applications"));
    }

    [Fact]
    public async Task PagesReportWhereTheyAre()
    {
        var h = new Harness();
        h.Api.With("/api/applications", Apps(250));
        var seen = new List<(int Index, int Count)>();

        await foreach (var page in h.Client().GetPagesAsync("/api/applications"))
        {
            using (page)
            {
                seen.Add((page.PageIndex, page.Records.Count));
            }
        }

        Assert.Equal([(0, 100), (1, 100), (2, 50)], seen);
    }

    // ---- retries ----

    [Fact]
    public async Task AConnectionProblemIsRetriedWithBackoffUntilItWorks()
    {
        var h = new Harness();
        h.Api.With("/api/applications", Apps(2));
        var failures = 2;
        h.Api.Intercept = (r, _) => r.Method == "GET" && failures-- > 0
            ? throw new HttpRequestException("connection reset")
            : Task.FromResult<HttpResponseMessage?>(null);

        var ids = await ReadAllAsync(h.Client(), "/api/applications");

        Assert.Equal(2, ids.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], h.Delays);
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
        h.Api.With("/api/applications", Apps(1));
        var failures = 1;
        h.Api.Intercept = (r, _) => Task.FromResult(r.Method == "GET" && failures-- > 0 ? FakeCheckMarx.Status(status) : null);

        Assert.Single(await ReadAllAsync(h.Client(), "/api/applications"));
        Assert.Equal([TimeSpan.FromSeconds(1)], h.Delays);
    }

    [Theory]
    [InlineData("2", 2.0)]
    [InlineData("2.5", 2.5)]
    public async Task WhenTheServerSaysHowLongToWaitThatIsHowLongTheImportWaits(string retryAfter, double seconds)
    {
        var h = new Harness();
        h.Api.With("/api/applications", Apps(1));
        var failures = 1;
        h.Api.Intercept = (r, _) => Task.FromResult(r.Method == "GET" && failures-- > 0 ? FakeCheckMarx.Status(429, retryAfter: retryAfter) : null);

        await ReadAllAsync(h.Client(), "/api/applications");

        Assert.Equal([TimeSpan.FromSeconds(seconds)], h.Delays);
    }

    [Fact]
    public async Task ProblemsThatNeverClearAreGivenUpOnAfterEightAttempts()
    {
        var h = new Harness();
        h.Api.Intercept = (r, _) => r.Method == "GET" ? throw new HttpRequestException("no route to host") : Task.FromResult<HttpResponseMessage?>(null);

        var error = await Assert.ThrowsAsync<CheckMarxApiException>(() => ReadAllAsync(h.Client(), "/api/applications"));

        Assert.False(error.IsAuthFailure);
        Assert.Contains("Giving up", error.Message);
        Assert.Equal(8, h.Api.Count("/api/applications"));
    }

    [Fact]
    public async Task AStatusThatRetryingWillNotFixIsReportedAtOnceWithTheAddressAndAnExcerpt()
    {
        var h = new Harness();
        h.Api.Intercept = (r, _) => Task.FromResult(r.Method == "GET" ? FakeCheckMarx.Status(404, "{\"code\":404,\"message\":\"scan-id not found\"}") : null);

        var error = await Assert.ThrowsAsync<CheckMarxApiException>(() => ReadAllAsync(h.Client(), "/api/applications"));

        Assert.Equal(404, error.StatusCode);
        Assert.False(error.IsAuthFailure);
        Assert.Contains("HTTP 404 from https://cx.test/api/applications", error.Message);
        Assert.Contains("scan-id not found", error.Message);
        Assert.Equal(1, h.Api.Count("/api/applications"));
        Assert.Empty(h.Delays);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task ARefusedApiCallIsAnAuthenticationFailureAtOnce(int status)
    {
        var h = new Harness();
        h.Api.Intercept = (r, _) => Task.FromResult(r.Method == "GET" ? FakeCheckMarx.Status(status) : null);

        var error = await Assert.ThrowsAsync<CheckMarxApiException>(() => ReadAllAsync(h.Client(), "/api/applications"));

        Assert.True(error.IsAuthFailure);
        Assert.Equal(1, h.Api.Count("/api/applications"));
    }

    [Fact]
    public async Task ARequestThatTakesTooLongIsRetried()
    {
        var h = new Harness();
        h.Api.With("/api/applications", Apps(1));
        var slow = 1;
        h.Api.Intercept = async (r, ct) =>
        {
            if (r.Method == "GET" && slow-- > 0)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return null;
        };

        var ids = await ReadAllAsync(h.Client(timeout: TimeSpan.FromMilliseconds(60)), "/api/applications");

        Assert.Single(ids);
        Assert.Contains("timed out", h.Retries.Single());
    }

    [Fact]
    public async Task CancellingStopsTheImportInsteadOfRetrying()
    {
        var h = new Harness();
        using var cancel = new CancellationTokenSource();
        h.Api.Intercept = async (r, ct) =>
        {
            if (r.Method == "GET")
            {
                cancel.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
            }

            return null;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var page in h.Client().GetPagesAsync("/api/applications", null, cancel.Token))
            {
                page.Dispose();
            }
        });

        Assert.Equal(1, h.Api.Count("/api/applications"));
        Assert.Empty(h.Delays);
    }
}
