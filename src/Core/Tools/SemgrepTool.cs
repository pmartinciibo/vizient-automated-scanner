namespace Vizient.AutomatedScanner.Core.Tools;

public sealed class SemgrepTool : ITool
{
    public string Name => "Semgrep";

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"I am {Name}: runs static code analysis.");
        return Task.CompletedTask;
    }
}
