using Yabt.Common;
using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

/// <summary>
/// Operation-local discovery frontier. A completed folder schedules its immediate
/// children; this disposable index is not a durable resume checkpoint.
/// </summary>
internal sealed class BackupFolderTraversal : IDisposable
{
    private readonly DiskEntryCollection<BackupFolderWorkItem> _pendingFolders;
    private readonly DiskSet _desiredFolders;
    private long _nextFolder;

    public BackupFolderTraversal(TemporaryIndex index)
    {
        _pendingFolders = index.CreateCollection<BackupFolderWorkItem>();
        _desiredFolders = index.CreateSet();
        _pendingFolders.Add(new(string.Empty));
        _desiredFolders.Add(string.Empty);
    }

    public IReadOnlySet<string> DesiredFolderPaths => _desiredFolders;

    public bool TryTake(out BackupFolderWorkItem work)
    {
        if (_nextFolder == _pendingFolders.LongCount)
        {
            work = null!;
            return false;
        }

        work = _pendingFolders.Get(_nextFolder++);
        return true;
    }

    public static void ValidateUnit(BackupFolderWorkItem work, ArchiveBackupWorkUnit unit)
    {
        var folder = ObjectPath.Parse(work.RelativePath);
        if (ObjectPath.Parse(unit.RelativePath) != folder)
        {
            throw new YabtSyncException("A backup folder projection returned a different work-unit path.");
        }

        foreach (var item in unit.Objects)
        {
            RequireImmediateChild(folder, item.RelativePath);
        }

        var children = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in unit.ChildFolderPaths)
        {
            RequireImmediateChild(folder, child);
            if (!children.Add(child))
            {
                throw new YabtSyncException($"Backup folder '{folder}' returned child '{child}' more than once.");
            }
        }
    }

    public void ScheduleChildren
    (
        BackupFolderWorkItem work,
        ArchiveBackupWorkUnit unit,
        IEnumerable<BackupRepresentationConflict> conflicts,
        StringComparer pathComparer,
        CancellationToken cancellationToken
    )
    {
        // Child discovery can continue after a collected payload-read failure.
        // Scheduling is not a successful-folder checkpoint.
        var removedPaths = new Dictionary<string, string>(pathComparer);
        foreach (var conflict in conflicts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            removedPaths.TryAdd(conflict.RelativePath, conflict.RelativePath);
        }

        var orderedChildren = unit.ChildFolderPaths.Order(StringComparer.Ordinal);
        foreach (var child in orderedChildren)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_desiredFolders.Add(child))
            {
                throw new YabtSyncException($"Backup folder '{child}' was scheduled more than once.");
            }

            // The earliest removed ancestor models the entire recreated subtree.
            // Keep only that path instead of accumulating every preceding removal.
            removedPaths.TryGetValue(child, out var removedChild);
            _pendingFolders.Add(new(child, work.RemovedAncestorPath ?? removedChild));
        }
    }

    private static void RequireImmediateChild(ObjectPath folder, string childPath)
    {
        var child = ObjectPath.Parse(childPath);
        if (child.IsRoot || child.Parent != folder)
        {
            throw new YabtSyncException
            (
                $"Backup work unit '{folder}' produced '{child}' outside its immediate namespace."
            );
        }
    }

    public void Dispose()
    {
        _pendingFolders.Dispose();
        _desiredFolders.Dispose();
    }
}
