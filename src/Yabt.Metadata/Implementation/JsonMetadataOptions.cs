using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yabt.Metadata.Implementation;

internal static class JsonMetadataOptions
{
    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
