namespace Vizient.AutomatedScanner.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Secrets;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.Tools;

/// <summary>One request the fake Checkmarx received.</summary>
public sealed record CxRequest(
    int Number, string Method, Uri Uri, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers, string Body);

/// <summary>
/// A stand-in for Checkmarx One. The token address swaps a refresh token for numbered access tokens
/// (access-token-1, access-token-2 ...). The API serves the collections it is given by offset and limit, and findings
/// per scan. Every request is recorded, and a test can replace, delay or fail any response.
/// </summary>
public sealed class FakeCheckMarx : HttpMessageHandler
{
    public const string RefreshToken = "cx-REFRESH-TOKEN-VALUE-77c2e1a4-9b30";

    private readonly Dictionary<string, List<string>> _collections = new();
    private readonly Dictionary<string, List<string>> _resultsByScan = new();
    private readonly object _gate = new();
    private int _tokens;

    public List<CxRequest> Requests { get; } = new();

    /// <summary>Called for each request. Return a response to use instead of the real one, or null to carry on.</summary>
    public Func<CxRequest, CancellationToken, Task<HttpResponseMessage?>>? Intercept { get; set; }

    /// <summary>When true, lists come back bare instead of inside an object.</summary>
    public bool BareLists { get; set; }

    /// <summary>The newest access token issued.</summary>
    public string? LatestAccessToken { get; private set; }

    public int TokensIssued => _tokens;

    public FakeCheckMarx With(string path, params string[] rawRecords)
    {
        _collections[path] = [.. rawRecords];
        return this;
    }

    public FakeCheckMarx WithResults(string scanId, params string[] rawRecords)
    {
        _resultsByScan[scanId] = [.. rawRecords];
        return this;
    }

    public List<CxRequest> Snapshot()
    {
        lock (_gate)
        {
            return Requests.ToList();
        }
    }

    public int Count(string path) => Snapshot().Count(r => r.Path == path && r.Method == "GET");

    public List<string> ScanIdsQueried() =>
        Snapshot().Where(r => r.Path == "/api/results" && r.Query["offset"] == "0").Select(r => r.Query["scan-id"]).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p.Length > 1 ? p[1].Replace('+', ' ') : ""));
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

        CxRequest recorded;
        lock (_gate)
        {
            recorded = new CxRequest(Requests.Count + 1, request.Method.Method, uri, uri.AbsolutePath, query, headers, body);
            Requests.Add(recorded);
        }

        if (Intercept is not null && await Intercept(recorded, cancellationToken) is { } custom)
        {
            return custom;
        }

        if (request.Method == HttpMethod.Post && uri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
        {
            var token = $"access-token-{Interlocked.Increment(ref _tokens)}";
            LatestAccessToken = token;
            return Json(HttpStatusCode.OK, $"{{\"access_token\":\"{token}\",\"expires_in\":300,\"token_type\":\"Bearer\"}}");
        }

        var limit = int.Parse(query["limit"]);
        var offset = int.Parse(query["offset"]);
        var all = recorded.Path == "/api/results"
            ? _resultsByScan.GetValueOrDefault(query["scan-id"]) ?? []
            : _collections.GetValueOrDefault(recorded.Path) ?? [];
        var page = $"[{string.Join(",", all.Skip(offset).Take(limit))}]";

        var key = recorded.Path[(recorded.Path.LastIndexOf('/') + 1)..]; // applications, projects, scans, results
        return Json(HttpStatusCode.OK, BareLists ? page : $"{{\"{key}\":{page},\"totalCount\":{all.Count}}}");
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(int status, string body = "{}", string? retryAfter = null)
    {
        var response = Json((HttpStatusCode)status, body);
        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        return response;
    }
}

/// <summary>A clock a test can move.</summary>
public sealed class MovableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>A run with a secrets file holding the Checkmarx refresh token, ready to run the CheckMarx import against a <see cref="FakeCheckMarx"/>.</summary>
public sealed class CheckMarxRun : IDisposable
{
    private static int _counter;

    public CheckMarxRun(
        Func<ToolSettings, ToolSettings>? settings = null,
        ScanMode mode = ScanMode.Normal,
        string? runId = null,
        string? root = null,
        bool writeSecret = true)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
        OwnsRoot = root is null;
        Directory.CreateDirectory(Root);

        if (writeSecret)
        {
            var store = new SecretStore();
            store.Set("checkmarx-token", FakeCheckMarx.RefreshToken, null, DateTimeOffset.UtcNow);
            store.SaveAsync(Path.Combine(Root, "secrets.dat")).GetAwaiter().GetResult();
        }

