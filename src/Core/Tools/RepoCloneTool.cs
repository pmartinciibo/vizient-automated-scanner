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

        // Listing comes first and covers every source and org, so the status can say how far along the whole of it is, not
        // only the org being read: how many orgs are listed of those known, and how many repos have been found in all.
        var orgsKnown = 0;
        var orgsFinished = 0;
        var reposFound = 0;
        string Overall() => $"discovery: {orgsFinished} of {orgsKnown} orgs listed, {reposFound} repos found; ";
        if (sources.Count == 0)
        {
            throw new InvalidOperationException("RepoClone needs at least one entry in engine.json \"sources\", an ADO or a GitHub one.");
        }

        // What the last scan found, so each org, application and repo can be told apart as new or existing.
        var lastSnapshot = await RepoSnapshotKeeper.LoadAsync(context, cancellationToken);
        var prior = lastSnapshot.ToRepoOutcomes().ToDictionary(r => r.Key, StringComparer.Ordinal);

        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout.InfiniteTimeSpan; // each request has its own timeout

        // One requester for each source, so a retry note says which source is being slow.
        ApiRequester ApiFor(string source) => new(
            http,
            (message, status) => new RepoSourceException(message, status),
            Mask,
            delay ?? ((wait, token) => Task.Delay(wait, token)),
            TimeSpan.FromSeconds(60),
            message => context.Status.UpdateStep(Name, step => step with { Message = $"{Overall()}{source}: {message}" }));
        // While listing, the status says which source, which org and how far it has got.
        var gitHub = new GitHubLister(ApiFor(SourceSettings.GitHub))
        {
            Progress = note => context.Status.UpdateStep(Name, step => step with { Message = $"{Overall()}GitHub: {note}" })
        };
        var ado = new AdoLister(ApiFor(SourceSettings.Ado))
        {
            Progress = note => context.Status.UpdateStep(Name, step => step with { Message = $"{Overall()}ADO: {note}" })
        };
        var entra = new AdoEntraAccess(context.Processes, settings, clock, AddSecret);

        // 1. Discover. An org that cannot be listed is flagged and its repos are carried forward, not dropped.
        var discovered = new List<DiscoveredRepo>();
        var credentials = new Dictionary<string, IGitCredential>(StringComparer.OrdinalIgnoreCase);
        var incompleteOrgs = new List<(string Source, string Org)>();
        var failures = new List<string>();
        var stateOfOrg = new Dictionary<string, SourceState>(StringComparer.OrdinalIgnoreCase);
        var listedGitHubOrgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listedOrgs = new List<(string Source, string Org)>();

        // The GitHub orgs the last scan knew, for keeping them when this scan cannot say what the token can see.
        List<string> KnownGitHubOrgs() => prior.Values
            .Where(r => r.Source == SourceSettings.GitHub)
            .Select(r => r.OrgName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        void Fail(string source, string org, string label, Exception ex)
        {
            failures.Add($"{label}: {Mask(ex.Message)}");
            context.Errors.Record(RunErrorScope.Org, Mask(ex.Message), Name, "OrgDiscoveryFailed", source, org.Length > 0 ? org : null);
            if (org.Length > 0)
            {
                orgsFinished++;
                context.Repos.SetOrgStatus(source, org, Mask(ex.Message));
            }

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
        var requested = sources.Where(s => context.Request.IsSourceEnabled(s.Name)).ToList();
        var leftOut = new List<(string Source, string Org)>();
        foreach (var off in sources.Except(requested).ToList())
        {
            // For GitHub that is every org it knew from before, besides the ones the entry names.
            var offOrgs = off.Source == SourceSettings.GitHub
                ? KnownGitHubOrgs().Concat(off.NamedOrgs()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : off.NamedOrgs().ToList();
            leftOut.AddRange(offOrgs.Select(o => (off.Source, o)));
            foreach (var offOrg in offOrgs)
            {
                context.Repos.SetOrgStatus(off.Source, offOrg, "turned off by the request");
            }

            // Not a problem, but the status should say why these repos are not in this run.
            context.Status.AddAlert(new StatusAlert
            {
                Type = "SourceLeftOut",
                Tool = Name,
                Name = $"{EntryLabel(off)}: turned off by the request",
                At = clock.GetUtcNow()
            });
        }

        orgsKnown = requested.Where(s => s.Source != SourceSettings.GitHub).Sum(s => s.NamedOrgs().Count);

        if (requested.Count == 0)
        {
            throw new InvalidOperationException(
                $"request.json turns off every source RepoClone has ({string.Join(", ", sources.Select(s => s.Source).Distinct())}). To skip RepoClone for this run, turn off the RepoClone tool instead.");
        }

        foreach (var listOnly in requested.Where(s => context.Request.StateOf(s.Name) == SourceState.ListOnly))
        {
            // Nothing is cloned or updated for this entry, and the status should say so.
            context.Status.AddAlert(new StatusAlert
            {
                Type = "SourceListOnly",
                Tool = Name,
                Name = $"{EntryLabel(listOnly)}: listed only by the request, nothing cloned or updated",
                At = clock.GetUtcNow()
            });
        }

        foreach (var source in requested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryState = context.Request.StateOf(source.Name);

            if (source.Source == SourceSettings.GitHub)
            {
                // The token says which orgs there are. Then each is listed on its own, so one that fails costs only itself.
                IGitCredential credential;
                List<(string Login, bool IsPerson)> owners;
                try
                {
                    var token = await RequireSecretAsync(context, source, cancellationToken);
                    credential = new FixedCredential(GitAuth.Basic("https://github.com/", "x-access-token", token, "Bearer"));
                    var auth = await credential.GetAsync(cancellationToken);
                    var orgs = await gitHub.ListOrgsAsync(auth, cancellationToken);

                    // A fine-grained token sees no membership list. Its repositories still say which organizations it reaches.
                    if (orgs.Count == 0)
                    {
                        orgs = await gitHub.ListOrgsFromReposAsync(auth, cancellationToken);
                        if (orgs.Count > 0)
                        {
                            context.Status.UpdateStep(Name, step => step with
                            {
                                Message = $"{Overall()}GitHub: the token lists no organization memberships (usual for a fine-grained token); found {orgs.Count} from the repositories it can reach: {string.Join(", ", orgs)}"
                            });
                        }
                    }

                    owners = orgs.Select(o => (o, false)).ToList();

                    // Orgs engine.json names are scanned too. An org found both ways is listed once.
                    foreach (var named in source.Orgs ?? [])
                    {
                        if (!owners.Any(o => string.Equals(o.Login, named, StringComparison.OrdinalIgnoreCase)))
                        {
                            owners.Add((named.Trim(), false));
                        }
                    }

                    // Only if engine.json asks: the account's own repos, under the account's name.
                    if (source.IncludeOwnRepos)
                    {
                        var login = await gitHub.GetLoginAsync(auth, cancellationToken);
                        if (!owners.Any(o => string.Equals(o.Login, login, StringComparison.OrdinalIgnoreCase)))
                        {
                            owners.Add((login, true));
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Which orgs the token sees is unknown, so every GitHub org known from before is kept, not dropped.
                    foreach (var known in KnownGitHubOrgs())
                    {
                        incompleteOrgs.Add((SourceSettings.GitHub, known));
                    }

                    Fail(SourceSettings.GitHub, string.Empty, "GitHub (organizations of the token)", ex);

                    // The orgs this entry names were never tried, and the ones known from before are only kept. Say so for each.
                    foreach (var named in source.NamedOrgs())
                    {
                        context.Repos.SetOrgStatus(SourceSettings.GitHub, named, $"not listed: {Mask(ex.Message)}");
                    }

                    foreach (var known in KnownGitHubOrgs().Except(source.NamedOrgs(), StringComparer.OrdinalIgnoreCase))
                    {
                        context.Repos.SetOrgStatus(SourceSettings.GitHub, known, $"kept from the last scan; the organizations of the token could not be listed: {Mask(ex.Message)}");
                    }

                    continue;
                }

                if (owners.Count == 0)
                {
                    var who = await gitHub.DescribeTokenAsync(await credential.GetAsync(cancellationToken), cancellationToken);
                    Fail(
                        SourceSettings.GitHub,
                        string.Empty,
                        $"GitHub (the token in secret '{source.TokenSecret}')",
                        new InvalidOperationException(
                            $"{who}, and it reaches no organization's repositories, so there is nothing to list. "
                            + (who.Contains("fine-grained")
                                ? "A fine-grained token covers one resource owner: create it with the organization as the resource owner (an owner may have to approve it) and grant it the repositories to scan, with Contents and Metadata read-only. For several organizations use one token, and one GitHub entry in sources, for each. If the token is for your own account and its repos are what you want scanned, set includeOwnRepos to true on the GitHub entry."
                                : "Check that the account is a member of the organizations, that the token has the read:org scope, and that an organization that enforces SSO has the token authorized.")));
                    continue;
                }

                orgsKnown += owners.Count;

                foreach (var (org, isPerson) in owners)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    listedGitHubOrgs.Add(org);
                    stateOfOrg[$"{SourceSettings.GitHub}|{org}"] = entryState;

                    try
                    {
                        var auth = await credential.GetAsync(cancellationToken);
                        var listed = isPerson
                            ? await gitHub.ListOwnReposAsync(org, auth, cancellationToken)
                            : await gitHub.ListAsync(org, auth, cancellationToken);
                        credentials[RepoSynchronizer.CredentialKey(SourceSettings.GitHub, org)] = credential;
                        discovered.AddRange(listed);
                        listedOrgs.Add((SourceSettings.GitHub, org));
                        orgsFinished++;
                        reposFound += listed.Count;
                        context.Repos.SetOrgStatus(SourceSettings.GitHub, org, $"{listed.Count} repos found");
                        context.Status.UpdateStep(Name, step => step with { Message = $"{Overall()}listed GitHub/{org}{(isPerson ? " (the account's own)" : string.Empty)}" });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Fail(SourceSettings.GitHub, org, $"GitHub/{org}", ex);
                    }
                }

                continue;
            }

            foreach (var adoOrgName in source.NamedOrgs())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var adoOrg = adoOrgName;
                stateOfOrg[$"{source.Source}|{adoOrg}"] = entryState;
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
                    listedOrgs.Add((source.Source, adoOrg));
                    orgsFinished++;
                    reposFound += listed.Count;
                    context.Repos.SetOrgStatus(source.Source, adoOrg, $"{listed.Count} repos found");
                    context.Status.UpdateStep(Name, step => step with { Message = $"{Overall()}listed {source.Source}/{adoOrg}" });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Fail(source.Source, adoOrg, $"{source.Source}/{adoOrg}", ex);
                }
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
                context.Repos.SetOrgStatus(SourceSettings.GitHub, known, "not among the organizations of the token this time; kept from the last scan");
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
        if (listedOrgs.Count == 0)
        {
            var offByRequest = leftOut.Count > 0
                ? $" Turned off by the request: {string.Join(", ", leftOut.Select(o => o.Org.Length > 0 ? $"{o.Source}/{o.Org}" : o.Source).Distinct())}."
                : string.Empty;
            throw new InvalidOperationException(
                $"No organization could be listed. Tried: {string.Join("; ", failures)}.{offByRequest} See the OrgDiscoveryFailed alerts.");
        }

        SourceState StateOfRepo(string source, string org) =>
            stateOfOrg.GetValueOrDefault($"{source}|{org}", SourceState.Clone);

        context.Status.UpdateStep(Name, step => step with
        {
            Message = $"discovery done: {reposFound} repos found in {orgsFinished} orgs"
                + (failures.Count > 0 ? $", {failures.Count} could not be listed" : string.Empty)
        });

        // 2. Report what is known for every repo before any cloning, so results.json shows them even if the run stops.
        var unique = discovered.GroupBy(r => r.Key, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var paths = RepoLayout.Assign(context.Config.ReposRoot, unique);
        var byKey = unique.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var outcomes = new List<RepoOutcome>();

        foreach (var repo in unique.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            var path = paths[repo.Key];
            prior.TryGetValue(repo.Key, out var earlier);

            // A listing changes nothing on disk, not even to move a renamed repo's folder. It keeps pointing at the old one.
            if (StateOfRepo(repo.Source, repo.Org) == SourceState.ListOnly)
            {
                if (earlier?.LocalPath is { } known && Directory.Exists(known))
                {
                    path = known;
                }
            }
            else
            {
                MoveIfRenamed(context, repo.Source, repo.Org, repo.Key, earlier?.LocalPath, path);
            }

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
            repo => StateOfRepo(repo.Source, repo.Org) == SourceState.ListOnly
                ? new ToolSkippedMarker { Reason = "ListOnly" }
                : repo.RemoteStatus switch
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
            await RepoSnapshotKeeper.SaveAsync(context, lastSnapshot, incompleteOrgs.Concat(leftOut).ToList(), clock, CancellationToken.None, listedOrgs);
        }
    }

    private async Task<string> RequireSecretAsync(RunContext context, SourceSettings source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.TokenSecret))
        {
            throw new InvalidOperationException($"{Label(source)}: the sources entry in engine.json needs tokenSecret.");
        }

        var secret = await context.Secrets.GetSecretAsync(source.TokenSecret, $"{Name} ({Label(source)})", cancellationToken);
        AddSecret(secret);
        return secret;
    }

    // "ADO VizientInc (vizientinc, RnD-Vizient)" or "GitHub GitHubRepos": the source first, then the entry's name.
    private static string EntryLabel(SourceSettings source) =>
        source.NamedOrgs().Count > 0
            ? $"{source.Source} {source.Name} ({string.Join(", ", source.NamedOrgs())})"
            : $"{source.Source} {source.Name}";

    // "GitHub" for a source that names no org, "ADO/org" for one that does.
    private static string Label(SourceSettings source) =>
        string.IsNullOrWhiteSpace(source.Org) ? source.Source : $"{source.Source}/{source.Org.Trim()}";

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
    private void MoveIfRenamed(RunContext context, string source, string org, string key, string? oldPath, string newPath)
    {
        var reposRoot = context.Config.ReposRoot;
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
            // Left where it was. The new location is cloned instead, but the failed move is reported.
            context.Errors.Record(RunErrorScope.Repo, $"Could not move the renamed repo's folder from '{oldPath}' to '{newPath}': {Mask(ex.Message)}", Name, "MoveFailed", source, org, key);
        }
    }
}
