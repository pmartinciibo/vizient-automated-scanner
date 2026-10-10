namespace Vizient.AutomatedScanner.Core.Models;

/// <summary>Per tool, kept in FoundOrgAppRepo.json.</summary>
public sealed record ToolInventoryState
{
    public DateTimeOffset? LastFullScanAt { get; init; }
    public bool ForceFull { get; init; }
}

public enum RepositoryStatus
{
    Active,
    Archived,
    Disabled,
    Deleted,
    Unreachable
}

/// <summary>The result of one tool's last clean scan of one repo, kept in FoundOrgAppRepo.json.</summary>
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
