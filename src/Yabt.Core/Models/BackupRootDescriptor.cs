using System.Text.Json.Serialization;

namespace Yabt.Core.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BackupRootDescriptor
(
    string DocumentType,
    int SchemaVersion,
    string ArchiveId,
    DateTimeOffset CreatedAtUtc,
    ArchiveLayout Layout,
    IEnumerable<BackupRootStore> Stores,
    string? RootRole = default,
    string? Name = default,
    string? DefaultStoreId = default,
    string? ChangeManifestCompression = default
)
{
    public const string ExpectedDocumentType = "yabt.backupRoot";
    public const int ExpectedSchemaVersion = 1;
}
