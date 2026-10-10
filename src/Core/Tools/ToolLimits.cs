using Vizient.AutomatedScanner.Core.Processes;

namespace Vizient.AutomatedScanner.Core.Tools;

/// <summary>
/// The limits a tool's repos run under. For each setting a per-run override in request.json wins, then the
/// tool's entry in engine.json, then the design's default (section 9.1), so an engine.json with no tool
/// settings still works.
/// </summary>
public sealed record ToolLimits(int MaxParallel, TimeSpan Timeout, RetryPolicy Retry)
{
    private static readonly Dictionary<string, (int MaxParallel, int TimeoutMinutes)> Defaults = new()
    {
        [ToolNames.RepoClone] = (12, 60),
        [ToolNames.TruffleHog] = (8, 30),
        [ToolNames.Semgrep] = (8, 30),
        [ToolNames.Snyk] = (4, 45),
        [ToolNames.SonarStandard] = (4, 60),
        [ToolNames.SonarDotNet] = (4, 60)
    };

    public static ToolLimits For(RunContext context, string tool)
    {
        var (defaultParallel, defaultMinutes) = Defaults.GetValueOrDefault(tool, (1, 30));
        var settings = context.Config.Current;
        var configured = settings.Tool(tool);

        var maxParallel = configured.MaxParallel ?? defaultParallel;
        var timeoutMinutes = configured.TimeoutMinutes ?? defaultMinutes;

        if (context.Request.Tools.TryGetValue(tool, out var overrides))
        {
            if (overrides.MaxParallel is > 0 and var requestedParallel)
            {
                maxParallel = requestedParallel;
            }

            if (overrides.TimeoutMinutes is > 0 and var requestedMinutes)
            {
                timeoutMinutes = requestedMinutes;
            }
        }

        var retry = new RetryPolicy(settings.Retry.MaxAttempts, TimeSpan.FromSeconds(settings.Retry.BackoffSeconds));
        return new ToolLimits(maxParallel, TimeSpan.FromMinutes(timeoutMinutes), retry);
    }
}
