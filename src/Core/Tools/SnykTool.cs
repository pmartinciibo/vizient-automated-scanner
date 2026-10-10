namespace Vizient.AutomatedScanner.Core.Tools;

public sealed class SnykTool : ITool
{
    public string Name => "Snyk";

    public Task RunAsync(RunContext context, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"I am {Name}: scans code and dependencies.");
        return Task.CompletedTask;
    }
}
