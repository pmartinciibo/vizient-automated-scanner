namespace Vizient.AutomatedScanner.Core.Models;

/// <summary>
/// The plain-text content of secrets.dat, before it is encrypted (design section 12). It exists in memory only
/// for as long as it takes to read or write the file.
/// </summary>
public sealed record SecretsDocument
{
    public int SchemaVersion { get; init; } = 1;
    public Dictionary<string, SecretEntry> Secrets { get; init; } = new(StringComparer.Ordinal);
}

public sealed record SecretEntry
{
    public string Value { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The last day the secret is good, or null if it does not expire.</summary>
    public DateOnly? ExpiresAt { get; init; }

    // A record prints all its members. This one must never print the value, in a log, an error or a test report.
    public override string ToString() => $"SecretEntry {{ Value = ***, CreatedAt = {CreatedAt:O}, ExpiresAt = {ExpiresAt?.ToString("O") ?? "never"} }}";
}
