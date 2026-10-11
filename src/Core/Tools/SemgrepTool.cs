using Vizient.AutomatedScanner.Core.Processes;
using Vizient.AutomatedScanner.Core.Work;

namespace Vizient.AutomatedScanner.Core.Tools;

/// <summary>
/// Tool 5: static analysis. <c>semgrep scan --config &lt;pack&gt; ... --json --metrics off --exclude &lt;glob&gt; ...</c> in the working
/// copy, once per repo, one --config for each pinned rule pack in engine.json (p/default if none). Pinned packs are
/// part of the skip key, so changing them rescans everything. Output is Semgrep's own JSON.
/// </summary>
public sealed class SemgrepTool : ProcessScanTool
{
    public const string DefaultPack = "p/default";

    public override string Name => ToolNames.Semgrep;

    protected override string DefaultExe => "semgrep";

    protected override bool NeedsWorkingCopy(RunContext context) => true;

    protected override async Task<RepoWorkResult> ScanRepoAsync(RepoWorkContext work, CancellationToken cancellationToken)
    {
        var context = work.Run;
        var started = Time.GetUtcNow();
        EnsureResultsFolder(work);

        List<string> args = ["scan"];
        foreach (var pack in Settings(context).Packs is { Count: > 0 } packs ? packs : [DefaultPack])
        {
            args.AddRange(["--config", pack]);
        }

        // "auto" and metrics off cannot go together, which is why packs are named.
        args.AddRange(["--json", "--metrics", "off", "--disable-version-check"]);
        foreach (var pattern in Exclusions.ForSemgrep(context.Config))
        {
            args.AddRange(["--exclude", pattern]);
        }

        args.Add(".");

        var spec = await BuildSpecAsync(work, new ProcessSpec
        {
            FileName = Exe(context),
            Arguments = args,
            WorkingDirectory = work.WorkingPath,
            Environment = new Dictionary<string, string>
            {
                // Semgrep is Python. Without these a repo with non-ASCII text can fail on a Windows code page.
                ["PYTHONUTF8"] = "1",
                ["PYTHONIOENCODING"] = "utf-8",
                ["SEMGREP_SEND_METRICS"] = "off"
            },
            StdOutFile = work.ResultFile
        }, cancellationToken);

        var process = await context.Processes.RunAsync(spec, cancellationToken);
        if (!process.Succeeded)
        {
            return Failure(process);
        }

        try
        {
            return Success(started, ScanOutput.CountArray(work.ResultFile, "results"));
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            return RepoWorkResult.Failed("ParseError", $"Semgrep's output could not be read: {ex.Message}");
        }
    }
}
