using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Processes;
using Vizient.AutomatedScanner.Core.Work;

namespace Vizient.AutomatedScanner.Core.Tools;

/// <summary>
/// Tool 6. Mode "code" (the default) is <c>snyk code test --json</c>. Mode "dependencies" is
/// <c>snyk test --all-projects --json</c>, which can run the repo's build tools, so it belongs under the locked-down
/// account (<c>runAs</c>), and it is never skipped: new CVEs appear without a code change. The token is the
/// SNYK_TOKEN environment variable. Snyk exits 1 when it finds something and 3 when there is nothing it can
/// scan. Both are clean runs.
/// </summary>
public sealed class SnykTool : ProcessScanTool
{
    public const string CodeMode = "code";
    public const string DependenciesMode = "dependencies";

    // 0: nothing found. 1: findings. 3: no supported files or projects. 2 and above that are failures.
    private static readonly int[] CleanExitCodes = [0, 1, 3];

    public override string Name => ToolNames.Snyk;

    protected override string DefaultExe => "snyk";

    protected override bool NeedsWorkingCopy(RunContext context) => true;

    private string Mode(RunContext context) => Settings(context).Mode ?? CodeMode;

    protected override Task PrepareAsync(RunContext context, CancellationToken cancellationToken)
    {
        if (Mode(context) is not (CodeMode or DependenciesMode))
        {
            throw new InvalidOperationException($"Snyk mode must be \"{CodeMode}\" or \"{DependenciesMode}\", not \"{Mode(context)}\".");
        }

        return base.PrepareAsync(context, cancellationToken);
    }

    protected override async Task<RepoWorkResult> ScanRepoAsync(RepoWorkContext work, CancellationToken cancellationToken)
    {
        var context = work.Run;
        var settings = Settings(context);
        var started = Time.GetUtcNow();
        EnsureResultsFolder(work);

        List<string> args = Mode(context) == DependenciesMode
            ? ["test", "--all-projects", "--json"]
            : ["code", "test", "--json"];

        if (!string.IsNullOrWhiteSpace(settings.Org))
        {
            args.Add($"--org={settings.Org}");
        }

        if (Exclusions.ForSnyk(context.Config) is { Count: > 0 } names)
        {
            args.Add($"--exclude={string.Join(',', names)}");
        }

        var spec = await BuildSpecAsync(work, new ProcessSpec
        {
            FileName = Exe(context),
            Arguments = args,
            WorkingDirectory = work.WorkingPath,
            SuccessExitCodes = CleanExitCodes,
            StdOutFile = work.ResultFile
        }, cancellationToken);

        var process = await context.Processes.RunAsync(spec, cancellationToken);
        if (!process.Succeeded)
        {
            return Failure(process);
        }

        // Exit 3 can leave no output at all. That is a scan of nothing.
        if (!File.Exists(work.ResultFile) || new FileInfo(work.ResultFile).Length == 0)
        {
            await File.WriteAllTextAsync(work.ResultFile, "[]", cancellationToken);
            return Success(started, 0, severities: new SeverityCounts());
        }

        try
        {
            return Success(started, ScanOutput.CountSnyk(work.ResultFile), severities: ScanOutput.SnykSeverities(work.ResultFile));
        }
        catch (System.Text.Json.JsonException ex)
        {
            return RepoWorkResult.Failed("ParseError", $"Snyk's output could not be read: {ex.Message}");
        }
    }
}
