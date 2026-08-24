using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yabt.Core.Models;

public sealed record BackupRootStore
(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Kind,
    string? CredentialRef = default,
    string? ConfigSectionPath = default
)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ProviderProperties { get; init; }
}
