using System.Text.Encodings.Web;
using System.Text.Json;
using Vizient.AutomatedScanner.Core.Imports;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Core.CheckMarx;

/// <summary>What the import left behind.</summary>
/// <param name="Applications">Applications in the file.</param>
/// <param name="Results">Findings in the file, across all of them.</param>
/// <param name="ScansFailed">Scans whose findings could not be read. They are listed in the file.</param>
/// <param name="Completed">False if a Stop or DisableTool command ended it early, in which case nothing was kept.</param>
public sealed record CheckMarxImportResult(int Applications, long Results, int ScansFailed, bool Completed);

/// <summary>A scan whose findings could not be read. It is skipped, as in the script, and listed in the output.</summary>
internal sealed record ScanFailure(string ScanId, string? ProjectId, string? ProjectName, string Error);

/// <summary>
/// The CheckMarx import (design section 9.2, tool 2). Reads applications and projects, then the findings of the
/// latest completed SAST scan of each project, and writes them to one file, checkmarx.json in the CheckMarx results
/// folder: a list of apps, each with its projects and the list of its findings. _import.done.json, written last, says
/// the import is complete. It is never skipped: every run imports everything again.
/// </summary>
public sealed class CheckMarxImporter(RunContext context, CheckMarxClient client, CheckMarxOptions options)
{
    public const string Tool = ToolNames.CheckMarx;

    /// <summary>The name of the output file, without .json.</summary>
    public const string OutputKey = "checkmarx";

    /// <summary>The bucket for projects with no application, and findings with no project. Not a valid id.</summary>
    public const string UnassignedKey = "(unassigned)";

    // Names a record might already use that this file uses for its own lists.
    private static readonly HashSet<string> ReservedAppNames = new(StringComparer.Ordinal) { "projects", "results" };
    private static readonly HashSet<string> ReservedProjectNames = new(StringComparer.Ordinal) { "scan" };

