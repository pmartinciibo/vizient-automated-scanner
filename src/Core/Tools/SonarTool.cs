using System.Net.Http.Headers;
using System.Text.Json;
using Vizient.AutomatedScanner.Core.Imports;
using Vizient.AutomatedScanner.Core.Processes;
using Vizient.AutomatedScanner.Core.Work;

namespace Vizient.AutomatedScanner.Core.Tools;

/// <summary>A SonarQube call failed or its answer cannot be trusted. The message never holds the token.</summary>
public sealed class SonarException(string message, string category, int? status = null) : Exception(message)
{
    public string Category { get; } = category;
    public int? Status { get; } = status;
}

/// <summary>
/// What SonarStandard and SonarDotNet have in common once a scanner has sent its analysis to the server: wait for the
/// server to finish it (the scanner only queues it), then fetch the issues through the web API and write them to the
/// repo's result file. Needs <c>serverUrl</c> and an <c>env</c> entry for SONAR_TOKEN in engine.json.
/// </summary>
public abstract class SonarTool(
    HttpMessageHandler? handler,
    Func<TimeSpan, CancellationToken, Task>? delay) : ProcessScanTool
{
    public const string TokenVariable = "SONAR_TOKEN";

    // The issues search API will not return anything past the 10,000th issue of one query.
    internal const int MaxIssuesPerQuery = 10_000;
    internal const int PageSize = 500;

    private static readonly TimeSpan AnalysisPoll = TimeSpan.FromSeconds(2);

    // Slices, in the order they are tried when a query has too many issues: by type, then by severity.
    private static readonly (string Name, string[] Values)[] Slices =
    [
        ("types", ["CODE_SMELL", "BUG", "VULNERABILITY"]),
        ("severities", ["INFO", "MINOR", "MAJOR", "CRITICAL", "BLOCKER"])
    ];

    private HttpClient? _http;

    protected string ServerUrl(RunContext context) =>
        Settings(context).ServerUrl?.TrimEnd('/') ?? throw new InvalidOperationException($"{Name} needs tools.{Name}.serverUrl in engine.json.");

    protected string TokenSecretName(RunContext context) =>
        Settings(context).Env is { } env && env.TryGetValue(TokenVariable, out var name)
            ? name
            : throw new InvalidOperationException($"{Name} needs tools.{Name}.env with {TokenVariable} naming the secret that holds the SonarQube token.");

    protected override async Task PrepareAsync(RunContext context, CancellationToken cancellationToken)
    {
        await base.PrepareAsync(context, cancellationToken);

        var server = ServerUrl(context);
        TokenSecretName(context);

        // The scan scripts checked the same: no point scanning anything against a server that is not ready.
        try
        {
            using var response = await Http().GetAsync($"{server}/api/system/status", cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            var status = json.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (status != "UP")
            {
                throw new InvalidOperationException($"{Name}: SonarQube at {server} is not ready (status: {status ?? "unknown"}).");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new InvalidOperationException($"{Name}: cannot reach SonarQube at {server}: {ex.Message}");
        }
    }

    protected HttpClient Http() => _http ??= CreateHttp();

    private HttpClient CreateHttp()
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(60);
        return http;
    }

    /// <summary>The SonarQube token. For calls the engine makes itself. The scanners get it as an environment variable or argument.</summary>
    protected Task<string> TokenAsync(RunContext context, CancellationToken cancellationToken) =>
        context.Secrets.GetSecretAsync(TokenSecretName(context), Name, cancellationToken);

    /// <summary>
    /// After the scanner has sent its analysis: wait for the server to process it, fetch the issues into the result
    /// file and report the clean run. A server that stops short, or gives a different count than it reported, is a
    /// failure (ParseError), never a partial result.
    /// </summary>
    protected async Task<RepoWorkResult> CollectAsync(
        RepoWorkContext work,
        string projectKey,
        string reportTaskFile,
        DateTimeOffset started,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var context = work.Run;
        var token = await TokenAsync(context, cancellationToken);
        var api = new ApiRequester(
            Http(),
            (message, status) => new SonarException(message, "ToolError", status),
            text => text.Replace(token, SecretRedactor.Mask, StringComparison.Ordinal),
            delay ?? ((wait, ct) => Task.Delay(wait, ct)),
            TimeSpan.FromSeconds(60),
            onRetry: null);

        try
        {
            var taskUrl = ReadTaskUrl(reportTaskFile);
            await WaitForAnalysisAsync(api, taskUrl, token, deadline, cancellationToken);

            EnsureResultsFolder(work);
            var count = await FetchIssuesAsync(api, ServerUrl(context), projectKey, token, work.ResultFile, cancellationToken);
            return Success(started, count, severities: ScanOutput.SonarSeverities(Name, work.ResultFile));
        }
        catch (SonarException ex)
        {
            return RepoWorkResult.Failed(ex.Category, ex.Message);
        }
    }

    private static string ReadTaskUrl(string reportTaskFile)
    {
        if (!File.Exists(reportTaskFile))
        {
            throw new SonarException("The scanner finished but wrote no report-task.txt, so there is no analysis to wait for.", "ToolError");
        }

        var line = File.ReadLines(reportTaskFile).FirstOrDefault(l => l.StartsWith("ceTaskUrl=", StringComparison.Ordinal));
        return line is null
            ? throw new SonarException("report-task.txt has no ceTaskUrl.", "ToolError")
            : line["ceTaskUrl=".Length..].Trim();
    }

    private async Task WaitForAnalysisAsync(ApiRequester api, string taskUrl, string token, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var document = await api.GetJsonAsync(taskUrl, () => Authorized(taskUrl, token), cancellationToken);
            var task = document.RootElement.TryGetProperty("task", out var t) ? t : default;
            var status = task.ValueKind == JsonValueKind.Object && task.TryGetProperty("status", out var s) ? s.GetString() : null;

            switch (status)
            {
                case "SUCCESS":
                    return;
                case "FAILED" or "CANCELED":
                    var why = task.TryGetProperty("errorMessage", out var e) ? e.GetString() : null;
                    throw new SonarException($"The SonarQube analysis {status.ToLowerInvariant()}{(why is null ? "." : $": {why}")}", "AnalysisFailed");
            }

            if (Time.GetUtcNow() >= deadline)
            {
                throw new SonarException("The SonarQube analysis was still not finished when the time for this repo ran out.", "Timeout");
            }

            await (delay ?? ((wait, ct) => Task.Delay(wait, ct)))(AnalysisPoll, cancellationToken);
        }
    }

    // Writes { project, total, issues: [...] }. The file is made beside the result and moved into place at the end.
    private async Task<int> FetchIssuesAsync(
        ApiRequester api,
        string server,
        string projectKey,
        string token,
        string resultFile,
        CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetDirectoryName(resultFile)!, $".{Path.GetFileName(resultFile)}.{Guid.NewGuid():N}.tmp");
        try
        {
            int total, fetched;
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new Utf8JsonWriter(stream))
            {
                total = await TotalAsync(api, server, projectKey, token, [], cancellationToken);

                writer.WriteStartObject();
                writer.WriteString("project", projectKey);
                writer.WriteNumber("total", total);
                writer.WriteStartArray("issues");
                fetched = await FetchSliceAsync(api, server, projectKey, token, writer, [], 0, total, cancellationToken);
                writer.WriteEndArray();
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken);
            }

            // The API stops at 10,000 and slicing can miss issues. A count that does not match is an error.
            if (fetched != total)
            {
                throw new SonarException($"SonarQube reported {total} issues but {fetched} were fetched.", "ParseError");
            }

            File.Move(temp, resultFile, overwrite: true);
            return fetched;
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private async Task<int> FetchSliceAsync(
        ApiRequester api,
        string server,
        string projectKey,
        string token,
        Utf8JsonWriter writer,
        List<(string Name, string Value)> filters,
        int sliceLevel,
        int total,
        CancellationToken cancellationToken)
    {
        if (total == 0)
        {
            return 0;
        }

        if (total > MaxIssuesPerQuery)
        {
            if (sliceLevel >= Slices.Length)
            {
                throw new SonarException($"More than {MaxIssuesPerQuery} issues in one slice, which the SonarQube API cannot return.", "ParseError");
            }

            var (name, values) = Slices[sliceLevel];
            var sliced = 0;
            foreach (var value in values)
            {
                var next = new List<(string, string)>(filters) { (name, value) };
                var count = await TotalAsync(api, server, projectKey, token, next, cancellationToken);
                sliced += await FetchSliceAsync(api, server, projectKey, token, writer, next, sliceLevel + 1, count, cancellationToken);
            }

            return sliced;
        }

        var fetched = 0;
        for (var page = 1; (page - 1) * PageSize < total; page++)
        {
            var url = IssuesUrl(server, projectKey, filters, PageSize, page);
            using var document = await api.GetJsonAsync(url, () => Authorized(url, token), cancellationToken);
            if (!document.RootElement.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
            {
                throw new SonarException("SonarQube's answer has no list of issues.", "ParseError");
            }

            foreach (var issue in issues.EnumerateArray())
            {
                issue.WriteTo(writer);
                fetched++;
            }
        }

        return fetched;
    }

    private static async Task<int> TotalAsync(
        ApiRequester api,
        string server,
        string projectKey,
        string token,
        List<(string Name, string Value)> filters,
        CancellationToken cancellationToken)
    {
        var url = IssuesUrl(server, projectKey, filters, pageSize: 1, page: 1);
        using var document = await api.GetJsonAsync(url, () => Authorized(url, token), cancellationToken);
        var root = document.RootElement;
        if (root.TryGetProperty("paging", out var paging) && paging.TryGetProperty("total", out var pagingTotal) && pagingTotal.TryGetInt32(out var n))
        {
            return n;
        }

        return root.TryGetProperty("total", out var plain) && plain.TryGetInt32(out var m)
            ? m
            : throw new SonarException("SonarQube's answer has no total.", "ParseError");
    }

    private static string IssuesUrl(string server, string projectKey, List<(string Name, string Value)> filters, int pageSize, int page) =>
        $"{server}/api/issues/search?componentKeys={Uri.EscapeDataString(projectKey)}&ps={pageSize}&p={page}"
        + string.Concat(filters.Select(f => $"&{f.Name}={Uri.EscapeDataString(f.Value)}"));

    private static HttpRequestMessage Authorized(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    /// <summary>Time left for this repo out of the tool's timeout.</summary>
    protected static TimeSpan Remaining(DateTimeOffset deadline, DateTimeOffset now) =>
        deadline - now > TimeSpan.Zero ? deadline - now : TimeSpan.FromSeconds(1);

    protected static RepoWorkResult NotApplicable() =>
        RepoWorkResult.Skipped(new Models.ToolSkippedMarker { Reason = RepoEligibility.NotApplicable });
}
