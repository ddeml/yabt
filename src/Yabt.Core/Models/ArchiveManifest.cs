using System.Text.Json.Serialization;

namespace Yabt.Core.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveManifest
(
    [property: JsonRequired] string DocumentType,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string SourcePath,
    [property: JsonRequired] DateTimeOffset CreatedAtUtc,
    [property: JsonRequired] string Format,
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] string ProjectionId,
    [property: JsonRequired] string PackageName,
    [property: JsonRequired] FolderPolicy Policy,
    [property: JsonRequired] IEnumerable<ArchiveManifestEntry> Entries,
    [property: JsonRequired] long TotalBytes,
    [property: JsonRequired] string ManifestHash
)
{
    public const string ExpectedDocumentType = "yabt.packageManifest";
    public const int ExpectedSchemaVersion = 1;
}
