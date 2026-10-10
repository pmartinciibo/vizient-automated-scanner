namespace Vizient.AutomatedScanner.Core.IO;

/// <summary>
/// The invoker's side of the file exchange: the folder holding request.json. The engine watches it for
/// command.json and writes the live status.json and the final results.json here, overwriting any left by
/// an earlier run.
/// </summary>
public sealed record InvocationPaths(string Directory)
{
    public const string StatusFileName = "status.json";
    public const string ResultsFileName = "results.json";
    public const string CommandFileName = "command.json";

    public string Status => Path.Combine(Directory, StatusFileName);
    public string Results => Path.Combine(Directory, ResultsFileName);
    public string Command => Path.Combine(Directory, CommandFileName);

    public static InvocationPaths For(string requestFile) =>
        new(Path.GetDirectoryName(Path.GetFullPath(requestFile))
            ?? throw new ArgumentException("The request file path must include a folder.", nameof(requestFile)));
}
