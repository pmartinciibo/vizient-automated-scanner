using System.Net.Http.Headers;
using System.Text.Json;
using Vizient.AutomatedScanner.Core.Imports;
using Vizient.AutomatedScanner.Core.IO;
using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.RepoClone;

/// <summary>Lists the repos of one GitHub organization, a hundred to a page, until a short page.</summary>
internal sealed class GitHubLister(ApiRequester api, string apiBase = "https://api.github.com")
{
    private const int PageSize = 100;

    /// <summary>Told what the lister is doing and how far it has got, for the status. Optional.</summary>
    public Action<string>? Progress { get; set; }

    /// <summary>
    /// Every organization the token's user belongs to (GET /user/orgs), which includes the orgs of an enterprise the
    /// user is a member of. Nothing is named in engine.json: what the token can see is what is scanned.
    /// </summary>
    public async Task<List<string>> ListOrgsAsync(GitAuth auth, CancellationToken cancellationToken)
    {
        var orgs = new List<string>();

        for (var page = 1; ; page++)
        {
            Progress?.Invoke(page == 1 ? "finding the organizations of the token" : $"finding the organizations of the token (page {page}, {orgs.Count} so far)");
            var url = $"{apiBase.TrimEnd('/')}/user/orgs?per_page={PageSize}&page={page}";
            using var document = await api.GetJsonAsync(url, () => Request(url, auth), cancellationToken);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new RepoSourceException("GitHub did not return a list of organizations.");
            }

            var count = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                count++;
                if (!item.TryGetProperty("login", out var login) || login.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(login.GetString()))
                {
                    throw new RepoSourceException("GitHub returned an organization without a login.");
                }

                orgs.Add(login.GetString()!);
            }

            if (count < PageSize)
            {
                return orgs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
    }

    /// <summary>The account the token belongs to (GET /user).</summary>
    public async Task<string> GetLoginAsync(GitAuth auth, CancellationToken cancellationToken)
    {
        var url = $"{apiBase.TrimEnd('/')}/user";
        using var document = await api.GetJsonAsync(url, () => Request(url, auth), cancellationToken);
        return document.RootElement.TryGetProperty("login", out var login) && !string.IsNullOrWhiteSpace(login.GetString())
            ? login.GetString()!
            : throw new RepoSourceException("GitHub did not say which account the token belongs to.");
    }

    /// <summary>The repos the token's own account owns (a person, not an organization), private ones included if the token reaches them.</summary>
    public async Task<List<DiscoveredRepo>> ListOwnReposAsync(string login, GitAuth auth, CancellationToken cancellationToken)
    {
        var repos = new List<DiscoveredRepo>();

        for (var page = 1; ; page++)
        {
            Progress?.Invoke($"listing the repos owned by {login} (page {page}, {repos.Count} so far)");
            var url = $"{apiBase.TrimEnd('/')}/user/repos?per_page={PageSize}&page={page}&affiliation=owner";
            using var document = await api.GetJsonAsync(url, () => Request(url, auth), cancellationToken);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new RepoSourceException($"GitHub did not return a list of the repos owned by {login}.");
            }

            var count = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                count++;
                repos.Add(Parse(login, item));
            }

            if (count < PageSize)
            {
                return repos;
            }
        }
    }

    /// <summary>
    /// The organizations that own the repositories the token can reach (GET /user/repos). For tokens that
    /// <see cref="ListOrgsAsync"/> returns nothing for: a fine-grained token is made for one resource owner and sees no
    /// membership list, but it does see the repositories it was granted. Repositories owned by a person rather than
    /// an organization are not in scope.
    /// </summary>
    public async Task<List<string>> ListOrgsFromReposAsync(GitAuth auth, CancellationToken cancellationToken)
    {
        var orgs = new List<string>();

        for (var page = 1; ; page++)
        {
            Progress?.Invoke($"reading the repositories the token can reach to find their organizations (page {page}, {orgs.Count} so far)");
            var url = $"{apiBase.TrimEnd('/')}/user/repos?per_page={PageSize}&page={page}&affiliation=owner,organization_member";
            using var document = await api.GetJsonAsync(url, () => Request(url, auth), cancellationToken);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new RepoSourceException("GitHub did not return a list of the repositories the token can reach.");
            }

            var count = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                count++;
                if (item.TryGetProperty("owner", out var owner)
                    && owner.TryGetProperty("type", out var type) && type.GetString() == "Organization"
                    && owner.TryGetProperty("login", out var login) && !string.IsNullOrWhiteSpace(login.GetString()))
                {
                    orgs.Add(login.GetString()!);
                }
            }

            if (count < PageSize)
            {
                return orgs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
    }

    /// <summary>
    /// Who GitHub says the token is, and what it is allowed to do (GET /user and its X-OAuth-Scopes header). For explaining
    /// an empty list of organizations: the account or the scopes are usually not what was expected. Never throws.
    /// </summary>
    public async Task<string> DescribeTokenAsync(GitAuth auth, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{apiBase.TrimEnd('/')}/user";
            using var response = await api.GetAsync(url, () => Request(url, auth), cancellationToken);
            var login = response.Json.RootElement.TryGetProperty("login", out var l) ? l.GetString() : null;
            var scopes = response.Headers.TryGetValue("x-oauth-scopes", out var s) ? s : null;

            // Fine-grained tokens send no scopes header at all.
            var scopeText = scopes is null ? "no scopes header, which means a fine-grained token" : $"scopes: {(scopes.Length == 0 ? "none" : scopes)}";
            return $"GitHub says the token is for the account '{login ?? "unknown"}' ({scopeText})";
        }
        catch (Exception ex) when (ex is RepoSourceException)
        {
            return $"GitHub did not say who the token is for ({ex.Message})";
        }
    }

    public async Task<List<DiscoveredRepo>> ListAsync(string org, GitAuth auth, CancellationToken cancellationToken)
    {
        var repos = new List<DiscoveredRepo>();

        for (var page = 1; ; page++)
        {
            Progress?.Invoke($"listing the repos of {org} (page {page}, {repos.Count} so far)");
            var url = $"{apiBase.TrimEnd('/')}/orgs/{Uri.EscapeDataString(org)}/repos?per_page={PageSize}&page={page}&type=all";
            using var document = await api.GetJsonAsync(url, () => Request(url, auth), cancellationToken);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new RepoSourceException($"GitHub did not return a list of repos for {org}.");
            }

            var count = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                count++;
                repos.Add(Parse(org, item));
            }

            if (count < PageSize)
            {
                return repos;
            }
        }
    }

    private static DiscoveredRepo Parse(string org, JsonElement item)
    {
        if (!item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id)
            || !item.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String
            || !item.TryGetProperty("clone_url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
        {
            throw new RepoSourceException($"GitHub returned a repo for {org} without an id, name or clone_url.");
        }

        var status = Flag(item, "archived") ? RepositoryStatus.Archived
            : Flag(item, "disabled") ? RepositoryStatus.Disabled
            : RepositoryStatus.Active;

        return new DiscoveredRepo($"gh-{id}", RepoOutcome.GitHub, org, null, nameElement.GetString()!, urlElement.GetString()!, status);
    }

    private static bool Flag(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static HttpRequestMessage Request(string url, GitAuth auth)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(auth.ApiScheme, auth.ApiCredential);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("Vizient-AutomatedScanner");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }
}

