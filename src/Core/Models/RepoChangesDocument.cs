namespace Vizient.AutomatedScanner.Core.Models;

public sealed record RepoChangesDocument
{
    public int SchemaVersion { get; init; } = 3;
    public string RunId { get; init; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; init; }
    public List<DiscoveryResult> Discovery { get; init; } = new();
    public RepositoryTotals Totals { get; init; } = new();
    public List<RepositoryChange> Changes { get; init; } = new();
    public ScanPlan ScanPlan { get; init; } = new();
}

public sealed record DiscoveryResult
{
    public string Source { get; init; } = string.Empty;
    public string Org { get; init; } = string.Empty;
    public bool Completed { get; init; }
    public int ReposFound { get; init; }
    public string? Error { get; init; }
}

public sealed record RepositoryTotals
{
    public int Known { get; init; }
    public int Active { get; init; }
    public int Archived { get; init; }
    public int Disabled { get; init; }
    public int Deleted { get; init; }
    public int Unreachable { get; init; }
    public int New { get; init; }
    public int Changed { get; init; }
}

public sealed record RepositoryChange
{
    public RepositoryChangeType Type { get; init; }
    public string Key { get; init; } = string.Empty;
    public string? Source { get; init; }
    public string? Org { get; init; }
    public string? Name { get; init; }
    public RepositoryStatus? Status { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public DateTimeOffset? DetectedAt { get; init; }
    public string? LastSeenRunId { get; init; }
    public string? Reason { get; init; }
}

public enum RepositoryChangeType
{
    New,
    StatusChanged,
    Renamed,
    Deleted,
    Reappeared,
    Unreachable
}

public sealed record ScanPlan
{
    public ScanMode Mode { get; init; } = ScanMode.Normal;
    public Dictionary<string, ToolScanPlan> PerTool { get; init; } = new();
    public List<string> ForcedFullTools { get; init; } = new();
}

public sealed record ToolScanPlan
{
    public int ToScan { get; init; }
    public int SkippedUnchanged { get; init; }
    public int Canaries { get; init; }
    public int SkippedStatus { get; init; }
}