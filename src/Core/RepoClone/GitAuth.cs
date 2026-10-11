using System.Text;

namespace Vizient.AutomatedScanner.Core.RepoClone;

/// <summary>
/// What it takes to talk to one host. The API and git are given the credential differently (GitHub wants a bearer
/// token for its API but basic auth for git), so both forms are kept. git gets its form in an http.extraheader.
/// </summary>
public sealed record GitAuth(string HostPrefix, string GitScheme, string GitCredential, string ApiScheme, string ApiCredential, IReadOnlyList<string> Secrets)
{
    /// <summary>The value of the extra header git sends. It is a secret.</summary>
    public string GitHeader => $"AUTHORIZATION: {GitScheme} {GitCredential}";

    /// <summary>Basic auth for git: the user and the token or password, base64 encoded. The API form is given by the caller.</summary>
    public static GitAuth Basic(string hostPrefix, string user, string secret, string apiScheme)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}"));
        var api = apiScheme.Equals("basic", StringComparison.OrdinalIgnoreCase) ? encoded : secret;
        return new GitAuth(hostPrefix, "basic", encoded, apiScheme, api, [secret, encoded]);
    }

    public static GitAuth Bearer(string hostPrefix, string token) =>
        new(hostPrefix, "bearer", token, "Bearer", token, [token]);
}

/// <summary>Hands out the credential for an org. For tokens that expire it is asked again for every git command.</summary>
public interface IGitCredential
{
    Task<GitAuth> GetAsync(CancellationToken cancellationToken);
}

internal sealed class FixedCredential(GitAuth auth) : IGitCredential
{
    public Task<GitAuth> GetAsync(CancellationToken cancellationToken) => Task.FromResult(auth);
}
