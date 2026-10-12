using Vizient.AutomatedScanner.Core.Processes;
using Vizient.AutomatedScanner.Core.Work;

namespace Vizient.AutomatedScanner.Core.Tools;

/// <summary>
/// Tool 4: secrets. Mode "filesystem" (the default) scans the files of the working copy. Mode "git" scans the history of
/// the repo's mirror itself, every branch and tag, with no working copy. Verification is off unless
/// <c>verify</c> is true, which also turns skipping off for this tool (see <see cref="Skip.ToolScanKey.NeverSkip"/>). Output is
/// TruffleHog's own: one JSON object per finding, a line each. It holds the secret values it found, so the run
/// folder needs restricted permissions.
/// </summary>
/// <remarks>
/// Unverified against a real TruffleHog: that "git" mode accepts a bare mirror as a file:// address, and that a
/// skip on the default branch's SHA is enough in git mode (a new commit on another branch does not change it).
/// </remarks>
public sealed class TruffleHogTool : ProcessScanTool
{
    public const string FilesystemMode = "filesystem";
    public const string GitMode = "git";

    public override string Name => ToolNames.TruffleHog;

    protected override string DefaultExe => "trufflehog";

    private string Mode(RunContext context) => Settings(context).Mode ?? FilesystemMode;

    protected override bool NeedsWorkingCopy(RunContext context) => Mode(context) != GitMode;

    protected override Task PrepareAsync(RunContext context, CancellationToken cancellationToken)
    {
        if (Mode(context) is not (FilesystemMode or GitMode))
        {
            throw new InvalidOperationException($"TruffleHog mode must be \"{FilesystemMode}\" or \"{GitMode}\", not \"{Mode(context)}\".");
        }

        return base.PrepareAsync(context, cancellationToken);
    }

    protected override async Task<RepoWorkResult> ScanRepoAsync(RepoWorkContext work, CancellationToken cancellationToken)
    {
        var context = work.Run;
        var settings = Settings(context);
        var started = Time.GetUtcNow();
        EnsureResultsFolder(work);

        var git = Mode(context) == GitMode;
        List<string> args = git
            ? ["git", "file://" + work.Repo.LocalPath!.Replace('\\', '/')]
            : ["filesystem", work.WorkingPath!];

        args.AddRange(["--json", "--no-update"]);
        args.Add(settings.Verify == true ? "--results=verified,unknown" : "--no-verification");
        if (context.Config.ExclusionsFile is { } exclusions)
        {
            args.Add($"--exclude-paths={exclusions}");
        }

        var spec = await BuildSpecAsync(work, new ProcessSpec
        {
            FileName = Exe(context),
            Arguments = args,
            WorkingDirectory = git ? null : work.WorkingPath,
            StdOutFile = work.ResultFile
        }, cancellationToken);

        var process = await context.Processes.RunAsync(spec, cancellationToken);
        if (!process.Succeeded)
        {
            return Failure(process);
        }

        return Success(started, ScanOutput.CountLines(work.ResultFile), severities: ScanOutput.TruffleHogSeverities(work.ResultFile));
    }
}
