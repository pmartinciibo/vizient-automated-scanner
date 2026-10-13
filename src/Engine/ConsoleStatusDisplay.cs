using System.Runtime.InteropServices;
using System.Text;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Status;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Engine;

/// <summary>
/// One console line per step ("name  percent (elapsed/remaining): message"), redrawn in place as the status changes. Other console
/// output (warnings, the closing summary) is printed above the block, so it never ends up inside it. When the output is
/// redirected there is no cursor to move, so nothing is installed and the console is left as it was.
/// </summary>
public sealed class ConsoleStatusDisplay
{
    private readonly RunStatusTracker _tracker;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private readonly TextWriter _out = Console.Out;
    private readonly TextWriter _error = Console.Error;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private int _drawnLines;

    public ConsoleStatusDisplay(RunStatusTracker tracker, TimeSpan interval)
    {
        _tracker = tracker;
        _interval = interval;
    }

    /// <summary>False when the console cannot redraw lines (output redirected, or no virtual terminal support).</summary>
    public static bool IsSupported() =>
        !Console.IsOutputRedirected && !Console.IsErrorRedirected && EnableVirtualTerminal();

    public void Start()
    {
        Console.SetOut(new LineWriter(this, _out));
        Console.SetError(new LineWriter(this, _error));
        Redraw();
        _loop = Task.Run(LoopAsync, CancellationToken.None);
    }

    /// <summary>Stops the refresh, draws the final state and gives the console back. The block stays on screen.</summary>
    public async Task StopAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            await _loop;
        }

        Redraw();
        Console.SetOut(_out);
        Console.SetError(_error);
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                Redraw();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private void Redraw()
    {
        var lines = Render();
        lock (_gate)
        {
            Erase();
            foreach (var line in lines)
            {
                _out.Write(line + "\n");
            }

            _out.Flush();
            _drawnLines = lines.Count;
        }
    }

    private List<string> Render()
    {
        int width;
        try
        {
            width = Math.Max(20, Console.WindowWidth - 1);
        }
        catch (IOException)
        {
            width = 119;
        }

        // A line that wraps would take two rows and the cursor would climb the wrong distance.
        var lines = new List<string>();
        var snapshot = _tracker.PublishedSnapshot();
        foreach (var s in snapshot.Steps)
        {
            if (s.Name == ToolNames.Scanning && snapshot.Phase is not (RunPhase.Scan or RunPhase.Finalize))
            {
                continue;
            }

            lines.Add($"    {s.Name,-10} {s.PercentComplete}% ({s.ElapsedTime}/{s.EstimatedRemainingTime}): {s.Message}".TrimEnd());

            // Under "RepoClone": each sources entry, whether the request has it on, and how its repos are going.
            foreach (var source in s.Sources ?? [])
            {
                lines.Add(source.Enabled
                    ? $"      {source.Source} {source.Name} [{source.State}]: {source.KnownRepos} known, {source.Found} found, "
                        + $"{source.Done + source.Failed} of {source.Total} cloned/updated ({source.Failed} failed, {source.Skipped} skipped)"
                    : $"      {source.Source} {source.Name} [off]: {source.KnownRepos} known, not used this run");
            }

            // Under "Scanning": each scanner that is on, indented, with what it has found so far.
            foreach (var scanner in s.Scanners?.Where(t => t.Status != ToolRunState.Disabled) ?? [])
            {
                var c = scanner.Severities;
                lines.Add($"      {scanner.Name}: Critical({c.Critical}) High({c.High}) Moderate({c.Moderate}) Low({c.Low}) Info({c.Information})"
                    + (c.Unmapped > 0 ? $" Unmapped({c.Unmapped})" : string.Empty));
            }
        }

        return lines.Select(line => line.Length > width ? line[..width] : line).ToList();
    }

    // Caller holds the gate.
    private void Erase()
    {
        if (_drawnLines > 0)
        {
            _out.Write($"\u001b[{_drawnLines}A\r\u001b[J");
            _drawnLines = 0;
        }
    }

    // Other output goes above the block: take the block away, print, put it back.
    private void WriteAbove(TextWriter target, string line)
    {
        var lines = Render();
        lock (_gate)
        {
            Erase();
            target.Write(line + "\n");
            foreach (var step in lines)
            {
                _out.Write(step + "\n");
            }

            _out.Flush();
            target.Flush();
            _drawnLines = lines.Count;
        }
    }

    private sealed class LineWriter(ConsoleStatusDisplay display, TextWriter target) : TextWriter
    {
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => target.Encoding;

        public override void Write(char value)
        {
            lock (_line)
            {
                if (value == '\n')
                {
                    var text = _line.ToString().TrimEnd('\r');
                    _line.Clear();
                    display.WriteAbove(target, text);
                }
                else
                {
                    _line.Append(value);
                }
            }
        }

        public override void Write(string? value)
        {
            foreach (var c in value ?? string.Empty)
            {
                Write(c);
            }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            for (var i = index; i < index + count; i++)
            {
                Write(buffer[i]);
            }
        }
    }

    private static bool EnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        const int stdOutputHandle = -11;
        const uint enableVirtualTerminalProcessing = 0x0004;
        var handle = GetStdHandle(stdOutputHandle);
        return GetConsoleMode(handle, out var mode)
            && SetConsoleMode(handle, mode | enableVirtualTerminalProcessing);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
