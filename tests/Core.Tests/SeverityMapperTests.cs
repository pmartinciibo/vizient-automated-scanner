using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Tests;

public class SeverityMapperTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("severity").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string text)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, text);
        return path;
    }

    private static (int Critical, int High, int Moderate, int Low, int Information, int Unmapped) Tuple(SeverityCounts c) =>
        (c.Critical, c.High, c.Moderate, c.Low, c.Information, c.Unmapped);

    [Theory]
    // Sonar: the legacy scale, where CRITICAL is High and only BLOCKER is Critical.
    [InlineData("SonarStandard", "BLOCKER", NormalizedSeverity.Critical)]
    [InlineData("SonarDotNet", "CRITICAL", NormalizedSeverity.High)]
    [InlineData("SonarStandard", "MAJOR", NormalizedSeverity.Moderate)]
    [InlineData("SonarStandard", "MINOR", NormalizedSeverity.Low)]
    [InlineData("SonarStandard", "INFO", NormalizedSeverity.Information)]
    // Sonar: the impact scale.
    [InlineData("SonarStandard", "HIGH", NormalizedSeverity.High)]
    [InlineData("SonarDotNet", "MEDIUM", NormalizedSeverity.Moderate)]
    [InlineData("SonarDotNet", "LOW", NormalizedSeverity.Low)]
    [InlineData("SonarDotNet", "nonsense", NormalizedSeverity.Unmapped)]
    [InlineData("SonarDotNet", null, NormalizedSeverity.Unmapped)]
    // Semgrep: no Critical, and anything unlisted is Moderate.
    [InlineData("Semgrep", "ERROR", NormalizedSeverity.High)]
    [InlineData("Semgrep", "WARNING", NormalizedSeverity.Moderate)]
    [InlineData("Semgrep", "INFO", NormalizedSeverity.Low)]
    [InlineData("Semgrep", "CRITICAL", NormalizedSeverity.Moderate)]
    [InlineData("Semgrep", "MEDIUM", NormalizedSeverity.Moderate)]
    [InlineData("Semgrep", "nonsense", NormalizedSeverity.Moderate)]
    [InlineData("Semgrep", null, NormalizedSeverity.Moderate)]
    // Snyk open source: its own severity. Snyk Code: the SARIF level. Anything unlisted is Moderate.
    [InlineData("Snyk", "critical", NormalizedSeverity.Critical)]
    [InlineData("Snyk", "high", NormalizedSeverity.High)]
    [InlineData("Snyk", "medium", NormalizedSeverity.Moderate)]
    [InlineData("Snyk", "low", NormalizedSeverity.Low)]
    [InlineData("Snyk", "error", NormalizedSeverity.High)]
    [InlineData("Snyk", "warning", NormalizedSeverity.Moderate)]
    [InlineData("Snyk", "note", NormalizedSeverity.Low)]
    [InlineData("Snyk", "nonsense", NormalizedSeverity.Moderate)]
    [InlineData("Snyk", null, NormalizedSeverity.Moderate)]
    // The portal's severity, as it is.
    [InlineData("CheckMarx", "Critical", NormalizedSeverity.Critical)]
    [InlineData("CheckMarx", "Medium", NormalizedSeverity.Moderate)]
    [InlineData("CheckMarx", "Info", NormalizedSeverity.Information)]
    [InlineData("Rapid7", "INFORMATIONAL", NormalizedSeverity.Information)]
    [InlineData("TruffleHog", "high", NormalizedSeverity.Unmapped)]
    public void MapsEachToolsOwnScale(string tool, string? raw, NormalizedSeverity expected) =>
        Assert.Equal(expected, SeverityMapper.Map(tool, raw));

    [Fact]
    public void EveryTruffleHogSecretIsHigh() =>
        Assert.Equal(NormalizedSeverity.High, SeverityMapper.MapSecret());

    [Fact]
    public void CountsAddUpAndSemgrepNeverReportsCritical()
    {
        var counts = SeverityMapper.Count("Semgrep", ["ERROR", "ERROR", "WARNING", "INFO", "weird", "CRITICAL"]);

        Assert.Equal((0, 2, 3, 1, 0, 0), Tuple(counts));
    }

    [Fact]
    public void ASonarIssueGoesByItsHighestImpactElseItsLegacySeverity()
    {
        // The legacy CRITICAL is overruled by the impact.
        Assert.Equal(NormalizedSeverity.Moderate, SeverityMapper.MapSonar("SonarStandard", ["MEDIUM"], "CRITICAL"));
        Assert.Equal(NormalizedSeverity.Critical, SeverityMapper.MapSonar("SonarStandard", ["LOW", "BLOCKER"], "MINOR"));
        Assert.Equal(NormalizedSeverity.High, SeverityMapper.MapSonar("SonarStandard", ["LOW", "HIGH", "MEDIUM"], "INFO"));

        // No impact, or none that is known: the legacy one. CRITICAL is High, never Critical.
        Assert.Equal(NormalizedSeverity.High, SeverityMapper.MapSonar("SonarDotNet", [], "CRITICAL"));
        Assert.Equal(NormalizedSeverity.Low, SeverityMapper.MapSonar("SonarDotNet", ["weird"], "MINOR"));
        Assert.Equal(NormalizedSeverity.Unmapped, SeverityMapper.MapSonar("SonarDotNet", [], null));
    }

    [Fact]
    public void SonarCountsReadImpactsWhenTheIssueHasThem()
    {
        var path = Write("""
            { "project": "p", "total": 4, "issues": [
              { "severity": "CRITICAL", "impacts": [ { "softwareQuality": "SECURITY", "severity": "HIGH" } ] },
              { "severity": "CRITICAL", "impacts": [ { "softwareQuality": "MAINTAINABILITY", "severity": "LOW" }, { "softwareQuality": "SECURITY", "severity": "BLOCKER" } ] },
              { "severity": "CRITICAL" },
              { "severity": "MAJOR", "impacts": [] } ] }
            """);

        // HIGH -> High, BLOCKER -> Critical, legacy CRITICAL -> High, legacy MAJOR -> Moderate.
        Assert.Equal((1, 2, 1, 0, 0, 0), Tuple(ScanOutput.SonarSeverities("SonarStandard", path)));
    }

    [Fact]
    public void SnykOpenSourceUsesItsOwnSeverityNotThePriorityScore()
    {
        var path = Write("""
            { "vulnerabilities": [
              { "severity": "critical", "priorityScore": 100 },
              { "severity": "high", "priorityScore": 900 },
              { "severity": "medium" }, { "severity": "low" }, { "severity": "strange" }, { } ] }
            """);

        Assert.Equal((1, 1, 3, 1, 0, 0), Tuple(ScanOutput.SnykSeverities(path)));
    }

    [Fact]
    public void SnykCodeUsesTheSarifLevelNotThePriorityScoreAndHasNoCritical()
    {
        var path = Write("""
            { "runs": [ { "results": [
              { "level": "error", "properties": { "priorityScore": 1000 } },
              { "level": "warning", "properties": { "priorityScore": 100 } },
              { "level": "note", "properties": { "priorityScore": 900 } },
              { "level": "none" }, { } ] } ] }
            """);

        Assert.Equal((0, 1, 3, 1, 0, 0), Tuple(ScanOutput.SnykSeverities(path)));
    }

    [Fact]
    public void SemgrepCountsReadExtraSeverity()
    {
        var path = Write("""{ "results": [ { "extra": { "severity": "ERROR" } }, { "extra": { "severity": "INFO" } }, { "extra": { "severity": "MEDIUM" } }, { } ] }""");

        Assert.Equal((0, 1, 2, 1, 0, 0), Tuple(ScanOutput.SemgrepSeverities(path)));
    }

    [Fact]
    public void EveryTruffleHogFindingCountsAsHighVerifiedOrNot()
    {
        var path = Write("{\"Verified\":true}\n{\"Verified\":false}\n\nnot json\n");

        // The line that is not a finding object is kept apart rather than lost.
        Assert.Equal((0, 2, 0, 0, 0, 1), Tuple(ScanOutput.TruffleHogSeverities(path)));
    }
}
