using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Vizient.AutomatedScanner.Core.Imports;

/// <summary>Where a spool stood at one moment, so what was added after it can be taken back.</summary>
internal readonly record struct SpoolCheckpoint(long Length, int Count);

/// <summary>
/// A temporary file holding records set aside while the rest are still being fetched, because they arrive in an order
/// that is not the order they have to be written in. A record is stored as it arrived (length, then its JSON text), so
/// reading it back changes nothing about it. Used by the Rapid7 and CheckMarx imports.
/// </summary>
internal sealed class RecordSpool : IDisposable
{
    private FileStream? _writer;
    private bool _readStarted;

    public RecordSpool(string path) => Path = path;

    public string Path { get; }

    public int Count { get; private set; }

    public void Append(JsonElement record) => AppendRaw(Encoding.UTF8.GetBytes(record.GetRawText()));

    /// <summary>Adds a record that is already JSON text.</summary>
    public void AppendRaw(ReadOnlySpan<byte> json)
    {
        if (_readStarted)
        {
            throw new InvalidOperationException("Nothing can be added once the records have been read back.");
        }

        var writer = _writer ??= new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.None, 64 * 1024);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, json.Length);
        writer.Write(length);
        writer.Write(json);
        Count++;
    }

    public SpoolCheckpoint Checkpoint()
    {
        _writer?.Flush();
        return new SpoolCheckpoint(File.Exists(Path) ? new FileInfo(Path).Length : 0, Count);
    }

    /// <summary>Takes back everything added since the checkpoint, as if it had never been.</summary>
    public void Rollback(SpoolCheckpoint checkpoint)
    {
        Close();
        if (File.Exists(Path))
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.SetLength(checkpoint.Length);
        }

        Count = checkpoint.Count;
    }

    /// <summary>Pushes what has been set aside to disk, so memory does not build up between pages.</summary>
    public Task FlushAsync(CancellationToken cancellationToken) =>
        _writer is null ? Task.CompletedTask : _writer.FlushAsync(cancellationToken);

    /// <summary>
    /// Closes the file but keeps what is in it, so a spool that is done with for now does not hold a file open.
    /// More can be added later and goes after what is there.
    /// </summary>
    public void Close()
    {
        _writer?.Dispose();
        _writer = null;
    }

    /// <summary>
    /// Writes every record held, in the order they arrived, as values of the array the writer is in. The records are
    /// copied through untouched. Nothing more can be added afterwards.
    /// </summary>
    public async Task CopyTo(Utf8JsonWriter writer, CancellationToken cancellationToken)
    {
        Close();
        _readStarted = true;
        if (Count == 0 || !File.Exists(Path))
        {
            return;
        }

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
        Close();
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}
