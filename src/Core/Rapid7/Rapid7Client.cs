using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Vizient.AutomatedScanner.Core.Imports;
using Vizient.AutomatedScanner.Core.Processes;

namespace Vizient.AutomatedScanner.Core.Rapid7;

/// <summary>A Rapid7 call failed for a reason retrying will not fix (or retrying ran out).</summary>
public class Rapid7ApiException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;

    /// <summary>The token was refused, or is not allowed to read this. The import stops at once.</summary>
    public bool IsAuthFailure => StatusCode is 401 or 403;
}

/// <summary>One page of an area. Holds the response, so its records are valid only until it is disposed.</summary>
public sealed class Rapid7Page : IDisposable
{
    private readonly JsonDocument _document;

    internal Rapid7Page(string area, int pageIndex, int? totalPages, JsonDocument document, IReadOnlyList<JsonElement> records)
    {
        Area = area;
        PageIndex = pageIndex;
        TotalPages = totalPages;
        _document = document;
        Records = records;
    }

    public string Area { get; }

    /// <summary>Zero-based.</summary>
    public int PageIndex { get; }

    /// <summary>What the server says, or null if it does not say.</summary>
    public int? TotalPages { get; }

    public IReadOnlyList<JsonElement> Records { get; }

    public void Dispose() => _document.Dispose();
}

/// <summary>
/// Reads the Rapid7 InsightAppSec API v1 (ported from rapid7_export.py): a page at a time, retrying what is worth
/// retrying. The token goes in a header on each request. It is masked out of every message this class produces.
/// </summary>
public sealed class Rapid7Client
{
    public const int MaxAttempts = ApiRequester.MaxAttempts;
    public const string UserAgent = "Rapid7-JSON-export/1.0";

    private readonly Rapid7Options _options;
    private readonly string _token;
    private readonly SecretRedactor _redactor;
    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public Rapid7Client(
        HttpClient http,
        Rapid7Options options,
        string token,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _options = options;
        _token = token;
        _redactor = new SecretRedactor([token]);
        _delay = delay ?? Task.Delay;
    }

    /// <summary>How long one request may take before it counts as a connection problem and is retried.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Called when a request is about to be retried, with a message that is safe to show.</summary>
    public Action<string>? OnRetry { get; init; }

    /// <summary>
    /// Every page of an area, until the last one. Follows the server's page token when it gives one, otherwise the
    /// page index. Stops early at <see cref="Rapid7Options.MaxPages"/>. The caller disposes each page.
    /// </summary>
    public async IAsyncEnumerable<Rapid7Page> GetPagesAsync(
        string area,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var endpoint = $"{_options.BaseUrl}/{Rapid7Options.Endpoint(area)}";
        var pageIndex = 0;
        string? pageToken = null;

        while (true)
        {
            var query = pageToken is null
                ? $"index={pageIndex}&size={_options.PageSize}"
                : $"page-token={Uri.EscapeDataString(pageToken)}&size={_options.PageSize}";
            var url = $"{endpoint}?{query}";

            var document = await Requester().GetJsonAsync(url, () => CreateRequest(url), cancellationToken);
            var records = ReadRecords(document, url);
            var (totalPages, nextToken) = ReadMetadata(document);

            // Work out whether this is the last page before handing it over: the page is disposed by the caller.
            var finished =
                (_options.MaxPages > 0 && pageIndex + 1 >= _options.MaxPages)
                || records.Count == 0
                || (totalPages is not null && pageIndex + 1 >= totalPages);

            yield return new Rapid7Page(area, pageIndex, totalPages, document, records);

            if (finished)
            {
                yield break;
            }

            if (nextToken is not null && nextToken == pageToken)
            {
                throw new Rapid7ApiException($"Pagination token did not advance for {endpoint}.");
            }

            pageToken = nextToken;
            pageIndex++;
        }
    }

    private static List<JsonElement> ReadRecords(JsonDocument document, string url)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new Rapid7ApiException($"Unexpected response from {url}: expected a JSON object.");
        }

        // A page with no usable "data" list simply has no records, as in the script.
        return document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().ToList()
            : [];
    }

    private static (int? TotalPages, string? PageToken) ReadMetadata(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        int? totalPages = metadata.TryGetProperty("total_pages", out var pages) && pages.ValueKind == JsonValueKind.Number && pages.TryGetInt32(out var n)
            ? n
            : null;
        var token = metadata.TryGetProperty("page_token", out var t) && t.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(t.GetString())
            ? t.GetString()
            : null;
        return (totalPages, token);
    }

    // Built each time, so a change to RequestTimeout or OnRetry made after construction is picked up.
    private ApiRequester Requester() => new(
        _http,
        (message, status) => new Rapid7ApiException(message, status),
        _redactor.Redact,
        _delay,
        RequestTimeout,
        OnRetry);

    private HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        if (_options.AuthMode == Rapid7Options.ApiKeyMode)
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", _token);
        }
        else
        {
            // An InsightVM console token is sent as HTTP Basic with the token as the user name and no password.
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_token}:")));
        }

        return request;
    }
}
