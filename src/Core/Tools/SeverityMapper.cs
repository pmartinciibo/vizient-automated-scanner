using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.Tools;

/// <summary>
/// Maps each scanner's own severity words onto Critical, High, Moderate (Medium), Low and Information (Info). For the
/// downstream consumer: the raw results keep the tool's own value, and only the counts in the status use this. For most
/// tools a value it does not know is <see cref="NormalizedSeverity.Unmapped"/>, never a guess; Snyk and Semgrep are the
/// exception, where it is Moderate (see <see cref="DefaultsToModerate"/>).
/// </summary>
public static class SeverityMapper
{
    // Tools whose unlisted severities (and missing ones) count as Moderate, as the business decided.
    private static readonly HashSet<string> DefaultsToModerate = new(StringComparer.Ordinal)
    {
        ToolNames.Snyk,
        ToolNames.Semgrep
    };

    // Per tool, because the same word means different things: Sonar's legacy CRITICAL is High.
    private static readonly Dictionary<string, Dictionary<string, NormalizedSeverity>> Tables = new(StringComparer.Ordinal)
    {
        // The newer impact levels (BLOCKER, HIGH, MEDIUM, LOW, INFO) and the legacy ones (BLOCKER, CRITICAL, MAJOR, MINOR, INFO).
        // Only CRITICAL differs from what the word suggests: it is High, never Critical. See MapSonar for which is read.
        [ToolNames.SonarStandard] = Sonar(),
        [ToolNames.SonarDotNet] = Sonar(),

        // Open source: Snyk's own severity as it is. Snyk Code: the SARIF level, with no Critical.
        [ToolNames.Snyk] = Table(
            ("critical", NormalizedSeverity.Critical),
            ("high", NormalizedSeverity.High), ("error", NormalizedSeverity.High),
            ("medium", NormalizedSeverity.Moderate), ("warning", NormalizedSeverity.Moderate),
            ("low", NormalizedSeverity.Low), ("note", NormalizedSeverity.Low)),

        // No Critical. Anything else Semgrep says (MEDIUM, CRITICAL, INVENTORY, ...) is Moderate.
        [ToolNames.Semgrep] = Table(
            ("error", NormalizedSeverity.High),
            ("warning", NormalizedSeverity.Moderate),
            ("info", NormalizedSeverity.Low)),

        // The portal's own severity, as it is.
        [ToolNames.CheckMarx] = Table(
            ("critical", NormalizedSeverity.Critical),
            ("high", NormalizedSeverity.High),
            ("medium", NormalizedSeverity.Moderate),
            ("low", NormalizedSeverity.Low),
            ("info", NormalizedSeverity.Information), ("information", NormalizedSeverity.Information)),

        [ToolNames.Rapid7] = Table(
            ("critical", NormalizedSeverity.Critical),
            ("high", NormalizedSeverity.High),
            ("medium", NormalizedSeverity.Moderate),
            ("low", NormalizedSeverity.Low),
            ("informational", NormalizedSeverity.Information), ("safe", NormalizedSeverity.Information), ("info", NormalizedSeverity.Information))
    };

    /// <summary>The normalized severity of a finding the tool called <paramref name="raw"/>. Case does not matter.</summary>
    public static NormalizedSeverity Map(string tool, string? raw)
    {
        if (raw is not null && Tables.TryGetValue(tool, out var table) && table.TryGetValue(raw.Trim().ToLowerInvariant(), out var severity))
        {
            return severity;
        }

        return DefaultsToModerate.Contains(tool) ? NormalizedSeverity.Moderate : NormalizedSeverity.Unmapped;
    }

    /// <summary>
    /// A Sonar issue's severity: the highest of its impact severities when it has any the mapping knows (an issue can have
    /// one per software quality), otherwise its legacy severity.
    /// </summary>
    public static NormalizedSeverity MapSonar(string tool, IEnumerable<string?> impactSeverities, string? legacySeverity)
    {
        var impacts = impactSeverities.Select(i => Map(tool, i)).Where(s => s != NormalizedSeverity.Unmapped).ToList();

        // The enum runs from Critical down, so the lowest value is the most severe.
        return impacts.Count > 0 ? impacts.Min() : Map(tool, legacySeverity);
    }

    /// <summary>TruffleHog has no severity. Every secret found is High, verified or not.</summary>
    public static NormalizedSeverity MapSecret() => NormalizedSeverity.High;

    public static SeverityCounts Count(string tool, IEnumerable<string?> rawSeverities) =>
        rawSeverities.Aggregate(new SeverityCounts(), (counts, raw) => counts.Plus(Map(tool, raw)));

    private static Dictionary<string, NormalizedSeverity> Sonar() => Table(
        ("blocker", NormalizedSeverity.Critical),
        ("critical", NormalizedSeverity.High), ("high", NormalizedSeverity.High),
        ("major", NormalizedSeverity.Moderate), ("medium", NormalizedSeverity.Moderate),
        ("minor", NormalizedSeverity.Low), ("low", NormalizedSeverity.Low),
        ("info", NormalizedSeverity.Information));

    private static Dictionary<string, NormalizedSeverity> Table(params (string Raw, NormalizedSeverity Severity)[] rows) =>
        rows.ToDictionary(r => r.Raw, r => r.Severity, StringComparer.Ordinal);
}
