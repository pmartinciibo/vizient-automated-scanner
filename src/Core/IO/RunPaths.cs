namespace Vizient.AutomatedScanner.Core.IO;

/// <summary>The one place that knows the layout of a run folder (design section 5).</summary>
public sealed record RunPaths(string RunFolder)
{
    public string RunId => Path.GetFileName(RunFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    // Inputs
    public string Request => Path.Combine(RunFolder, "request.json");
    public string Command => Path.Combine(RunFolder, "command.json");

    // Outputs. The live status.json and results.json sit beside the request (see InvocationPaths);
    // these two are the copies kept here when the run ends.
    public string Status => Path.Combine(RunFolder, "status.json");
    public string ResultsSummary => Path.Combine(RunFolder, "results.json");
    public string RepoChanges => Path.Combine(RunFolder, "repo-changes.json");
    public string Results => Path.Combine(RunFolder, "results");

    public string ResultsDir(string tool) => Path.Combine(Results, tool);

    /// <summary>results\&lt;tool&gt;.json: the detail of one step, written when the run ends. Beside the tool's own folder.</summary>
    public string ToolResult(string tool) => Path.Combine(Results, tool + ".json");

    // Per-entity result files, results\<tool>\<key>.*  (key is a repo key, r7-<id> or cx-<id>)
    public string ResultFile(string tool, string key) => Entity(tool, key, ".json");
    public string DoneMarker(string tool, string key) => Entity(tool, key, ".done.json");
    public string FailedMarker(string tool, string key) => Entity(tool, key, ".failed.json");
    public string SkippedMarker(string tool, string key) => Entity(tool, key, ".skipped.json");
    public string ImportDoneMarker(string tool) => Path.Combine(ResultsDir(tool), "_import.done.json");

    private string Entity(string tool, string key, string suffix)
    {
        if (!SafeName.IsValid(key))
        {
            throw new ArgumentException($"'{key}' is not a valid result key.", nameof(key));
        }

        return Path.Combine(ResultsDir(tool), key + suffix);
    }
}
