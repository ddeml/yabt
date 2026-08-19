using System.Text.Json.Serialization;

namespace Yabt.Metadata;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveHistoryManifest
(
    [property: JsonRequired] string DocumentType,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] IEnumerable<ArchiveHistoryManifestEntry> Entries,
    [property: JsonRequired] string ManifestHash
)
{
    public const string InvalidationMarkerFileName = ".yabt-history-manifest.invalid";
    public const string ExpectedDocumentType = "yabt.historyManifest";
    public const int ExpectedSchemaVersion = 1;
}
