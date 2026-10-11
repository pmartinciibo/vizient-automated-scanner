using Vizient.AutomatedScanner.Core.Processes;
using Vizient.AutomatedScanner.Core.Work;

namespace Vizient.AutomatedScanner.Core.Tools;

/// <summary>
/// Tool 7: SonarQube analysis of repos that are not .NET (no .sln or .csproj tracked). Runs the standard
/// Sonar Scanner CLI in the working copy with the repo key as the project key, waits for the server to finish the
/// analysis, and fetches the issues. The token is the SONAR_TOKEN environment variable, which the scanner reads, so it is
/// on no command line. A .NET repo is left to <see cref="SonarDotNetTool"/>: this tool skips it as NotApplicable.
/// </summary>
public sealed class SonarStandardTool(
    HttpMessageHandler? handler = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : SonarTool(handler, delay)
{
    public override string Name => ToolNames.SonarStandard;

    protected override string DefaultExe => "sonar-scanner.bat";

    protected override bool NeedsWorkingCopy(RunContext context) => true;

    protected override async Task<RepoWorkResult> ScanRepoAsync(RepoWorkContext work, CancellationToken cancellationToken)
    {
        var context = work.Run;
        var started = Time.GetUtcNow();
        var deadline = started + work.Limits.Timeout;

        if ((await DotNetProjects.FindAsync(context, work.WorkingPath!, cancellationToken)).Count > 0)
        {
            return NotApplicable();
        }

        List<string> args =
        [
            $"-Dsonar.projectKey={work.Repo.Key}",
            $"-Dsonar.projectName={work.Repo.Name}",
            "-Dsonar.sources=.",
            $"-Dsonar.host.url={ServerUrl(context)}"
        ];
        if (Exclusions.ForSonar(context.Config) is { Count: > 0 } patterns)
        {
            args.Add($"-Dsonar.exclusions={string.Join(',', patterns)}");
        }

        var spec = await BuildSpecAsync(work, new ProcessSpec
        {
            FileName = Exe(context),
            Arguments = args,
            WorkingDirectory = work.WorkingPath
        }, cancellationToken);

        var scan = await context.Processes.RunAsync(spec, cancellationToken);
        if (!scan.Succeeded)
        {
            return Failure(scan);
        }

        return await CollectAsync(
            work,
            work.Repo.Key,
            Path.Combine(work.WorkingPath!, ".scannerwork", "report-task.txt"),
            started,
            deadline,
            cancellationToken);
    }
}