    private static readonly JsonWriterOptions CompactWriter = new() { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public string OutputPath => context.Paths.ResultFile(Tool, OutputKey);

    public async Task<CheckMarxImportResult> RunAsync(CancellationToken cancellationToken)
    {
        var resultsDir = context.Paths.ResultsDir(Tool);
        Directory.CreateDirectory(resultsDir);

        // An import that is running again is not complete until it finishes: drop the old marker and the old output.
        File.Delete(context.Paths.ImportDoneMarker(Tool));
        File.Delete(OutputPath);

        // Findings are set aside here as they arrive, then put together. They can add up to gigabytes, so this is on the
        // configured temp drive, not in the results.
        var spoolDir = Path.Combine(context.Config.TmpRoot, $"checkmarx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(spoolDir);

        var applications = new List<JsonElement>();
        var projects = new List<JsonElement>();
        var spools = new Dictionary<string, RecordSpool>(StringComparer.Ordinal);
        var failures = new List<ScanFailure>();
        var selector = new ScanSelector(options.ResultsScope, options.ResultsEngine, options.ResultsScanStatus);
        var selectedScans = new List<ScanRef>();
        var totalResults = 0L;

        try
        {
            if (options.Includes(CheckMarxOptions.Applications))
            {
                await foreach (var record in ReadAreaAsync(CheckMarxOptions.ApplicationsPath, "applications", cancellationToken))
                {
                    applications.Add(record);
                }
            }

            if (options.Includes(CheckMarxOptions.Projects))
            {
                await foreach (var record in ReadAreaAsync(CheckMarxOptions.ProjectsPath, "projects", cancellationToken))
                {
                    projects.Add(record);
                }
            }

            if (options.Includes(CheckMarxOptions.Results))
            {
                await SelectScansAsync(selector, cancellationToken);
                selectedScans = selector.Selected.ToList();
                if (options.ResultsMaxScans > 0)
                {
                    selectedScans = selectedScans.Take(options.ResultsMaxScans).ToList();
                }

                totalResults = await ReadResultsAsync(selectedScans, spoolDir, spools, failures, cancellationToken);
            }

            var layout = Layout(applications, projects, spools);
            await WriteFileAsync(layout, selector, spools, selectedScans.Count, failures, cancellationToken);
            await WriteMarkerAsync(layout.Apps.Count, totalResults, failures.Count, cancellationToken);

            context.Status.UpdateStep(Tool, step => step with
            {
                Total = layout.Apps.Count,
                Done = layout.Apps.Count,
                Failed = failures.Count,
                Message = null
            });
            return new CheckMarxImportResult(layout.Apps.Count, totalResults, failures.Count, Completed: true);
        }
        catch (ImportStoppedException)
        {
            context.Status.UpdateStep(Tool, step => step with { Message = "stopped before the import finished; nothing was kept" });
            return new CheckMarxImportResult(0, 0, 0, Completed: false);
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
        string path,
        string label,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var page in client.GetPagesAsync(path, null, cancellationToken))
        {
            using (page)
            {
                await HoldWhilePausedOrStopAsync(cancellationToken);
                SetMessage($"{label} page {page.PageIndex + 1}");

                foreach (var record in page.Records)
                {
                    // Detached from the page, which is about to be disposed.
                    yield return record.Clone();
                }
            }
        }
    }

    /// <summary>
    /// Reads every scan, keeping only the ones findings will be read from. There can be a hundred thousand scans, so each
    /// is looked at as it arrives and then let go.
    /// </summary>
    private async Task SelectScansAsync(ScanSelector selector, CancellationToken cancellationToken)
    {
        await foreach (var page in client.GetPagesAsync(CheckMarxOptions.ScansPath, null, cancellationToken))
        {
            using (page)
            {
                await HoldWhilePausedOrStopAsync(cancellationToken);
                foreach (var scan in page.Records)
                {
                    selector.Add(scan);
                }

                SetMessage($"scans page {page.PageIndex + 1}: {selector.Seen} seen, {selector.Selected.Count} selected");
            }
        }
    }

    /// <summary>Reads the findings of each selected scan into the spool of its project. Returns how many were read.</summary>
    private async Task<long> ReadResultsAsync(
        List<ScanRef> scans,
        string spoolDir,
        Dictionary<string, RecordSpool> spools,
        List<ScanFailure> failures,
        CancellationToken cancellationToken)
    {
        var total = 0L;
        var succeeded = 0;

        // Selecting is over, so the number of scans to read is known from here on.
        context.Status.UpdateStep(Tool, step => step with { Total = scans.Count, Done = 0, Failed = 0 });

        for (var index = 0; index < scans.Count; index++)
        {
            var scan = scans[index];
            await HoldWhilePausedOrStopAsync(cancellationToken);

            var projectKey = scan.ProjectId ?? UnassignedKey;
            if (!spools.TryGetValue(projectKey, out var spool))
            {
                spools[projectKey] = spool = new RecordSpool(Path.Combine(spoolDir, $"{spools.Count:D6}.spool"));
            }

            var checkpoint = spool.Checkpoint();
            var found = 0;
            try
            {
                var query = new Dictionary<string, string> { ["scan-id"] = scan.Id };
                await foreach (var page in client.GetPagesAsync(CheckMarxOptions.ResultsPath, query, cancellationToken))
                {
                    using (page)
                    {
                        foreach (var result in page.Records)
                        {
                            spool.AppendRaw(Tagged(result, scan));
                            found++;
                        }

                        await spool.FlushAsync(cancellationToken);
                        SetMessage($"results: scan {index + 1}/{scans.Count} ({scan.ProjectName}), {total + found} so far");
                    }
                }

                spool.Close();
                total += found;
                var read = ++succeeded;
                context.Status.UpdateStep(Tool, step => step with { Done = read });
            }
            catch (CheckMarxApiException ex) when (!ex.IsAuthFailure)
            {
                // This scan's findings could not be read (a deleted project, a permission). Take back what came of it,
                // note it, and carry on with the rest, as the script does. A refused token is different: it stops everything.
                spool.Rollback(checkpoint);
                failures.Add(new ScanFailure(scan.Id, scan.ProjectId, scan.ProjectName, ex.Message));
                var failedSoFar = failures.Count;
                context.Status.UpdateStep(Tool, step => step with { Failed = failedSoFar });
            }
        }

        return total;
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

    private void SetMessage(string message) =>
        context.Status.UpdateStep(Tool, step => step with { Message = message });

    /// <summary>
    /// A finding as it came, plus _scan_id, _project_id and _project_name so it can be joined back to its project and
    /// application. A tag the finding already has is left alone. Anything that is not an object is passed through.
    /// </summary>
    private static byte[] Tagged(JsonElement result, ScanRef scan)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, CompactWriter))
        {
            if (result.ValueKind != JsonValueKind.Object)
            {
                result.WriteTo(writer);
            }
            else
            {
                writer.WriteStartObject();
                foreach (var property in result.EnumerateObject())
                {
                    property.WriteTo(writer);
                }

                WriteMissing(writer, result, "_scan_id", scan.Id);
                WriteMissing(writer, result, "_project_id", scan.ProjectId);
                WriteMissing(writer, result, "_project_name", scan.ProjectName);
                writer.WriteEndObject();
            }
        }

        return buffer.ToArray();
    }

