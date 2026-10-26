using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed class RestoreFolderSource
(
    IObjectStore _archiveStore,
    ArchiveLayout _layout,
    RestoreFolderIndex _index
)
{
    internal async Task<(IReadOnlyList<ArchiveProjectedObject> Objects, string[] Children,
        string[] DesiredChildren)> ReadAsync
    (
        string folderPath,
        CancellationToken cancellationToken
    )
    {
        var entries = new Dictionary<string, ArchiveChangeManifestEntry>(StringComparer.Ordinal);
        var children = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in _index.GetFolder(folderPath))
        {
            if (record.Entry is not null) { entries.Add(record.Entry.RelativePath, record.Entry); }
            if (record.ChildPath is not null) { children.Add(record.ChildPath); }
        }
        var desiredChildren = children.Order(StringComparer.Ordinal).ToArray();
        var listed = new Dictionary<string, ArchiveObjectInfo>(StringComparer.Ordinal);
        var folderKey = _layout.ToLiveObjectKey(folderPath);
        var internalKeys = ArchivePathRules.CreateInternalObjectKeys(_layout);
        var internalPrefixes = ArchivePathRules.CreateInternalObjectPrefixes(_layout);
        var items = _archiveStore.GetFolderItemsAsync(folderKey, false, cancellationToken);
        await foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = ObjectPath.Parse(item.Key);
            if (key.IsRoot || key.Parent != ObjectPath.Parse(folderKey))
            {
                throw new YabtSyncException($"Archive folder '{folderPath}' listed non-child '{key}'.");
            }
            if (ArchivePathRules.IsInternalObjectPrefix(key.Value, internalPrefixes)) { continue; }
            var relativePath = ArchiveLayout.RemovePrefix(key.Value, _layout.LivePrefix);
            if (item.IsFolder)
            {
                children.Add(relativePath);
                continue;
            }
            if (ArchivePathRules.IsInternalObjectKey(key.Value, internalKeys)) { continue; }
            if (!entries.ContainsKey(relativePath))
            {
                throw new YabtSyncException(
                    $"Archive contains live object '{relativePath}' that is not covered by the change manifest.");
            }
            if (item.Object is not null && !listed.TryAdd(relativePath, item.Object))
            {
                throw new YabtSyncException($"Archive live object '{relativePath}' was listed more than once.");
            }
        }
        var objects = new List<ArchiveProjectedObject>(entries.Count);
        foreach (var (path, entry) in entries)
        {
            var key = _layout.ToLiveObjectKey(path);
            listed.TryGetValue(path, out var info);
            if (info is null && !await _archiveStore.ExistsAsync(key, cancellationToken))
            {
                throw new YabtSyncException($"Change manifest restore object '{path}' is missing from the archive.");
            }
            var lastModified = info?.LastModifiedUtc;
            if (ArchiveChangeFingerprint.TryParse(entry.ChangeFingerprint, out _, out var sourceTime))
            {
                lastModified = sourceTime;
            }
            objects.Add(new(path, token => _archiveStore.OpenReadAsync(key, token),
                info?.ContentLength ?? entry.ArtifactLength, lastModified, entry.ContentHash,
                entry.ChangeFingerprint, entry.Projection));
        }
        return (objects, children.Order(StringComparer.Ordinal).ToArray(), desiredChildren);
    }
}
