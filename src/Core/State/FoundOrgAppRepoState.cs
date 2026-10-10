using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.State;

/// <summary>
/// FoundOrgAppRepo.json while a run is going. Loaded once and shared by RepoClone and the scanners running in
/// parallel. RepoClone replaces the list of orgs, applications and repos. The scanners add what each clean scan
/// found and the per-tool bookkeeping for full rescans. <see cref="SaveAsync"/> writes it all back. Safe to call
/// from several threads.
/// </summary>
public sealed class FoundOrgAppRepoState(FoundOrgAppRepoStore store, string runId)
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly HashSet<string> _forcedThisRun = new(StringComparer.Ordinal);
    private Dictionary<string, StoredRepo> _repos = new(StringComparer.Ordinal);
    private Dictionary<string, ToolInventoryState> _tools = new(StringComparer.Ordinal);
    private bool _loaded;

    /// <summary>The state as it was on disk when the run started, before anything in this run changed it.</summary>
    public FoundOrgAppRepoDocument LoadedSnapshot { get; private set; } = new();

    /// <summary>Whether there are any repos in the state now.</summary>
    public bool HasAnyRepo
    {
        get { lock (_gate) { return _repos.Count > 0; } }
    }

    /// <summary>Reads the file once. Throws if it exists but cannot be read, rather than treating it as empty.</summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_loaded)
            {
                return;
            }

            var document = await store.LoadAsync(cancellationToken);
            lock (_gate)
            {
                Apply(document);
                LoadedSnapshot = document;
                _loaded = true;
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// RepoClone's result: replace the repos with what the sources listed this time. See
    /// <see cref="FoundSnapshotBuilder.Build"/> for what is kept and dropped. Not written until <see cref="SaveAsync"/>.
    /// </summary>
    public void ReplaceSnapshot(
        IEnumerable<RepoOutcome> reposThisScan,
        IEnumerable<(string Source, string Org)> incompleteOrgs,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            Apply(FoundSnapshotBuilder.Build(runId, now, reposThisScan, ToDocument(now), incompleteOrgs));
        }
    }

    /// <summary>The last clean scan of this repo by this tool, or null if there has not been one.</summary>
    public RepositoryScanState? Get(string tool, string repoKey)
    {
        lock (_gate)
        {
            return _repos.TryGetValue(repoKey, out var stored) ? stored.Scan.GetValueOrDefault(tool) : null;
        }
    }

    public ToolInventoryState ToolState(string tool)
    {
        lock (_gate)
        {
            return _tools.GetValueOrDefault(tool) ?? new ToolInventoryState();
        }
    }

    /// <summary>
    /// Records a clean scan. Call only once the repo's done marker is written. A repo not yet in the state is
    /// added, so the file never holds scan state for a repo it does not list.
    /// </summary>
    public void Record(string tool, RepoOutcome repo, RepositoryScanState scan)
    {
        lock (_gate)
        {
            if (!_repos.TryGetValue(repo.Key, out var stored))
            {
                _repos[repo.Key] = stored = new StoredRepo(
                    repo with { ExistedRemotely = true },
                    new Dictionary<string, RepositoryScanState>(StringComparer.Ordinal));
            }

            stored.Scan[tool] = scan;
        }
    }

    /// <summary>The next run must scan this tool's repos all over again.</summary>
    public void SetForceFull(string tool)
    {
        lock (_gate)
        {
            _tools[tool] = ToolState(tool) with { ForceFull = true };
            _forcedThisRun.Add(tool);
        }
    }

    /// <summary>
    /// The tool has tried every repo it could. Notes the time and clears a forced rescan, unless one was asked for
    /// during this very run, which the next run still has to carry out.
    /// </summary>
    public void CompleteFullPass(string tool, DateTimeOffset at)
    {
        lock (_gate)
        {
            _tools[tool] = new ToolInventoryState
            {
                LastFullScanAt = at,
                ForceFull = _forcedThisRun.Contains(tool)
            };
        }
    }

    /// <summary>Writes the state to disk. RepoClone and each scanner call this when they finish, even if cut short.</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _saveLock.WaitAsync(cancellationToken);
        try
        {
            FoundOrgAppRepoDocument snapshot;
            lock (_gate)
            {
                snapshot = ToDocument(DateTimeOffset.UtcNow);
            }

            await store.SaveAsync(snapshot, cancellationToken);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    // Callers hold _gate.
    private void Apply(FoundOrgAppRepoDocument document)
    {
        _repos = document.ToStoredRepos().ToDictionary(s => s.Repo.Key, StringComparer.Ordinal);
        _tools = new Dictionary<string, ToolInventoryState>(document.Tools, StringComparer.Ordinal);
    }

    // Callers hold _gate.
    private FoundOrgAppRepoDocument ToDocument(DateTimeOffset now) =>
        FoundSnapshotBuilder.ToDocument(runId, now, _tools, _repos.Values);
}
