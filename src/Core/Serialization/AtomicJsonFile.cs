using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.Serialization;

public static class AtomicJsonFile
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    /// <summary>
    /// Reads a JSON file. // and /* */ comments and trailing commas are tolerated, so a file the invoker
    /// edits by hand (command.json) can carry notes. The file is opened so that someone else can still
    /// replace it while it is being read; otherwise a reader could make a writer's rename fail.
    /// </summary>
    public static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);

        var value = await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken);
        return value ?? throw new InvalidDataException($"The JSON file '{path}' contains null.");
    }

    public static Task WriteAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken = default) =>
        ReplaceAsync(
            path,
            stream => JsonSerializer.SerializeAsync(stream, value, SerializerOptions, cancellationToken),
            cancellationToken);

    /// <summary>
    /// Writes the value as JSON with comment lines above it. Each line of <paramref name="commentLines"/>
    /// becomes a // comment. <see cref="ReadAsync{T}"/> skips them.
    /// </summary>
    public static Task WriteWithCommentsAsync<T>(
        string path,
        T value,
        IEnumerable<string> commentLines,
        CancellationToken cancellationToken = default) =>
        ReplaceAsync(
            path,
            async stream =>
            {
                var header = new StringBuilder();
                foreach (var line in commentLines)
                {
                    header.Append(line.Length == 0 ? "//" : "// " + line).Append('\n');
                }

                await stream.WriteAsync(new UTF8Encoding(false).GetBytes(header.ToString()), cancellationToken);
                await JsonSerializer.SerializeAsync(stream, value, SerializerOptions, cancellationToken);
            },
            cancellationToken);

    /// <summary>Writes bytes with the same temp-file-then-rename guarantee as <see cref="WriteAsync{T}"/>.</summary>
    public static Task WriteBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default) =>
        ReplaceAsync(path, stream => stream.WriteAsync(bytes, cancellationToken).AsTask(), cancellationToken);

    /// <summary>Copies a file with the same temp-file-then-rename guarantee as <see cref="WriteAsync{T}"/>.</summary>
    public static async Task CopyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);

        await ReplaceAsync(destinationPath, target => source.CopyToAsync(target, cancellationToken), cancellationToken);
    }

    // Writes to a temp file beside the target, flushes it to disk, then renames it over the target.
    private static async Task ReplaceAsync(
        string path,
        Func<FileStream, Task> write,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The path must include a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await write(stream);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        options.Converters.Add(new RunPhaseJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
