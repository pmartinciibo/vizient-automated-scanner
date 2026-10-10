using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;

namespace Vizient.AutomatedScanner.Core.IO;

/// <summary>
/// Writes the per-entity markers (design section 7.3). A marker is written only once the outcome is known,
/// and an entity never keeps two outcomes: writing one removes the others left by an earlier attempt.
/// </summary>
public sealed class MarkerStore(RunPaths paths)
{
    public Task WriteDoneAsync(string tool, string key, ToolDoneMarker marker, CancellationToken ct = default) =>
        WriteOutcomeAsync(paths.DoneMarker(tool, key), marker, [paths.FailedMarker(tool, key), paths.SkippedMarker(tool, key)], ct);

    public Task WriteFailedAsync(string tool, string key, ToolFailedMarker marker, CancellationToken ct = default) =>
        WriteOutcomeAsync(paths.FailedMarker(tool, key), marker, [paths.DoneMarker(tool, key), paths.SkippedMarker(tool, key)], ct);

    public Task WriteSkippedAsync(string tool, string key, ToolSkippedMarker marker, CancellationToken ct = default) =>
        WriteOutcomeAsync(paths.SkippedMarker(tool, key), marker, [paths.DoneMarker(tool, key), paths.FailedMarker(tool, key)], ct);

    public Task WriteImportDoneAsync(string tool, ImportDoneMarker marker, CancellationToken ct = default) =>
        AtomicJsonFile.WriteAsync(paths.ImportDoneMarker(tool), marker, ct);

    public bool IsDone(string tool, string key) => File.Exists(paths.DoneMarker(tool, key));

    public bool IsFailed(string tool, string key) => File.Exists(paths.FailedMarker(tool, key));

    private static async Task WriteOutcomeAsync<T>(string path, T marker, string[] superseded, CancellationToken ct)
    {
        await AtomicJsonFile.WriteAsync(path, marker, ct);
        foreach (var old in superseded)
        {
            File.Delete(old);
        }
    }
}
