namespace Vizient.AutomatedScanner.Core.Models;

/// <summary>The one scale findings are counted on, whatever scale the tool that found them uses.</summary>
public enum NormalizedSeverity
{
    Critical,
    High,
    Moderate,
    Low,
    Information,

    /// <summary>A severity the mapping does not know. Counted apart, so it never lands in a real level by accident.</summary>
    Unmapped
}

/// <summary>How many findings there are at each <see cref="NormalizedSeverity"/>.</summary>
public sealed record SeverityCounts
{
    public int Critical { get; init; }
    public int High { get; init; }
    public int Moderate { get; init; }
    public int Low { get; init; }
    public int Information { get; init; }
    public int Unmapped { get; init; }

    public SeverityCounts Plus(NormalizedSeverity severity, int count = 1) => severity switch
    {
        NormalizedSeverity.Critical => this with { Critical = Critical + count },
        NormalizedSeverity.High => this with { High = High + count },
        NormalizedSeverity.Moderate => this with { Moderate = Moderate + count },
        NormalizedSeverity.Low => this with { Low = Low + count },
        NormalizedSeverity.Information => this with { Information = Information + count },
        _ => this with { Unmapped = Unmapped + count }
    };

    public SeverityCounts Plus(SeverityCounts? other) => other is null
        ? this
        : new SeverityCounts
        {
            Critical = Critical + other.Critical,
            High = High + other.High,
            Moderate = Moderate + other.Moderate,
            Low = Low + other.Low,
            Information = Information + other.Information,
            Unmapped = Unmapped + other.Unmapped
        };
}