    private static void WriteMissing(Utf8JsonWriter writer, JsonElement result, string name, string? value)
    {
        if (result.TryGetProperty(name, out _))
        {
            return;
        }

        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    // ---- which application each project belongs to ----

    private sealed record AppEntry(string Key, JsonElement? Record, List<ProjectEntry> Projects);

    private sealed record ProjectEntry(string Key, string Name, JsonElement Record);

    private sealed record FileLayout(List<AppEntry> Apps);

    /// <summary>
    /// Files each project under the application(s) it says it belongs to, and the application's own list of projects too.
    /// A project with none, and findings whose project is not in the list, go under the unassigned entry.
    /// A project in several applications is listed under each, with its findings, so those findings appear more than once.
    /// </summary>
    private static FileLayout Layout(List<JsonElement> applications, List<JsonElement> projects, Dictionary<string, RecordSpool> spools)
    {
        var apps = new Dictionary<string, AppEntry>(StringComparer.Ordinal);

        foreach (var record in applications)
        {
            var key = IdOf(record) ?? UnassignedKey;
            apps[key] = new AppEntry(key, record, []);
        }

        AppEntry AppFor(string key) =>
            apps.TryGetValue(key, out var entry) ? entry : apps[key] = new AppEntry(key, null, []);

        // An application also lists the projects it holds, so either side can say it.
        var applicationIdsOfProject = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var record in applications)
        {
            var appId = IdOf(record);
            foreach (var projectId in StringList(record, "projectIds"))
            {
                Add(applicationIdsOfProject, projectId, appId);
            }
        }

        var knownProjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var projectId = IdOf(project) ?? UnassignedKey;
            knownProjects.Add(projectId);
            var entry = new ProjectEntry(projectId, NameOf(project), project);

            var owners = StringList(project, "applicationIds").ToHashSet(StringComparer.Ordinal);
            if (applicationIdsOfProject.TryGetValue(projectId, out var more))
            {
                owners.UnionWith(more);
            }

            foreach (var owner in owners.Count > 0 ? owners : [UnassignedKey])
            {
                AppFor(owner).Projects.Add(entry);
            }
        }

        // Findings of a project that is not in the list still go somewhere.
        if (spools.Keys.Any(projectId => !knownProjects.Contains(projectId)))
        {
            AppFor(UnassignedKey);
        }

        var ordered = apps.Values
            .OrderBy(a => a.Key == UnassignedKey)
            .ThenBy(a => a.Record is { } r ? NameOf(r) : string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Key, StringComparer.Ordinal)
            .ToList();
        foreach (var app in ordered)
        {
            app.Projects.Sort((a, b) => Compare(a.Name, a.Key, b.Name, b.Key));
        }

        return new FileLayout(ordered);

        static int Compare(string nameA, string keyA, string nameB, string keyB) =>
            StringComparer.OrdinalIgnoreCase.Compare(nameA, nameB) is var byName and not 0
                ? byName
                : StringComparer.Ordinal.Compare(keyA, keyB);

