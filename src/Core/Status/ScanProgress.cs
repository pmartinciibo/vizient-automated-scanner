namespace Vizient.AutomatedScanner.Core.Status;

/// <summary>
/// How the scan phase is getting on, counted in repos, not in tool runs: the five scanners take one repo at a time, so
/// "Scanning" in status.json says how many repos need a scan, how many are done, and which are being worked on.
/// </summary>
/// <param name="Active">False until the scan phase has started reporting. Before that the step is built from the tools alone.</param>
/// <param name="Total">Repos at least one scanner has work for. Known once every repo has been looked at, before any is scanned.</param>
/// <param name="Skipped">Repos no scanner needs to scan (unchanged, not applicable, not cloned, left out).</param>
public sealed record ScanProgress(bool Active, int Total, int Done, int Failed, int Skipped, int Running, string? Message)
{
    public static readonly ScanProgress None = new(false, 0, 0, 0, 0, 0, null);
}
