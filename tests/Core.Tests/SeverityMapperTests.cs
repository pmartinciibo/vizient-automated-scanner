using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Tools;

namespace Vizient.AutomatedScanner.Tests;

public class SeverityMapperTests
{
    [Theory]
    [InlineData("SonarStandard", "BLOCKER", NormalizedSeverity.Critical)]
    [InlineData("SonarDotNet", "CRITICAL", NormalizedSeverity.High)]
    [InlineData("SonarStandard", "MAJOR", NormalizedSeverity.Moderate)]
    [InlineData("SonarStandard", "MINOR", NormalizedSeverity.Low)]
    [InlineData("SonarStandard", "INFO", NormalizedSeverity.Information)]
    [InlineData("Semgrep", "CRITICAL", NormalizedSeverity.Critical)]
    [InlineData("Semgrep", "ERROR", NormalizedSeverity.Critical)]
    [InlineData("Semgrep", "MEDIUM", NormalizedSeverity.High)]
    [InlineData("Semgrep", "WARNING", NormalizedSeverity.Moderate)]
    [InlineData("Semgrep", "INFO", NormalizedSeverity.Information)]
    [InlineData("Snyk", "error", NormalizedSeverity.High)]
    [InlineData("Snyk", "medium", NormalizedSeverity.Moderate)]
    [InlineData("Snyk", "note", NormalizedSeverity.Low)]
    [InlineData("CheckMarx", "Medium", NormalizedSeverity.Moderate)]
    [InlineData("Rapid7", "INFORMATIONAL", NormalizedSeverity.Information)]
    [InlineData("Semgrep", "nonsense", NormalizedSeverity.Unmapped)]
    [InlineData("Semgrep", null, NormalizedSeverity.Unmapped)]
    [InlineData("TruffleHog", "high", NormalizedSeverity.Unmapped)]
    public void MapsEachToolsOwnScale(string tool, string? raw, NormalizedSeverity expected) =>
        Assert.Equal(expected, SeverityMapper.Map(tool, raw));

    [Fact]
    public void ASecretIsCriticalWhenVerifiedAndHighOtherwise()
    {
        Assert.Equal(NormalizedSeverity.Critical, SeverityMapper.MapSecret(true));
        Assert.Equal(NormalizedSeverity.High, SeverityMapper.MapSecret(false));
    }

    [Fact]
    public void CountsAddUpAndUnknownValuesAreKeptApart()
    {
        var counts = SeverityMapper.Count("Semgrep", ["ERROR", "ERROR", "WARNING", "INFO", "weird"]);

        Assert.Equal((2, 0, 1, 0, 1, 1), (counts.Critical, counts.High, counts.Moderate, counts.Low, counts.Information, counts.Unmapped));
    }
}
