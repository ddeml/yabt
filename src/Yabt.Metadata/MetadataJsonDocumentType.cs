using System.Text;
using System.Text.Json;
using Yabt.Metadata.Implementation;

namespace Yabt.Metadata;

/// <summary>Checks document identity without materializing an entire JSON document.</summary>
public static class MetadataJsonDocumentType
{
    public static async Task<bool> HasAsync
    (
        Stream source,
        string expectedDocumentType,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(expectedDocumentType);
        using var tokens = new JsonProbeTokenStream(source, Math.Max("documentType".Length, expectedDocumentType.Length));
        var expected = Encoding.UTF8.GetBytes(expectedDocumentType);
        var buffer = new byte[16 * 1024];
        var state = new JsonReaderState();
        var count = 0;
        var finalBlock = false;
        var preambleChecked = false;
        var firstToken = true;
        var rootObject = false;
        var documentTypeProperty = false;
        var matched = false;
        try
        {
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
                var consumed = 0;
                if (preambleChecked)
                {
                    var reader = new Utf8JsonReader(buffer.AsSpan(0, count), finalBlock, state);
                    while (reader.Read())
                    {
                        if (firstToken)
                        {
                            rootObject = reader.TokenType == JsonTokenType.StartObject;
                            firstToken = false;
                        }
                        if (documentTypeProperty)
                        {
                            matched = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals(expected);
                            documentTypeProperty = false;
                        }
                        if (rootObject && reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.PropertyName)
                        {
                            documentTypeProperty = reader.ValueTextEquals("documentType"u8);
                        }
                    }
                    state = reader.CurrentState;
                    consumed = checked((int)reader.BytesConsumed);
                }
                if (consumed != 0)
                {
                    buffer.AsSpan(consumed, count - consumed).CopyTo(buffer);
                    count -= consumed;
                }
                if (finalBlock) { return rootObject && matched; }
                if (count == buffer.Length) { Array.Resize(ref buffer, checked(buffer.Length * 2)); }
                var read = await tokens.ReadAsync(buffer.AsMemory(count), cancellationToken);
                count += read;
                finalBlock = read == 0;
            }
        }
        catch (Exception ex)
        {
            if (ex is JsonException or DecoderFallbackException) { return false; }
            throw;
        }
    }
}
