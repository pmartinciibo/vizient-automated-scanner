using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
    public const int MaxAttempts = 8;
    public const string UserAgent = "Rapid7-JSON-export/1.0";

    private static readonly int[] RetryableStatuses = [429, 500, 502, 503, 504];
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly Rapid7Options _options;
    private readonly string _token;
    private readonly SecretRedactor _redactor;
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

            var document = await RequestJsonAsync(url, cancellationToken);
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

    // ---- one request, with retries ----

    private async Task<JsonDocument> RequestJsonAsync(string url, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var last = attempt == MaxAttempts - 1;
            HttpStatusCode status;
            byte[] body;
            TimeSpan? retryAfter;

            try
            {
                (status, body, retryAfter) = await SendAsync(url, cancellationToken);
            }
            catch (Exception ex) when (IsConnectionProblem(ex, cancellationToken))
            {
                // A dropped connection, a DNS blip or a request that timed out: the call itself may be fine.
                if (last)
                {
                    throw new Rapid7ApiException(Mask($"Giving up on {url} after {MaxAttempts} attempts: {Describe(ex)}"));
                }

                var wait = Backoff(attempt);
                OnRetry?.Invoke(Mask($"connection issue ({Describe(ex)}); retrying in {wait.TotalSeconds:0}s"));
                await _delay(wait, cancellationToken);
                continue;
            }

            if ((int)status is >= 200 and < 300)
            {
                try
                {
                    return JsonDocument.Parse(body);
                }
                catch (JsonException)
                {
                    throw new Rapid7ApiException(Mask($"Non-JSON response from {url}: {Snippet(body)}"), (int)status);
                }
            }

            if (RetryableStatuses.Contains((int)status) && !last)
            {
                var wait = retryAfter ?? Backoff(attempt);
                OnRetry?.Invoke($"HTTP {(int)status}; retrying in {wait.TotalSeconds:0}s");
                await _delay(wait, cancellationToken);
                continue;
            }

            throw new Rapid7ApiException(Mask($"HTTP {(int)status} from {url}: {Snippet(body)}"), (int)status);
        }

        throw new Rapid7ApiException($"Giving up on {url} after {MaxAttempts} attempts.");
    }

    private async Task<(HttpStatusCode Status, byte[] Body, TimeSpan? RetryAfter)> SendAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        var body = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        return (response.StatusCode, body, ReadRetryAfter(response));
    }

    // The header can be a whole number of seconds or, as the script allowed, a decimal one.
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.NonValidated.TryGetValues("Retry-After", out var values))
        {
            return null;
        }

        var text = values.FirstOrDefault();
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    // An HttpRequestException, or a timeout (cancelled by our own timer rather than by the caller).
    private static bool IsConnectionProblem(Exception ex, CancellationToken caller) =>
        ex is HttpRequestException || (ex is OperationCanceledException && !caller.IsCancellationRequested);

    // 1, 2, 4, 8 ... seconds, never more than a minute.
    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), MaxBackoff.TotalSeconds));

    private static string Describe(Exception ex) =>
        ex is OperationCanceledException ? "the request timed out" : ex.Message;

    private static string Snippet(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 400)).Replace('\n', ' ').Replace('\r', ' ');
        return text;
    }

    private string Mask(string text) => _redactor.Redact(text);
}
