using System.Text.Json;
using System.Text.Json.Serialization;
using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.Serialization;

public static class AtomicJsonFile
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        var value = await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken);
        return value ?? throw new InvalidDataException($"The JSON file '{path}' contains null.");
    }

    public static async Task WriteAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken = default)
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
                await JsonSerializer.SerializeAsync(stream, value, SerializerOptions, cancellationToken);
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
            WriteIndented = true
        };
        options.Converters.Add(new RunPhaseJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}