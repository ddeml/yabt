using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed record RestoreFolderRecord
(
    string FolderPath,
    ArchiveChangeManifestEntry? Entry = null,
    string? ChildPath = null
);
