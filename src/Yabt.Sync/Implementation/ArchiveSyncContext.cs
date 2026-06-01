using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

internal sealed record ArchiveSyncContext
(
    string SourceRoot,
    IObjectStore SourceStore,
    IObjectStore TargetStore,
    BackupRootDescriptor SourceDescriptor,
    BackupRootDescriptor TargetDescriptor,
    FolderPolicy Policy,
    IArchiveFormatProjector Projector
);
