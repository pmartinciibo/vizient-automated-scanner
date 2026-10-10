namespace Vizient.AutomatedScanner.Core.Models;

public sealed record EngineConfigDocument
{
    public int SchemaVersion { get; init; } = 3;
    public EnginePathsConfig Paths { get; init; } = new();
}

public sealed record EnginePathsConfig
{
    public string ReposRoot { get; init; } = string.Empty;
    public string Runs { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Tmp { get; init; } = string.Empty;
    public string Logs { get; init; } = string.Empty;
}
