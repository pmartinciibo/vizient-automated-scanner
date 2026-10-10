namespace Vizient.AutomatedScanner.Core.Tools;

public sealed class SonarDotNetTool : ITool
{
    public string Name => "SonarDotNet";

    public Task RunAsync(RunContext context, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"I am {Name}: runs SonarQube analysis on .NET repos.");
        return Task.CompletedTask;
    }
}
