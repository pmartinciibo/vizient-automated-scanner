namespace Vizient.AutomatedScanner.Core.Processes;

/// <summary>
/// Masks known secret values in text. It only knows the exact values it is given: a secret the tool prints in
/// another form (URL-encoded, base64) is not caught.
/// </summary>
public sealed class SecretRedactor
{
    public const string Mask = "***";

    public static readonly SecretRedactor None = new([]);

    // Longest first, so a secret that contains another is masked whole.
    private readonly string[] _secrets;

    public SecretRedactor(IEnumerable<string?> secrets)
    {
        _secrets = secrets
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length)
            .ToArray();
    }

    public bool HasSecrets => _secrets.Length > 0;

    public string Redact(string text)
    {
        foreach (var secret in _secrets)
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>
    /// For streamed text that arrives in pieces. Splits already-masked text into what is safe to write now and
    /// a tail to hold back: the longest ending that could be the start of a secret whose rest has not arrived.
    /// </summary>
    public (string Emit, string Hold) SplitForStreaming(string masked)
    {
        var hold = 0;
        foreach (var secret in _secrets)
        {
            for (var length = Math.Min(secret.Length - 1, masked.Length); length > hold; length--)
            {
                if (masked.AsSpan().EndsWith(secret.AsSpan(0, length), StringComparison.Ordinal))
                {
                    hold = length;
                    break;
                }
            }
        }

        return hold == 0 ? (masked, string.Empty) : (masked[..^hold], masked[^hold..]);
    }
}
