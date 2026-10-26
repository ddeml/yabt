using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

/// <summary>
/// Prioritizes complete immediate package groups inside an already validated
/// folder unit. Naming/projection discovery still precedes any of its writes.
/// </summary>
internal sealed class BackupPackageProgress
(
    OperationProgress? _progress,
    ArchiveProjectionObjectStore _source
)
{
    private readonly Dictionary<RestoreProjectionKey, string> _packages = [];
    private readonly HashSet<RestoreProjectionKey> _previouslyCompleted = [];
    private readonly HashSet<RestoreProjectionKey> _started = [];

    internal IEnumerable<ArchiveProjectedObject> PrepareUnit(ArchiveBackupWorkUnit unit)
    {
        _packages.Clear();
        _previouslyCompleted.Clear();
        _started.Clear();
        if (_progress is null) { return unit.Objects; }
        var folder = ObjectPath.Parse(unit.RelativePath);
        var groups = unit.Objects.Where(item => item.Projection is not null).GroupBy(item => Key(item.Projection!));
        foreach (var group in groups)
        {
            var projection = group.First().Projection!;
            var logicalPath = ObjectPath.Parse(projection.LogicalPath);
            if (logicalPath.IsRoot || logicalPath.Parent != folder ||
                !_source.TryGetProjectionArtifactCount(projection, out var completeCount) ||
                completeCount != group.Count())
            {
                continue;
            }
            _packages.Add(group.Key, logicalPath.Value);
            if (!_progress.HasUnfinishedSubtree(logicalPath.Value)) { _previouslyCompleted.Add(group.Key); }
        }
        if (_previouslyCompleted.Count == 0) { return unit.Objects; }
        // The unit already owns a stable list. Partition it in two linear passes
        // without allocating another ordered array or sorting every file.
        return unit.Objects.Where(item => !WasCompleted(item)).Concat(unit.Objects.Where(WasCompleted));
    }

    internal void StartGroup(ArchiveProjectionProvenance? projection)
    {
        if (projection is null) { return; }
        var key = Key(projection);
        if (_packages.TryGetValue(key, out var path) && _started.Add(key)) { _progress?.StartUnit(path); }
    }

    internal void CompleteGroup(RestoreProjectionKey key, bool succeeded)
    {
        if (succeeded && _packages.TryGetValue(key, out var path)) { _progress?.CompleteUnit(path, []); }
    }

    internal IEnumerable<string> ChildFrontier(ArchiveBackupWorkUnit unit) =>
        unit.ChildFolderPaths.Concat(_packages.Values).Distinct(StringComparer.Ordinal);

    private bool WasCompleted(ArchiveProjectedObject item) =>
        item.Projection is not null && _previouslyCompleted.Contains(Key(item.Projection));

    private static RestoreProjectionKey Key(ArchiveProjectionProvenance projection) => new
    (
        projection.LogicalPath, projection.Format, projection.FormatVersion, projection.ProjectionId
    );
}
