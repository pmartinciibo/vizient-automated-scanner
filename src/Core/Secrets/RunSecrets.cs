using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Processes;

namespace Vizient.AutomatedScanner.Core.Secrets;

/// <summary>
/// The secrets for one run. The file is read once, the first time a tool needs a secret, and held in memory. A tool
/// gets only the secrets in its own env map in engine.json, as environment variables on its child process, and
/// the engine's own environment variables that carry any tool's secret names are cleared from the child first.
/// </summary>
public sealed class RunSecrets(string path, EngineSettings settings, TimeProvider? time = null)
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private SecretStore? _store;

    /// <summary>Loads the file if it has not been. A file that cannot be read throws <see cref="SecretsFileException"/>.</summary>
    public async Task<SecretStore> StoreAsync(CancellationToken cancellationToken = default)
    {
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            return _store ??= await SecretStore.LoadAsync(path, cancellationToken);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// One secret's value, for a tool that uses it in the engine itself (an API client) rather than through a child
    /// process. Throws <see cref="SecretUnavailableException"/> if it is missing or expired. Never log or store it.
    /// </summary>
    public async Task<string> GetSecretAsync(string name, string neededBy, CancellationToken cancellationToken = default)
    {
        var store = await StoreAsync(cancellationToken);
        return store.GetRequired(name, DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime), neededBy);
    }

    /// <summary>
    /// Returns the spec with this tool's secrets set as environment variables (masked in anything captured) and
    /// every other tool's secret variable names cleared. Throws <see cref="SecretUnavailableException"/> if a secret
    /// the tool needs is missing or expired.
    /// </summary>
    public async Task<ProcessSpec> ApplyAsync(ProcessSpec spec, string tool, CancellationToken cancellationToken = default)
    {
        var store = await StoreAsync(cancellationToken);
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        var toolEnv = settings.Tool(tool).Env ?? new Dictionary<string, string>();

        var secretEnvironment = new Dictionary<string, string>(spec.SecretEnvironment, StringComparer.OrdinalIgnoreCase);
        foreach (var (variable, secretName) in toolEnv)
        {
            secretEnvironment[variable] = store.GetRequired(secretName, today, $"{tool} ({variable})");
        }

        // Variable names are not case-sensitive on Windows, so neither is the clearing.
        var remove = settings.Tools.Values
            .SelectMany(t => (IEnumerable<string>?)t.Env?.Keys ?? [])
            .Concat(spec.RemoveEnvironment)
            .Where(name => !secretEnvironment.ContainsKey(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return spec with { SecretEnvironment = secretEnvironment, RemoveEnvironment = remove };
    }
}
