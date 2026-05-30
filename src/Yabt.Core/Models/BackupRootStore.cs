using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yabt.Core.Models;

public sealed record BackupRootStore
(
    string Id,
    string Kind,
    string? CredentialRef = default
)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ProviderProperties { get; init; }
}
