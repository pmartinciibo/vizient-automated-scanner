using System.Text.Json;

namespace Vizient.AutomatedScanner.Core.Models;

public sealed record CommandDocument
{
    public int SchemaVersion { get; init; } = 3;
    public string RunId { get; init; } = string.Empty;
    public List<CommandEntry> Commands { get; init; } = new();
}

public sealed record CommandEntry
{
    public long Seq { get; init; }
    public string Id { get; init; } = string.Empty;
    // Kept as text so an unknown type can be acknowledged as Rejected instead of failing the whole file.
    public string Type { get; init; } = string.Empty;
    public DateTimeOffset IssuedAt { get; init; }
    public string IssuedBy { get; init; } = string.Empty;
    public Dictionary<string, JsonElement> Args { get; init; } = new();
}
