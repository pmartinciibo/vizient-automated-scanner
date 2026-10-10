namespace Vizient.AutomatedScanner.Core.Tools;

public sealed class Rapid7Import : ITool
{
    public string Name => "Rapid7";

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"I am {Name}: imports Rapid7 results.");
        return Task.CompletedTask;
    }
}
