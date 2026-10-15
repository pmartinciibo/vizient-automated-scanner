namespace Vizient.AutomatedScanner.Core.Models;

public sealed record ToolDoneMarker
{
    public int SchemaVersion { get; init; } = 3;
    public string Sha { get; init; } = string.Empty;
    public string ScanMode { get; init; } = string.Empty;
    public string ToolVersion { get; init; } = string.Empty;
    public string RulesVersion { get; init; } = string.Empty;
    public string ConfigHash { get; init; } = string.Empty;
    public string? FallbackFrom { get; init; }
    public string? FailureCode { get; init; }
    public string? DiagnosticFile { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
    public int FindingCount { get; init; }

    /// <summary>The findings by normalized severity, for a scanner that reads them. Null where the tool does not.</summary>
    public SeverityCounts? Severities { get; init; }

    public bool Canary { get; init; }
}

public sealed record ToolFailedMarker
{
    public int SchemaVersion { get; init; } = 3;
    public string Category { get; init; } = string.Empty;
    public string? FailureCode { get; init; }
    public string? DiagnosticFile { get; init; }
    public int Attempts { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Sha { get; init; }
}

public sealed record ToolSkippedMarker
{
    public int SchemaVersion { get; init; } = 3;
    public string Reason { get; init; } = string.Empty;
    public string? Sha { get; init; }
    public SkipKey? Key { get; init; }
    public DateTimeOffset? LastScannedAt { get; init; }
}

public sealed record SkipKey
{
    public string ScanMode { get; init; } = string.Empty;
    public string ToolVersion { get; init; } = string.Empty;
    public string RulesVersion { get; init; } = string.Empty;
    public string ConfigHash { get; init; } = string.Empty;
}

public sealed record ImportDoneMarker
{
    public int SchemaVersion { get; init; } = 3;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
    public int Entities { get; init; }
    public long Findings { get; init; }
    public int FailedEntities { get; init; }
}