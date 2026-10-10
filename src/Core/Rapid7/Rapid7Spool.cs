using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Vizient.AutomatedScanner.Core.Rapid7;

/// <summary>
/// A temporary file holding one application's vulnerabilities while the rest are still being fetched. The API does
/// not return them grouped by application, so each is set aside here until they can be written together. A record
/// is stored as it arrived (length, then its JSON text), so reading it back changes nothing about it.
/// </summary>
internal sealed class Rapid7Spool : IDisposable
{
    private FileStream? _writer;

    public Rapid7Spool(string path)
    {
        Path = path;
        _writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
    }

    public string Path { get; }

    public int Count { get; private set; }

    public void Append(JsonElement record)
    {
        var bytes = Encoding.UTF8.GetBytes(record.GetRawText());
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        _writer!.Write(length);
        _writer.Write(bytes);
        Count++;
    }

    /// <summary>Pushes what has been set aside to disk, so memory does not build up between pages.</summary>
    public Task FlushAsync(CancellationToken cancellationToken) =>
        _writer is null ? Task.CompletedTask : _writer.FlushAsync(cancellationToken);

    /// <summary>
    /// Writes every record held, in the order they arrived, as values of the array the writer is in. The records are
    /// copied through untouched. Nothing more can be added afterwards.
    /// </summary>
    public async Task CopyTo(Utf8JsonWriter writer, CancellationToken cancellationToken)
    {
        _writer?.Dispose();
        _writer = null;

        await using var reader = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.None, 64 * 1024, useAsync: true);
        var length = new byte[4];
        var buffer = new byte[64 * 1024];

        for (var i = 0; i < Count; i++)
        {
            await reader.ReadExactlyAsync(length, cancellationToken);
            var size = BinaryPrimitives.ReadInt32LittleEndian(length);
            if (size > buffer.Length)
            {
                buffer = new byte[size];
            }

            await reader.ReadExactlyAsync(buffer.AsMemory(0, size), cancellationToken);

            // These bytes came from a record that parsed, so they are known to be valid JSON.
            writer.WriteRawValue(buffer.AsSpan(0, size), skipInputValidation: true);
        }
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}
