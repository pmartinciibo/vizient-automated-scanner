namespace Vizient.AutomatedScanner.Core.Tools;

public sealed class SonarStandardTool : ITool
{
    public string Name => "SonarStandard";

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"I am {Name}: runs SonarQube analysis on non-.NET repos.");
        return Task.CompletedTask;
    }
}
