namespace Vizient.AutomatedScanner.Core.Tools;

public sealed class RepoCloneTool : ITool
{
    public string Name => "RepoClone";

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"I am {Name}: discovers, tracks and clones repos.");
        return Task.CompletedTask;
    }
}
