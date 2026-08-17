using System.Text.Json.Serialization;

namespace Yabt.Metadata;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveChangeManifestEntry
(
    [property: JsonRequired] string RelativePath,
    [property: JsonRequired] string ChangeFingerprint,
    long? ArtifactLength = default,
    string? ContentHash = default
);
