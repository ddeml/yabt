using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yabt.Common.Implementation;

/// <summary>
/// Preserves exact UTF-16 strings in ephemeral records. Ordinary JSON encoding
/// replaces unpaired surrogates; these values instead use reversible code units.
/// Strings beginning with the tag are also encoded to prevent aliases.
/// </summary>
internal sealed class TemporaryIndexStringConverter : JsonConverter<string>
{
    private const string Prefix = "~yabt-utf16:";

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Decode(reader.GetString());

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Encode(value));

    public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Decode(reader.GetString())!;

    public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WritePropertyName(Encode(value));

    private static string Encode(string value)
    {
        if (!RequiresEncoding(value)) { return value; }
        var bytes = new byte[checked(value.Length * 2)];
        for (var index = 0; index < value.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index * 2), value[index]);
        }
        return Prefix + Convert.ToBase64String(bytes);
    }

    private static string? Decode(string? value)
    {
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal)) { return value; }
        var bytes = Convert.FromBase64String(value[Prefix.Length..]);
        if (bytes.Length % 2 != 0) { throw new JsonException("Temporary UTF-16 value has an incomplete code unit."); }
        return string.Create(bytes.Length / 2, bytes, static (destination, source) =>
        {
            for (var index = 0; index < destination.Length; index++)
            {
                destination[index] = (char)BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(index * 2));
            }
        });
    }

    private static bool RequiresEncoding(string value)
    {
        if (value.StartsWith(Prefix, StringComparison.Ordinal)) { return true; }
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])) { return true; }
                index++;
            }
            else if (char.IsLowSurrogate(value[index])) { return true; }
        }
        return false;
    }
}
