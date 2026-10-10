namespace Vizient.AutomatedScanner.Core.Tools;

public sealed class TruffleHogTool : ITool
{
    public string Name => "TruffleHog";

    public Task RunAsync(RunContext context, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"I am {Name}: scans repos for secrets.");
        return Task.CompletedTask;
    }
}
