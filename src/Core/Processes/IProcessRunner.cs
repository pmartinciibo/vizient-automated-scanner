namespace Vizient.AutomatedScanner.Core.Processes;

/// <summary>Runs a tool as a child process. Tools depend on this so tests can stand in a fake.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs the program, retrying as the spec says. Failure, timeout and a program that will not start are
    /// results, not exceptions. Cancelling kills the process tree and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken = default);
}
