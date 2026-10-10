using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Vizient.AutomatedScanner.Core.Processes;

public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>How much of each output stream is kept in memory (start and end together).</summary>
    public const int CaptureLimitChars = 256 * 1024;

    // After the process is gone and its job is terminated, the pipes close almost at once. This is a backstop.
    private static readonly TimeSpan PipeDrainGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KillWaitLimit = TimeSpan.FromSeconds(10);

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default)
    {
        Validate(spec);

        var redactor = new SecretRedactor(spec.SecretEnvironment.Values.Concat(spec.SecretValues));
        var maxAttempts = Math.Max(1, spec.Retry.MaxAttempts);
        var clock = Stopwatch.StartNew();

        Attempt attempt;
        var attempts = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempts > 0 && spec.Retry.Backoff > TimeSpan.Zero)
            {
                await Task.Delay(spec.Retry.Backoff, cancellationToken);
            }

            attempts++;
            attempt = await RunOnceAsync(spec, redactor, cancellationToken);
        }
        while (attempts < maxAttempts && attempt.Outcome is ProcessOutcome.Failed or ProcessOutcome.TimedOut);

        return new ProcessResult
        {
            Outcome = attempt.Outcome,
            ExitCode = attempt.ExitCode,
            Attempts = attempts,
            Duration = clock.Elapsed,
            StdOut = attempt.StdOut,
            StdErr = attempt.StdErr,
            StdOutTruncated = attempt.StdOutTruncated,
            StdErrTruncated = attempt.StdErrTruncated,
            Message = attempts > 1 ? $"{attempt.Message} (attempt {attempts} of {maxAttempts})" : attempt.Message,
            StdOutFile = attempt.StdOutFile,
            JobObjectUsed = attempt.JobObjectUsed
        };
    }

    private static async Task<Attempt> RunOnceAsync(ProcessSpec spec, SecretRedactor redactor, CancellationToken ct)
    {
        var startInfo = CreateStartInfo(spec);
        var stdOutText = new BoundedText(CaptureLimitChars);
        var stdErrText = new BoundedText(CaptureLimitChars);

        string? tempFilePath = null;
        FileStream? stdOutFile = null;
        if (spec.StdOutFile is not null)
        {
            var finalPath = Path.GetFullPath(spec.StdOutFile);
            var directory = Path.GetDirectoryName(finalPath)!;
            Directory.CreateDirectory(directory);
            tempFilePath = Path.Combine(directory, $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");
            stdOutFile = new FileStream(tempFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        }

        using var process = new Process { StartInfo = startInfo };
        ProcessJob? job = OperatingSystem.IsWindows() ? ProcessJob.TryCreate() : null;

        try
        {
            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                return new Attempt(ProcessOutcome.FailedToStart)
                {
                    Message = $"could not start '{spec.FileName}': {ex.Message}"
                };
            }

            var jobAssigned = OperatingSystem.IsWindows() && job is not null && job.TryAssign(process);
            CloseStdin(process);

            var stdOutPump = stdOutFile is null
                ? TextPump.RunAsync(process.StandardOutput.BaseStream, redactor, stdOutText.Append, CancellationToken.None)
                : TextPump.ToFileAsync(process.StandardOutput.BaseStream, stdOutFile, redactor, CancellationToken.None);
            var stdErrPump = TextPump.RunAsync(process.StandardError.BaseStream, redactor, stdErrText.Append, CancellationToken.None);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(spec.Timeout);

            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = !ct.IsCancellationRequested;
                KillTree(process, job);
                await WaitForExitBoundedAsync(process);
            }

            // Whatever the tool left running goes with it.
            if (OperatingSystem.IsWindows())
            {
                job?.Terminate();
            }
            await DrainAsync(stdOutPump, stdErrPump);

            ct.ThrowIfCancellationRequested();

            stdOutFile?.Dispose();
            stdOutFile = null;

            var exitCode = timedOut ? (int?)null : process.ExitCode;
            var outcome = timedOut
                ? ProcessOutcome.TimedOut
                : spec.SuccessExitCodes.Contains(process.ExitCode) ? ProcessOutcome.Succeeded : ProcessOutcome.Failed;

            string? keptFile = null;
            if (outcome == ProcessOutcome.Succeeded && tempFilePath is not null)
            {
                keptFile = Path.GetFullPath(spec.StdOutFile!);
                File.Move(tempFilePath, keptFile, overwrite: true);
                tempFilePath = null;
            }

            return new Attempt(outcome)
            {
                ExitCode = exitCode,
                StdOut = stdOutText.ToString(),
                StdErr = stdErrText.ToString(),
                StdOutTruncated = stdOutText.Truncated,
                StdErrTruncated = stdErrText.Truncated,
                Message = outcome switch
                {
                    ProcessOutcome.Succeeded => $"exited with code {exitCode}",
                    ProcessOutcome.TimedOut => $"exceeded {FormatTimeout(spec.Timeout)}",
                    _ => $"exited with code {exitCode}"
                },
                StdOutFile = keptFile,
                JobObjectUsed = jobAssigned
            };
        }
        finally
        {
            // Cancelled, faulted or finished: nothing of this attempt may keep running or be left on disk.
            if (!HasExited(process))
            {
                KillTree(process, job);
            }

            if (OperatingSystem.IsWindows())
            {
                job?.Dispose();
            }
            stdOutFile?.Dispose();
            if (tempFilePath is not null && File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(ProcessSpec spec)
    {
        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (spec.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = spec.WorkingDirectory;
        }

        foreach (var name in spec.RemoveEnvironment)
        {
            startInfo.Environment.Remove(name);
        }

        foreach (var (name, value) in spec.Environment.Concat(spec.SecretEnvironment))
        {
            startInfo.Environment[name] = value;
        }

        return startInfo;
    }

    // Tools must never wait for input, so give them an immediately closed stdin.
    private static void CloseStdin(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // It exited already.
        }
    }

    private static void KillTree(Process process, ProcessJob? job)
    {
        if (OperatingSystem.IsWindows())
        {
            if (OperatingSystem.IsWindows())
            {
                job?.Terminate();
            }
        }

        // Covers a process that never got into the job.
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    private static async Task WaitForExitBoundedAsync(Process process)
    {
        using var limit = new CancellationTokenSource(KillWaitLimit);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            // It would not die in time. Report what we have.
        }
    }

    private static async Task DrainAsync(Task stdOut, Task stdErr)
    {
        try
        {
            await Task.WhenAll(stdOut, stdErr).WaitAsync(PipeDrainGrace);
        }
        catch (TimeoutException)
        {
            // Something outside the job still holds the pipes. Keep what was read.
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true; // never started
        }
    }

    private static string FormatTimeout(TimeSpan timeout) =>
        timeout.TotalMinutes >= 1 ? $"{timeout.TotalMinutes:0.#} min" : $"{timeout.TotalSeconds:0.#} s";

    private static void Validate(ProcessSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.FileName))
        {
            throw new ArgumentException("FileName is required.", nameof(spec));
        }

        if (spec.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Timeout must be greater than zero.", nameof(spec));
        }

        if (spec.Retry.MaxAttempts < 1)
        {
            throw new ArgumentException("Retry.MaxAttempts must be at least 1.", nameof(spec));
        }
    }

    private sealed record Attempt(ProcessOutcome Outcome)
    {
        public int? ExitCode { get; init; }
        public string StdOut { get; init; } = string.Empty;
        public string StdErr { get; init; } = string.Empty;
        public bool StdOutTruncated { get; init; }
        public bool StdErrTruncated { get; init; }
        public string Message { get; init; } = string.Empty;
        public string? StdOutFile { get; init; }
        public bool JobObjectUsed { get; init; }
    }
}