        var checkMarx = new ToolSettings
        {
            BaseUrl = "https://cx.test",
            TokenSecret = "checkmarx-token",
            Tenant = "acme"
        };
        Config = new EngineConfig(
            Path.Combine(Root, "engine.json"), Path.Combine(Root, "repos"), Path.Combine(Root, "runs"),
            Path.Combine(Root, "state"), Path.Combine(Root, "tmp"), Path.Combine(Root, "logs"),
            Settings: new EngineSettings { Tools = new() { ["CheckMarx"] = (settings ?? (s => s))(checkMarx) } });

        var requestFile = Path.Combine(Root, "requests", "request.json");
        AtomicJsonFile.WriteAsync(requestFile, new RunRequestDocument
        {
            RunId = runId ?? $"run-{Interlocked.Increment(ref _counter)}",
            Mode = mode,
            Revision = mode is ScanMode.Resume or ScanMode.RetryFailed ? 2 : 1
        }).GetAwaiter().GetResult();
        Context = RunContext.StartAsync(Config, requestFile).GetAwaiter().GetResult();
    }

    public string Root { get; }
    private bool OwnsRoot { get; }
    public EngineConfig Config { get; }
    public RunContext Context { get; }
    public string ResultsDir => Context.Paths.ResultsDir("CheckMarx");
    public List<TimeSpan> Delays { get; } = new();

    /// <summary>The CheckMarx import wired to the fake API, with waits between retries recorded and skipped.</summary>
    public CheckMarxImport Tool(FakeCheckMarx api, TimeProvider? time = null) => new(api, (wait, _) =>
    {
        lock (Delays)
        {
            Delays.Add(wait);
        }

        return Task.CompletedTask;
    }, time);

    public ToolProgress Step => Context.Status.Snapshot().Steps.Single(s => s.Name == "CheckMarx");

    public string OutputPath => Path.Combine(ResultsDir, "checkmarx.json");

    public JsonDocument ReadOutput() => JsonDocument.Parse(File.ReadAllText(OutputPath));

    public IEnumerable<string> ResultFiles() =>
        Directory.Exists(ResultsDir) ? Directory.GetFiles(ResultsDir).Select(Path.GetFileName)!.Order()! : [];

    /// <summary>The apps in the output, by id (the unassigned entry is under "(null)").</summary>
    public Dictionary<string, JsonElement> Apps(JsonDocument document) =>
        document.RootElement.GetProperty("apps").EnumerateArray()
            .ToDictionary(a => a.GetProperty("id").ValueKind == JsonValueKind.Null ? "(null)" : a.GetProperty("id").GetString()!);

    public string[] SpoolFolders() =>
        Directory.Exists(Path.Combine(Root, "tmp")) ? Directory.GetDirectories(Path.Combine(Root, "tmp"), "checkmarx-*") : [];

    public void Dispose()
    {
        if (OwnsRoot && Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

/// <summary>Builders for the records the fake serves.</summary>
public static class Cx
{
    public static string App(string id, string name, params string[] projectIds) =>
        $"{{\"id\":\"{id}\",\"name\":\"{name}\",\"description\":\"\",\"type\":\"\",\"criticality\":3," +
        $"\"projectIds\":[{string.Join(",", projectIds.Select(p => $"\"{p}\""))}],\"tags\":{{}}}}";

    public static string Project(string id, string name, params string[] applicationIds) =>
        $"{{\"id\":\"{id}\",\"name\":\"{name}\",\"repoUrl\":\"https://git.example.test/{name}?a=1&b=2\",\"mainBranch\":\"main\"," +
        $"\"applicationIds\":[{string.Join(",", applicationIds.Select(a => $"\"{a}\""))}]}}";

    public static string Scan(string id, string projectId, string createdAt, string status = "Completed", string[]? engines = null, string? projectName = null) =>
        $"{{\"id\":\"{id}\",\"status\":\"{status}\",\"projectId\":\"{projectId}\",\"projectName\":\"{projectName ?? $"name-of-{projectId}"}\"," +
        $"\"createdAt\":\"{createdAt}\",\"engines\":[{string.Join(",", (engines ?? ["sast"]).Select(e => $"\"{e}\""))}]}}";

    public static string Finding(string id, string severity = "HIGH") =>
        $"{{\"type\":\"sast\",\"id\":\"{id}\",\"severity\":\"{severity}\",\"state\":\"TO_VERIFY\"," +
        "\"data\":{\"queryName\":\"SQL_Injection\",\"fileName\":\"/src/a&b.cs\",\"line\":12}}";

    public static string[] Findings(string prefix, int count) =>
        Enumerable.Range(1, count).Select(n => Finding($"{prefix}-{n}")).ToArray();
}
