using System.Text.Json;
using Yabt.Common;

namespace Yabt.Metadata.Implementation;

internal static class StreamedManifestJson
{
    public static async Task<StreamedManifestDocument<TEntry>> ReadAsync<TEntry>
    (
        Stream source,
        IEnumerable<string> allowedProperties,
        JsonSerializerOptions jsonOptions,
        TemporaryContentOptions? temporaryOptions,
        CancellationToken cancellationToken = default
    )
    {
        var entries = new TemporaryManifestEntries<TEntry>(temporaryOptions, jsonOptions, ordered: false);
        try
        {
            using var header = new MemoryStream();
            using var writer = new Utf8JsonWriter(header);
            var parser = new ManifestParser<TEntry>(allowedProperties, jsonOptions, entries, writer);
            var buffer = new byte[16 * 1024];
            var count = 0;
            var finalBlock = false;
            var preambleChecked = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!preambleChecked && (count >= 3 || finalBlock))
                {
                    if (count >= 3 && buffer.AsSpan(0, 3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }))
                    {
                        buffer.AsSpan(3, count - 3).CopyTo(buffer);
                        count -= 3;
                    }
                    preambleChecked = true;
                }
                var consumed = preambleChecked ? parser.Process(buffer.AsSpan(0, count), finalBlock, cancellationToken) : 0;
                if (consumed != 0)
                {
                    buffer.AsSpan(consumed, count - consumed).CopyTo(buffer);
                    count -= consumed;
                }
                if (finalBlock)
                {
                    parser.EnsureComplete();
                    break;
                }
                if (count == buffer.Length) { Array.Resize(ref buffer, checked(buffer.Length * 2)); }
                var read = await source.ReadAsync(buffer.AsMemory(count), cancellationToken);
                count += read;
                finalBlock = read == 0;
            }
            await writer.FlushAsync(cancellationToken);
            var document = JsonDocument.Parse(header.GetBuffer().AsMemory(0, checked((int)header.Length)));
            return new(document, entries, parser.HasEntryProjectionProperty);
        }
        catch (Exception)
        {
            entries.Dispose();
            throw;
        }
    }

    private sealed class ManifestParser<TEntry>
    {
        private readonly JsonSerializerOptions _options;
        private readonly TemporaryManifestEntries<TEntry> _entries;
        private readonly Utf8JsonWriter _header;
        private readonly HashSet<string> _allowed;
        private readonly HashSet<string> _seen;
        private JsonReaderState _readerState;
        private ParsePhase _phase;

        public ManifestParser
        (
            IEnumerable<string> allowedProperties,
            JsonSerializerOptions options,
            TemporaryManifestEntries<TEntry> entries,
            Utf8JsonWriter header
        )
        {
            _options = options;
            _entries = entries;
            _header = header;
            var comparer = options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            _allowed = new(allowedProperties, comparer);
            _seen = new(comparer);
            _readerState = new(new JsonReaderOptions
            {
                AllowTrailingCommas = options.AllowTrailingCommas,
                CommentHandling = options.ReadCommentHandling,
                MaxDepth = options.MaxDepth
            });
        }

        public bool HasEntryProjectionProperty { get; private set; }

        public int Process(ReadOnlySpan<byte> bytes, bool finalBlock, CancellationToken cancellationToken)
        {
            var reader = new Utf8JsonReader(bytes, finalBlock, _readerState);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_phase == ParsePhase.Value)
                {
                    if (!JsonDocument.TryParseValue(ref reader, out var value)) { break; }
                    using (value) { value.RootElement.WriteTo(_header); }
                    _phase = ParsePhase.Property;
                    continue;
                }
                if (_phase == ParsePhase.Entry)
                {
                    var before = reader;
                    if (!reader.Read()) { break; }
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        _phase = ParsePhase.Property;
                        continue;
                    }
                    if (!JsonDocument.TryParseValue(ref reader, out var entryDocument))
                    {
                        reader = before;
                        break;
                    }
                    using (entryDocument)
                    {
                        var root = entryDocument.RootElement;
                        HasEntryProjectionProperty |= root.ValueKind == JsonValueKind.Object &&
                            root.TryGetProperty("projection", out _);
                        var entry = root.Deserialize<TEntry>(_options);
                        if (entry is null) { throw new JsonException("Manifest entries cannot contain null values."); }
                        _entries.Add(entry);
                    }
                    continue;
                }

                if (!reader.Read()) { break; }
                switch (_phase)
                {
                    case ParsePhase.Start:
                        if (reader.TokenType != JsonTokenType.StartObject)
                        {
                            throw new JsonException("Manifest JSON must contain an object.");
                        }
                        _header.WriteStartObject();
                        _phase = ParsePhase.Property;
                        break;
                    case ParsePhase.Property:
                        if (reader.TokenType == JsonTokenType.EndObject)
                        {
                            _header.WriteEndObject();
                            _phase = ParsePhase.Complete;
                            break;
                        }
                        if (reader.TokenType != JsonTokenType.PropertyName)
                        {
                            throw new JsonException("Manifest JSON expected a property name.");
                        }
                        var property = reader.GetString()!;
                        if (!_allowed.Contains(property)) { throw new JsonException($"Unknown manifest property '{property}'."); }
                        if (!_seen.Add(property)) { throw new JsonException($"Duplicate manifest property '{property}'."); }
                        _header.WritePropertyName(property);
                        _phase = _seen.Comparer.Equals(property, "entries") ? ParsePhase.EntryArray : ParsePhase.Value;
                        break;
                    case ParsePhase.EntryArray:
                        if (reader.TokenType != JsonTokenType.StartArray)
                        {
                            throw new JsonException("Manifest entries must be an array.");
                        }
                        _header.WriteStartArray();
                        _header.WriteEndArray();
                        _phase = ParsePhase.Entry;
                        break;
                    default:
                        throw new JsonException("Manifest JSON contains trailing content.");
                }
            }
            _readerState = reader.CurrentState;
            return checked((int)reader.BytesConsumed);
        }

        public void EnsureComplete()
        {
            if (_phase != ParsePhase.Complete) { throw new JsonException("Manifest JSON ended before its object was complete."); }
        }

        private enum ParsePhase
        {
            Start,
            Property,
            Value,
            EntryArray,
            Entry,
            Complete
        }
    }
}
