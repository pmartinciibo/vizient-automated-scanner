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

    /// <summary>
    /// Every organization the token's user belongs to (GET /user/orgs), which includes the orgs of an enterprise the
    /// user is a member of. Nothing is named in engine.json: what the token can see is what is scanned.
    /// </summary>
    public async Task<List<string>> ListOrgsAsync(GitAuth auth, CancellationToken cancellationToken)
    {
        var orgs = new List<string>();

        for (var page = 1; ; page++)
        {
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

    public async Task<List<DiscoveredRepo>> ListAsync(string org, GitAuth auth, CancellationToken cancellationToken)
    {
        var repos = new List<DiscoveredRepo>();

        for (var page = 1; ; page++)
        {
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

    public async Task<List<DiscoveredRepo>> ListAsync(string org, GitAuth auth, CancellationToken cancellationToken)
    {
        var repos = new List<DiscoveredRepo>();

        foreach (var (projectId, projectName) in await ListProjectsAsync(org, auth, 100, cancellationToken))
        {
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
