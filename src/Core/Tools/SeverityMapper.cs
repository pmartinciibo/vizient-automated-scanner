using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.Tools;

/// <summary>
/// Maps each scanner's own severity words onto Critical, High, Moderate, Low and Information. For the downstream
/// consumer: the raw results keep the tool's own value, and only the counts in the status use this. A value it does not
/// know is <see cref="NormalizedSeverity.Unmapped"/>, never a guess.
/// </summary>
public static class SeverityMapper
{
    // Per tool, because the same word means different things: Sonar's CRITICAL is High, Semgrep's ERROR is Critical.
    private static readonly Dictionary<string, Dictionary<string, NormalizedSeverity>> Tables = new(StringComparer.Ordinal)
    {
        // SonarQube's classic scale, and the newer impact levels.
        [ToolNames.SonarStandard] = Sonar(),
        [ToolNames.SonarDotNet] = Sonar(),

        // Dependency scans use critical to low. Snyk Code writes SARIF levels (error, warning, note).
        [ToolNames.Snyk] = Table(
            ("critical", NormalizedSeverity.Critical),
            ("high", NormalizedSeverity.High), ("error", NormalizedSeverity.High),
            ("medium", NormalizedSeverity.Moderate), ("warning", NormalizedSeverity.Moderate),
            ("low", NormalizedSeverity.Low), ("note", NormalizedSeverity.Low)),

        // Semgrep's own scale, as the business set it: ERROR is Critical, MEDIUM is High, WARNING is Moderate, INFO is Information.
        // The CLI writes ERROR, WARNING and INFO (and MEDIUM for some rules). The other words are kept in case a rule or a newer
        // version uses them.
        [ToolNames.Semgrep] = Table(
            ("critical", NormalizedSeverity.Critical), ("error", NormalizedSeverity.Critical),
            ("high", NormalizedSeverity.High), ("medium", NormalizedSeverity.High),
            ("warning", NormalizedSeverity.Moderate),
            ("low", NormalizedSeverity.Low),
            ("info", NormalizedSeverity.Information)),

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
    public static NormalizedSeverity Map(string tool, string? raw) =>
        raw is not null
        && Tables.TryGetValue(tool, out var table)
        && table.TryGetValue(raw.Trim().ToLowerInvariant(), out var severity)
            ? severity
            : NormalizedSeverity.Unmapped;

    /// <summary>
    /// TruffleHog has no severity, only whether a secret was verified as live. A verified one is Critical. One that is
    /// not verified (verification is off unless engine.json turns it on) is High: a secret in a repo is serious either way.
    /// </summary>
    public static NormalizedSeverity MapSecret(bool verified) =>
        verified ? NormalizedSeverity.Critical : NormalizedSeverity.High;

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
