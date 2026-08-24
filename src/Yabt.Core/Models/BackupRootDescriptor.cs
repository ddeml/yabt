using System.Text.Json.Serialization;

namespace Yabt.Core.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BackupRootDescriptor
(
    [property: JsonRequired] string DocumentType,
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string ArchiveId,
    [property: JsonRequired] DateTimeOffset CreatedAtUtc,
    [property: JsonRequired] ArchiveLayout Layout,
    [property: JsonRequired] IEnumerable<BackupRootStore> Stores,
    string? RootRole = default,
    string? Name = default,
    string? DefaultStoreId = default,
    string? ChangeManifestCompression = default,
    long? HistoryDeduplicationTinyFileMaximumBytes = default
)
{
    public const string ExpectedDocumentType = "yabt.backupRoot";
    public const int ExpectedSchemaVersion = 1;
}
