using Yabt.Core.Abstractions;

namespace Yabt.Core.Models;

public sealed record ArchiveFormatRestoreRequest
(
    IObjectStore SourceStore,
    IObjectStore TargetStore,
    BackupRootDescriptor SourceRoot,
    BackupRootDescriptor TargetRoot,
    FolderPolicy Policy
);
