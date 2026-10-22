using System.Text.Json;

namespace Yabt.Metadata.Implementation;

internal static class JsonWriterBuffering
{
    // Utf8JsonWriter keeps pending bytes even when its destination is a stream. Flush
    // between records so that an arbitrarily large document cannot grow that buffer.
    public static void FlushIfNeeded(Utf8JsonWriter writer)
    {
        if (writer.BytesPending >= 16 * 1024) { writer.Flush(); }
    }
}
