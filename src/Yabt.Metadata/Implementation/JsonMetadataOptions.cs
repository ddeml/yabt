using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yabt.Metadata.Implementation;

internal static class JsonMetadataOptions
{
    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        return options;
    }
}
