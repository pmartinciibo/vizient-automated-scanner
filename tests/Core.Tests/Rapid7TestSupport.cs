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

/// <summary>One request the fake Rapid7 received.</summary>
public sealed record RecordedRequest(int Number, Uri Uri, string Endpoint, Dictionary<string, string> Query, Dictionary<string, string> Headers);

/// <summary>
/// A stand-in for the Rapid7 API. It serves the records it is given a page at a time in the API's data/metadata
/// shape, records every request, and lets a test replace any response, delay it, or make it fail.
/// </summary>
public sealed class FakeRapid7 : HttpMessageHandler
{
    private readonly Dictionary<string, List<string>> _records = new();
    private readonly object _gate = new();

    public List<RecordedRequest> Requests { get; } = new();

    /// <summary>Called for each request. Return a response to use instead of the real one, or null to carry on.</summary>
    public Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage?>>? Intercept { get; set; }

    /// <summary>When true the metadata carries a page token for the next page, as the real API does.</summary>
    public bool PageTokens { get; set; }

    public FakeRapid7 With(string endpoint, params string[] rawRecords)
    {
        _records[endpoint] = [.. rawRecords];
        return this;
    }

    public int Count(string endpoint) => Snapshot().Count(r => r.Endpoint == endpoint);

    public List<RecordedRequest> Snapshot()
    {
        lock (_gate)
        {
            return Requests.ToList();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""));
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));

        RecordedRequest recorded;
        lock (_gate)
        {
            recorded = new RecordedRequest(Requests.Count + 1, uri, uri.AbsolutePath.Split('/').Last(), query, headers);
            Requests.Add(recorded);
        }

        if (Intercept is not null && await Intercept(recorded, cancellationToken) is { } custom)
        {
            return custom;
        }

        var size = int.Parse(query["size"]);
        var index = query.TryGetValue("page-token", out var token) ? int.Parse(token[3..]) : int.Parse(query["index"]);
        var all = _records.GetValueOrDefault(recorded.Endpoint) ?? [];
        var page = all.Skip(index * size).Take(size).ToList();
        var totalPages = (int)Math.Ceiling(all.Count / (double)size);

        var metadata = PageTokens
            ? $"{{\"total_pages\":{totalPages},\"page_token\":\"tok{index + 1}\"}}"
            : $"{{\"total_pages\":{totalPages}}}";
        return Json(HttpStatusCode.OK, $"{{\"data\":[{string.Join(",", page)}],\"metadata\":{metadata}}}");
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

/// <summary>A run with a secrets file holding the Rapid7 token, ready to run the Rapid7 import against a <see cref="FakeRapid7"/>.</summary>
public sealed class Rapid7Run : IDisposable
{
    public const string Token = "r7-TOKEN-VALUE-5a1c9e77-0b2d-4c3f";
    public const string BaseUrl = "https://rapid7.test";

    private static int _counter;

    public Rapid7Run(
        Func<ToolSettings, ToolSettings>? settings = null,
        ScanMode mode = ScanMode.Normal,
        string? runId = null,
        string? root = null,
        bool writeSecret = true,
        DateOnly? tokenExpires = null)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
        OwnsRoot = root is null;
        Directory.CreateDirectory(Root);

        if (writeSecret)
        {
            var store = new SecretStore();
            store.Set("rapid7-token", Token, tokenExpires, DateTimeOffset.UtcNow);
            store.SaveAsync(Path.Combine(Root, "secrets.dat")).GetAwaiter().GetResult();
        }

        var rapid7 = new ToolSettings { BaseUrl = BaseUrl, TokenSecret = "rapid7-token" };
        Config = new EngineConfig(
            Path.Combine(Root, "engine.json"), Path.Combine(Root, "repos"), Path.Combine(Root, "runs"),
            Path.Combine(Root, "state"), Path.Combine(Root, "tmp"), Path.Combine(Root, "logs"),
            Settings: new EngineSettings { Tools = new() { ["Rapid7"] = (settings ?? (s => s))(rapid7) } });

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
    public string ResultsDir => Context.Paths.ResultsDir("Rapid7");
    public List<TimeSpan> Delays { get; } = new();

    /// <summary>The Rapid7 import wired to the fake API, with waits between retries recorded and skipped.</summary>
    public Rapid7Import Tool(FakeRapid7 api) => new(api, (wait, _) =>
    {
        lock (Delays)
        {
            Delays.Add(wait);
        }

        return Task.CompletedTask;
    });

    public ToolProgress Step => Context.Status.Snapshot().Steps.Single(s => s.Name == "Rapid7");

    /// <summary>The one file the import writes: the list of apps, each with its vulnerabilities.</summary>
    public string OutputPath => Path.Combine(ResultsDir, "rapid7.json");

    public JsonDocument ReadOutput() => JsonDocument.Parse(File.ReadAllText(OutputPath));

    /// <summary>The apps in the output, by id.</summary>
    public Dictionary<string, JsonElement> Apps(JsonDocument document) =>
        document.RootElement.GetProperty("apps").EnumerateArray()
            .ToDictionary(a => a.GetProperty("id").ValueKind == JsonValueKind.Null ? "(null)" : a.GetProperty("id").GetString()!);

    /// <summary>Folders the import set vulnerabilities aside in. None should be left once it has finished.</summary>
    public string[] SpoolFolders() =>
        Directory.Exists(Path.Combine(Root, "tmp")) ? Directory.GetDirectories(Path.Combine(Root, "tmp"), "rapid7-*") : [];

    public IEnumerable<string> ResultFiles() =>
        Directory.Exists(ResultsDir) ? Directory.GetFiles(ResultsDir).Select(Path.GetFileName)!.Order()! : [];

    public void Dispose()
    {
        if (OwnsRoot && Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

/// <summary>Builders for the records the fake serves.</summary>
public static class R7
{
    public static string App(string id, string name = "App") => $"{{\"id\":\"{id}\",\"name\":\"{name} {id}\",\"description\":\"\",\"links\":[]}}";

    public static string Scan(string id, string appId) => $"{{\"id\":\"{id}\",\"app\":{{\"id\":\"{appId}\"}},\"status\":\"COMPLETE\",\"scan_type\":\"REGULAR\"}}";

    public static string Report(string id, string appId) => $"{{\"id\":\"{id}\",\"name\":\"Report {id}\",\"app\":{{\"id\":\"{appId}\"}},\"status\":\"COMPLETE\"}}";

    public static string Vuln(string id, string appId) =>
        $"{{\"id\":\"{id}\",\"app\":{{\"id\":\"{appId}\"}},\"severity\":\"HIGH\",\"status\":\"UNREVIEWED\"," +
        "\"root_cause\":{\"url\":\"https://shop.example.test/search?q=caf\\u00e9&page=2+3\",\"method\":\"GET\"}}";

    public static string[] Vulns(string appId, int count, string prefix = "v") =>
        Enumerable.Range(1, count).Select(n => Vuln($"{prefix}{appId}-{n}", appId)).ToArray();
}
