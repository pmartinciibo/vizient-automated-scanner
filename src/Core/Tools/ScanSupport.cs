using System.Text.Json;
using System.Text.RegularExpressions;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.RepoClone;

namespace Vizient.AutomatedScanner.Core.Tools;

/// <summary>
/// The shared exclusions file (one entry a line: a regular expression for file names such as <c>.*\.dll$</c>, or a plain
/// folder name such as <c>bin</c>), in the form each scanner wants it. The same conversions the scan scripts used.
/// </summary>
public static partial class Exclusions
{
    // ".*\.ext$" is the one pattern shape that converts to a glob.
    [GeneratedRegex(@"^\.\*\\\.(?<ext>[A-Za-z0-9]+)\$$")]
    private static partial Regex Extension();

    public static IReadOnlyList<string> Lines(EngineConfig config) =>
        config.ExclusionsFile is null
            ? []
            : File.ReadAllLines(config.ExclusionsFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>Semgrep --exclude takes globs: ".*\.yml$" becomes "*.yml", plain names pass through.</summary>
    public static IReadOnlyList<string> ForSemgrep(EngineConfig config) =>
        Lines(config).Select(line => Extension().Match(line) is { Success: true } m ? $"*.{m.Groups["ext"].Value}" : line).ToList();

    /// <summary>Snyk --exclude takes only plain names, so entries with any pattern characters are dropped.</summary>
    public static IReadOnlyList<string> ForSnyk(EngineConfig config) =>
        Lines(config).Where(line => !PatternCharacters().IsMatch(line)).ToList();

    /// <summary>Sonar takes Ant-style paths: ".*\.yml$" becomes "**/*.yml", "bin" becomes "**/bin/**".</summary>
    public static IReadOnlyList<string> ForSonar(EngineConfig config) =>
        Lines(config).Select(line => Extension().Match(line) is { Success: true } m ? $"**/*.{m.Groups["ext"].Value}" : $"**/{line}/**").ToList();

    [GeneratedRegex(@"[\\.*$^+?(){}\[\]|]")]
    private static partial Regex PatternCharacters();
}

/// <summary>Reads the number of findings out of what a tool wrote, so the skip policy can compare it with the last scan.</summary>
internal static class ScanOutput
{
    /// <summary>Lines that are not blank. TruffleHog writes one JSON object per finding.</summary>
    public static int CountLines(string path)
    {
        var count = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>The length of the array in a property at the top of the file, for example Semgrep's "results".</summary>
    public static int CountArray(string path, string property)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty(property, out var array)
            && array.ValueKind == JsonValueKind.Array
                ? array.GetArrayLength()
                : throw new InvalidDataException($"The output has no \"{property}\" list.");
    }

    /// <summary>TruffleHog's findings by severity: a line each, and every one is High, verified or not.</summary>
    public static SeverityCounts TruffleHogSeverities(string path)
    {
        var counts = new SeverityCounts();
        foreach (var line in File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                counts = counts.Plus(SeverityMapper.MapSecret());
            }
            catch (JsonException)
            {
                // A line that is not a finding object is still counted as a line, so it shows up rather than vanishing.
                counts = counts.Plus(NormalizedSeverity.Unmapped);
            }
        }

        return counts;
    }

    /// <summary>Semgrep's findings by severity, from each result's "extra.severity".</summary>
    public static SeverityCounts SemgrepSeverities(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var severities = document.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Select(r => r.TryGetProperty("extra", out var extra) ? StringOf(extra, "severity") : null).ToList()
            : [];
        return SeverityMapper.Count(ToolNames.Semgrep, severities);
    }

