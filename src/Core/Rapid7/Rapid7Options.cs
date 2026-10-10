using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.Rapid7;

/// <summary>The Rapid7 import's settings from engine.json, with the script's defaults filled in.</summary>
public sealed record Rapid7Options(
    string BaseUrl,
    string AuthMode,
    int PageSize,
    int MaxPages,
    IReadOnlyList<string> Areas)
{
    public const string ApiPrefix = "/ias/v1";
    public const string ApiKeyMode = "api_key";
    public const string ConsoleTokenMode = "console_token";

    public const string Applications = "applications";
    public const string Scans = "scans";
    public const string Vulnerabilities = "vulnerabilities";
    public const string Reports = "reports";

    /// <summary>Every area, in the order they are fetched. The first three are small and are held in memory.</summary>
    public static readonly IReadOnlyList<string> AllAreas = [Applications, Scans, Reports, Vulnerabilities];

    /// <summary>What is exported when engine.json does not say: the applications, each with its vulnerabilities.</summary>
    public static readonly IReadOnlyList<string> DefaultAreas = [Applications, Vulnerabilities];

    private static readonly Dictionary<string, string> Endpoints = new()
    {
        [Applications] = "apps",
        [Scans] = "scans",
        [Vulnerabilities] = "vulnerabilities",
        [Reports] = "reports"
    };

    public bool Includes(string area) => Areas.Contains(area);

    /// <summary>The path under the API address that serves an area.</summary>
    public static string Endpoint(string area) =>
        Endpoints.TryGetValue(area, out var endpoint) ? endpoint : throw new ArgumentException($"'{area}' is not a Rapid7 area.", nameof(area));

    /// <summary>
    /// Reads the Rapid7 tool's settings. The address may be given with or without the API path, as in the script.
    /// Throws <see cref="InvalidOperationException"/> naming the setting if the address or the token's secret is missing.
    /// </summary>
    public static Rapid7Options From(ToolSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            throw new InvalidOperationException("The Rapid7 import needs tools.Rapid7.baseUrl in engine.json.");
        }

        var baseUrl = settings.BaseUrl.Trim().TrimEnd('/');
        if (!baseUrl.EndsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            baseUrl += ApiPrefix;
        }

        var areas = settings.Areas is { Count: > 0 }
            ? AllAreas.Where(a => settings.Areas.Contains(a)).ToList()
            : [.. DefaultAreas];

        return new Rapid7Options(
            baseUrl,
            settings.AuthMode ?? ApiKeyMode,
            settings.PageSize ?? 100,
            settings.MaxPages ?? 0,
            areas);
    }
}
