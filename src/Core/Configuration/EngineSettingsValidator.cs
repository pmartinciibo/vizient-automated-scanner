using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Core.Configuration;

/// <summary>
/// Checks engine.json settings when the file is loaded, so a typo or a bad number stops the engine at the
/// start with a message that names the setting, not part way through a scan.
/// </summary>
public static class EngineSettingsValidator
{
    public const int MaxParallelLimit = 256;

    private static readonly string[] SnykModes = ["code", "dependencies"];

    /// <summary>Throws <see cref="InvalidDataException"/> naming the first setting that is wrong.</summary>
    public static void Validate(EngineSettings settings)
    {
        ValidateSources(settings.Sources);
        ValidateRetry(settings.Retry);
        ValidateSkip(settings.Skip);
        ValidateSecrets(settings.Secrets);

        foreach (var (name, tool) in settings.Tools)
        {
            ValidateTool(name, tool);
        }
    }

    private static void ValidateSources(List<SourceSettings> sources)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            var where = $"sources[{i}]";

            if (source.Source is not (SourceSettings.Ado or SourceSettings.GitHub))
            {
                throw Bad($"{where}.source must be \"{SourceSettings.Ado}\" or \"{SourceSettings.GitHub}\", not \"{source.Source}\".");
            }

            if (string.IsNullOrWhiteSpace(source.Org))
            {
                throw Bad($"{where}.org is required.");
            }

            if (!seen.Add($"{source.Source}|{source.Org.Trim()}"))
            {
                throw Bad($"{where} lists {source.Source} org \"{source.Org}\" a second time.");
            }
        }
    }

    private static void ValidateRetry(RetrySettings retry)
    {
        if (retry.MaxAttempts < 1)
        {
            throw Bad("retry.maxAttempts must be at least 1.");
        }

        if (retry.BackoffSeconds < 0)
        {
            throw Bad("retry.backoffSeconds cannot be negative.");
        }
    }

    private static void ValidateSecrets(SecretsSettings secrets)
    {
        if (secrets.ExpiryWarnDays < 0)
        {
            throw Bad("secrets.expiryWarnDays cannot be negative.");
        }

        if (secrets.RequiredAccount is not null && string.IsNullOrWhiteSpace(secrets.RequiredAccount))
        {
            throw Bad("secrets.requiredAccount is blank. Leave it out to allow any account.");
        }
    }

    private static void ValidateSkip(SkipSettings skip)
    {
        if (skip.CanaryPercent is < 0 or > 100)
        {
            throw Bad("skip.canaryPercent must be from 0 to 100.");
        }

        if (skip.FullRescanEveryDays < 1)
        {
            throw Bad("skip.fullRescanEveryDays must be at least 1.");
        }
    }

    private static void ValidateTool(string name, ToolSettings tool)
    {
        if (!ToolNames.All.Contains(name))
        {
            throw Bad($"tools names \"{name}\", which is not a tool. The tools are: {string.Join(", ", ToolNames.All)}.");
        }

        if (tool.MaxParallel is < 1 or > MaxParallelLimit)
        {
            throw Bad($"tools.{name}.maxParallel must be from 1 to {MaxParallelLimit}.");
        }

        if (tool.TimeoutMinutes is < 1)
        {
            throw Bad($"tools.{name}.timeoutMinutes must be at least 1.");
        }

        if (name == ToolNames.Snyk && tool.Mode is not null && !SnykModes.Contains(tool.Mode))
        {
            throw Bad($"tools.Snyk.mode must be \"code\" or \"dependencies\", not \"{tool.Mode}\".");
        }

        if (name == ToolNames.Rapid7)
        {
            ValidateRapid7(tool);
        }

        if (tool.RunAs is { } runAs && (string.IsNullOrWhiteSpace(runAs.Account) || string.IsNullOrWhiteSpace(runAs.PasswordSecret)))
        {
            throw Bad($"tools.{name}.runAs needs both account and passwordSecret.");
        }

        foreach (var (variable, secret) in tool.Env ?? [])
        {
            if (string.IsNullOrWhiteSpace(variable) || string.IsNullOrWhiteSpace(secret))
            {
                throw Bad($"tools.{name}.env must map an environment variable name to a secret name.");
            }
        }
    }

    private static readonly string[] Rapid7AuthModes = ["api_key", "console_token"];
    private static readonly string[] Rapid7Areas = ["applications", "scans", "vulnerabilities", "reports"];

    private static void ValidateRapid7(ToolSettings tool)
    {
        if (tool.BaseUrl is not null
            && !(Uri.TryCreate(tool.BaseUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"))
        {
            throw Bad("tools.Rapid7.baseUrl must be a full http or https address.");
        }

        if (tool.AuthMode is not null && !Rapid7AuthModes.Contains(tool.AuthMode))
        {
            throw Bad($"tools.Rapid7.authMode must be \"api_key\" or \"console_token\", not \"{tool.AuthMode}\".");
        }

        if (tool.PageSize is < 1)
        {
            throw Bad("tools.Rapid7.pageSize must be at least 1.");
        }

        if (tool.MaxPages is < 0)
        {
            throw Bad("tools.Rapid7.maxPages cannot be negative. Use 0 or leave it out for all pages.");
        }

        if (tool.Areas is { } areas)
        {
            var unknown = areas.Except(Rapid7Areas).ToList();
            if (areas.Count == 0 || unknown.Count > 0)
            {
                throw Bad($"tools.Rapid7.areas must be some of: {string.Join(", ", Rapid7Areas)}." +
                    (unknown.Count > 0 ? $" Not known: {string.Join(", ", unknown)}." : string.Empty));
            }
        }
    }

    private static InvalidDataException Bad(string message) =>
        new($"{EngineConfig.FileName}: {message}");
}
