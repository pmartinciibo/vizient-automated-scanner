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

    private static InvalidDataException Bad(string message) =>
        new($"{EngineConfig.FileName}: {message}");
}
