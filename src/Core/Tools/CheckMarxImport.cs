namespace Vizient.AutomatedScanner.Core.Tools;

public sealed class CheckMarxImport : ITool
{
    public string Name => "CheckMarx";

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"I am {Name}: imports CheckMarx results.");
        return Task.CompletedTask;
    }
}
