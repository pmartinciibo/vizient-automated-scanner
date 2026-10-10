using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vizient.AutomatedScanner.Core.Models;
using Vizient.AutomatedScanner.Core.Serialization;

namespace Vizient.AutomatedScanner.Core.Secrets;

/// <summary>The secrets file is damaged, is not a secrets file, or cannot be decrypted by this account here.</summary>
public sealed class SecretsFileException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// secrets.dat (design section 12): an 8-byte clear header "VSCRT001" followed by one Windows DPAPI blob,
/// protected for the current Windows user with fixed application entropy. Only the account that wrote it can read
/// it, and only on the machine it was written on. The blob holds a <see cref="SecretsDocument"/> as JSON.
/// </summary>
public static class SecretsFile
{
    public const string Header = "VSCRT001";

    private static readonly byte[] HeaderBytes = Encoding.ASCII.GetBytes(Header);

    // Not a secret. It ties the blob to this application, so another program running as the same user and calling
    // DPAPI without it cannot read the file by accident.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Vizient.AutomatedScanner.Secrets.v1");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>Reads and decrypts the file. Throws <see cref="SecretsFileException"/> if it cannot be read.</summary>
    public static async Task<SecretsDocument> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);

        if (bytes.Length <= HeaderBytes.Length || !bytes.AsSpan(0, HeaderBytes.Length).SequenceEqual(HeaderBytes))
        {
            throw new SecretsFileException($"'{path}' is not a secrets file (the {Header} header is missing).");
        }

        byte[] plain;
        try
        {
            plain = Unprotect(bytes.AsSpan(HeaderBytes.Length).ToArray());
        }
        catch (CryptographicException ex)
        {
            throw new SecretsFileException(
                $"'{path}' cannot be decrypted. It can only be read by the Windows account that created it, on the machine " +
                "it was created on, and it may be damaged.",
                ex);
        }

        try
        {
            return JsonSerializer.Deserialize<SecretsDocument>(plain, Json)
                ?? throw new SecretsFileException($"'{path}' decrypted to nothing.");
        }
        catch (JsonException ex)
        {
            throw new SecretsFileException($"'{path}' decrypted, but its content is not valid.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>
    /// Encrypts and writes the file atomically (temp file, then rename). The version it replaces is kept as
    /// secrets.dat.bak, protected the same way, unless it is not a secrets file.
    /// </summary>
    public static async Task WriteAsync(string path, SecretsDocument document, CancellationToken cancellationToken = default)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(document, Json);
        byte[] blob;
        try
        {
            blob = Protect(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        if (File.Exists(path) && await HasHeaderAsync(path, cancellationToken))
        {
            await AtomicJsonFile.CopyAsync(path, path + ".bak", cancellationToken);
        }

        await AtomicJsonFile.WriteBytesAsync(path, [.. HeaderBytes, .. blob], cancellationToken);
    }

    internal static byte[] Protect(byte[] plain) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)
            : throw NotWindows();

    internal static byte[] Unprotect(byte[] blob) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser)
            : throw NotWindows();

    private static async Task<bool> HasHeaderAsync(string path, CancellationToken cancellationToken)
    {
        var start = new byte[HeaderBytes.Length];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var read = await stream.ReadAtLeastAsync(start, start.Length, throwOnEndOfStream: false, cancellationToken);
        return read == start.Length && start.AsSpan().SequenceEqual(HeaderBytes);
    }

    private static PlatformNotSupportedException NotWindows() =>
        new("The secrets file uses Windows DPAPI, so it only works on Windows.");
}
