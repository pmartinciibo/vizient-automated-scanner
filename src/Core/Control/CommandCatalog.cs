namespace Vizient.AutomatedScanner.Core.Control;

/// <summary>The commands the engine understands, described once so the command.json template cannot drift from them.</summary>
public static class CommandCatalog
{
    public sealed record CommandInfo(string Type, string Summary, string Args);

    public static readonly IReadOnlyList<CommandInfo> All =
    [
        new("Pause", "Finish repos in progress, start no new ones, and hold.", "none"),
        new("Resume", "Continue after a Pause.", "none"),
        new("Stop", "Finish repos in progress, then end the run as Stopped.", "none"),
        new("Abort", "Stop now: kill running tools and end the run as Stopped.", "none"),
        new("SetConcurrency", "Change how many repos a tool works on at once.",
            "tool (a tool name, or \"all\"), maxParallel (a whole number, 1 to 256)"),
        new("DisableTool", "Start no new work for a tool.", "tool, reason (optional)"),
        new("RetryFailed", "Requeue the repos that failed in this run.", "tool (optional, leave out for every tool)"),
        new("SkipRepo", "Drop one repo from all the work that remains.", "repoKey, for example \"gh-91234\"")
    ];
}
