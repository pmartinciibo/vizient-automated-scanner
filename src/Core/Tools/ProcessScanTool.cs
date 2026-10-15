using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Processes;
using Vizient.AutomatedScanner.Core.Work;

namespace Vizient.AutomatedScanner.Core.Tools;

/// <summary>
/// What the scanners that run a program have in common: finding the program, giving it only its own secrets,
/// running it as a locked-down account when engine.json says so, applying the tool's timeout and retry, and turning
/// a failure into a marker whose message has no secrets in it.
/// </summary>
public abstract class ProcessScanTool : ScanToolBase
{
    /// <summary>The program to run if engine.json gives no exePath.</summary>
    protected abstract string DefaultExe { get; }

    /// <summary>Additional programs this tool needs. Checked up front with the main executable.</summary>
    protected virtual IEnumerable<string> OtherPrograms(RunContext context) => [];

    protected ToolSettings Settings(RunContext context) => context.Config.Current.Tool(Name);

    protected string Exe(RunContext context) =>
        string.IsNullOrWhiteSpace(Settings(context).ExePath) ? DefaultExe : Settings(context).ExePath!;

    protected override async Task PrepareAsync(RunContext context, CancellationToken cancellationToken)
    {
        foreach (var program in OtherPrograms(context).Prepend(Exe(context)))
        {
            if (!ExecutableFinder.Exists(program))
            {
                throw new InvalidOperationException(
                    $"{Name}: cannot find '{program}'. Install it, put it on the PATH of the account the engine runs as, or set tools.{Name}.exePath in engine.json.");
            }
        }

        // A missing or expired secret fails the tool here, once, naming the secret, not once for every repo.
        var settings = Settings(context);
        foreach (var (variable, secret) in settings.Env ?? new Dictionary<string, string>())
        {
            await context.Secrets.GetSecretAsync(secret, $"{Name} ({variable})", cancellationToken);
        }

        if (settings.RunAs is { } runAs && !string.IsNullOrWhiteSpace(runAs.Account))
        {
            await context.Secrets.GetSecretAsync(runAs.PasswordSecret, $"{Name} (runAs {runAs.Account})", cancellationToken);
        }
    }

    /// <summary>
    /// A spec for one run of the tool: this tool's secrets as environment variables (every other tool's cleared), the
    /// run-as account if there is one, the tool's retry policy, and at most <paramref name="timeout"/> (the tool's own
    /// timeout if none is given).
    /// </summary>
    protected async Task<ProcessSpec> BuildSpecAsync(
        RepoWorkContext work,
        ProcessSpec spec,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        RetryPolicy? retry = null)
    {
        spec = spec with
        {
            Timeout = timeout ?? work.Limits.Timeout,
            Retry = retry ?? work.Limits.Retry
        };
        spec = await work.Run.Secrets.ApplyAsync(spec, Name, cancellationToken);

        if (Settings(work.Run).RunAs is { } runAs && !string.IsNullOrWhiteSpace(runAs.Account))
        {
            var password = await work.Run.Secrets.GetSecretAsync(runAs.PasswordSecret, $"{Name} (runAs {runAs.Account})", cancellationToken);
            spec = spec with { RunAs = new RunAsAccount(runAs.Account, password) };
        }

        return spec;
    }

    /// <summary>A failed run as a marker: the category (Timeout, or the one given), what happened, and the end of what the tool said. The runner already masked the secrets.</summary>
    protected static RepoWorkResult Failure(ProcessResult process, string category = "ToolError")
    {
        var detail = Tail(process.StdErr);
        var message = $"{process.Message}{(detail.Length > 0 ? $" | {detail}" : string.Empty)}";
        return RepoWorkResult.Failed(process.Outcome == ProcessOutcome.TimedOut ? "Timeout" : category, message, process.Attempts);
    }

    protected static string Tail(string text, int length = 400)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= length ? flat : flat[^length..];
    }

    /// <summary>The marker for a clean run. The skip key is filled in by <see cref="ScanToolBase"/>.</summary>
    protected RepoWorkResult Success(DateTimeOffset started, int findingCount, string? scanMode = null, SeverityCounts? severities = null) =>
        RepoWorkResult.Done(new ToolDoneMarker
        {
            StartedAt = started,
            EndedAt = Time.GetUtcNow(),
            FindingCount = findingCount,
            Severities = severities,
            ScanMode = scanMode ?? string.Empty
        });

    protected void EnsureResultsFolder(RepoWorkContext work) =>
        Directory.CreateDirectory(Path.GetDirectoryName(work.ResultFile)!);
}

/// <summary>Finds a program the way the operating system will when it starts it: a path as it is, a name on the PATH with each of the PATHEXT endings.</summary>
internal static class ExecutableFinder
{
    public static bool Exists(string program)
    {
        if (program.Contains('\\') || program.Contains('/') || Path.IsPathRooted(program))
        {
            return File.Exists(program);
        }

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Prepend(string.Empty)
            .ToList();

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory.Trim('"'), program + extension)))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    // A bad entry on the PATH is not a reason to stop looking.
                }
            }
        }

        return false;
    }
}
