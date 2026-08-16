using System.Text.Json.Serialization;

namespace Yabt.Metadata;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveChangeManifest
(
    [property: JsonRequired] string DocumentType,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] IEnumerable<ArchiveChangeManifestEntry> Entries,
    [property: JsonRequired] string ManifestHash
)
{
    public const string FileName = ".yabt-change-manifest.json";
    public const string ExpectedDocumentType = "yabt.changeManifest";
    public const int ExpectedSchemaVersion = 1;
}
