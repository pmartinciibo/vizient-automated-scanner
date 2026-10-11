using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Processes;
using Vizient.AutomatedScanner.Core.Work;

namespace Vizient.AutomatedScanner.Core.RepoClone;

/// <summary>
/// Keeps one bare mirror (<c>git clone --mirror</c>) of a repo up to date: every branch, tag and other ref, no files.
/// What the clone scripts proved is kept: a half-made clone is removed and made again, an existing clone is
/// updated with <c>remote update --prune</c>, and a failed update leaves the old clone where it is. Nothing is
/// deleted that is not a broken git folder, so a working clone or any other content at the path is refused rather
/// than overwritten. The scanners get files from <see cref="RepoExpander"/>, which makes a temporary working copy.
/// </summary>
internal sealed partial class RepoSynchronizer(
    RunContext context,
    GitRunner git,
    IReadOnlyDictionary<string, DiscoveredRepo> repos,
    IReadOnlyDictionary<string, IGitCredential> credentials,
    TimeProvider time)
{
    [GeneratedRegex("Authentication failed|Access denied|could not read Username|Invalid username or password|returned error: 40[13]|TF4008|Repository not found|HTTP/?\\s?40[13]", RegexOptions.IgnoreCase)]
    private static partial Regex AuthFailure();

    private enum Kind { Missing, Valid, Corrupt, Foreign }

    public static string CredentialKey(string source, string org) => $"{source}|{org}";

    /// <summary>A bare repo has HEAD and objects at the top of its folder. A cheap look, with no git call.</summary>
    public static bool IsMirror(string? path) =>
        path is not null && File.Exists(Path.Combine(path, "HEAD")) && Directory.Exists(Path.Combine(path, "objects"));

    public async Task<RepoWorkResult> SyncAsync(RepoWorkContext work, CancellationToken cancellationToken)
    {
        var outcome = work.Repo;
        var repo = repos[outcome.Key];
        var path = outcome.LocalPath!;
        var started = time.GetUtcNow();

        GitAuth auth;
        try
        {
            auth = await credentials[CredentialKey(repo.Source, repo.Org)].GetAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(outcome, "AuthFailed", ex.Message, 1);
        }

        var (kind, problem) = await InspectAsync(path, cancellationToken);
        if (kind == Kind.Foreign)
        {
            return Fail(outcome, "CloneFailed", problem!, 1);
        }

        if (kind == Kind.Corrupt)
        {
            RepoExpander.DeleteDirectory(path);
            kind = Kind.Missing;
        }

        var existedLocally = kind == Kind.Valid;
        var cloned = false;
        string? before = null;

        if (existedLocally)
        {
            before = await RefsFingerprintAsync(path, cancellationToken);

            // The remote can have moved (an org or repo rename), so point origin at where the source says it is now.
            await git.LocalAsync(path, ["remote", "set-url", "origin", repo.CloneUrl], cancellationToken);

            // Always updated, never skipped on an unchanged default branch: the mirror holds every branch, and any of
            // them can have moved. When nothing changed this is a cheap round trip.
            var update = await git.RemoteAsync(path, auth, ["remote", "update", "--prune"], cancellationToken);
            if (!update.Succeeded)
            {
                return Fail(outcome, update, "update");
            }
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var clone = await git.RemoteAsync(null, auth, ["clone", "--mirror", "--quiet", repo.CloneUrl, path], cancellationToken);
            if (!clone.Succeeded)
            {
                return Fail(outcome, clone, "clone");
            }

            cloned = true;
        }

        // The tip of the default branch. Null for a repo with no commits yet: nothing to scan, and not a failure.
        var head = await HeadAsync(path, cancellationToken);
        var after = await RefsFingerprintAsync(path, cancellationToken);

        context.Repos.Record(outcome with
        {
            ExistedLocally = existedLocally,
            UpdatedLocally = cloned || before != after,
            HeadSha = head
        });

        return RepoWorkResult.Done(new ToolDoneMarker { Sha = head ?? string.Empty, StartedAt = started, EndedAt = time.GetUtcNow() });
    }

    private async Task<(Kind Kind, string? Problem)> InspectAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            return (Kind.Missing, null);
        }

        // An empty folder (a clone that was cut off before it wrote anything) can be cloned into as it is.
        if (!Directory.EnumerateFileSystemEntries(path).Any())
        {
            return (Kind.Missing, null);
        }

        var bare = await git.LocalAsync(path, ["rev-parse", "--is-bare-repository"], cancellationToken);
        if (bare.Succeeded)
        {
            return bare.StdOut.Trim() == "true" && IsMirror(path)
                ? (Kind.Valid, null)
                : (Kind.Foreign, $"'{path}' holds a working clone or is not a mirror. It was left alone.");
        }

        // Only a folder that is plainly git's own internals, and that git says is not a repository, is a broken
        // clone that may be removed. Anything else (git missing, access denied, someone's files) must be kept.
        var looksLikeGit = File.Exists(Path.Combine(path, "HEAD")) || Directory.Exists(Path.Combine(path, "objects"));
        return looksLikeGit && bare.StdErr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
            ? (Kind.Corrupt, null)
            : (Kind.Foreign, $"'{path}' exists but is not a git mirror ({bare.Message}). It was left alone.");
    }

    private async Task<string?> HeadAsync(string path, CancellationToken cancellationToken)
    {
        var result = await git.LocalAsync(path, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken);
        var sha = result.StdOut.Trim();
        return result.Succeeded && sha.Length >= 7 ? sha : null;
    }

    // Every ref and the commit it points at, boiled down to one value. Different before and after means something
    // changed anywhere in the repo, not only on the default branch.
    private async Task<string?> RefsFingerprintAsync(string path, CancellationToken cancellationToken)
    {
        var result = await git.LocalAsync(path, ["for-each-ref", "--format=%(objectname) %(refname)"], cancellationToken);
        return result.Succeeded ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.StdOut))) : null;
    }

    private RepoWorkResult Fail(RepoOutcome outcome, ProcessResult process, string verb)
    {
        var category = AuthFailure().IsMatch(process.StdErr) ? "AuthFailed"
            : process.Outcome == ProcessOutcome.TimedOut ? "Timeout"
            : "CloneFailed";
        var detail = Tail(process.StdErr);
        var message = $"git {verb} failed: {process.Message}{(detail.Length > 0 ? $" | {detail}" : string.Empty)}";
        return Fail(outcome, category, message, process.Attempts);
    }

    // A repo that could not be brought up to date is flagged and kept out of the scans. Whatever is on disk stays.
    private RepoWorkResult Fail(RepoOutcome outcome, string category, string message, int attempts)
    {
        message = $"{Describe(outcome)}: {message}";
        context.Repos.Record(outcome with
        {
            ExistedLocally = IsMirror(outcome.LocalPath),
            UpdatedLocally = false,
            RemoteStatus = RepositoryStatus.Unreachable
        });
        return RepoWorkResult.Failed(category, message, attempts);
    }

    /// <summary>"GitHub acme/repo" or "ADO org/project/repo": the source first, so a message never leaves out where the repo is.</summary>
    internal static string Describe(RepoOutcome repo) =>
        $"{repo.Source} {string.Join('/', new[] { repo.Org, repo.Project, repo.Name }.Where(part => !string.IsNullOrWhiteSpace(part)))}";

    private static string Tail(string text)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= 300 ? flat : flat[^300..];
    }
}
