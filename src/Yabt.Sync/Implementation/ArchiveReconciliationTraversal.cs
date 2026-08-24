namespace Yabt.Sync.Implementation;

internal sealed record ArchiveReconciliationTraversal
(
    IReadOnlySet<string> DesiredObjectPaths,
    IReadOnlySet<string> DesiredFolderPaths
);
