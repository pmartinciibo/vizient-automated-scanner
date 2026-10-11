using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Core.RepoClone;

/// <summary>
/// Turns a repo's mirror into a temporary working copy the scanners can read: a local <c>git clone</c> of the mirror
/// with the default branch checked out, under the tmp folder. On the same drive git hardlinks the objects, so the
/// history costs almost no extra space and only the files themselves are written. Dispose the result to delete
/// the copy; do that before moving to the next repo. A copy left by a crashed run is replaced.
/// </summary>
public sealed class RepoExpander(RunContext context)
{
    public const string FolderName = "expanded";

    /// <summary>Makes the working copy. Throws <see cref="InvalidOperationException"/> if there is no mirror or git fails.</summary>
    public async Task<ExpandedRepo> ExpandAsync(RepoOutcome repo, CancellationToken cancellationToken)
    {
        if (!RepoSynchronizer.IsMirror(repo.LocalPath))
        {
            throw new InvalidOperationException($"{RepoSynchronizer.Describe(repo)} has no mirror to expand ({repo.LocalPath ?? "no local path"}).");
        }

        var target = Path.Combine(context.Config.TmpRoot, FolderName, context.Paths.RunId, repo.Key);
        if (Directory.Exists(target))
        {
            DeleteDirectory(target);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var settings = context.Config.Current.Tool(ToolNames.RepoClone);
        var git = new GitRunner(
            context,
            ToolLimits.For(context, ToolNames.RepoClone),
            string.IsNullOrWhiteSpace(settings.GitPath) ? "git" : settings.GitPath,
            settings.LongPaths ?? true);

        var clone = await git.LocalAsync(null, ["clone", "--quiet", repo.LocalPath!, target], cancellationToken, git.Timeout);
        if (!clone.Succeeded)
        {
            // Whatever a failed clone left behind goes too, so a retry starts clean.
            TryDelete(target);
            throw new InvalidOperationException($"Could not expand {RepoSynchronizer.Describe(repo)}: {clone.Message} {clone.StdErr.Trim()}".Trim());
        }

        var head = await git.LocalAsync(target, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken);
        return new ExpandedRepo(target, head.Succeeded ? head.StdOut.Trim() : null);
    }

    /// <summary>Removes a folder git made, including the read-only files in .git. Retries briefly: a scanner that was just killed can still hold a file.</summary>
    internal static void DeleteDirectory(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(200 * attempt);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                DeleteDirectory(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next expansion of this repo tries again.
        }
    }
}

/// <summary>A temporary working copy of a repo. Disposing it deletes the folder.</summary>
public sealed class ExpandedRepo(string path, string? headSha) : IAsyncDisposable
{
    public string Path { get; } = path;

    /// <summary>The commit checked out, or null for a repo with no commits.</summary>
    public string? HeadSha { get; } = headSha;

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(Path))
        {
            RepoExpander.DeleteDirectory(Path);
        }

        return ValueTask.CompletedTask;
    }
}
