using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Processes;

namespace Vizient.AutomatedScanner.Core.RepoClone;

/// <summary>
/// Azure DevOps access through the signed-in Azure CLI, the way the clone scripts did it: an Entra access token
/// for the Azure DevOps resource, per tenant, from <c>az account get-access-token</c>. For each org the first
/// tenant whose token can list its projects is used. Tokens are kept for 30 minutes and then fetched again, so a
/// long run never sends an expired one. The token is never logged, and it is handed to <paramref name="onSecret"/>
/// so everything the engine says is masked of it.
/// </summary>
internal sealed class AdoEntraAccess(IProcessRunner processes, ToolSettings settings, TimeProvider time, Action<string> onSecret)
{
    public const string HostPrefix = "https://dev.azure.com/";
    private const string AdoResourceId = "499b84ac-1321-427f-aa17-267ca6975798";
    private static readonly TimeSpan TokenLife = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AzTimeout = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (string Token, DateTimeOffset At)> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string>? _tenants;

    private string AzPath => string.IsNullOrWhiteSpace(settings.AzPath) ? "az.cmd" : settings.AzPath;

    /// <summary>Finds the tenant that can see the org and returns a credential that stays fresh. Throws if none can.</summary>
    public async Task<IGitCredential> ResolveAsync(string org, AdoLister lister, CancellationToken cancellationToken)
    {
        var problems = new List<string>();

        foreach (var tenant in await TenantsAsync(cancellationToken))
        {
            try
            {
                var auth = Bearer(await TokenAsync(tenant, cancellationToken));
                await lister.ListProjectsAsync(org, auth, 1, cancellationToken);
                return new TenantCredential(this, tenant);
            }
            catch (Exception ex) when (ex is RepoSourceException or InvalidOperationException)
            {
                problems.Add($"tenant {tenant}: {ex.Message}");
            }
        }

        throw new RepoSourceException(problems.Count == 0
            ? $"No Entra tenant is available to read {org}. Run 'az login'."
            : $"No Entra tenant could read {org}. {string.Join(" | ", problems)}");
    }

    private async Task<IReadOnlyList<string>> TenantsAsync(CancellationToken cancellationToken)
    {
        if (settings.AdoTenantIds is { Count: > 0 } configured)
        {
            return configured;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_tenants is not null)
            {
                return _tenants;
            }

            var result = await processes.RunAsync(new ProcessSpec
            {
                FileName = AzPath,
                Arguments = ["account", "list", "--all", "--query", "[].tenantId", "-o", "tsv"],
                Timeout = AzTimeout
            }, cancellationToken);

            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"Could not list the signed-in Azure accounts ({result.Message}). Run 'az login'.");
            }

            _tenants = result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(Core.IO.SafeName.IsValid)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return _tenants;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The tenant's access token, fetched again once it is 30 minutes old.</summary>
    public async Task<string> TokenAsync(string tenant, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_tokens.TryGetValue(tenant, out var cached) && time.GetUtcNow() - cached.At < TokenLife)
            {
                return cached.Token;
            }

            var result = await processes.RunAsync(new ProcessSpec
            {
                FileName = AzPath,
                Arguments = ["account", "get-access-token", "--tenant", tenant, "--resource", AdoResourceId, "--query", "accessToken", "-o", "tsv"],
                Timeout = AzTimeout,
                Retry = new RetryPolicy(2, TimeSpan.FromSeconds(2))
            }, cancellationToken);

            var token = result.StdOut.Trim();
            if (!result.Succeeded || token.Length == 0 || token.Any(char.IsWhiteSpace))
            {
                throw new InvalidOperationException(
                    $"Could not get an Azure DevOps token for tenant {tenant} ({result.Message}). Run 'az login --tenant {tenant}' and complete any MFA prompt.");
            }

            onSecret(token);
            _tokens[tenant] = (token, time.GetUtcNow());
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static GitAuth Bearer(string token) => GitAuth.Bearer(HostPrefix, token);

    private sealed class TenantCredential(AdoEntraAccess owner, string tenant) : IGitCredential
    {
        public async Task<GitAuth> GetAsync(CancellationToken cancellationToken) =>
            Bearer(await owner.TokenAsync(tenant, cancellationToken));
    }
}
