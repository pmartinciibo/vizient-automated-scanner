namespace Vizient.AutomatedScanner.Core.Models;

public sealed record RunRequestDocument
{
    public int SchemaVersion { get; init; } = 3;
    public string RunId { get; init; } = string.Empty;
    public int Revision { get; init; } = 1;
    public DateTimeOffset RequestedAt { get; init; }
    public string RequestedBy { get; init; } = string.Empty;
    public ScanMode Mode { get; init; } = ScanMode.Normal;
    public Dictionary<string, ToolRequestOverride> Tools { get; init; } = new();
    public RequestScope Scope { get; init; } = new();
    public SkipOverrides Skip { get; init; } = new();
}

public enum ScanMode
{
    Normal,
    Full,
    Resume,
    RetryFailed
}

public sealed record ToolRequestOverride
{
    public bool? Enabled { get; init; }
    public int? MaxParallel { get; init; }
    public int? TimeoutMinutes { get; init; }
}

public sealed record RequestScope
{
    public List<string> IncludeRepos { get; init; } = new();
    public List<string> ExcludeRepos { get; init; } = new();
}

public sealed record SkipOverrides
{
    public int? CanaryPercent { get; init; }
}