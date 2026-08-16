using System.Text.Json.Serialization;

namespace Yabt.Metadata;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveChangeManifestEntry
(
    [property: JsonRequired] string RelativePath,
    [property: JsonRequired]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    long Length,
    DateTimeOffset? LastModifiedUtc,
    [property: JsonRequired] string ChangeFingerprint,
    string? ContentHash = default
);
