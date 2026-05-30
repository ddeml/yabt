namespace Yabt.Core.Models;

public sealed record ArchiveFormatRestoreRequest
(
    BackupRootDescriptor SourceRoot,
    BackupRootDescriptor TargetRoot,
    FolderPolicy Policy
);