/// <summary>Lists the repos of one Azure DevOps organization: its projects, then each project's repos.</summary>
internal sealed class AdoLister(ApiRequester api, string baseUrl = "https://dev.azure.com")
{
    public const string SourceName = "ADO";

    /// <summary>Told what the lister is doing and how far it has got, for the status. Optional.</summary>
    public Action<string>? Progress { get; set; }

    public async Task<List<DiscoveredRepo>> ListAsync(string org, GitAuth auth, CancellationToken cancellationToken)
    {
        var repos = new List<DiscoveredRepo>();

        var projects = await ListProjectsAsync(org, auth, 100, cancellationToken);
        var index = 0;
        foreach (var (projectId, projectName) in projects)
        {
            index++;
            Progress?.Invoke($"{org}: listing the repos of project {index} of {projects.Count} ({projectName}), {repos.Count} repos so far");
            var url = $"{baseUrl.TrimEnd('/')}/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(projectId)}/_apis/git/repositories?api-version=7.1";
            using var document = await api.GetJsonAsync(url, () => Request(url, auth), cancellationToken);

            if (!document.RootElement.TryGetProperty("value", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                throw new RepoSourceException($"Azure DevOps did not return a list of repos for {org}/{projectName}.");
            }

            foreach (var item in list.EnumerateArray())
            {
                var id = Text(item, "id");
                var name = Text(item, "name");
                var remoteUrl = Text(item, "remoteUrl");
                if (id is null || name is null || remoteUrl is null || !SafeName.IsValid(id))
                {
                    throw new RepoSourceException($"Azure DevOps returned a repo in {org}/{projectName} without a usable id, name or remoteUrl.");
                }

                var disabled = item.TryGetProperty("isDisabled", out var flag) && flag.ValueKind == JsonValueKind.True;
                repos.Add(new DiscoveredRepo($"ado-{id}", SourceName, org, projectName, name, remoteUrl,
                    disabled ? RepositoryStatus.Disabled : RepositoryStatus.Active));
            }
        }

        return repos;
    }

    /// <summary>The org's projects, following the continuation token. With top = 1 it is also how a tenant's token is tried for an org.</summary>
    public async Task<List<(string Id, string Name)>> ListProjectsAsync(string org, GitAuth auth, int top, CancellationToken cancellationToken)
    {
        var projects = new List<(string, string)>();
        string? continuation = null;

        do
        {
            if (top > 1)
            {
                Progress?.Invoke($"{org}: finding the projects ({projects.Count} so far)");
            }

            var url = $"{baseUrl.TrimEnd('/')}/{Uri.EscapeDataString(org)}/_apis/projects?api-version=7.1&$top={top}";
            if (continuation is not null)
            {
                url += $"&continuationToken={Uri.EscapeDataString(continuation)}";
            }

            using var response = await api.GetAsync(url, () => Request(url, auth), cancellationToken);
            if (!response.Json.RootElement.TryGetProperty("value", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                throw new RepoSourceException($"Azure DevOps did not return a list of projects for {org}.");
            }

            foreach (var item in list.EnumerateArray())
            {
                var id = Text(item, "id");
                var name = Text(item, "name");
                if (id is null || name is null)
                {
                    throw new RepoSourceException($"Azure DevOps returned a project in {org} without an id or name.");
                }

                projects.Add((id, name));
            }

            continuation = top == 1 ? null : response.Headers.GetValueOrDefault("x-ms-continuationtoken");
        }
        while (!string.IsNullOrEmpty(continuation));

        return projects;
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static HttpRequestMessage Request(string url, GitAuth auth)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(auth.ApiScheme, auth.ApiCredential);
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }
}
