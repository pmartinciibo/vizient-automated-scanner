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

    /// <summary>
    /// What RepoClone is to do with a sources entry of engine.json, by its name (tools.RepoClone.sources.&lt;name&gt; in
    /// request.json). An entry not named there is cloned.
    /// </summary>
    public SourceState StateOf(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && Tools.TryGetValue(RepoCloneKey, out var repoClone)
        && repoClone.Sources is { } sources
        && sources.TryGetValue(name, out var overrides)
            ? overrides.State ?? SourceState.Clone
            : SourceState.Clone;

    public bool IsSourceEnabled(string? name) => StateOf(name) != SourceState.Disabled;

    private const string RepoCloneKey = "RepoClone";
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

    /// <summary>RepoClone only. By the name of a sources entry in engine.json: leave it out of this run, or only list it.</summary>
    public Dictionary<string, SourceRequestOverride>? Sources { get; init; }
}

/// <summary>What RepoClone does with one source in one run.</summary>
public enum SourceState
{
    /// <summary>Not used at all. Nothing is listed or cloned, and what the last scan knew of it is kept.</summary>
    Disabled,

    /// <summary>Its orgs and repos are listed and recorded, but nothing is cloned or updated on disk.</summary>
    ListOnly,

    /// <summary>Its repos are listed and every one is cloned or updated. The default.</summary>
    Clone
}

public sealed record SourceRequestOverride
{
    public SourceState? State { get; init; }

    /// <summary>
    /// Any member this record does not know. Kept so the request can be refused rather than half understood: an
    /// old "enabled" member would otherwise be ignored and the source cloned.
    /// </summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Unknown { get; init; }
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