namespace Vizient.AutomatedScanner.Core.Processes;

public enum ProcessOutcome
{
    /// <summary>Exited with one of the success codes.</summary>
    Succeeded,

    /// <summary>Exited with any other code, on the last attempt.</summary>
    Failed,

    /// <summary>Ran past the timeout on the last attempt and its process tree was killed.</summary>
    TimedOut,

    /// <summary>The program could not be started at all (not found, not allowed). Never retried.</summary>
    FailedToStart
}

public sealed record ProcessResult
{
    public ProcessOutcome Outcome { get; init; }

    /// <summary>Null when the process never started or was killed for running too long.</summary>
    public int? ExitCode { get; init; }

    /// <summary>How many times the program was run, counting retries.</summary>
    public int Attempts { get; init; }

    /// <summary>Total time across all attempts, including the waits between them.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Standard output of the last attempt, masked and capped. Empty when it went to a file.</summary>
    public string StdOut { get; init; } = string.Empty;

    /// <summary>Standard error of the last attempt, masked and capped.</summary>
    public string StdErr { get; init; } = string.Empty;

    public bool StdOutTruncated { get; init; }
    public bool StdErrTruncated { get; init; }

    /// <summary>A short, secret-free summary of what happened, safe for markers and status messages.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>The file standard output was written to. Set only when the run succeeded.</summary>
    public string? StdOutFile { get; init; }

    /// <summary>
    /// False if the process could not be placed in its Job Object. The runner then falls back to killing the
    /// process tree by walking it, which can miss a child started before the fallback ran.
    /// </summary>
    public bool JobObjectUsed { get; init; }

    public bool Succeeded => Outcome == ProcessOutcome.Succeeded;

    /// <summary>The category for a .failed.json marker, or null when the run succeeded.</summary>
    public string? FailureCategory => Outcome switch
    {
        ProcessOutcome.Succeeded => null,
        ProcessOutcome.TimedOut => "Timeout",
        _ => "ToolError"
    };
}
