using System.Text.RegularExpressions;
using Vizient.AutomatedScanner.Core.Processes;
using Vizient.AutomatedScanner.Core.Work;

namespace Vizient.AutomatedScanner.Core.Tools;

/// <summary>
/// Tool 8: SonarQube analysis of .NET repos (a .sln or .csproj is tracked). <c>dotnet-sonarscanner begin</c>, then
/// an SDK-style <c>dotnet build</c> or Visual Studio <c>MSBuild.exe</c> for legacy projects, then <c>end</c>, so the
/// analysis sees the compiled code. Then the same wait and fetch as <see cref="SonarStandardTool"/>. It builds the repo, which runs the repo's
/// code, so it runs under the locked-down account (<c>runAs</c>) when engine.json names one. A failed restore or
/// build is BuildFailed, and the repo is not retried with the standard scanner. A repo that is not .NET is skipped
/// as NotApplicable.
/// </summary>
/// <remarks>
/// The scanner's begin and end steps take the token as an argument (/d:sonar.token), so it is on that command line
/// for the length of the step. It is masked in everything the engine records.
/// </remarks>
public sealed partial class SonarDotNetTool(
    HttpMessageHandler? handler = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : SonarTool(handler, delay)
{
    public override string Name => ToolNames.SonarDotNet;

    protected override string DefaultExe => "dotnet-sonarscanner";

    protected override IEnumerable<string> OtherPrograms(RunContext context) => [DotnetExe(context)];

    protected override bool NeedsWorkingCopy(RunContext context) => true;

    // The scanner's project key allows only letters, digits and - _ . :
    [GeneratedRegex(@"[^A-Za-z0-9\-_.:]")]
    private static partial Regex InvalidKeyCharacters();

    private string DotnetExe(RunContext context) =>
        string.IsNullOrWhiteSpace(Settings(context).DotnetPath) ? "dotnet" : Settings(context).DotnetPath!;

    protected override async Task<RepoWorkResult> ScanRepoAsync(RepoWorkContext work, CancellationToken cancellationToken)
    {
        var context = work.Run;
        var started = Time.GetUtcNow();
        var deadline = started + work.Limits.Timeout;

        var files = await DotNetProjects.FindAsync(context, work.WorkingPath!, cancellationToken);
        if (DotNetProjects.BuildTarget(files) is not { } target)
        {
            return NotApplicable();
        }

        var useMsbuild = DotNetProjects.RequiresMsbuild(work.WorkingPath!, target);
        var buildProgram = useMsbuild ? await MsbuildExeAsync(context, cancellationToken) : DotnetExe(context);
        var targetPath = Path.Combine(work.WorkingPath!, target);
        var token = await TokenAsync(context, cancellationToken);
        var key = InvalidKeyCharacters().Replace(work.Repo.Key, "-");
        var environment = new Dictionary<string, string>
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_CLI_UI_LANGUAGE"] = "en",
            ["MSBUILDDISABLENODEREUSE"] = "1"
        };

        // Each step gets what is left of the repo's time. None is retried: a second begin after a half-done run would
        // analyze a different build, and a build that failed once will fail again.
        async Task<ProcessResult> StepAsync(string program, List<string> args)
        {
            var spec = await BuildSpecAsync(work, new ProcessSpec
            {
                FileName = program,
                Arguments = args,
                WorkingDirectory = work.WorkingPath,
                Environment = environment,
                SecretValues = [token]
            }, cancellationToken, Remaining(deadline, Time.GetUtcNow()), RetryPolicy.None);
            return await context.Processes.RunAsync(spec, cancellationToken);
        }

        List<string> begin =
        [
            "begin",
            $"/k:{key}",
            $"/n:{work.Repo.Name}",
            $"/d:sonar.host.url={ServerUrl(context)}",
            $"/d:sonar.token={token}"
        ];
        if (Exclusions.ForSonar(context.Config) is { Count: > 0 } patterns)
        {
            begin.Add($"/d:sonar.exclusions={string.Join(',', patterns)}");
        }

        var begun = await StepAsync(Exe(context), begin);
        if (!begun.Succeeded)
        {
            return Failure(begun);
        }

        List<string> buildArguments = useMsbuild
            ? new List<string> { "-restore", targetPath, "/nologo", "/verbosity:minimal" }
            : ["build", targetPath, "--nologo", "-v:minimal"];
        var build = await StepAsync(buildProgram, buildArguments);
        if (!build.Succeeded)
        {
            // Build tools report diagnostics on standard output.
            var detail = Tail(build.StdOut + " " + build.StdErr, 600);
            return RepoWorkResult.Failed(
                build.Outcome == ProcessOutcome.TimedOut ? "Timeout" : "BuildFailed",
                $"{build.Message} | {detail}",
                build.Attempts);
        }

        var end = await StepAsync(Exe(context), ["end", $"/d:sonar.token={token}"]);
        if (!end.Succeeded)
        {
            return Failure(end);
        }

        var reportTask = Directory.Exists(Path.Combine(work.WorkingPath!, ".sonarqube"))
            ? Directory.EnumerateFiles(Path.Combine(work.WorkingPath!, ".sonarqube"), "report-task.txt", SearchOption.AllDirectories).FirstOrDefault()
            : null;

        return await CollectAsync(work, key, reportTask ?? Path.Combine(work.WorkingPath!, ".sonarqube", "report-task.txt"), started, deadline, cancellationToken);
    }

    private static async Task<string> MsbuildExeAsync(RunContext context, CancellationToken cancellationToken)
    {
        var configuredPath = context.Config.Current.Tool(ToolNames.SonarDotNet).MsbuildPath;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!ExecutableFinder.Exists(configuredPath))
            {
                throw new InvalidOperationException($"SonarDotNet cannot find the configured MSBuild executable '{configuredPath}'.");
            }

            return configuredPath;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var vswhere = Path.Combine(programFiles, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(vswhere))
        {
            var result = await context.Processes.RunAsync(new ProcessSpec
            {
                FileName = vswhere,
                Arguments = ["-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe"],
                Timeout = TimeSpan.FromSeconds(15)
            }, cancellationToken);
            if (result.Succeeded)
            {
                var path = result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim().Trim('"'))
                    .FirstOrDefault(File.Exists);
                if (path is not null)
                {
                    return path;
                }
            }
        }

        if (ExecutableFinder.Exists("MSBuild.exe"))
        {
            return "MSBuild.exe";
        }

        throw new InvalidOperationException(
            "SonarDotNet needs Visual Studio MSBuild for this legacy project, but MSBuild.exe was not found. " +
            "Install the Visual Studio Build Tools MSBuild component or set tools.SonarDotNet.msbuildPath in engine.json.");
    }
}
