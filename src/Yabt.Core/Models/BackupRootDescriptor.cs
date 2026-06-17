namespace Yabt.Core.Models;

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
    string? DefaultStoreId = default
)
{
    public const string ExpectedDocumentType = "yabt.backupRoot";
}
