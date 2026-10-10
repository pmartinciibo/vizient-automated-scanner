namespace Vizient.AutomatedScanner.Core.Models;

public sealed record RepositoryInventoryDocument
{
    public int SchemaVersion { get; init; } = 3;
    public DateTimeOffset UpdatedAt { get; init; }
    public Dictionary<string, ToolInventoryState> Tools { get; init; } = new();
    public List<RepositoryRecord> Repos { get; init; } = new();
}

public sealed record ToolInventoryState
{
    public DateTimeOffset? LastFullScanAt { get; init; }
    public bool ForceFull { get; init; }
}

public sealed record RepositoryRecord
{
    public string Key { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Org { get; init; } = string.Empty;
    public string? Project { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public RepositoryStatus Status { get; init; } = RepositoryStatus.Active;
    public DateTimeOffset? StatusChangedAt { get; init; }
    public string FirstSeenRunId { get; init; } = string.Empty;
    public string LastSeenRunId { get; init; } = string.Empty;
    public string? LocalPath { get; init; }
    public string? LastCloneResult { get; init; }
    public Dictionary<string, RepositoryScanState> Scan { get; init; } = new();
}

public enum RepositoryStatus
{
    Active,
    Archived,
    Disabled,
    Deleted,
    Unreachable
}

public sealed record RepositoryScanState
{
    public string Sha { get; init; } = string.Empty;
    public string ScanMode { get; init; } = string.Empty;
    public string ToolVersion { get; init; } = string.Empty;
    public string RulesVersion { get; init; } = string.Empty;
    public string ConfigHash { get; init; } = string.Empty;
    public DateTimeOffset ScannedAt { get; init; }
    public int FindingCount { get; init; }
}