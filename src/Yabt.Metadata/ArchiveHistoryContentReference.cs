using System.Text.Json.Serialization;

namespace Yabt.Metadata;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveHistoryContentReference
(
    [property: JsonRequired] string DocumentType,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string Message,
    [property: JsonRequired] ArchiveHistoryManifestEntry Entry,
    [property: JsonRequired] string ReferenceHash
)
{
    public const string ReferenceFileNameSuffix = ".yabt-ref.json";
    public const string ExpectedDocumentType = "yabt.historyContentReference";
    public const int ExpectedSchemaVersion = 1;
    public const string DefaultMessage =
        "This historical object has the same content as another materialized historical object.";
}