        static void Add(Dictionary<string, HashSet<string>> map, string key, string? value)
        {
            if (value is null)
            {
                return;
            }

            if (!map.TryGetValue(key, out var set))
            {
                map[key] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.Add(value);
        }
    }

    private static string? IdOf(JsonElement record) =>
        record.ValueKind == JsonValueKind.Object && record.TryGetProperty("id", out var id)
            ? id.ValueKind switch
            {
                JsonValueKind.String when !string.IsNullOrWhiteSpace(id.GetString()) => id.GetString(),
                JsonValueKind.Number => id.GetRawText(),
                _ => null
            }
            : null;

    private static string NameOf(JsonElement record) =>
        record.ValueKind == JsonValueKind.Object && record.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString() ?? string.Empty
            : string.Empty;

    private static IEnumerable<string> StringList(JsonElement record, string property) =>
        record.ValueKind == JsonValueKind.Object && record.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString())).Select(e => e.GetString()!)
            : [];

    // ---- writing ----

    private async Task WriteFileAsync(
        FileLayout layout,
        ScanSelector selector,
        Dictionary<string, RecordSpool> spools,
        int scansQueried,
        List<ScanFailure> failures,
        CancellationToken cancellationToken)
    {
        context.Status.UpdateStep(Tool, step => step with { Total = layout.Apps.Count, Done = 0, Message = "writing the file" });
        var written = 0;

        var temporary = Path.Combine(
            Path.GetDirectoryName(OutputPath)!,
            $".{Path.GetFileName(OutputPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                await using (var writer = new Utf8JsonWriter(stream, CompactWriter))
                {
                    writer.WriteStartObject();
                    writer.WriteString("generatedAt", DateTimeOffset.UtcNow.ToString("O"));
                    writer.WriteString("apiUrl", options.ApiUrl);
                    writer.WriteString("api", "Checkmarx One REST API");
                    writer.WriteStartArray("areas");
                    foreach (var area in options.Areas)
                    {
                        writer.WriteStringValue(area);
                    }

                    writer.WriteEndArray();

                    if (options.Includes(CheckMarxOptions.Results))
                    {
                        writer.WriteString("resultsScope", options.ResultsScope);
                        writer.WriteString("resultsEngine", options.ResultsEngine);
                        writer.WriteString("resultsScanStatus", options.ResultsScanStatus);
                        writer.WriteNumber("scansAvailable", selector.Seen);
                        writer.WriteNumber("scansQueried", scansQueried);
                        writer.WriteNumber("scansFailed", failures.Count);
                    }

                    writer.WriteStartArray("apps");
                    var knownProjects = layout.Apps.SelectMany(a => a.Projects).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
                    foreach (var app in layout.Apps)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await WriteAppAsync(writer, app, selector, spools, knownProjects, cancellationToken);
                        await writer.FlushAsync(cancellationToken);
                        var appsWritten = ++written;
                        context.Status.UpdateStep(Tool, step => step with { Done = appsWritten, Message = $"writing the file: {appsWritten} of {layout.Apps.Count} applications" });
                    }

                    writer.WriteEndArray();

                    writer.WriteStartArray("errors");
                    foreach (var failure in failures)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("scanId", failure.ScanId);
                        writer.WriteString("projectId", failure.ProjectId);
                        writer.WriteString("projectName", failure.ProjectName);
                        writer.WriteString("error", failure.Error);
                        writer.WriteEndObject();
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
        AppEntry app,
        ScanSelector selector,
        Dictionary<string, RecordSpool> spools,
        HashSet<string> knownProjects,
        CancellationToken cancellationToken)
    {
        writer.WriteStartObject();

        // The application's own fields first, as the API gave them. One only known from a project has just its id.
        if (app.Record is { ValueKind: JsonValueKind.Object } record)
        {
            foreach (var property in record.EnumerateObject().Where(p => !ReservedAppNames.Contains(p.Name)))
            {
                property.WriteTo(writer);
            }
        }
        else if (app.Key == UnassignedKey)
        {
            writer.WriteNull("id");
        }
        else
        {
            writer.WriteString("id", app.Key);
        }

        if (options.Includes(CheckMarxOptions.Projects))
        {
            writer.WriteStartArray("projects");
            foreach (var project in app.Projects)
            {
                WriteProject(writer, project, selector);
            }

            writer.WriteEndArray();
        }

        if (options.Includes(CheckMarxOptions.Results))
        {
            writer.WriteStartArray("results");
            foreach (var project in app.Projects)
            {
                if (spools.TryGetValue(project.Key, out var spool))
                {
                    await spool.CopyTo(writer, cancellationToken);
                }
            }

            if (app.Key == UnassignedKey)
            {
                // Findings of projects that are not in the project list.
                foreach (var (projectId, spool) in spools.Where(s => !knownProjects.Contains(s.Key)).OrderBy(s => s.Key, StringComparer.Ordinal))
                {
                    await spool.CopyTo(writer, cancellationToken);
                }
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteProject(Utf8JsonWriter writer, ProjectEntry project, ScanSelector selector)
    {
        writer.WriteStartObject();
        foreach (var property in project.Record.EnumerateObject().Where(p => !ReservedProjectNames.Contains(p.Name)))
        {
            property.WriteTo(writer);
        }

        // The scan its findings came from, when there is exactly one such scan to point to.
        if (selector.RecordFor(project.Key) is { } scan)
        {
            writer.WritePropertyName("scan");
            scan.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    private Task WriteMarkerAsync(int applicationCount, long resultCount, int failedScans, CancellationToken cancellationToken) =>
        context.Markers.WriteImportDoneAsync(Tool, new ImportDoneMarker
        {
            StartedAt = _startedAt,
            EndedAt = DateTimeOffset.UtcNow,
            Entities = applicationCount,
            Findings = resultCount,
            FailedEntities = failedScans
        }, cancellationToken);

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

/// <summary>A scan chosen to have its findings read.</summary>
/// <param name="Record">The scan as the API gave it. Kept only when there is one scan per project, to show where findings came from.</param>
internal sealed record ScanRef(string Id, string? ProjectId, string? ProjectName, string CreatedAt, JsonElement? Record);

/// <summary>
/// Chooses which scans to read findings from (the script's select_scans_for_results): those with the right status that
/// ran the right engine, and by default only the latest of each project. Fed one scan at a time, so a hundred thousand
/// scans never have to be held at once.
/// </summary>
internal sealed class ScanSelector(string scope, string? engine, string? status)
{
    private readonly Dictionary<string, ScanRef> _selected = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public int Seen { get; private set; }

    /// <summary>In the order each was first chosen.</summary>
    public IReadOnlyList<ScanRef> Selected => _order.Select(key => _selected[key]).ToList();

    /// <summary>The scan findings of a project were read from, if there is one to point to.</summary>
    public JsonElement? RecordFor(string projectId) =>
        scope == CheckMarxOptions.LatestPerProject && _selected.TryGetValue(projectId, out var scan) ? scan.Record : null;

    public void Add(JsonElement scan)
    {
        Seen++;
        if (scan.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (status is not null && Text(scan, "status") != status)
        {
            return;
        }

        if (engine is not null && !Engines(scan).Contains(engine.ToLowerInvariant()))
        {
            return;
        }

        var id = Text(scan, "id") ?? string.Empty;
        var projectId = Text(scan, "projectId");
        var created = Text(scan, "createdAt") ?? string.Empty;

        if (scope == CheckMarxOptions.AllScans)
        {
            Select(id, new ScanRef(id, projectId, Text(scan, "projectName"), created, null));
            return;
        }

        var key = projectId ?? string.Empty;
        if (!_selected.TryGetValue(key, out var current) || string.CompareOrdinal(created, current.CreatedAt) > 0)
        {
            Select(key, new ScanRef(id, projectId, Text(scan, "projectName"), created, scan.Clone()));
        }
    }

    private void Select(string key, ScanRef scan)
    {
        if (!_selected.ContainsKey(key))
        {
            _order.Add(key);
        }

        _selected[key] = scan;
    }

    private static string? Text(JsonElement scan, string name) =>
        scan.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static HashSet<string> Engines(JsonElement scan) =>
        scan.TryGetProperty("engines", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.ToLowerInvariant()).ToHashSet()
            : [];
}
