using System.Text.Json;
using Vizient.AutomatedScanner.Core.CheckMarx;
using Vizient.AutomatedScanner.Core.Imports;
using Vizient.AutomatedScanner.Core.IO;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Rapid7;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Core;

/// <summary>Writes results\&lt;Tool&gt;.json, one file per step, into the run folder when the run ends.</summary>
public static class ToolResultsWriter
{
    private const string DoneSuffix = ".done.json";
    private const string FailedSuffix = ".failed.json";
    private const string SkippedSuffix = ".skipped.json";

    /// <summary>The path of a step's file relative to the run folder, as results.json names it.</summary>
    public static string RelativePath(string tool) => $"results/{tool}.json";

    public static async Task WriteAllAsync(
        RunContext context,
        IReadOnlyList<ToolProgress> steps,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(context.Paths.Results);

        foreach (var step in steps)
        {
            var document = await BuildAsync(context, step, cancellationToken);
            await AtomicJsonFile.WriteAsync(context.Paths.ToolResult(step.Name), document, cancellationToken);
        }
    }

    private static async Task<ToolResultDocument> BuildAsync(RunContext context, ToolProgress step, CancellationToken cancellationToken)
    {
        var tool = step.Name;
        var isRepoClone = tool == ToolNames.RepoClone;
        var isImport = tool is ToolNames.Rapid7 or ToolNames.CheckMarx;

        return new ToolResultDocument
        {
            RunId = context.Request.RunId,
            Tool = tool,
            Summary = ToolOutcome.From(step, RelativePath(tool)),
            Import = isImport ? await ReadImportAsync(context.Paths.ImportDoneMarker(tool), cancellationToken) : null,
            RepoTotals = isRepoClone ? RepoSummary.From(context.Repos.Snapshot()) : null,
            Orgs = isRepoClone ? [.. context.Repos.Orgs()] : null,
            Items = [.. isImport ? ReadImportedApps(context.Paths.ResultFile(tool, ImportKey(tool))) : [],
                .. await ReadItemsAsync(context.Paths.ResultsDir(tool), cancellationToken)]
        };
    }

    private static string ImportKey(string tool) => tool == ToolNames.Rapid7 ? Rapid7Importer.OutputKey : CheckMarxImporter.OutputKey;

    // An import has no per-repo markers. Its record of what happened is the one raw file, summarised here by application
    // (the file itself can be gigabytes and is not copied into this one).
    private static List<ToolItemResult> ReadImportedApps(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return ImportFileSummarizer.Summarize(path);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [new ToolItemResult { Key = Path.GetFileName(path), Outcome = "Unreadable" }];
        }
    }

    private static async Task<ImportDoneMarker?> ReadImportAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return await AtomicJsonFile.ReadAsync<ImportDoneMarker>(path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    // The markers are the record of what happened to each repo. Names starting "_" belong to the tool, not to an item.
    private static async Task<List<ToolItemResult>> ReadItemsAsync(string directory, CancellationToken cancellationToken)
    {
        var items = new List<ToolItemResult>();
        if (!Directory.Exists(directory))
        {
            return items;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith('_'))
            {
                continue;
            }

            try
            {
                if (name.EndsWith(DoneSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(new ToolItemResult
                    {
                        Key = name[..^DoneSuffix.Length],
                        Outcome = "Done",
                        Done = await AtomicJsonFile.ReadAsync<ToolDoneMarker>(file, cancellationToken)
                    });
                }
                else if (name.EndsWith(FailedSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(new ToolItemResult
                    {
                        Key = name[..^FailedSuffix.Length],
                        Outcome = "Failed",
                        Failed = await AtomicJsonFile.ReadAsync<ToolFailedMarker>(file, cancellationToken)
                    });
                }
                else if (name.EndsWith(SkippedSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(new ToolItemResult
                    {
                        Key = name[..^SkippedSuffix.Length],
                        Outcome = "Skipped",
                        Skipped = await AtomicJsonFile.ReadAsync<ToolSkippedMarker>(file, cancellationToken)
                    });
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
            {
                items.Add(new ToolItemResult { Key = name, Outcome = "Unreadable" });
            }
        }

        return items;
    }
}
