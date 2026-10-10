using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.Tools;

/// <summary>Whether a tool is on for a run: the request wins over engine.json, which wins over the default of on.</summary>
public static class ToolEnablement
{
    /// <summary>Why the tool is off, or null if it is on.</summary>
    public static string? DisabledReason(RunRequestDocument request, ToolSettings? configured, string tool)
    {
        if (request.Tools.TryGetValue(tool, out var overrides) && overrides.Enabled is { } requested)
        {
            return requested ? null : "disabled in request.json";
        }

        return configured?.Enabled == false ? "disabled in engine.json" : null;
    }
}
