using System.Text.Json;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;

namespace Vizient.AutomatedScanner.Core.State;

/// <summary>
/// Reads and writes FoundOrgAppRepo.json in the engine's state folder. Only this app uses the file; it is not a
/// run output and is not copied anywhere. The write keeps the previous version as a .bak.
/// </summary>
public sealed class FoundOrgAppRepoStore(string stateRoot)
{
    public const string FileName = "FoundOrgAppRepo.json";

    public string Path { get; } = System.IO.Path.Combine(stateRoot, FileName);

    private string BackupPath => Path + ".bak";

    /// <summary>
    /// The last saved state, or an empty one if there has never been a scan. If a file exists but cannot be read,
    /// this throws: treating it as empty would make every org, application and repo look new, and the next save
    /// would overwrite what was there.
    /// </summary>
    public async Task<FoundOrgAppRepoDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        Exception? unreadable = null;

        foreach (var candidate in new[] { Path, BackupPath })
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
