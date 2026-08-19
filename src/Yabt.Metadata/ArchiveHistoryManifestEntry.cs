using System.Text.Json.Serialization;

namespace Yabt.Metadata;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveHistoryManifestEntry
(
    [property: JsonRequired] string RelativePath,
    [property: JsonRequired] string StoredRelativePath,
    [property: JsonRequired] string Representation,
    [property: JsonRequired]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    long ContentLength,
    [property: JsonRequired] string ContentHash,
    DateTimeOffset? LastModifiedUtc = default,
    string? ContentType = default,
    IReadOnlyDictionary<string, string>? Metadata = default
);
