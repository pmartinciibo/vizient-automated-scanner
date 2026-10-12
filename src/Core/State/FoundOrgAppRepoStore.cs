using System.Text.Json;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;

namespace Vizient.AutomatedScanner.Core.State;

/// <summary>
/// Reads and writes FoundOrgAppRepo.json. The one that counts is in the engine's state folder, and its write keeps the
/// previous version as a .bak. When a runs folder is given, every save also refreshes a "latest" copy directly in it and a
/// copy in the run's own folder, so the snapshot a run worked from stays beside its other output. A new run starts from the
/// latest copy and a continuing one (Resume, RetryFailed) from its own, each falling back to the state folder's.
/// </summary>
public sealed class FoundOrgAppRepoStore(string stateRoot, string? runsRoot = null, string? runId = null, bool continuingRun = false)
{
    public const string FileName = "FoundOrgAppRepo.json";

    public string Path { get; } = System.IO.Path.Combine(stateRoot, FileName);

    /// <summary>The newest snapshot, directly in the runs folder. Null when no runs folder was given.</summary>
    public string? LatestPath { get; } = runsRoot is null ? null : System.IO.Path.Combine(runsRoot, FileName);

    /// <summary>This run's own copy, in its run folder. Null when there is no runs folder or run.</summary>
    public string? RunPath { get; } = runsRoot is null || runId is null ? null : System.IO.Path.Combine(runsRoot, runId, FileName);

    private string BackupPath => Path + ".bak";

    /// <summary>
    /// The last saved state, or an empty one if there has never been a scan. If a file exists but cannot be read,
    /// this throws: treating it as empty would make every org, application and repo look new, and the next save
    /// would overwrite what was there.
    /// </summary>
    public async Task<FoundOrgAppRepoDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        Exception? unreadable = null;

        var preferred = continuingRun ? RunPath : LatestPath;
        foreach (var candidate in new[] { preferred, Path, BackupPath }.OfType<string>())
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                return await AtomicJsonFile.ReadAsync<FoundOrgAppRepoDocument>(candidate, cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                unreadable ??= ex;
            }
        }

        return unreadable is null
            ? new FoundOrgAppRepoDocument()
            : throw new InvalidDataException(
                $"{FileName} in '{stateRoot}' cannot be read, and neither can its backup: {unreadable.Message}");
    }

    public async Task SaveAsync(FoundOrgAppRepoDocument snapshot, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(stateRoot);

        // Keep the last good version. Never replace the backup with a file that cannot be read.
        if (File.Exists(Path) && await IsReadableAsync(Path, cancellationToken))
        {
            await AtomicJsonFile.CopyAsync(Path, BackupPath, cancellationToken);
        }

        await AtomicJsonFile.WriteAsync(Path, snapshot, cancellationToken);
        await WriteCopiesAsync(snapshot, includeLatest: true, cancellationToken);
    }

    /// <summary>
    /// Gives a new run its own copy of the snapshot it starts from, so the run folder has it even if the run never
    /// reaches a save. An existing copy (a continuing run's) is left as it is.
    /// </summary>
    public async Task SeedRunCopyAsync(FoundOrgAppRepoDocument snapshot, CancellationToken cancellationToken = default)
    {
        if (RunPath is not null && !File.Exists(RunPath))
        {
            await WriteCopiesAsync(snapshot, includeLatest: false, cancellationToken);
        }
    }

    // The copies are for people and for the next run's start. The state folder's file is the real one, and it is already
    // written, so a copy that cannot be written (a reader has it open) is skipped, not an error.
    private async Task WriteCopiesAsync(FoundOrgAppRepoDocument snapshot, bool includeLatest, CancellationToken cancellationToken)
    {
        foreach (var copy in new[] { includeLatest ? LatestPath : null, RunPath }.OfType<string>())
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(copy)!);
                await AtomicJsonFile.WriteAsync(copy, snapshot, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Next save tries again.
            }
        }
    }

    private static async Task<bool> IsReadableAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await AtomicJsonFile.ReadAsync<FoundOrgAppRepoDocument>(path, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return false;
        }
    }
}
