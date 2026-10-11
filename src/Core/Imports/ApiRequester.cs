using System.Net;
using System.Text;
using System.Text.Json;

namespace Vizient.AutomatedScanner.Core.Imports;

/// <summary>
/// Makes one JSON request to a vendor API and retries what is worth retrying. Shared by the Rapid7 and CheckMarx
/// imports so they behave the same: up to 8 attempts, a connection problem or a request that takes too long is
/// retried, so are 429 and 5xx, waiting what the server says or 1, 2, 4 ... seconds and never more than a minute.
/// Any other status is reported at once with an excerpt of the response. Everything it says is masked of secrets.
/// </summary>
internal sealed class ApiRequester(
    HttpClient http,
    Func<string, int?, Exception> createError,
    Func<string, string> mask,
    Func<TimeSpan, CancellationToken, Task> delay,
    TimeSpan requestTimeout,
    Action<string>? onRetry)
{
    public const int MaxAttempts = 8;

    private static readonly int[] RetryableStatuses = [429, 500, 502, 503, 504];
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    /// <param name="createRequest">Builds the request afresh for each attempt, since a sent request cannot be sent again.</param>
    /// <param name="url">Only for messages. It must not contain a secret.</param>
    public async Task<JsonDocument> GetJsonAsync(string url, Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken) =>
        (await GetAsync(url, createRequest, cancellationToken)).Json;

    /// <summary>As <see cref="GetJsonAsync"/>, and the response headers too, for APIs that page through a header.</summary>
    public async Task<ApiResponse> GetAsync(string url, Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var last = attempt == MaxAttempts - 1;
            HttpStatusCode status;
            byte[] body;
            TimeSpan? retryAfter;
            Dictionary<string, string> headers;

            try
            {
                (status, body, retryAfter, headers) = await SendAsync(createRequest, cancellationToken);
            }
            catch (Exception ex) when (IsConnectionProblem(ex, cancellationToken))
            {
                // A dropped connection, a DNS blip or a request that timed out: the call itself may be fine.
                if (last)
                {
                    throw createError(mask($"Giving up on {url} after {MaxAttempts} attempts: {Describe(ex)}"), null);
                }

                var wait = Backoff(attempt);
                onRetry?.Invoke(mask($"connection issue ({Describe(ex)}); retrying in {wait.TotalSeconds:0}s"));
                await delay(wait, cancellationToken);
                continue;
            }

            if ((int)status is >= 200 and < 300)
            {
                try
                {
                    return new ApiResponse(JsonDocument.Parse(body), headers);
                }
                catch (JsonException)
                {
                    throw createError(mask($"Non-JSON response from {url}: {Snippet(body)}"), (int)status);
                }
            }

            if (RetryableStatuses.Contains((int)status) && !last)
            {
                var wait = retryAfter ?? Backoff(attempt);
                onRetry?.Invoke($"HTTP {(int)status}; retrying in {wait.TotalSeconds:0}s");
                await delay(wait, cancellationToken);
                continue;
            }

            throw createError(mask($"HTTP {(int)status} from {url}: {Snippet(body)}"), (int)status);
        }

        throw createError($"Giving up on {url} after {MaxAttempts} attempts.", null);
    }

    private async Task<(HttpStatusCode Status, byte[] Body, TimeSpan? RetryAfter, Dictionary<string, string> Headers)> SendAsync(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        using var request = createRequest();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        var body = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        var headers = response.Headers.NonValidated
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        return (response.StatusCode, body, ReadRetryAfter(response), headers);
    }

    // The header can be a whole number of seconds or, as the scripts allowed, a decimal one.
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.NonValidated.TryGetValues("Retry-After", out var values))
        {
            return null;
        }

        return double.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
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

    private static string Snippet(byte[] body) =>
        Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 400)).Replace('\n', ' ').Replace('\r', ' ');
}

/// <summary>A parsed JSON response and its headers (names in lower case). Dispose the document when done.</summary>
internal sealed record ApiResponse(JsonDocument Json, IReadOnlyDictionary<string, string> Headers) : IDisposable
{
    public void Dispose() => Json.Dispose();
}
