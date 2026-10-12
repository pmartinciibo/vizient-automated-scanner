namespace Vizient.AutomatedScanner.Core.Models;

public sealed record EngineConfigDocument
{
    public int SchemaVersion { get; init; } = 3;
    public EnginePathsConfig Paths { get; init; } = new();
    public int StatusIntervalSeconds { get; init; } = 2;
    public int CommandIntervalSeconds { get; init; } = 2;

    // The settings below sit at the top level of engine.json. They are kept together as EngineSettings once loaded.
    public List<SourceSettings> Sources { get; init; } = new();
    public bool ScanArchived { get; init; }
    public SkipSettings Skip { get; init; } = new();
    public RetrySettings Retry { get; init; } = new();
    public SecretsSettings Secrets { get; init; } = new();
    public Dictionary<string, ToolSettings> Tools { get; init; } = new();

    public EngineSettings ToSettings() => new()
    {
        Sources = Sources,
        ScanArchived = ScanArchived,
        Skip = Skip,
        Retry = Retry,
        Secrets = Secrets,
        Tools = Tools
    };
}

public sealed record EnginePathsConfig
{
    public string ReposRoot { get; init; } = string.Empty;
    public string Runs { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Tmp { get; init; } = string.Empty;
    public string Logs { get; init; } = string.Empty;

    /// <summary>Optional. The shared exclusions file every tool uses. It is part of each tool config hash.</summary>
    public string? Exclusions { get; init; }
}
