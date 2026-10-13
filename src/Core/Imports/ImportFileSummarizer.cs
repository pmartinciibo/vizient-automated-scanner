using System.Text;
using System.Text.Json;
using Vizient.AutomatedScanner.Core.Models;

namespace Vizient.AutomatedScanner.Core.Imports;

/// <summary>
/// Reads an import's raw file (rapid7.json, checkmarx.json) and says what is in it: one entry per application, with
/// its id, name and how many records are in each of its lists (scans, reports, vulnerabilities, projects, results).
/// The file can be gigabytes, so it is read as a stream and nothing but the counts is kept.
/// Both files are shaped { ..., "apps": [ { "id": ..., "name": ..., "<list>": [ ... ], ... }, ... ] }.
/// </summary>
public static class ImportFileSummarizer
{
    private const string Unassigned = "(unassigned)";
    private const int AppsDepth = 1;
    private const int AppDepth = 2;
    private const int AppFieldDepth = 3;
    private const int ListItemDepth = 4;

    public static List<ToolItemResult> Summarize(string path)
    {
        var items = new List<ToolItemResult>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

        var buffer = new byte[1 << 20];
        var held = 0;
        var state = default(JsonReaderState);
        var scan = new Scan(items);

        while (true)
        {
            var read = stream.Read(buffer, held, buffer.Length - held);
            var final = read == 0;
            held += read;

            var consumed = Feed(buffer.AsSpan(0, held), final, ref state, scan);

            var left = held - consumed;
            if (left == buffer.Length)
            {
                // One token is bigger than the buffer. Make room for it.
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            Buffer.BlockCopy(buffer, consumed, buffer, 0, left);
            held = left;

            if (final)
            {
                return items;
            }
        }
    }

    // Kept apart from the loop above: the reader is a ref struct and may not live across the stream reads there.
    private static int Feed(ReadOnlySpan<byte> bytes, bool final, ref JsonReaderState state, Scan scan)
    {
        var reader = new Utf8JsonReader(bytes, final, state);
        while (reader.Read())
        {
            scan.Take(ref reader);
        }

        state = reader.CurrentState;
        return (int)reader.BytesConsumed;
    }

    private sealed class Scan(List<ToolItemResult> items)
    {
        private bool _inApps;
        private string? _property;
        private string? _list;
        private string? _key;
        private string? _name;
        private Dictionary<string, int> _counts = new();

        public void Take(ref Utf8JsonReader reader)
        {
            var depth = reader.CurrentDepth;
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName when depth == AppsDepth:
                    _inApps = reader.ValueTextEquals("apps");
                    break;
                case JsonTokenType.PropertyName when _inApps && depth == AppFieldDepth:
                    _property = reader.GetString();
                    break;
                case JsonTokenType.StartObject when _inApps && depth == AppDepth:
                    _key = null;
                    _name = null;
                    _counts = new Dictionary<string, int>();
                    break;
                case JsonTokenType.EndObject when _inApps && depth == AppDepth:
                    items.Add(new ToolItemResult
                    {
                        Key = _key ?? Unassigned,
                        Name = _name,
                        Outcome = "Imported",
                        Counts = _counts
                    });
                    break;
                case JsonTokenType.StartArray when _inApps && depth == AppFieldDepth && _property is not null:
                    _list = _property;
                    _counts[_list] = 0;
                    break;
                case JsonTokenType.EndArray when _inApps && depth == AppFieldDepth:
                    _list = null;
                    break;
                case JsonTokenType.String or JsonTokenType.Number when _inApps && depth == AppFieldDepth:
                    if (_property == "id")
                    {
                        _key = Encoding.UTF8.GetString(reader.ValueSpan);
                    }
                    else if (_property == "name" && reader.TokenType == JsonTokenType.String)
                    {
                        _name = reader.GetString();
                    }

                    break;
                default:
                    // An element of one of the application's lists: its first token is at this depth, whatever it is.
                    if (_inApps && _list is not null && depth == ListItemDepth
                        && reader.TokenType is not (JsonTokenType.EndObject or JsonTokenType.EndArray))
                    {
                        _counts[_list]++;
                    }

                    break;
            }
        }
    }
}
