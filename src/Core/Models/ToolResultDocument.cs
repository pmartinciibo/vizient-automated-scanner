using System.Text.Json.Serialization;

namespace Vizient.AutomatedScanner.Core.Models;

/// <summary>
/// The detail of one step of one run, written to results\&lt;Tool&gt;.json in the run folder when the run ends.
/// results.json carries only the stats; the per-item outcomes (and, for RepoClone, the org, application and repo
/// lists) are here. The raw tool output stays in results\&lt;Tool&gt;\&lt;key&gt;.json.
/// </summary>
public sealed record ToolResultDocument
{
    public int SchemaVersion { get; init; } = 3;
    public string RunId { get; init; } = string.Empty;
    public string Tool { get; init; } = string.Empty;
    public ToolOutcome Summary { get; init; } = new();

    /// <summary>Rapid7 and CheckMarx: the import's own totals, from its done marker.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImportDoneMarker? Import { get; init; }

    /// <summary>RepoClone: what the run learned about the repos, grouped org, application, repo.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RepoSummary? RepoTotals { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OrgOutcome>? Orgs { get; init; }

    /// <summary>One entry per repo (or imported entity) that has a marker in the run folder.</summary>
    public List<ToolItemResult> Items { get; init; } = new();
}

public sealed record ToolItemResult
{
    public string Key { get; init; } = string.Empty;

    /// <summary>An imported application's name.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    /// <summary>An imported application's record counts by list (scans, vulnerabilities, projects, results and so on).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, int>? Counts { get; init; }

    /// <summary>Done, Failed, Skipped, Imported (an application in an import's file), or Unreadable when the marker or file could not be read.</summary>
    public string Outcome { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolDoneMarker? Done { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolFailedMarker? Failed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolSkippedMarker? Skipped { get; init; }
}
