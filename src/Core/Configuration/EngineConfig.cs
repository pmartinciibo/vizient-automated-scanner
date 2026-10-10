using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;

namespace Vizient.AutomatedScanner.Core.Configuration;

/// <summary>Engine configuration with every path resolved to an absolute path.</summary>
public sealed record EngineConfig(
    string ConfigFile,
    string ReposRoot,
    string RunsRoot,
    string StateRoot,
    string TmpRoot,
    string LogsRoot,
    int StatusIntervalSeconds = 5,
    int CommandIntervalSeconds = 2)
{
    public const string FileName = "engine.json";

    /// <summary>Loads engine.json from the folder the executable runs from. It is never searched for.</summary>
    public static Task<EngineConfig> LoadAsync(CancellationToken cancellationToken = default) =>
        LoadAsync(Path.Combine(AppContext.BaseDirectory, FileName), cancellationToken);

    public static async Task<EngineConfig> LoadAsync(string configFile, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(configFile);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"{FileName} not found at '{fullPath}'. It must sit beside the executable.");
        }

        var document = await AtomicJsonFile.ReadAsync<EngineConfigDocument>(fullPath, cancellationToken);
        var baseDir = Path.GetDirectoryName(fullPath)!;
        var paths = document.Paths;

        if (document.StatusIntervalSeconds < 1 || document.CommandIntervalSeconds < 1)
        {
            throw new InvalidDataException($"{FileName}: statusIntervalSeconds and commandIntervalSeconds must be at least 1.");
        }

        return new EngineConfig(
            fullPath,
            Resolve(baseDir, paths.ReposRoot, nameof(paths.ReposRoot)),
            Resolve(baseDir, paths.Runs, nameof(paths.Runs)),
            Resolve(baseDir, paths.State, nameof(paths.State)),
            Resolve(baseDir, paths.Tmp, nameof(paths.Tmp)),
            Resolve(baseDir, paths.Logs, nameof(paths.Logs)),
            document.StatusIntervalSeconds,
            document.CommandIntervalSeconds);
    }

    /// <summary>Creates any folder the engine writes into.</summary>
    public void EnsureDirectories()
    {
        foreach (var dir in new[] { ReposRoot, RunsRoot, StateRoot, TmpRoot, LogsRoot })
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static string Resolve(string baseDir, string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"{FileName}: paths.{name} is required.");
        }

        return Path.GetFullPath(value, baseDir);
    }
}
