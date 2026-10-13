namespace Vizient.AutomatedScanner.Tests;

using System.Diagnostics;
using Vizient.AutomatedScanner.Core.Processes;

// These run real child processes (cmd.exe and powershell.exe), so they need Windows, which is what the engine targets.
public class ProcessRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"automated-scanner-tests-{Guid.NewGuid():N}");
    private readonly ProcessRunner _runner = new();

    public ProcessRunnerTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ProcessSpec Cmd(string command, TimeSpan? timeout = null) => new()
    {
        FileName = "cmd.exe",
        Arguments = ["/c", command],
        Timeout = timeout ?? TimeSpan.FromSeconds(30)
    };

    private static ProcessSpec PowerShell(string script, TimeSpan? timeout = null) => new()
    {
        FileName = "powershell.exe",
        Arguments = ["-NoProfile", "-NonInteractive", "-Command", script],
        Timeout = timeout ?? TimeSpan.FromSeconds(60)
    };

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // ---- basics ----

    [Fact]
    public async Task CapturesOutputAndExitCodeOfASuccessfulRun()
    {
        var result = await _runner.RunAsync(Cmd("echo hello"));

        Assert.Equal(ProcessOutcome.Succeeded, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, result.Attempts);
        Assert.Contains("hello", result.StdOut);
        Assert.Null(result.FailureCategory);
        Assert.False(result.StdOutTruncated);
    }

    [Fact]
    public async Task ANonZeroExitIsAFailureWithItsStderr()
    {
        var result = await _runner.RunAsync(Cmd("echo oops 1>&2 & exit /b 3"));

        Assert.Equal(ProcessOutcome.Failed, result.Outcome);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("oops", result.StdErr);
        Assert.Equal("ToolError", result.FailureCategory);
        Assert.Equal("exited with code 3", result.Message);
    }

    [Fact]
    public async Task ExitCodesListedAsSuccessCountAsSuccess()
    {
        var spec = Cmd("exit /b 1") with { SuccessExitCodes = [0, 1] };

        var result = await _runner.RunAsync(spec);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task ProgramThatCannotStartIsAResultAndIsNotRetried()
    {
        var spec = new ProcessSpec
        {
            FileName = Path.Combine(_root, "no-such-tool.exe"),
            Retry = new RetryPolicy(MaxAttempts: 3, Backoff: TimeSpan.FromMilliseconds(10))
        };

        var result = await _runner.RunAsync(spec);

        Assert.Equal(ProcessOutcome.FailedToStart, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.Equal(1, result.Attempts);
        Assert.Contains("could not start", result.Message);
        Assert.Equal("ToolError", result.FailureCategory);
    }

    [Fact]
    public async Task ArgumentsArePassedAsSeparateValuesWithoutShellParsing()
    {
        // -File hands the trailing values to the script as $args (-Command would join them into the command).
        var script = Path.Combine(_root, "show-args.ps1");
        await File.WriteAllTextAsync(script, "$args.Count; $args[0]; $args[1]");
        var spec = new ProcessSpec
        {
            FileName = "powershell.exe",
            Arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "a b", "c&d"],
            Timeout = TimeSpan.FromSeconds(60)
        };

        var result = await _runner.RunAsync(spec);

        var lines = result.StdOut.Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["2", "a b", "c&d"], lines);
    }

    [Fact]
    public async Task RunsBatchFilesWithArgumentsAsSeparateValues()
    {
        var batch = Path.Combine(_root, "scanner with spaces.cmd");
        await File.WriteAllTextAsync(batch, "@echo off\r\necho first=%~1\r\necho second=%~2\r\nexit /b 0\r\n");
        var spec = new ProcessSpec
        {
            FileName = batch,
            Arguments = ["first value", "second value"],
            Timeout = TimeSpan.FromSeconds(30)
        };

        var result = await _runner.RunAsync(spec);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("first=first value", result.StdOut);
        Assert.Contains("second=second value", result.StdOut);
    }

    [Fact]
    public async Task WorkingDirectoryAndEnvironmentAreApplied()
    {
        var spec = Cmd("cd & echo %SCAN_TEST_VAR%") with
        {
            WorkingDirectory = _root,
            Environment = new Dictionary<string, string> { ["SCAN_TEST_VAR"] = "plain-value" }
        };

        var result = await _runner.RunAsync(spec);

        Assert.Contains(_root, result.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("plain-value", result.StdOut);
    }

    [Fact]
    public async Task AToolIsNotGivenAnotherToolsSecret()
    {
        var spec = Cmd("echo [%SNYK_TOKEN%][%SONAR_TOKEN%]") with
        {
            SecretEnvironment = new Dictionary<string, string> { ["SNYK_TOKEN"] = "snyk-secret-value" }
        };

        var result = await _runner.RunAsync(spec);

        Assert.Contains("[***][%SONAR_TOKEN%]", result.StdOut);
    }

    [Fact]
    public async Task AToolThatReadsInputDoesNotHangBecauseStdinIsClosed()
    {
        var result = await _runner.RunAsync(PowerShell("$line = [Console]::In.ReadLine(); if ($null -eq $line) { 'no input' }"));

        Assert.True(result.Succeeded);
        Assert.Contains("no input", result.StdOut);
    }

    // ---- secrets ----

    [Fact]
    public async Task SecretEnvironmentValuesAreMaskedInOutputAndErrors()
    {
        var spec = Cmd("echo token=%MY_TOKEN% & echo failed with %MY_TOKEN% 1>&2") with
        {
            SecretEnvironment = new Dictionary<string, string> { ["MY_TOKEN"] = "s3cr3t-token-value" }
        };

        var result = await _runner.RunAsync(spec);

        Assert.Contains("token=***", result.StdOut);
        Assert.DoesNotContain("s3cr3t-token-value", result.StdOut);
        Assert.DoesNotContain("s3cr3t-token-value", result.StdErr);
        Assert.Contains("failed with ***", result.StdErr);
    }

    [Fact]
    public async Task ExtraSecretValuesAreMaskedToo()
    {
        var spec = Cmd("echo the password is hunter2-extra") with { SecretValues = ["hunter2-extra"] };

        var result = await _runner.RunAsync(spec);

        Assert.Contains("the password is ***", result.StdOut);
    }

    [Fact]
    public async Task ASecretSplitAcrossReadBoundariesIsStillMasked()
    {
        // 8190 characters, then the secret, so it straddles the 8192-character read size.
        var spec = PowerShell("[Console]::Out.Write(('A' * 8190) + 'BOUNDARY-SECRET-VALUE' + ('B' * 50))") with
        {
            SecretValues = ["BOUNDARY-SECRET-VALUE"]
        };

        var result = await _runner.RunAsync(spec);

        Assert.DoesNotContain("BOUNDARY", result.StdOut);
        Assert.DoesNotContain("SECRET-VALUE", result.StdOut);
        Assert.Contains("***", result.StdOut);
        Assert.Equal(8190 + 3 + 50, result.StdOut.Length);
    }

    [Fact]
    public async Task MessagesNeverContainTheSecrets()
    {
        var spec = Cmd("exit /b 9") with
        {
            SecretEnvironment = new Dictionary<string, string> { ["MY_TOKEN"] = "s3cr3t-token-value" }
        };

        var result = await _runner.RunAsync(spec);

        Assert.DoesNotContain("s3cr3t-token-value", result.Message);
    }

    // ---- redaction on its own ----

    [Fact]
    public void RedactorMasksLongestSecretFirstAndIgnoresEmptyValues()
    {
        var redactor = new SecretRedactor(["abc", "abcdef", "", null]);

        Assert.Equal("x *** y ***", redactor.Redact("x abcdef y abc"));
        Assert.True(redactor.HasSecrets);
        Assert.False(SecretRedactor.None.HasSecrets);
    }

    [Fact]
    public void RedactorHoldsBackOnlyAPossibleSecretPrefixWhenStreaming()
    {
        var redactor = new SecretRedactor(["SECRETVALUE"]);

        Assert.Equal(("hello ", "SECR"), redactor.SplitForStreaming("hello SECR"));
        Assert.Equal(("hello world", ""), redactor.SplitForStreaming("hello world"));
        Assert.Equal(("", "S"), redactor.SplitForStreaming("S"));
    }

    // ---- output capture ----

    [Fact]
    public async Task HugeOutputIsCappedKeepingTheStartAndTheEnd()
    {
        var result = await _runner.RunAsync(PowerShell(
            "[Console]::Out.Write('START' + ('x' * 1500000) + 'END')"));

        Assert.True(result.Succeeded);
        Assert.True(result.StdOutTruncated);
        Assert.True(result.StdOut.Length < ProcessRunner.CaptureLimitChars + 100);
        Assert.StartsWith("START", result.StdOut);
        Assert.EndsWith("END", result.StdOut);
        Assert.Contains("characters not kept", result.StdOut);
    }

    // ---- output to a file ----

    [Fact]
    public async Task StdOutCanGoToAFileThatAppearsOnlyOnSuccess()
    {
        var file = Path.Combine(_root, "results", "Semgrep", "gh-1.json");
        var spec = Cmd("echo {\"findings\":[]}") with { StdOutFile = file };

        var result = await _runner.RunAsync(spec);

        Assert.True(result.Succeeded);
        Assert.Equal(file, result.StdOutFile);
        Assert.Equal("", result.StdOut);
        Assert.Contains("findings", await File.ReadAllTextAsync(file));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp"));
    }

    [Fact]
    public async Task AFailedRunLeavesNoOutputFileAndNoTemporaryFile()
    {
        var file = Path.Combine(_root, "out", "gh-1.json");
        var spec = Cmd("echo partial & exit /b 2") with { StdOutFile = file };

        var result = await _runner.RunAsync(spec);

        Assert.False(result.Succeeded);
        Assert.Null(result.StdOutFile);
        Assert.False(File.Exists(file));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file)!));
    }

    [Fact]
    public async Task ARerunReplacesAnEarlierOutputFile()
    {
        var file = Path.Combine(_root, "gh-1.json");
        await File.WriteAllTextAsync(file, "old");

        await _runner.RunAsync(Cmd("echo new") with { StdOutFile = file });

        Assert.Contains("new", await File.ReadAllTextAsync(file));
        Assert.DoesNotContain("old", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task SecretsAreMaskedInTheOutputFileToo()
    {
        var file = Path.Combine(_root, "gh-1.json");
        var spec = PowerShell("[Console]::Out.Write(('A' * 8190) + 'FILE-SECRET-VALUE' + ('B' * 20))") with
        {
            StdOutFile = file,
            SecretValues = ["FILE-SECRET-VALUE"]
        };

        var result = await _runner.RunAsync(spec);

        var text = await File.ReadAllTextAsync(file);
        Assert.True(result.Succeeded);
        Assert.DoesNotContain("FILE-SECRET", text);
        Assert.Equal(8190 + 3 + 20, text.Length);
    }

    [Fact]
    public async Task OutputIsCopiedByteForByteWhenThereIsNothingToMask()
    {
        var file = Path.Combine(_root, "gh-1.json");
        // The child writes explicit UTF-8 bytes, including a multi-byte character.
        var spec = PowerShell(
            "$b = [Text.Encoding]::UTF8.GetBytes('{\"a\":\"café\"}'); " +
            "$o = [Console]::OpenStandardOutput(); $o.Write($b, 0, $b.Length); $o.Flush()") with { StdOutFile = file };

        await _runner.RunAsync(spec);

        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("{\"a\":\"café\"}"), await File.ReadAllBytesAsync(file));
    }

    // ---- timeout and the process tree ----

    [Fact]
    public async Task ARunPastItsTimeoutIsKilledAndReportedAsTimedOut()
    {
        var clock = Stopwatch.StartNew();

        var result = await _runner.RunAsync(Cmd("ping -n 60 127.0.0.1 >nul", TimeSpan.FromSeconds(1)));

        Assert.Equal(ProcessOutcome.TimedOut, result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.Equal("Timeout", result.FailureCategory);
        Assert.Equal("exceeded 1 s", result.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
    }

    private const string ChildTreeScript =
        "$p = Start-Process -PassThru -WindowStyle Hidden ping -ArgumentList '-n','120','127.0.0.1'; " +
        "[Console]::Out.WriteLine('CHILD=' + $p.Id); [Console]::Out.Flush(); Start-Sleep -Seconds 120";

    private static int ChildPid(string stdOut)
    {
        var line = stdOut.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).Single(l => l.StartsWith("CHILD="));
        return int.Parse(line["CHILD=".Length..]);
    }

    [Fact]
    public async Task TimeoutKillsTheWholeProcessTreeNotJustTheTool()
    {
        var result = await _runner.RunAsync(PowerShell(ChildTreeScript, TimeSpan.FromSeconds(6)));

        var childPid = ChildPid(result.StdOut);
        Assert.Equal(ProcessOutcome.TimedOut, result.Outcome);
        Assert.True(result.JobObjectUsed);
        await Task.Delay(500);
        Assert.False(IsRunning(childPid), "the tool's child process is still running");
    }

    [Fact]
    public async Task CancellingKillsTheWholeProcessTreeAndThrows()
    {
        using var cancel = new CancellationTokenSource();
        var running = _runner.RunAsync(PowerShell(ChildTreeScript), cancel.Token);

        // Wait until the tool has started its child, then abort.
        var childPid = 0;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (childPid == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            childPid = Process.GetProcessesByName("ping")
                .Where(p => { try { return !p.HasExited && p.StartTime > DateTime.Now.AddSeconds(-30); } catch { return false; } })
                .Select(p => p.Id)
                .FirstOrDefault();
        }

        Assert.NotEqual(0, childPid);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await Task.Delay(500);
        Assert.False(IsRunning(childPid), "the tool's child process is still running");
    }

    [Fact]
    public async Task ACancelledTokenStopsBeforeStartingAnything()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _runner.RunAsync(Cmd("echo should-not-run"), cancel.Token));
    }

    [Fact]
    public async Task ChildrenLeftRunningAfterTheToolExitsAreKilledWithIt()
    {
        // The tool starts a child and exits straight away, leaving the child behind.
        var result = await _runner.RunAsync(PowerShell(
            "$p = Start-Process -PassThru -WindowStyle Hidden ping -ArgumentList '-n','120','127.0.0.1'; " +
            "[Console]::Out.WriteLine('CHILD=' + $p.Id)"));

        var childPid = ChildPid(result.StdOut);
        Assert.True(result.Succeeded);
        await Task.Delay(500);
        Assert.False(IsRunning(childPid), "a child left behind by the tool is still running");
    }

    // ---- retries ----

    [Fact]
    public async Task AFailingRunIsTriedAgainUpToTheLimit()
    {
        var spec = Cmd("exit /b 2") with { Retry = new RetryPolicy(MaxAttempts: 3, Backoff: TimeSpan.FromMilliseconds(20)) };

        var result = await _runner.RunAsync(spec);

        Assert.Equal(ProcessOutcome.Failed, result.Outcome);
        Assert.Equal(3, result.Attempts);
        Assert.Equal("exited with code 2 (attempt 3 of 3)", result.Message);
    }

    [Fact]
    public async Task ARunThatSucceedsOnTheSecondAttemptStopsRetrying()
    {
        var flag = Path.Combine(_root, "flag.txt").Replace("'", "''");
        var spec = PowerShell($"if (Test-Path '{flag}') {{ 'second time lucky'; exit 0 }} else {{ New-Item '{flag}' | Out-Null; exit 1 }}") with
        {
            Retry = new RetryPolicy(MaxAttempts: 4, Backoff: TimeSpan.FromMilliseconds(20))
        };

        var result = await _runner.RunAsync(spec);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Attempts);
        Assert.Contains("second time lucky", result.StdOut);
    }

    [Fact]
    public async Task ATimedOutRunIsRetriedToo()
    {
        var spec = Cmd("ping -n 60 127.0.0.1 >nul", TimeSpan.FromMilliseconds(500)) with
        {
            Retry = new RetryPolicy(MaxAttempts: 2, Backoff: TimeSpan.FromMilliseconds(20))
        };

        var result = await _runner.RunAsync(spec);

        Assert.Equal(ProcessOutcome.TimedOut, result.Outcome);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task OnlyTheLastAttemptsOutputIsReturnedAndNoOutputFileIsLeftFromEarlierOnes()
    {
        var file = Path.Combine(_root, "gh-1.json");
        var flag = Path.Combine(_root, "flag.txt").Replace("'", "''");
        var spec = PowerShell(
            $"if (Test-Path '{flag}') {{ 'good'; exit 0 }} else {{ New-Item '{flag}' | Out-Null; 'bad'; exit 1 }}") with
        {
            StdOutFile = file,
            Retry = new RetryPolicy(MaxAttempts: 2, Backoff: TimeSpan.FromMilliseconds(20))
        };

        var result = await _runner.RunAsync(spec);

        Assert.True(result.Succeeded);
        var text = await File.ReadAllTextAsync(file);
        Assert.Contains("good", text);
        Assert.DoesNotContain("bad", text);
    }

    // ---- bad specs ----

    [Theory]
    [InlineData("", 30, 1)]
    [InlineData("cmd.exe", 0, 1)]
    [InlineData("cmd.exe", 30, 0)]
    public async Task InvalidSpecsAreRejected(string fileName, int timeoutSeconds, int attempts)
    {
        var spec = new ProcessSpec
        {
            FileName = fileName,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            Retry = new RetryPolicy(MaxAttempts: attempts)
        };

        await Assert.ThrowsAsync<ArgumentException>(() => _runner.RunAsync(spec));
    }
}
