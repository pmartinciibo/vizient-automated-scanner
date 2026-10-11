using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using Vizient.AutomatedScanner.Core.Imports;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Core.Rapid7;

/// <summary>What the import left behind.</summary>
/// <param name="Applications">Applications in the file.</param>
/// <param name="Vulnerabilities">Vulnerabilities in the file, across all of them.</param>
/// <param name="Completed">False if a Stop or DisableTool command ended it early, in which case nothing was kept.</param>
public sealed record Rapid7ImportResult(int Applications, long Vulnerabilities, bool Completed);

/// <summary>
/// The Rapid7 import (design section 9.2, tool 1). Reads applications and their vulnerabilities through the API and
/// writes them to one file, rapid7.json in the Rapid7 results folder: a list of apps, each with the list of its
/// vulnerabilities. Scans and reports are added to each app too if engine.json asks for them. _import.done.json,
/// written last, says the import is complete. It is never skipped: every run imports everything again.
/// </summary>
public sealed class Rapid7Importer(RunContext context, Rapid7Client client, Rapid7Options options)
{
    public const string Tool = ToolNames.Rapid7;

    /// <summary>The name of the output file, without .json.</summary>
    public const string OutputKey = "rapid7";

    /// <summary>The bucket for records that carry no application id. Not a valid id, so it cannot be mistaken for one.</summary>
    public const string UnassignedKey = "(unassigned)";

    // Names an application's own record might use that this file uses for its lists.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal) { "vulnerabilities", "scans", "reports" };

    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public string OutputPath => context.Paths.ResultFile(Tool, OutputKey);

    public async Task<Rapid7ImportResult> RunAsync(CancellationToken cancellationToken)
    {
        var resultsDir = context.Paths.ResultsDir(Tool);
        Directory.CreateDirectory(resultsDir);

        // An import that is running again is not complete until it finishes: drop the old marker and the old output.
        File.Delete(context.Paths.ImportDoneMarker(Tool));
        File.Delete(OutputPath);

        // Vulnerabilities are set aside here as they arrive, then put together. They can add up to gigabytes, so this
        // is on the configured temp drive, not in the results.
        var spoolDir = Path.Combine(context.Config.TmpRoot, $"rapid7-{Guid.NewGuid():N}");
        Directory.CreateDirectory(spoolDir);

        var applications = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var scans = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        var reports = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        var spools = new Dictionary<string, RecordSpool>(StringComparer.Ordinal);
        var totalVulnerabilities = 0L;

        try
        {
            // The small areas are held in memory.
            if (options.Includes(Rapid7Options.Applications))
            {
                await foreach (var record in ReadAreaAsync(Rapid7Options.Applications, cancellationToken))
                {
                    applications[KeyFor(record, topLevel: true)] = record;
                }
            }

            if (options.Includes(Rapid7Options.Scans))
            {
                await foreach (var record in ReadAreaAsync(Rapid7Options.Scans, cancellationToken))
                {
                    Group(scans, KeyFor(record, topLevel: false), record);
                }
            }

            if (options.Includes(Rapid7Options.Reports))
            {
                await foreach (var record in ReadAreaAsync(Rapid7Options.Reports, cancellationToken))
                {
                    Group(reports, KeyFor(record, topLevel: false), record);
                }
            }

            // The vulnerabilities are far too many to hold, so each goes straight to its application's spool file.
            if (options.Includes(Rapid7Options.Vulnerabilities))
            {
                var clock = Stopwatch.StartNew();
                await foreach (var page in client.GetPagesAsync(Rapid7Options.Vulnerabilities, cancellationToken))
                {
                    using (page)
                    {
                        await HoldWhilePausedOrStopAsync(cancellationToken);
                        var touched = new HashSet<RecordSpool>();

                        foreach (var vulnerability in page.Records)
                        {
                            var key = KeyFor(vulnerability, topLevel: false);
                            if (!spools.TryGetValue(key, out var spool))
                            {
                                spools[key] = spool = new RecordSpool(Path.Combine(spoolDir, $"{spools.Count:D6}.spool"));
                            }

                            spool.Append(vulnerability);
                            totalVulnerabilities++;
                            touched.Add(spool);
                        }

                        foreach (var spool in touched)
                        {
                            await spool.FlushAsync(cancellationToken);
                        }

                        Report(page, totalVulnerabilities, clock.Elapsed);
                    }
                }
            }

            var keys = AppKeys(applications, scans, reports, spools);
            await WriteFileAsync(keys, applications, scans, reports, spools, cancellationToken);
            await WriteMarkerAsync(keys.Count, totalVulnerabilities, cancellationToken);

            context.Status.UpdateStep(Tool, step => step with { Total = keys.Count, Done = keys.Count, Message = null });
            return new Rapid7ImportResult(keys.Count, totalVulnerabilities, Completed: true);
        }
        catch (ImportStoppedException)
        {
            context.Status.UpdateStep(Tool, step => step with { Message = "stopped before the import finished; nothing was kept" });
            return new Rapid7ImportResult(0, 0, Completed: false);
        }
        finally
        {
            // Whatever is left, however this ended: the set-aside files go.
            foreach (var spool in spools.Values)
            {
                spool.Dispose();
            }

            DeleteQuietly(spoolDir);
        }
    }

