using System.Text;

namespace Vizient.AutomatedScanner.Core.Processes;

/// <summary>Copies a child's output stream to a sink, masking secrets on the way.</summary>
internal static class TextPump
{
    private const int ChunkChars = 8192;

    /// <summary>Reads the stream as UTF-8 text and hands masked text to <paramref name="sink"/>.</summary>
    public static async Task RunAsync(
        Stream source,
        SecretRedactor redactor,
        Action<string> sink,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(source, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, ChunkChars);
        var buffer = new char[ChunkChars];
        var held = string.Empty;

        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            var masked = redactor.Redact(held + new string(buffer, 0, read));
            var (emit, hold) = redactor.SplitForStreaming(masked);
            held = hold;
            if (emit.Length > 0)
            {
                sink(emit);
            }
        }

        // The stream ended, so what was held back can no longer grow into a secret.
        if (held.Length > 0)
        {
            sink(held);
        }
    }

    /// <summary>Writes the stream to a file. With nothing to mask the bytes are copied as they are.</summary>
    public static async Task ToFileAsync(
        Stream source,
        FileStream file,
        SecretRedactor redactor,
        CancellationToken cancellationToken)
    {
        if (!redactor.HasSecrets)
        {
            await source.CopyToAsync(file, cancellationToken);
            return;
        }

        await using var writer = new StreamWriter(file, new UTF8Encoding(false), leaveOpen: true);
        await RunAsync(source, redactor, writer.Write, cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }
}

/// <summary>
/// Keeps the start and the end of text that may be far larger than we want to hold, because the error is
/// usually at the end and the context at the start.
/// </summary>
internal sealed class BoundedText(int limit)
{
    private readonly object _gate = new();
    private readonly int _headLimit = limit / 2;
    private readonly int _tailLimit = limit - limit / 2;
    private readonly StringBuilder _head = new();
    private readonly StringBuilder _tail = new();
    private long _dropped;

    public void Append(string text)
    {
        lock (_gate)
        {
            var offset = 0;
            if (_head.Length < _headLimit)
            {
                offset = Math.Min(_headLimit - _head.Length, text.Length);
                _head.Append(text, 0, offset);
            }

            if (offset < text.Length)
            {
                _tail.Append(text, offset, text.Length - offset);
                if (_tail.Length > _tailLimit)
                {
                    var excess = _tail.Length - _tailLimit;
                    _tail.Remove(0, excess);
                    _dropped += excess;
                }
            }
        }
    }

    public bool Truncated
    {
        get { lock (_gate) { return _dropped > 0; } }
    }

    public override string ToString()
    {
        lock (_gate)
        {
            return _dropped == 0
                ? _head.ToString() + _tail
                : $"{_head}\n[... {_dropped} characters not kept ...]\n{_tail}";
        }
    }
}
