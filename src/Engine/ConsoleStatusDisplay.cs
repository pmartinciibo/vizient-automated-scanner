using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Status;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Engine;

internal sealed class ConsoleStatusDisplay(
    RunStatusTracker tracker,
    TextWriter output,
    bool interactive,
    TimeSpan interval)
{
    private int? _top;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var toolNames = ToolNames.All;
        var previous = new string?[toolNames.Count];
        Render(tracker.Snapshot(), toolNames, previous);

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                Render(tracker.Snapshot(), toolNames, previous);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Render(StatusDocument status, IReadOnlyList<string> toolNames, string?[] previous)
    {
        var steps = status.Steps.ToDictionary(step => step.Name, StringComparer.Ordinal);
        var lines = toolNames.Select(name => Format(steps[name])).ToArray();

        if (interactive && _top is null)
        {
            try
            {
                foreach (var line in lines)
                {
                    output.WriteLine(line);
                }

                output.Flush();
                _top = Console.CursorTop - lines.Length;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentOutOfRangeException or PlatformNotSupportedException)
            {
                _top = null;
                interactive = false;
            }

            Array.Copy(lines, previous, lines.Length);
            return;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i] == previous[i])
            {
                continue;
            }

            previous[i] = lines[i];
            if (interactive && _top is { } top)
            {
                try
                {
                    var cursorTop = Console.CursorTop;
                    var width = Math.Max(1, Console.WindowWidth - 1);
                    var line = Clip(lines[i], width).PadRight(width);
                    Console.SetCursorPosition(0, top + i);
                    output.Write(line);
                    output.Flush();
                    Console.SetCursorPosition(0, cursorTop);
                    continue;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentOutOfRangeException or PlatformNotSupportedException)
                {
                    interactive = false;
                    _top = null;
                }
            }

            output.WriteLine(lines[i]);
            output.Flush();
        }
    }

    private static string Format(ToolProgress step)
    {
        var counts = step.Total > 0 ? $"{step.Done}/{step.Total}" : "-/-";
        var extras = new List<string>();
        if (step.Running > 0)
        {
            extras.Add($"{step.Running} running");
        }

        if (step.Failed > 0)
        {
            extras.Add($"{step.Failed} failed");
        }

        if (step.Skipped > 0)
        {
            extras.Add($"{step.Skipped} skipped");
        }

        if (step.Canaries > 0)
        {
            extras.Add($"{step.Canaries} canaries");
        }

        var detail = string.Join(" | ", extras.Append(step.Message).Where(value => !string.IsNullOrWhiteSpace(value)));
        return $"{step.Name,-16} {step.Status,-18} {counts,-9}{(detail.Length > 0 ? $" {detail}" : string.Empty)}";
    }

    private static string Clip(string value, int width) => value.Length <= width ? value : value[..width];
}