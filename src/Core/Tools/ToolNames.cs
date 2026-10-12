namespace Vizient.AutomatedScanner.Core.Tools;

public static class ToolNames
{
    public const string Rapid7 = "Rapid7";
    public const string CheckMarx = "CheckMarx";
    public const string RepoClone = "RepoClone";
    public const string TruffleHog = "TruffleHog";
    public const string Semgrep = "Semgrep";
    public const string Snyk = "Snyk";
    public const string SonarStandard = "SonarStandard";
    public const string SonarDotNet = "SonarDotNet";

    /// <summary>The one step status.json shows for the five scanners together, which take the repos one at a time.</summary>
    public const string Scanning = "Scanning";

    public static readonly IReadOnlyList<string> Scanners = [TruffleHog, Semgrep, Snyk, SonarStandard, SonarDotNet];

    public static readonly IReadOnlyList<string> All =
        [Rapid7, CheckMarx, RepoClone, TruffleHog, Semgrep, Snyk, SonarStandard, SonarDotNet];
}
