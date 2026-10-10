using Vizient.AutomatedScanner.Core.IO;
using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.Secrets;

public enum SecretStatus
{
    Ok,

    /// <summary>Expires within the warning period, but is still good.</summary>
    ExpiringSoon,

    Expired
}

/// <summary>What is known about a secret without its value, so it can be listed and logged.</summary>
public sealed record SecretInfo(string Name, DateTimeOffset CreatedAt, DateOnly? ExpiresAt, SecretStatus Status, int? DaysLeft);

/// <summary>A secret a tool needs is missing or expired. The message names the secret, never its value.</summary>
public sealed class SecretUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// The secrets held in memory. Load it from a <see cref="SecretsFile"/>, change it, save it back. Values go out
/// only through <see cref="GetRequired"/>; everything else works with names and dates.
/// </summary>
public sealed class SecretStore
{
    private readonly Dictionary<string, SecretEntry> _secrets;

    public SecretStore() => _secrets = new(StringComparer.Ordinal);

    private SecretStore(IEnumerable<KeyValuePair<string, SecretEntry>> secrets) =>
        _secrets = new(secrets, StringComparer.Ordinal);

    /// <summary>The file's secrets, or an empty store if there is no file yet. A file that cannot be read throws.</summary>
    public static async Task<SecretStore> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new SecretStore();
        }

        var document = await SecretsFile.ReadAsync(path, cancellationToken);
        return new SecretStore(document.Secrets);
    }

    public Task SaveAsync(string path, CancellationToken cancellationToken = default) =>
        SecretsFile.WriteAsync(path, new SecretsDocument { Secrets = new(_secrets, StringComparer.Ordinal) }, cancellationToken);

    public int Count => _secrets.Count;

    public bool Contains(string name) => _secrets.ContainsKey(name);

    /// <summary>Adds or replaces a secret. Its created time becomes now, since the value is new.</summary>
    public void Set(string name, string value, DateOnly? expiresAt, DateTimeOffset now)
    {
        if (!SafeName.IsValid(name))
        {
            throw new ArgumentException(
                $"'{name}' is not a valid secret name. Use letters, digits, '.', '_' and '-', starting with a letter or digit.",
                nameof(name));
        }

        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("A secret cannot be empty.", nameof(value));
        }

        if (expiresAt is { } date && date < DateOnly.FromDateTime(now.UtcDateTime))
        {
            throw new ArgumentException($"The expiry date {date:yyyy-MM-dd} has already passed.", nameof(expiresAt));
        }

        _secrets[name] = new SecretEntry { Value = value, CreatedAt = now, ExpiresAt = expiresAt };
    }

    public bool Remove(string name) => _secrets.Remove(name);

    /// <summary>All the secrets by name, with when they were made and when they expire. No values.</summary>
    public IReadOnlyList<SecretInfo> List(DateOnly today, int warnDays) =>
        _secrets
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => InfoFor(s.Key, s.Value, today, warnDays))
            .ToList();

    public SecretInfo? Info(string name, DateOnly today, int warnDays) =>
        _secrets.TryGetValue(name, out var entry) ? InfoFor(name, entry, today, warnDays) : null;

    /// <summary>
    /// The secret's value. Throws <see cref="SecretUnavailableException"/> if there is none, or it has expired, rather than
    /// handing a tool a token that will be refused. <paramref name="neededBy"/> goes in the message.
    /// </summary>
    public string GetRequired(string name, DateOnly today, string neededBy)
    {
        if (!_secrets.TryGetValue(name, out var entry))
        {
            throw new SecretUnavailableException($"Secret '{name}' (needed by {neededBy}) has not been set. Use: engine secrets set {name}");
        }

        if (entry.ExpiresAt is { } expires && today > expires)
        {
            throw new SecretUnavailableException($"Secret '{name}' (needed by {neededBy}) expired on {expires:yyyy-MM-dd}.");
        }

        return entry.Value;
    }

    private static SecretInfo InfoFor(string name, SecretEntry entry, DateOnly today, int warnDays)
    {
        if (entry.ExpiresAt is not { } expires)
        {
            return new SecretInfo(name, entry.CreatedAt, null, SecretStatus.Ok, null);
        }

        var daysLeft = expires.DayNumber - today.DayNumber;
        var status = daysLeft < 0 ? SecretStatus.Expired : daysLeft <= warnDays ? SecretStatus.ExpiringSoon : SecretStatus.Ok;
        return new SecretInfo(name, entry.CreatedAt, expires, status, daysLeft);
    }
}
