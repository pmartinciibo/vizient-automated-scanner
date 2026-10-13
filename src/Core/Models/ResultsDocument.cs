using System.Text.Json.Serialization;

namespace Vizient.AutomatedScanner.Core.Models;

/// <summary>
/// The outcome of one invocation, written next to request.json when the engine ends, including when it
/// could not start. runDirectory is null when no run folder was created. This is the high-level summary only:
/// the detail of each step is in results\&lt;Tool&gt;.json in the run folder.
/// </summary>
public sealed record ResultsDocument
{
    public int SchemaVersion { get; init; } = 4;
    public string? RunId { get; init; }
    public string RequestFile { get; init; } = string.Empty;
    public string? RunDirectory { get; init; }
    public RunState State { get; init; } = RunState.Failed;
    public int ExitCode { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
    public string? Error { get; init; }

    /// <summary>The eight steps, each with its stats and any error.</summary>
    public List<ToolOutcome> Tools { get; init; } = new();

    /// <summary>The scanners together, counted in repos, as status.json shows them.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolOutcome? Scanning { get; init; }

    public RepoSummary RepoTotals { get; init; } = new();

    /// <summary>How many error conditions the run met. The same as the length of <see cref="Errors"/>.</summary>
    public int ErrorCount { get; init; }

    /// <summary>
    /// Every error condition the run met, oldest first: tools that failed, orgs that could not be listed, repos that
    /// failed (clone or scan), imports that lost an item, secrets that were missing or expired.
    /// </summary>
    public List<RunError> Errors { get; init; } = new();
}

public sealed record ToolOutcome
{
    public string Name { get; init; } = string.Empty;
    public ToolRunState Status { get; init; }

    /// <summary>The error or note for the step: why it failed, was disabled, or what it was last doing.</summary>
    public string? Message { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? EndedAt { get; init; }

    public string ElapsedTime { get; init; } = "00:00:00";
    public int Total { get; init; }
    public int Done { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public int Canaries { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SeverityCounts? Severities { get; init; }

    /// <summary>Where the step's detail is, relative to the run folder. Null for the scanners-together entry.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResultFile { get; init; }

    public static ToolOutcome From(ToolProgress step, string? resultFile = null) => new()
    {
        Name = step.Name,
        Status = step.Status,
        Message = step.Message,
        StartedAt = step.StartedAt,
        EndedAt = step.EndedAt,
        ElapsedTime = step.ElapsedTime,
        Total = step.Total,
        Done = step.Done,
        Failed = step.Failed,
        Skipped = step.Skipped,
        Canaries = step.Canaries,
        Severities = step.Severities,
        ResultFile = resultFile
    };
}
