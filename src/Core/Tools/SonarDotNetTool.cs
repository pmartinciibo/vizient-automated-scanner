using System.Text.RegularExpressions;
using Vizient.AutomatedScanner.Core.Processes;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.Work;

namespace Vizient.AutomatedScanner.Core.Tools;

/// <summary>
/// Tool 8: SonarQube analysis of .NET repos (a .sln or .csproj is tracked). <c>dotnet-sonarscanner begin</c>, then
/// SDK-style projects with <c>dotnet build</c> and legacy projects with the Framework SonarScanner and Visual Studio
/// <c>MSBuild.exe</c>. A build failure falls back to SonarScanner CLI without a build. All builds run under the
/// locked-down account (<c>runAs</c>) when engine.json names one.
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
        if (DotNetProjects.IsSsisOnly(files))
        {
            return RepoWorkResult.Skipped(new Models.ToolSkippedMarker { Reason = "NotApplicable" });
        }

        if (DotNetProjects.BuildTarget(files, work.WorkingPath) is not { } target)
        {
            return NotApplicable();
        }

        var useMsbuild = DotNetProjects.RequiresMsbuild(work.WorkingPath!, target);
        string buildProgram;
        try
        {
            buildProgram = useMsbuild ? await MsbuildExeAsync(context, cancellationToken) : DotnetExe(context);
        }
        catch (InvalidOperationException ex) when (useMsbuild)
        {
            return await RunBuildlessFallbackOrFailAsync(
                work, started, deadline, "BuildToolsUnavailable", $"BuildToolsUnavailable: {ex.Message}", null, null, cancellationToken);
        }

        var scanner = useMsbuild ? MsbuildScannerExe(context) : Exe(context);
        if (scanner is null)
        {
            return await RunBuildlessFallbackOrFailAsync(
                work,
                started,
                deadline,
                "FrameworkScannerUnavailable",
                "FrameworkScannerUnavailable: Framework SonarScanner is not installed or tools.SonarDotNet.msbuildScannerPath is not set.",
                null,
                null,
                cancellationToken);
        }

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

        var begun = await StepAsync(scanner, begin);
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
            var failure = ClassifyBuildFailure(build.StdOut + "\n" + build.StdErr);
            var diagnostic = await WriteBuildDiagnosticsAsync(work, build, cancellationToken);
            if (build.Outcome == ProcessOutcome.TimedOut)
            {
                return RepoWorkResult.Failed(
                    "Timeout",
                    $"{build.Message} | {Tail(build.StdOut + " " + build.StdErr, 600)}{diagnostic.MessageSuffix}",
                    build.Attempts,
                    failureCode: failure.Code,
                    diagnosticFile: diagnostic.RelativePath);
            }

            return await RunBuildlessFallbackOrFailAsync(
                work,
                started,
                deadline,
                failure.Category,
                $"{failure.Category}{(failure.Code is null ? string.Empty : $" ({failure.Code})")}: {Tail(build.StdOut + " " + build.StdErr, 600)}{diagnostic.MessageSuffix}",
                failure.Code,
                diagnostic.RelativePath,
                cancellationToken);
        }

        var end = await StepAsync(scanner, ["end", $"/d:sonar.token={token}"]);
        if (!end.Succeeded)
        {
            return Failure(end);
        }

        var reportTask = Directory.Exists(Path.Combine(work.WorkingPath!, ".sonarqube"))
            ? Directory.EnumerateFiles(Path.Combine(work.WorkingPath!, ".sonarqube"), "report-task.txt", SearchOption.AllDirectories).FirstOrDefault()
            : null;

        return await CollectAsync(work, key, reportTask ?? Path.Combine(work.WorkingPath!, ".sonarqube", "report-task.txt"), started, deadline, cancellationToken);
    }

    private async Task<RepoWorkResult> RunBuildlessFallbackOrFailAsync(
        RepoWorkContext work,
        DateTimeOffset started,
        DateTimeOffset deadline,
        string failureCategory,
        string buildFailure,
        string? failureCode,
        string? diagnosticFile,
        CancellationToken cancellationToken)
    {
        if (Settings(work.Run).FallbackToStandardScanner == false)
        {
            return RepoWorkResult.Failed(failureCategory, buildFailure, failureCode: failureCode, diagnosticFile: diagnosticFile);
        }

        var standardSettings = work.Run.Config.Current.Tool(ToolNames.SonarStandard);
        var scanner = string.IsNullOrWhiteSpace(standardSettings.ExePath) ? "sonar-scanner" : standardSettings.ExePath!;
        if (!ExecutableFinder.Exists(scanner))
        {
            return RepoWorkResult.Failed(
                failureCategory,
                $"{buildFailure} | SonarScanner CLI was not found; install it or set tools.SonarStandard.exePath.",
                failureCode: failureCode,
                diagnosticFile: diagnosticFile);
        }

        var token = await TokenAsync(work.Run, cancellationToken);
        var key = InvalidKeyCharacters().Replace(work.Repo.Key, "-");
        var args = new List<string>
        {
            $"-Dsonar.projectKey={key}",
            $"-Dsonar.projectName={work.Repo.Name}",
            "-Dsonar.sources=.",
            $"-Dsonar.host.url={ServerUrl(work.Run)}",
            $"-Dsonar.token={token}"
        };
        var exclusions = Exclusions.ForSonar(work.Run.Config).Append(".sonarqube/**").ToList();
        if (exclusions.Count > 0)
        {
            args.Add($"-Dsonar.exclusions={string.Join(',', exclusions)}");
        }

        var spec = await BuildSpecAsync(work, new ProcessSpec
        {
            FileName = scanner,
            Arguments = args,
            WorkingDirectory = work.WorkingPath,
            Environment = new Dictionary<string, string>
            {
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_NOLOGO"] = "1"
            },
            SecretValues = [token]
        }, cancellationToken, Remaining(deadline, Time.GetUtcNow()), RetryPolicy.None, ToolNames.SonarStandard);
        var result = await work.Run.Processes.RunAsync(spec, cancellationToken);
        if (!result.Succeeded)
        {
            return RepoWorkResult.Failed(
                failureCategory,
                $"{buildFailure} | SonarScanner CLI: {result.Message} | {Tail(result.StdOut + " " + result.StdErr, 600)}",
                result.Attempts,
                failureCode: failureCode,
                diagnosticFile: diagnosticFile);
        }

        var reportTask = Path.Combine(work.WorkingPath!, ".scannerwork", "report-task.txt");
        var collected = await CollectAsync(work, key, reportTask, started, deadline, cancellationToken);
        if (collected.Kind == RepoWorkKind.Done)
        {
            return RepoWorkResult.Done(collected.DoneMarker! with
            {
                ScanMode = "buildless-fallback",
                FallbackFrom = failureCategory,
                FailureCode = failureCode,
                DiagnosticFile = diagnosticFile
            });
        }

        return collected;
    }

    private static async Task<(string? RelativePath, string MessageSuffix)> WriteBuildDiagnosticsAsync(
        RepoWorkContext work,
        ProcessResult build,
        CancellationToken cancellationToken)
    {
        var relativePath = Path.Combine("logs", ToolNames.SonarDotNet, $"{work.Repo.Key}.build.json");
        var fullPath = Path.Combine(work.Run.Paths.RunFolder, relativePath);
        try
        {
            await AtomicJsonFile.WriteAsync(fullPath, new
            {
                schemaVersion = 1,
                standardOutput = build.StdOut,
                standardError = build.StdErr,
                standardOutputTruncated = build.StdOutTruncated,
                standardErrorTruncated = build.StdErrTruncated
            }, cancellationToken);
            return (relativePath, $" | full build output: {relativePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $" | could not preserve full build output: {ex.Message}");
        }
    }

    private static (string Category, string? Code) ClassifyBuildFailure(string output)
    {
        var codeMatch = Regex.Match(output, @"\berror\s+(?<code>(?:MSB|NU|CS|BC)\d{4})\b", RegexOptions.IgnoreCase);
        if (!codeMatch.Success)
        {
            codeMatch = Regex.Match(output, @"\b(?<code>(?:MSB|NU|CS|BC)\d{4})\b", RegexOptions.IgnoreCase);
        }

        var code = codeMatch.Groups["code"].Value.ToUpperInvariant();
        var category = code == "CS0246" && Regex.IsMatch(output, @"\bMSB3284\b.*Library not registered", RegexOptions.IgnoreCase | RegexOptions.Singleline)
            ? "MissingComReference"
            : code switch
        {
            "MSB4019" or "MSB4278" => "MissingTargets",
            "MSB3644" => "MissingTargetingPack",
            "MSB3202" => "MissingProjectFile",
            "MSB3073" => "CustomBuildCommand",
            "MSB4803" => "FrameworkMSBuildRequired",
            "NU1301" => "NuGetFeed",
            "NU1101" or "NU1102" => "NuGetPackageNotFound",
            "NU1202" => "IncompatiblePackage",
            "MSB4249" => "FrameworkMSBuildRequired",
            "MSB4067" => "InvalidProjectFile",
            _ when code.StartsWith("CS", StringComparison.Ordinal) || code.StartsWith("BC", StringComparison.Ordinal) => "CompilerError",
            _ when code.StartsWith("NU", StringComparison.Ordinal) => "NuGetRestoreFailed",
            _ when code.Length > 0 => "BuildDiagnostic",
            _ => "BuildFailed"
        };
        return (category, string.IsNullOrEmpty(code) ? null : code);
    }

    private static string? MsbuildScannerExe(RunContext context)
    {
        var configuredPath = context.Config.Current.Tool(ToolNames.SonarDotNet).MsbuildScannerPath;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return ExecutableFinder.Exists(configuredPath) ? configuredPath : null;
        }

        return ExecutableFinder.Exists("SonarScanner.MSBuild.exe") ? "SonarScanner.MSBuild.exe" : null;
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
