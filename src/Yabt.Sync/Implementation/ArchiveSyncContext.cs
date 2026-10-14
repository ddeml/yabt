using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed record ArchiveSyncContext
(
    string SourceDisplayName,
    string? SourcePrefix,
    IObjectStore SourceStore,
    IObjectStore TargetStore,
    BackupRootDescriptor SourceDescriptor,
    BackupRootDescriptor TargetDescriptor,
    BackupRootDocument? SourceDocument,
    FolderPolicy Policy,
    IArchiveFormatHandler FormatHandler,
    ArchiveSourceLocation SourceLocation
);
