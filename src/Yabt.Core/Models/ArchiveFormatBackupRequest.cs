using Yabt.Core.Abstractions;

namespace Yabt.Core.Models;

public sealed record ArchiveFormatBackupRequest
(
    IObjectStore SourceStore,
    IObjectStore TargetStore,
    BackupRootDescriptor SourceRoot,
    BackupRootDescriptor TargetRoot,
    FolderPolicy Policy
);
