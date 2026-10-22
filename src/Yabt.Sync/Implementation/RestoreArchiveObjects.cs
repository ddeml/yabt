using System.Collections;
using Yabt.Common;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

/// <summary>
/// Retains validated archive metadata on disk and recreates read delegates only
/// as the restore planner consumes each record.
/// </summary>
internal sealed class RestoreArchiveObjects(IReadOnlyObjectStore store, ArchiveLayout layout)
    : IReadOnlyList<ArchiveProjectedObject>, IDisposable
{
    private readonly TemporaryIndex _index = TemporaryIndex.Create();
    private DiskEntryCollection<ArchiveObjectInfo>? _records;

    private DiskEntryCollection<ArchiveObjectInfo> Records =>
        _records ??= _index.CreateCollection<ArchiveObjectInfo>();

    public int Count => Records.Count;
    public ArchiveProjectedObject this[int index] => Project(Records[index]);

    public void Add(ArchiveObjectInfo value) => Records.Add(value);

    public IEnumerator<ArchiveProjectedObject> GetEnumerator()
    {
        foreach (var record in Records) { yield return Project(record); }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private ArchiveProjectedObject Project(ArchiveObjectInfo record)
    {
        var key = layout.ToLiveObjectKey(record.Key);
        return new(
            record.Key,
            token => store.OpenReadAsync(key, token),
            record.ContentLength,
            record.LastModifiedUtc,
            record.ContentHash,
            record.ChangeFingerprint,
            record.Projection);
    }

    public void Dispose()
    {
        _records?.Dispose();
        _index.Dispose();
    }
}