    // ---- reading ----

    private async IAsyncEnumerable<JsonElement> ReadAreaAsync(
        string area,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var page in client.GetPagesAsync(area, cancellationToken))
        {
            using (page)
            {
                await HoldWhilePausedOrStopAsync(cancellationToken);
                Report(page, null, null);

                foreach (var record in page.Records)
                {
                    // Detached from the page, which is about to be disposed.
                    yield return record.Clone();
                }
            }
        }
    }

    private async Task HoldWhilePausedOrStopAsync(CancellationToken cancellationToken)
    {
        var control = context.Control;
        await control.WaitWhilePausedAsync(cancellationToken);

        if (control.StopRequested || control.IsToolDisabled(Tool, out _))
        {
            throw new ImportStoppedException();
        }
    }

    private void Report(Rapid7Page page, long? vulnerabilitiesSoFar, TimeSpan? elapsed)
    {
        var of = page.TotalPages is > 0 and var total ? $"/{total}" : string.Empty;
        var message = vulnerabilitiesSoFar is { } count
            ? $"{page.Area} page {page.PageIndex + 1}{of}: {count} so far ({elapsed!.Value.TotalSeconds:0}s elapsed)"
            : $"{page.Area} page {page.PageIndex + 1}{of}";
        context.Status.UpdateStep(Tool, step => step with { Message = message });
    }

    // ---- writing ----

    /// <summary>Every application there is anything about, by name then id, with the unassigned bucket last.</summary>
    private static List<string> AppKeys(
        Dictionary<string, JsonElement> applications,
        Dictionary<string, List<JsonElement>> scans,
        Dictionary<string, List<JsonElement>> reports,
        Dictionary<string, RecordSpool> spools) =>
        applications.Keys
            .Concat(scans.Keys)
            .Concat(reports.Keys)
            .Concat(spools.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key == UnassignedKey)
            .ThenBy(key => NameOf(applications, key), StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key, StringComparer.Ordinal)
            .ToList();

    private static string NameOf(Dictionary<string, JsonElement> applications, string key) =>
        applications.TryGetValue(key, out var app) && app.ValueKind == JsonValueKind.Object
            && app.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() ?? string.Empty
                : string.Empty;

    private async Task WriteFileAsync(
        List<string> keys,
        Dictionary<string, JsonElement> applications,
        Dictionary<string, List<JsonElement>> scans,
        Dictionary<string, List<JsonElement>> reports,
        Dictionary<string, RecordSpool> spools,
        CancellationToken cancellationToken)
    {
        context.Status.UpdateStep(Tool, step => step with { Total = keys.Count, Message = "writing the file" });

        var temporary = Path.Combine(
            Path.GetDirectoryName(OutputPath)!,
            $".{Path.GetFileName(OutputPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                var writerOptions = new JsonWriterOptions { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                await using (var writer = new Utf8JsonWriter(stream, writerOptions))
                {
                    writer.WriteStartObject();
                    writer.WriteString("generatedAt", DateTimeOffset.UtcNow.ToString("O"));
                    writer.WriteString("baseUrl", options.BaseUrl);
                    writer.WriteString("api", "Rapid7 InsightAppSec API v1");
                    writer.WriteStartArray("areas");
                    foreach (var area in options.Areas)
                    {
                        writer.WriteStringValue(area);
                    }

                    writer.WriteEndArray();

                    writer.WriteStartArray("apps");
                    foreach (var key in keys)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await WriteAppAsync(writer, key, applications, scans, reports, spools, cancellationToken);
                        await writer.FlushAsync(cancellationToken);
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    await writer.FlushAsync(cancellationToken);
                }

                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, OutputPath, overwrite: true);
        }
        finally
        {
            // Placed, it is gone from here. Failed or stopped, a half-written file must not be left.
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task WriteAppAsync(
        Utf8JsonWriter writer,
        string key,
        Dictionary<string, JsonElement> applications,
        Dictionary<string, List<JsonElement>> scans,
        Dictionary<string, List<JsonElement>> reports,
        Dictionary<string, RecordSpool> spools,
        CancellationToken cancellationToken)
    {
        writer.WriteStartObject();

        // The application's own fields come first, as the API gave them. One only known from other records has just its id.
        if (applications.TryGetValue(key, out var application) && application.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in application.EnumerateObject().Where(p => !ReservedNames.Contains(p.Name)))
            {
                property.WriteTo(writer);
            }
        }
        else if (key == UnassignedKey)
        {
            writer.WriteNull("id");
        }
        else
        {
            writer.WriteString("id", key);
        }

        WriteList(writer, "scans", Rapid7Options.Scans, scans, key);
        WriteList(writer, "reports", Rapid7Options.Reports, reports, key);

        if (options.Includes(Rapid7Options.Vulnerabilities))
        {
            writer.WritePropertyName("vulnerabilities");
            writer.WriteStartArray();
            if (spools.TryGetValue(key, out var spool))
            {
                await spool.CopyTo(writer, cancellationToken);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private void WriteList(Utf8JsonWriter writer, string name, string area, Dictionary<string, List<JsonElement>> groups, string key)
    {
        if (!options.Includes(area))
        {
            return;
        }

        writer.WriteStartArray(name);
        foreach (var record in groups.GetValueOrDefault(key) ?? [])
        {
            record.WriteTo(writer);
        }

        writer.WriteEndArray();
    }

    private Task WriteMarkerAsync(int applicationCount, long vulnerabilityCount, CancellationToken cancellationToken) =>
        context.Markers.WriteImportDoneAsync(Tool, new ImportDoneMarker
        {
            StartedAt = _startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            Entities = applicationCount,
            Findings = vulnerabilityCount,
            FailedEntities = 0
        }, cancellationToken);

    // ---- keys ----

    private static void Group(Dictionary<string, List<JsonElement>> groups, string key, JsonElement record)
    {
        if (!groups.TryGetValue(key, out var list))
        {
            groups[key] = list = [];
        }

        list.Add(record);
    }

    /// <summary>
    /// The application a record belongs to. An application record carries its own id; the others point to theirs in
    /// app.id. A record with none goes in the unassigned bucket. The id is only data here, never used as a file name.
    /// </summary>
    internal static string KeyFor(JsonElement record, bool topLevel)
    {
        JsonElement id = default;
        var found = record.ValueKind == JsonValueKind.Object && (topLevel
            ? record.TryGetProperty("id", out id)
            : record.TryGetProperty("app", out var app) && app.ValueKind == JsonValueKind.Object && app.TryGetProperty("id", out id));

        var text = !found ? null : id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null
        };

        return string.IsNullOrWhiteSpace(text) ? UnassignedKey : text;
    }

    private static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Nothing to be done about a folder that is in use. It is on the temp drive and will be cleaned up there.
        }
    }

    private sealed class ImportStoppedException : Exception;
}
