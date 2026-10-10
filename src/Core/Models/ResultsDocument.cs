namespace Vizient.AutomatedScanner.Core.Models;

/// <summary>
/// The outcome of one invocation, written next to request.json when the engine ends, including when it
/// could not start. runDirectory is null when no run folder was created.
/// </summary>
public sealed record ResultsDocument
{
    public int SchemaVersion { get; init; } = 3;
    public string? RunId { get; init; }
    public string RequestFile { get; init; } = string.Empty;
    public string? RunDirectory { get; init; }
    public RunState State { get; init; } = RunState.Failed;
    public int ExitCode { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
    public string? Error { get; init; }
    public List<ToolOutcome> Tools { get; init; } = new();
    public RepoSummary RepoTotals { get; init; } = new();
    public List<OrgOutcome> Orgs { get; init; } = new();
}

public sealed record ToolOutcome
{
    public string Name { get; init; } = string.Empty;
    public ToolRunState Status { get; init; }
    public string? Message { get; init; }
}
