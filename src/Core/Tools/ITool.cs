namespace Vizient.AutomatedScanner.Core.Tools;

public interface ITool
{
    string Name { get; }

    Task RunAsync(CancellationToken cancellationToken = default);
}
