using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Vizient.AutomatedScanner.Core.Imports;
using Vizient.AutomatedScanner.Core.Processes;

namespace Vizient.AutomatedScanner.Core.CheckMarx;

/// <summary>A CheckMarx call failed for a reason retrying will not fix (or retrying ran out).</summary>
public sealed class CheckMarxApiException(string message, int? statusCode = null, Exception? inner = null) : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;

    /// <summary>The token was refused, or is not allowed to read this. The import stops at once.</summary>
    public bool IsAuthFailure => StatusCode is 401 or 403;
}

/// <summary>One page of records. Holds the response, so its records are valid only until it is disposed.</summary>
public sealed class CheckMarxPage : IDisposable
{
    private readonly JsonDocument _document;

    internal CheckMarxPage(string path, int pageIndex, JsonDocument document, IReadOnlyList<JsonElement> records)
    {
        Path = path;
        PageIndex = pageIndex;
        _document = document;
        Records = records;
    }

    public string Path { get; }

    /// <summary>Zero-based.</summary>
    public int PageIndex { get; }

    public IReadOnlyList<JsonElement> Records { get; }

    public void Dispose() => _document.Dispose();
}

/// <summary>
/// Reads the Checkmarx One REST API (ported from checkmarx_export.py). The refresh token is swapped for a short-lived
/// access token, which is replaced every few minutes because a full pull is thousands of calls and runs far longer than
/// one lasts. Neither token ever appears in a message this class produces.
/// </summary>
public sealed class CheckMarxClient
{
    public const string UserAgent = "Checkmarx-JSON-export/1.0";

    /// <summary>How old an access token may get before a new one is asked for, as in the script.</summary>
    public static readonly TimeSpan AccessTokenMaxAge = TimeSpan.FromSeconds(240);

    private static readonly string[] RecordListKeys = ["items", "data", "results", "applications", "projects", "scans"];

    private readonly HttpClient _http;
    private readonly CheckMarxOptions _options;
    private readonly string _refreshToken;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _time;
    private readonly List<string> _secrets;
    private string? _accessToken;
    private DateTimeOffset _issuedAt;

    public CheckMarxClient(
        HttpClient http,
        CheckMarxOptions options,
        string refreshToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? time = null)
    {
        _http = http;
        _options = options;
        _refreshToken = refreshToken;
        _delay = delay ?? Task.Delay;
        _time = time ?? TimeProvider.System;
        _secrets = [refreshToken];
    }

    /// <summary>How long one request may take before it counts as a connection problem and is retried.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Called when a request is about to be retried, with a message that is safe to show.</summary>
    public Action<string>? OnRetry { get; init; }

    /// <summary>How many access tokens have been asked for. A long import needs several.</summary>
    public int TokensIssued { get; private set; }

    /// <summary>
    /// Every page of a collection at <paramref name="path"/>, by offset and limit, until a page comes back short or empty.
    /// <paramref name="extraQuery"/> goes ahead of offset and limit, as in the script. The caller disposes each page.
    /// </summary>
    public async IAsyncEnumerable<CheckMarxPage> GetPagesAsync(
        string path,
        IReadOnlyDictionary<string, string>? extraQuery = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var endpoint = $"{_options.ApiUrl}{path}";
        var extra = string.Concat((extraQuery ?? new Dictionary<string, string>())
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}&"));
        var offset = 0;
        var pageIndex = 0;

        while (true)
        {
            var url = $"{endpoint}?{extra}offset={offset}&limit={_options.PageSize}";
            var token = await AccessTokenAsync(cancellationToken);

            var document = await Requester().GetJsonAsync(url, () => CreateGet(url, token), cancellationToken);
            var records = RecordsFrom(document.RootElement);

            var finished =
                (_options.MaxPages > 0 && pageIndex + 1 >= _options.MaxPages)
                || records.Count == 0
                || records.Count < _options.PageSize;

            yield return new CheckMarxPage(path, pageIndex, document, records);

            if (finished)
            {
                yield break;
            }

            offset += _options.PageSize;
            pageIndex++;
        }
    }

    // A bare list, or an object with the list under one of a few usual names, as in the script. Anything else has none.
    private static List<JsonElement> RecordsFrom(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().ToList();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        foreach (var key in RecordListKeys)
        {
            if (root.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
            {
                return list.EnumerateArray().ToList();
            }
        }

        return [];
    }

    // ---- the access token ----

    private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is not null && _time.GetUtcNow() - _issuedAt <= AccessTokenMaxAge)
        {
            return _accessToken;
        }

        try
        {
            var formText =
                $"grant_type=refresh_token&client_id={Uri.EscapeDataString(_options.ClientId)}&refresh_token={Uri.EscapeDataString(_refreshToken)}";

            using var document = await Requester().GetJsonAsync(
                _options.TokenUrl,
                () => CreatePost(_options.TokenUrl, formText),
                cancellationToken);

            var token = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("access_token", out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;

            if (string.IsNullOrEmpty(token))
            {
                throw new CheckMarxApiException("The token response did not contain access_token.", 401);
            }

            _secrets.Add(token);
            _accessToken = token;
            _issuedAt = _time.GetUtcNow();
            TokensIssued++;
            return token;
        }
        catch (CheckMarxApiException ex) when (!ex.IsAuthFailure)
        {
            // Whatever went wrong getting a token, nothing can be read without one, so the import stops.
            throw new CheckMarxApiException(Mask($"Authentication failed: {ex.Message}"), 401, ex);
        }
    }

    // ---- requests ----

    // Built each time, so a change to RequestTimeout or OnRetry made after construction is picked up.
    private ApiRequester Requester() => new(
        _http,
        (message, status) => new CheckMarxApiException(message, status),
        Mask,
        _delay,
        RequestTimeout,
        OnRetry);

    private static HttpRequestMessage CreateGet(string url, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static HttpRequestMessage CreatePost(string url, string formBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        return request;
    }

    // Masks both tokens, and every access token handed out so far, wherever they turn up.
    private string Mask(string text) => new SecretRedactor(_secrets).Redact(text);
}
