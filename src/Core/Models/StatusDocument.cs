using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vizient.AutomatedScanner.Core.Models;

public sealed record StatusDocument
{
    public int SchemaVersion { get; init; } = 3;
    public string RunId { get; init; } = string.Empty;
    public int Revision { get; init; }
    public int Attempt { get; init; } = 1;
    public RunState State { get; init; } = RunState.Starting;
    public bool Paused { get; init; }
    public int Pid { get; init; }
    public ScanMode Mode { get; init; } = ScanMode.Normal;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public RunPhase Phase { get; init; } = RunPhase.Preflight;
    public ProgressSummary Overall { get; init; } = new();
    public List<ToolProgress> Steps { get; init; } = new();
    public Dictionary<string, EffectiveToolSettings> Effective { get; init; } = new();
    public List<StatusAlert> Alerts { get; init; } = new();
    public long LastCommandSeq { get; init; }
    public List<CommandAcknowledgement> CommandAcks { get; init; } = new();
}

public enum RunState
{
    Starting,
    Running,
    Pausing,
    Paused,
    Stopping,
    Completed,
    CompletedWithFlags,
    Failed,
    Stopped
}

[JsonConverter(typeof(RunPhaseJsonConverter))]
public enum RunPhase
{
    Preflight,
    ImportAndClone,
    Scan,
    Finalize
}

public sealed class RunPhaseJsonConverter : JsonConverter<RunPhase>
{
    public override RunPhase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetString() switch
        {
            "Preflight" => RunPhase.Preflight,
            "Import+Clone" => RunPhase.ImportAndClone,
            "Scan" => RunPhase.Scan,
            "Finalize" => RunPhase.Finalize,
            _ => throw new JsonException("Unknown run phase.")
        };
    }

    public override void Write(Utf8JsonWriter writer, RunPhase value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            RunPhase.Preflight => "Preflight",
            RunPhase.ImportAndClone => "Import+Clone",
            RunPhase.Scan => "Scan",
            RunPhase.Finalize => "Finalize",
            _ => throw new JsonException("Unknown run phase.")
        });
    }
}

public enum ToolRunState
{
    Pending,
    Running,
    Completed,
    CompletedWithFlags,
    Failed,
    Disabled
}

public sealed record ProgressSummary
{
    public int Percent { get; init; }
    public int StepsCompleted { get; init; }
    public int StepsTotal { get; init; }
}

public sealed record ToolProgress
{
    public string Name { get; init; } = string.Empty;
    public ToolRunState Status { get; init; } = ToolRunState.Pending;
    public int Done { get; init; }
    public int Total { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public int Canaries { get; init; }
    public int Running { get; init; }

    /// <summary>When the step started and, once every part of it has ended, when it ended. Null before it starts. Filled in when the status is published.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>"hh:mm:ss": time since the step started while it runs, the total once it has ended. Filled in when the status is published.</summary>
    public string ElapsedTime { get; init; } = "00:00:00";

    /// <summary>0 to 100: items handled out of those to handle (skipped ones are not counted). 100 once the step has ended. Filled in when the status is published.</summary>
    public int PercentComplete { get; init; }

    /// <summary>"hh:mm:ss" left at the pace so far, "--:--:--" while unknown, zero once the step has ended. Filled in when the status is published.</summary>
    public string EstimatedRemainingTime { get; init; } = "00:00:00";

    /// <summary>Findings so far by normalized severity, for a scanner. Null for a step that has none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SeverityCounts? Severities { get; init; }

    /// <summary>On the "Scanning" step only: each scanner in it, with its own status and severity counts.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ScannerProgress>? Scanners { get; init; }

    /// <summary>On the "RepoClone" step only: each source entry in engine.json, with whether it is on and how its repos are going.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SourceProgress>? Sources { get; init; }

    public string? Message { get; init; }
}

/// <summary>One sources entry of engine.json as RepoClone sees it this run.</summary>
public sealed record SourceProgress
{
    /// <summary>The entry's name (what request.json calls it).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>ADO or GitHub.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Clone, ListOnly or Disabled, from request.json.</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>False for a Disabled entry: nothing is listed or cloned for it.</summary>
    public bool Enabled { get; init; }

    /// <summary>Repos of this entry the last scan knew.</summary>
    public int KnownRepos { get; init; }

    /// <summary>Repos found by listing this run. Zero until the listing has got to the entry.</summary>
    public int Found { get; init; }

    /// <summary>Repos to clone or update this run (found, less those skipped).</summary>
    public int Total { get; init; }

    public int Done { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
}

public sealed record ScannerProgress
{
    public string Name { get; init; } = string.Empty;
    public ToolRunState Status { get; init; } = ToolRunState.Pending;
    public SeverityCounts Severities { get; init; } = new();
}

public sealed record EffectiveToolSettings
{
    public int MaxParallel { get; init; }
}

public sealed record StatusAlert
{
    public string Type { get; init; } = string.Empty;
    public string? Tool { get; init; }
    public string? Repo { get; init; }
    public string? Name { get; init; }
    public string? ExpiresAt { get; init; }
    public int? Was { get; init; }
    public int? Now { get; init; }
    public DateTimeOffset At { get; init; }
}

public sealed record CommandAcknowledgement
{
    public long Seq { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTimeOffset At { get; init; }
    public string? Message { get; init; }
}