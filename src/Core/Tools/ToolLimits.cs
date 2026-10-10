using Vizient.AutomatedScanner.Core.Processes;

namespace Vizient.AutomatedScanner.Core.Tools;

/// <summary>
/// The limits a tool's repos run under. These are the design's defaults (section 9.1 and the engine.json
/// example), with the per-run overrides from request.json applied. They move into engine.json once its
/// tool settings exist.
/// </summary>
public sealed record ToolLimits(int MaxParallel, TimeSpan Timeout, RetryPolicy Retry)
{
    public static readonly RetryPolicy DefaultRetry = new(MaxAttempts: 2, Backoff: TimeSpan.FromSeconds(60));

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
        var (maxParallel, timeoutMinutes) = Defaults.GetValueOrDefault(tool, (1, 30));

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

        return new ToolLimits(maxParallel, TimeSpan.FromMinutes(timeoutMinutes), DefaultRetry);
    }
}
