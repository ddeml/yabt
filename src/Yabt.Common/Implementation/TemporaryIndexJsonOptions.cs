using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yabt.Common.Implementation;

internal static class TemporaryIndexJsonOptions
{
    public static JsonSerializerOptions Create(JsonSerializerOptions? options)
    {
        var result = new JsonSerializerOptions(options ?? JsonSerializerOptions.Default)
        {
            // Temporary records must replay every value, independently of a durable
            // format's omission rules (including required properties whose value is 0).
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            IgnoreReadOnlyProperties = false,
            IgnoreReadOnlyFields = false
        };
        result.Converters.Insert(0, new TemporaryIndexStringConverter());
        return result;
    }
}
