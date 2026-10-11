using Vizient.AutomatedScanner.Core.Imports;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Processes;
using Vizient.AutomatedScanner.Core.RepoClone;
using Vizient.AutomatedScanner.Core.State;
using Vizient.AutomatedScanner.Core.Work;

namespace Vizient.AutomatedScanner.Core.Tools;

/// <summary>
/// Tool 3: lists the repos of every org in engine.json "sources", keeps a bare mirror of each (cloning new ones,
/// updating the others), and reports each repo, with the tip of its default branch, for the scanners and for
/// results.json. Borrowed from the clone scripts that worked: GitHub is paged with a token, Azure DevOps is read with
/// Entra tokens from the Azure CLI (or a PAT if the source names a secret), git is given its credential as an
/// http.extraheader in the environment, a half-made clone is made again, a failed update keeps the old clone, a
/// failing repo is flagged and the run goes on. A mirror has no files, so a scan first makes a temporary working
/// copy with <see cref="RepoExpander"/> and deletes it afterwards. See <see cref="RepoSynchronizer"/> and
/// <see cref="RepoLayout"/>.
/// </summary>
/// <param name="handler">Replaces the network, for tests.</param>
/// <param name="delay">Replaces the wait between retries, for tests.</param>
/// <param name="time">Replaces the clock, for tests.</param>
public sealed class RepoCloneTool(
    HttpMessageHandler? handler = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    TimeProvider? time = null) : ITool
{
    public string Name => ToolNames.RepoClone;

    // Added to as tokens are met, so nothing the engine says about a failed call can hold one.
    private readonly object _maskGate = new();
    private readonly List<string> _secrets = new();

    public async Task RunAsync(RunContext context, CancellationToken cancellationToken = default)
    {
        var clock = time ?? TimeProvider.System;
        var settings = context.Config.Current.Tool(Name);
        var sources = context.Config.Current.Sources;
        if (sources.Count == 0)
        {
            throw new InvalidOperationException("RepoClone needs at least one entry in engine.json \"sources\".");
        }

        // What the last scan found, so each org, application and repo can be told apart as new or existing.
        var lastSnapshot = await RepoSnapshotKeeper.LoadAsync(context, cancellationToken);
        var prior = lastSnapshot.ToRepoOutcomes().ToDictionary(r => r.Key, StringComparer.Ordinal);

        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan; // each request has its own timeout

        var api = new ApiRequester(
            http,
            (message, status) => new RepoSourceException(message, status),
            Mask,
            delay ?? ((wait, token) => Task.Delay(wait, token)),
            TimeSpan.FromSeconds(60),
            message => context.Status.UpdateStep(Name, step => step with { Message = message }));
        var gitHub = new GitHubLister(api);
        var ado = new AdoLister(api);
        var entra = new AdoEntraAccess(context.Processes, settings, clock, AddSecret);

        // 1. Discover. An org that cannot be listed is flagged and its repos are carried forward, not dropped.
        var discovered = new List<DiscoveredRepo>();
        var credentials = new Dictionary<string, IGitCredential>(StringComparer.OrdinalIgnoreCase);
        var incompleteOrgs = new List<(string Source, string Org)>();
        var failures = new List<string>();
        var listedGitHubOrgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listedOrgs = 0;

        // The GitHub orgs the last scan knew, for keeping them when this scan cannot say what the token can see.
        List<string> KnownGitHubOrgs() => prior.Values
            .Where(r => r.Source == SourceSettings.GitHub)
            .Select(r => r.OrgName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        void Fail(string source, string org, string label, Exception ex)
        {
            failures.Add(label);
            context.Status.AddAlert(new StatusAlert
            {
                Type = "OrgDiscoveryFailed",
                Tool = Name,
                Name = $"{label}: {Mask(ex.Message)}",
                At = clock.GetUtcNow()
            });
            if (org.Length > 0)
            {
                incompleteOrgs.Add((source, org));
            }
        }

        // A source the request turns off is not listed or cloned. What the last scan knew of its repos is kept, as for an
        // org that could not be listed, but it is no failure.
        var requested = sources.Where(s => context.Request.IsSourceEnabled(s.Source)).ToList();
        var leftOut = new List<(string Source, string Org)>();
        foreach (var off in sources.Except(requested))
        {
            if (off.Source == SourceSettings.GitHub)
            {
                leftOut.AddRange(KnownGitHubOrgs().Select(o => (off.Source, o)));
            }
            else
            {
                leftOut.Add((off.Source, off.Org.Trim()));
            }

            // Not a problem, but the status should say why these repos are not in this run.
            context.Status.AddAlert(new StatusAlert
            {
                Type = "SourceLeftOut",
                Tool = Name,
                Name = off.Source == SourceSettings.GitHub
                    ? "GitHub: turned off by the request"
                    : $"{off.Source}/{off.Org.Trim()}: turned off by the request",
                At = clock.GetUtcNow()
            });
        }

        if (requested.Count == 0)
        {
            throw new InvalidOperationException(
                "request.json turns off every source RepoClone has. To skip RepoClone for this run, turn off the RepoClone tool instead.");
        }

        foreach (var source in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (source.Source == SourceSettings.GitHub)
            {
                // The token says which orgs there are. Then each org is listed on its own, so one that fails costs only itself.
                IGitCredential credential;
                List<string> orgs;
                try
                {
                    var token = await RequireSecretAsync(context, source, cancellationToken);
                    credential = new FixedCredential(GitAuth.Basic("https://github.com/", "x-access-token", token, "Bearer"));
                    orgs = await gitHub.ListOrgsAsync(await credential.GetAsync(cancellationToken), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Which orgs the token sees is unknown, so every GitHub org known from before is kept, not dropped.
                    foreach (var known in KnownGitHubOrgs())
                    {
                        incompleteOrgs.Add((SourceSettings.GitHub, known));
                    }

                    Fail(SourceSettings.GitHub, string.Empty, "GitHub (organizations of the token)", ex);
                    continue;
                }

                foreach (var org in orgs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    listedGitHubOrgs.Add(org);

                    try
                    {
                        var listed = await gitHub.ListAsync(org, await credential.GetAsync(cancellationToken), cancellationToken);
                        credentials[RepoSynchronizer.CredentialKey(SourceSettings.GitHub, org)] = credential;
                        discovered.AddRange(listed);
                        listedOrgs++;
                        context.Status.UpdateStep(Name, step => step with { Message = $"listed {listed.Count} repos in GitHub/{org}" });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Fail(SourceSettings.GitHub, org, $"GitHub/{org}", ex);
                    }
                }

                continue;
            }

            var adoOrg = source.Org.Trim();
            try
            {
                IGitCredential credential;
                if (!string.IsNullOrWhiteSpace(source.TokenSecret))
                {
                    var pat = await RequireSecretAsync(context, source, cancellationToken);
                    credential = new FixedCredential(GitAuth.Basic(AdoEntraAccess.HostPrefix, string.Empty, pat, "Basic"));
                }
                else
                {
                    credential = await entra.ResolveAsync(adoOrg, ado, cancellationToken);
                }

                var listed = await ado.ListAsync(adoOrg, await credential.GetAsync(cancellationToken), cancellationToken);
                credentials[RepoSynchronizer.CredentialKey(source.Source, adoOrg)] = credential;
                discovered.AddRange(listed);
                listedOrgs++;
                context.Status.UpdateStep(Name, step => step with { Message = $"listed {listed.Count} repos in {source.Source}/{adoOrg}" });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Fail(source.Source, adoOrg, $"{source.Source}/{adoOrg}", ex);
            }
        }

        // A GitHub org the last scan knew that the token no longer lists is kept and flagged, not dropped: losing access
        // (a token that expired, a missing SSO authorization) looks the same as an org that is really gone.
        if (requested.Any(s => s.Source == SourceSettings.GitHub))
        {
            var alreadyKept = incompleteOrgs.Concat(leftOut).Select(o => $"{o.Source}|{o.Org}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var known in KnownGitHubOrgs().Where(o => !listedGitHubOrgs.Contains(o) && !alreadyKept.Contains($"{SourceSettings.GitHub}|{o}")))
            {
                incompleteOrgs.Add((SourceSettings.GitHub, known));
                context.Status.AddAlert(new StatusAlert
                {
                    Type = "OrgNotListed",
                    Tool = Name,
                    Name = $"GitHub/{known}: not among the organizations of the token this time, kept from the last scan",
                    At = clock.GetUtcNow()
                });
            }
        }

        // Nothing could be listed at all: the sources, the tokens or the network are wrong. Fail the step and leave
        // the last snapshot exactly as it was.
        if (listedOrgs == 0)
        {
            throw new InvalidOperationException(
                failures.Count > 0
                    ? $"No organization could be listed ({string.Join(", ", failures)}). See the OrgDiscoveryFailed alerts."
                    : "No organization could be listed: the token or tokens in sources see no organizations.");
        }

        // 2. Report what is known for every repo before any cloning, so results.json shows them even if the run stops.
        var unique = discovered.GroupBy(r => r.Key, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var paths = RepoLayout.Assign(context.Config.ReposRoot, unique);
        var byKey = unique.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var outcomes = new List<RepoOutcome>();

        foreach (var repo in unique.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            var path = paths[repo.Key];
            prior.TryGetValue(repo.Key, out var earlier);
            MoveIfRenamed(context.Config.ReposRoot, earlier?.LocalPath, path);

            var outcome = new RepoOutcome
            {
                Key = repo.Key,
                Source = repo.Source,
                Org = repo.Org,
                Project = repo.Project,
                Name = repo.Name,
                LocalPath = path,
                HeadSha = earlier?.HeadSha,
                ExistedLocally = RepoSynchronizer.IsMirror(path),
                ExistedRemotely = true,
                RemoteStatus = repo.Status
            };
            outcomes.Add(outcome);
            context.Repos.Record(outcome);
        }

        // Repos of an org that could not be listed are shown as unreachable. They are not in scope for this run.
        var kept = incompleteOrgs.Concat(leftOut);
        var incomplete = kept.Select(o => $"{o.Source}|{o.Item2}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var earlier in prior.Values.Where(r => !byKey.ContainsKey(r.Key) && incomplete.Contains($"{r.Source}|{r.OrgName}")))
        {
            context.Repos.Record(earlier with
            {
                ExistedLocally = RepoSynchronizer.IsMirror(earlier.LocalPath),
                ExistedRemotely = false,
                UpdatedLocally = false,
                RemoteStatus = RepositoryStatus.Unreachable
            });
        }

        // 3. Clone or update, in parallel, under this tool's limits.
        var scanArchived = context.Config.Current.ScanArchived;
        var git = new GitRunner(
            context,
            ToolLimits.For(context, Name),
            string.IsNullOrWhiteSpace(settings.GitPath) ? "git" : settings.GitPath,
            settings.LongPaths ?? true);
        var synchronizer = new RepoSynchronizer(context, git, byKey, credentials, clock);

        var work = new RepoWork(
            synchronizer.SyncAsync,
            repo => repo.RemoteStatus switch
            {
                RepositoryStatus.Disabled => new ToolSkippedMarker { Reason = "StatusDisabled" },
                RepositoryStatus.Archived when !scanArchived => new ToolSkippedMarker { Reason = "StatusArchived" },
                _ => null
            });

        try
        {
            await new RepoWorkLoop(context, Name, ToolLimits.For(context, Name)).RunAsync(outcomes, work, cancellationToken);
        }
        finally
        {
            // Listing finished, so the snapshot is worth writing even if the run is stopped part way through.
            await RepoSnapshotKeeper.SaveAsync(context, lastSnapshot, incompleteOrgs.Concat(leftOut).ToList(), clock, CancellationToken.None);
        }
    }

    private async Task<string> RequireSecretAsync(RunContext context, SourceSettings source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.TokenSecret))
        {
            throw new InvalidOperationException($"sources entry {source.Source}/{source.Org} needs tokenSecret in engine.json.");
        }

        var secret = await context.Secrets.GetSecretAsync(source.TokenSecret, $"{Name} ({source.Source}/{source.Org})", cancellationToken);
        AddSecret(secret);
        return secret;
    }

    private void AddSecret(string value)
    {
        lock (_maskGate)
        {
            _secrets.Add(value);
        }
    }

    private string Mask(string text)
    {
        lock (_maskGate)
        {
            return new SecretRedactor(_secrets).Redact(text);
        }
    }

    // A renamed repo keeps its history on disk: move the folder instead of cloning it again. Only ever inside the
    // repos root, and never over something that is already there. If the move fails the repo is cloned fresh.
    private static void MoveIfRenamed(string reposRoot, string? oldPath, string newPath)
    {
        if (string.IsNullOrWhiteSpace(oldPath)
            || string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(oldPath)
            || Directory.Exists(newPath)
            || !Path.GetFullPath(oldPath).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(reposRoot)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            Directory.Move(oldPath, newPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left where it was. The new location is cloned instead.
        }
    }
}
