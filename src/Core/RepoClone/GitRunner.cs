using Vizient.AutomatedScanner.Core.Processes;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Core.RepoClone;

/// <summary>
/// Runs git as a child process. Authentication is an http.extraheader given through GIT_CONFIG_* environment
/// variables, as the clone scripts did, so the token is on no command line and in no config file. The header is a
/// secret environment value, masked in anything captured, together with the token and its base64 form.
/// </summary>
internal sealed class GitRunner(RunContext context, ToolLimits limits, string gitPath, bool longPaths)
{
    private static readonly TimeSpan LocalTimeout = TimeSpan.FromMinutes(5);

    /// <summary>A git command that touches no remote: one try, a short timeout unless the caller says otherwise.</summary>
    public Task<ProcessResult> LocalAsync(string? repoPath, IReadOnlyList<string> args, CancellationToken cancellationToken, TimeSpan? timeout = null) =>
        context.Processes.RunAsync(Spec(repoPath, args, auth: null, timeout ?? LocalTimeout, RetryPolicy.None), cancellationToken);

    /// <summary>The tool's own timeout, for local work that takes as long as the repo is big.</summary>
    public TimeSpan Timeout => limits.Timeout;

    /// <summary>A git command that talks to the remote: the tool's timeout and retry policy.</summary>
    public Task<ProcessResult> RemoteAsync(string? repoPath, GitAuth auth, IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        context.Processes.RunAsync(Spec(repoPath, args, auth, limits.Timeout, limits.Retry), cancellationToken);

    private ProcessSpec Spec(string? repoPath, IReadOnlyList<string> args, GitAuth? auth, TimeSpan timeout, RetryPolicy retry)
    {
        List<string> arguments =
        [
            "-c", "safe.directory=*",
            "-c", $"core.longpaths={(longPaths ? "true" : "false")}",
            "-c", "credential.helper=",
            "-c", "gc.auto=0"
        ];
        if (repoPath is not null)
        {
            arguments.AddRange(["-C", repoPath]);
        }

        arguments.AddRange(args);

        var environment = new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never"
        };
        var secretEnvironment = new Dictionary<string, string>();
        IReadOnlyCollection<string> secretValues = [];

        if (auth is not null)
        {
            environment["GIT_CONFIG_COUNT"] = "1";
            environment["GIT_CONFIG_KEY_0"] = $"http.{auth.HostPrefix}.extraheader";
            secretEnvironment["GIT_CONFIG_VALUE_0"] = auth.GitHeader;
            secretValues = auth.Secrets.ToList();
        }

        // Another tool's secret that happens to be set where the engine runs must not reach git.
        var remove = context.Config.Current.Tools.Values
            .SelectMany(t => (IEnumerable<string>?)t.Env?.Keys ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (auth is null)
        {
            remove.AddRange(["GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0"]);
        }

        return new ProcessSpec
        {
            FileName = gitPath,
            Arguments = arguments,
            Environment = environment,
            SecretEnvironment = secretEnvironment,
            SecretValues = secretValues,
            RemoveEnvironment = remove,
            Timeout = timeout,
            Retry = retry
        };
    }
}
