using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed record RestoreContext
(
    IObjectStore ArchiveStore,
    BackupRootDescriptor Descriptor,
    BackupRootDocument? SourceDocument,
    BackupRootDocument? ArchiveDocument,
    IArchiveFormatHandler? RootFormatHandler,
    bool RootIsPackaged,
    ArchiveSourceLocation SourceLocation
);
