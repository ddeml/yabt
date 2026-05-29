namespace Yabt.Core.Models;

public sealed record ArchiveFormatBackupRequest
(
    BackupRootDescriptor SourceRoot,
    BackupRootDescriptor TargetRoot,
    FolderPolicy Policy
);
