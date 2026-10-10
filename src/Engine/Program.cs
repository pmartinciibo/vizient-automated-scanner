using Vizient.AutomatedScanner.Core;
using Vizient.AutomatedScanner.Core.Configuration;
using Vizient.AutomatedScanner.Core.Tools;

EngineConfig config;
try
{
    config = await EngineConfig.LoadAsync();
    config.EnsureDirectories();
}
catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"Fatal: {ex.Message}");
    return 2;
}

Console.WriteLine($"Config: {config.ConfigFile}");
Console.WriteLine($"  runs:  {config.RunsRoot}");
Console.WriteLine($"  state: {config.StateRoot}");
Console.WriteLine($"  repos: {config.ReposRoot}");

var orchestrator = new RunOrchestrator(
    new Rapid7Import(),
    new CheckMarxImport(),
    new RepoCloneTool(),
    [
        new TruffleHogTool(),
        new SemgrepTool(),
        new SnykTool(),
        new SonarStandardTool(),
        new SonarDotNetTool()
    ]);

var results = await orchestrator.RunAsync();

var failures = results.Where(r => !r.Succeeded).ToList();
foreach (var failure in failures)
{
    Console.Error.WriteLine($"FAILED {failure.Tool}: {failure.Error}");
}

return failures.Count == 0 ? 0 : 1;
