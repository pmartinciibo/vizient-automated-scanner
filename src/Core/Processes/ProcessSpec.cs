namespace Vizient.AutomatedScanner.Core.Processes;

/// <summary>What to run, and the limits to run it under.</summary>
public sealed record ProcessSpec
{
    public required string FileName { get; init; }

    /// <summary>Passed as separate arguments, so nothing is re-parsed by a shell.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    /// <summary>Added to the engine's own environment for the child.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Environment variables whose values are secrets (a token for this tool). They are set for the child
    /// and masked in everything the runner captures. Give a tool only its own.
    /// </summary>
    public IReadOnlyDictionary<string, string> SecretEnvironment { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Environment variable names to remove from what the child inherits from the engine, before the two sets above are
    /// added. Keeps one tool from picking up another tool's secret that happens to be set in the engine's environment.
    /// </summary>
    public IReadOnlyCollection<string> RemoveEnvironment { get; init; } = [];

    /// <summary>Other values to mask in captured output, for secrets that reach the tool some other way.</summary>
    public IReadOnlyCollection<string> SecretValues { get; init; } = [];

    /// <summary>How long one attempt may run before its whole process tree is killed.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);

    public RetryPolicy Retry { get; init; } = RetryPolicy.None;

    /// <summary>
    /// Exit codes that count as success. Some tools exit non-zero when they find something, and that is
    /// still a clean run.
    /// </summary>
    public IReadOnlyCollection<int> SuccessExitCodes { get; init; } = [0];

    /// <summary>
    /// When set, standard output is streamed to this file instead of being held in memory. It is written to
    /// a temporary file and moved here only if the run succeeds, so a failed run leaves no output behind.
    /// </summary>
    public string? StdOutFile { get; init; }
}

/// <summary>A failed or timed-out attempt is tried again, up to <see cref="MaxAttempts"/> in all.</summary>
public sealed record RetryPolicy(int MaxAttempts = 1, TimeSpan Backoff = default)
{
    public static readonly RetryPolicy None = new();
}
