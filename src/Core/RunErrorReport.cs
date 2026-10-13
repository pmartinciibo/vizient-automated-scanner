using System.Text.Json;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Core;

/// <summary>Everything that went wrong in the run, for results.json: the errors logged as they happened plus every repo that is still failed.</summary>
public static class RunErrorReport
{
    private const string FailedSuffix = ".failed.json";

    public static async Task<List<RunError>> BuildAsync(RunContext context, string? runError, CancellationToken cancellationToken = default)
    {
        var errors = new List<RunError>(context.Errors.Snapshot());

        if (!string.IsNullOrWhiteSpace(runError))
        {
            errors.Add(new RunError { Scope = RunErrorScope.Run, At = DateTimeOffset.UtcNow, Message = runError });
        }

        // The failed markers are the record of a repo's failure. Reading them rather than counting as it happened means a
        // repo that failed in an earlier attempt and still fails is here, and one that was retried successfully is not.
        var repos = context.Repos.Snapshot()
            .GroupBy(r => r.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var tool in ToolNames.All)
        {
            var directory = context.Paths.ResultsDir(tool);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*" + FailedSuffix).Order(StringComparer.OrdinalIgnoreCase))
            {
                var key = Path.GetFileName(file)[..^FailedSuffix.Length];
                var at = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                repos.TryGetValue(key, out var repo);

                try
                {
                    var marker = await AtomicJsonFile.ReadAsync<ToolFailedMarker>(file, cancellationToken);
                    errors.Add(new RunError
                    {
                        Scope = RunErrorScope.Repo,
                        At = at,
                        Tool = tool,
                        Category = marker.Category,
                        Message = marker.Message,
                        Source = repo?.Source,
                        Org = repo?.Org,
                        Project = repo?.Project,
                        Key = key,
                        Repo = repo?.Name,
                        Attempts = marker.Attempts,
                        Sha = marker.Sha
                    });
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
                {
                    errors.Add(new RunError
                    {
                        Scope = RunErrorScope.Repo,
                        At = at,
                        Tool = tool,
                        Category = "Unreadable",
                        Message = $"The failure marker could not be read: {ex.Message}",
                        Key = key
                    });
                }
            }
        }

        return errors.OrderBy(e => e.At).ToList();
    }
}
