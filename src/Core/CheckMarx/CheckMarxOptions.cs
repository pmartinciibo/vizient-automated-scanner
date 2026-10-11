using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.CheckMarx;

/// <summary>The CheckMarx import's settings from engine.json, with the script's defaults filled in.</summary>
public sealed record CheckMarxOptions(
    string ApiUrl,
    string TokenUrl,
    string ClientId,
    int PageSize,
    int MaxPages,
    IReadOnlyList<string> Areas,
    string ResultsScope,
    string? ResultsEngine,
    string? ResultsScanStatus,
    int ResultsMaxScans)
{
    public const string DefaultApiUrl = "https://us.ast.checkmarx.net";
    public const string DefaultTokenUrl = "https://us.iam.checkmarx.net/auth/realms/{tenant}/protocol/openid-connect/token";
    public const string DefaultClientId = "ast-app";
    public const string LatestPerProject = "latest-per-project";
    public const string AllScans = "all";

    public const string Applications = "applications";
    public const string Projects = "projects";
    public const string Results = "results";

    public const string ApplicationsPath = "/api/applications";
    public const string ProjectsPath = "/api/projects";
    public const string ScansPath = "/api/scans";
    public const string ResultsPath = "/api/results";

    /// <summary>Scans are always read when findings are wanted, to choose which scan each project's findings come from.</summary>
    public static readonly IReadOnlyList<string> DefaultAreas = [Applications, Projects, Results];

    public bool Includes(string area) => Areas.Contains(area);

    /// <summary>
    /// Reads the CheckMarx tool's settings. Throws <see cref="InvalidOperationException"/> naming the setting if the token
    /// address cannot be worked out: it needs the tenant unless tokenUrl is given without a {tenant} in it.
    /// </summary>
    public static CheckMarxOptions From(ToolSettings settings)
    {
        var tenant = string.IsNullOrWhiteSpace(settings.Tenant) ? null : settings.Tenant.Trim();
        var tokenUrl = string.IsNullOrWhiteSpace(settings.TokenUrl) ? DefaultTokenUrl : settings.TokenUrl.Trim();

        if (tokenUrl.Contains("{tenant}", StringComparison.Ordinal))
        {
            tokenUrl = tenant is null
                ? throw new InvalidOperationException("The CheckMarx import needs tools.CheckMarx.tenant in engine.json (the realm in the token address), or a tokenUrl that does not need one.")
                : tokenUrl.Replace("{tenant}", tenant, StringComparison.Ordinal);
        }

        var areas = settings.Areas is { Count: > 0 }
            ? DefaultAreas.Where(a => settings.Areas.Contains(a)).ToList()
            : [.. DefaultAreas];

        return new CheckMarxOptions(
            (string.IsNullOrWhiteSpace(settings.BaseUrl) ? DefaultApiUrl : settings.BaseUrl.Trim()).TrimEnd('/'),
            tokenUrl,
            string.IsNullOrWhiteSpace(settings.ClientId) ? DefaultClientId : settings.ClientId.Trim(),
            settings.PageSize ?? 100,
            settings.MaxPages ?? 0,
            areas,
            settings.ResultsScope ?? LatestPerProject,
            // Left out means the script's default. An empty string means no filter at all.
            settings.ResultsEngine is null ? "sast" : NullIfEmpty(settings.ResultsEngine),
            settings.ResultsScanStatus is null ? "Completed" : NullIfEmpty(settings.ResultsScanStatus),
            settings.ResultsMaxScans ?? 0);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
