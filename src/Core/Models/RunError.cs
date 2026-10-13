using System.Text.Json.Serialization;

namespace Vizient.AutomatedScanner.Core.Models;

/// <summary>One error condition the run met, whether or not the run carried on. results.json lists them all.</summary>
public sealed record RunError
{
    /// <summary>Run, Tool, Org, Repo, Item or Secrets: what the error was about.</summary>
    public string Scope { get; init; } = string.Empty;

    public DateTimeOffset At { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tool { get; init; }

    /// <summary>The tool's own word for the kind of failure (AuthFailed, CloneFailed, Timeout, ToolError and so on).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; init; }

    public string Message { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Org { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Project { get; init; }

    /// <summary>The repo's stable key (ado-..., gh-...), or for an import, the item that failed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Key { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Repo { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Attempts { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha { get; init; }
}

public static class RunErrorScope
{
    public const string Run = "Run";
    public const string Tool = "Tool";
    public const string Org = "Org";
    public const string Repo = "Repo";
    public const string Item = "Item";
    public const string Secrets = "Secrets";
}
