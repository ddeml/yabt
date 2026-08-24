using System.Text.Json.Serialization;

namespace Yabt.Metadata;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveLogicalStateManifest
(
    [property: JsonRequired] string DocumentType,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] IEnumerable<ArchiveLogicalStateManifestEntry> Entries,
    [property: JsonRequired] string ManifestHash
)
{
    public const string FileName = ".yabt-logical-state-manifest.json";
    public const string InvalidationMarkerFileName = ".yabt-logical-state-manifest.invalid";
    public const string ExpectedDocumentType = "yabt.logicalStateManifest";
    public const int ExpectedSchemaVersion = 1;
}