    /// <summary>
    /// Sonar's findings by severity, from the file CollectAsync wrote ({ project, total, issues }). An issue that has
    /// "impacts" (newer servers) goes by the highest of them; one without goes by its classic "severity".
    /// </summary>
    public static SeverityCounts SonarSeverities(string tool, string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var counts = new SeverityCounts();
        if (document.RootElement.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array)
        {
            foreach (var issue in issues.EnumerateArray())
            {
                var impacts = issue.ValueKind == JsonValueKind.Object && issue.TryGetProperty("impacts", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(i => StringOf(i, "severity")).ToList()
                    : [];
                counts = counts.Plus(SeverityMapper.MapSonar(tool, impacts, StringOf(issue, "severity")));
            }
        }

        return counts;
    }

    /// <summary>
    /// Snyk's findings by severity. A SARIF result (Snyk Code) goes by its "level" (error, warning, note), and an open source
    /// vulnerability by its own "severity", not its priority score. Anything else, or none, is Moderate.
    /// </summary>
    public static SeverityCounts SnykSeverities(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var counts = new SeverityCounts();
        Collect(document.RootElement);
        return counts;

        void Collect(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Collect(item);
                    }

                    break;
                case JsonValueKind.Object:
                    if (element.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var run in runs.EnumerateArray())
                        {
                            if (run.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var result in results.EnumerateArray())
                                {
                                    counts = counts.Plus(SeverityMapper.Map(ToolNames.Snyk, StringOf(result, "level")));
                                }
                            }
                        }
                    }
                    else if (element.TryGetProperty("vulnerabilities", out var vulns) && vulns.ValueKind == JsonValueKind.Array)
                    {
                        counts = vulns.EnumerateArray().Aggregate(counts, (c, v) => c.Plus(SeverityMapper.Map(ToolNames.Snyk, StringOf(v, "severity"))));
                    }

                    break;
            }
        }
    }

    private static string? StringOf(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Snyk's findings. Snyk Code writes SARIF (runs, each with results). Snyk open source writes an object with
    /// vulnerabilities, or a list of them for several projects. All are counted the same way.
    /// </summary>
    public static int CountSnyk(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return Count(document.RootElement);

        static int Count(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    return element.EnumerateArray().Sum(Count);
                case JsonValueKind.Object:
                    if (element.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
                    {
                        return runs.EnumerateArray().Sum(run =>
                            run.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array ? results.GetArrayLength() : 0);
                    }

                    return element.TryGetProperty("vulnerabilities", out var vulns) && vulns.ValueKind == JsonValueKind.Array
                        ? vulns.GetArrayLength()
                        : 0;
                default:
                    return 0;
            }
        }
    }
}

/// <summary>Looks inside a repo's working copy for what decides which Sonar scanner applies.</summary>
public static class DotNetProjects
{
    /// <summary>The .sln and .csproj files git tracks in the working copy, paths relative to it. Empty if there are none.</summary>
    public static async Task<IReadOnlyList<string>> FindAsync(RunContext context, string workingPath, CancellationToken cancellationToken)
    {
        var settings = context.Config.Current.Tool(ToolNames.RepoClone);
        var git = new GitRunner(
            context,
            ToolLimits.For(context, ToolNames.RepoClone),
            string.IsNullOrWhiteSpace(settings.GitPath) ? "git" : settings.GitPath,
            settings.LongPaths ?? true);

        var result = await git.LocalAsync(workingPath, ["ls-files", "-z", "--", "*.sln", "*.csproj"], cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Could not list the files of the repo: {result.Message}");
        }

        return result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
    }

    /// <summary>
    /// What to build: the .sln nearest the top (then the shortest name), so a repo with several projects builds as
    /// one; otherwise the .csproj nearest the top. Null if there is neither.
    /// </summary>
    public static string? BuildTarget(IReadOnlyList<string> files)
    {
        static int Depth(string path) => path.Count(c => c is '/' or '\\');

        IEnumerable<string> Of(string extension) =>
            files.Where(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                 .OrderBy(Depth)
                 .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                 .ThenBy(f => f, StringComparer.Ordinal);

        return Of(".sln").Concat(Of(".csproj")).FirstOrDefault();
    }
}
