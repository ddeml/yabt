namespace Yabt.Core.Models;

public sealed record ArchiveFormatVerifyRequest
(
    BackupRootDescriptor SourceRoot,
    BackupRootDescriptor TargetRoot,
    FolderPolicy Policy
);
