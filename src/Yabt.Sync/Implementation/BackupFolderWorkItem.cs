namespace Yabt.Sync.Implementation;

internal sealed record BackupFolderWorkItem
(
    string RelativePath,
    string? RemovedAncestorPath = null
);
