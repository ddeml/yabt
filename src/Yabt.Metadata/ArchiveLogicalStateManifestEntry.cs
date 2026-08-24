using System.Text.Json.Serialization;

namespace Yabt.Metadata;

/// <summary>
/// Records quick filesystem evidence and the last validated content hash for one logical file.
/// The canonical stat-v1 fingerprint already contains length and modification time, so those
/// values are intentionally not duplicated in separate properties.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveLogicalStateManifestEntry
(
    [property: JsonRequired] string LogicalRelativePath,
    [property: JsonRequired] string StatFingerprint,
    [property: JsonRequired] string ContentHash
);
