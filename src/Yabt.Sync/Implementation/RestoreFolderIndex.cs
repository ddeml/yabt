using Yabt.Common;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

// The root manifest is validated first. This index retains only replayable
// metadata; no payload openers or complete in-memory restore plan are retained.
internal sealed class RestoreFolderIndex : IDisposable
{
    private readonly TemporaryIndex _index = TemporaryIndex.Create();
    private readonly DiskEntryCollection<RestoreFolderRecord> _records;
    private readonly DiskDictionary<RestoreFolderRange> _folders;

    internal RestoreFolderIndex(ArchiveChangeManifest manifest, CancellationToken cancellationToken)
    {
        _records = _index.CreateCollection<RestoreFolderRecord>();
        _folders = _index.CreateDictionary<RestoreFolderRange>();
        try
        {
            using var sorted = _index.CreateCollection<RestoreFolderRecord>();
            using var seen = _index.CreateSet();
            using var children = _index.CreateSet();
            sorted.Add(new(string.Empty), "\0");
            foreach (var entry in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ObjectPath.Parse(entry.RelativePath);
                if (path.IsRoot || !ArchiveHash.IsValid(entry.ContentHash) || !seen.Add(path.Value))
                {
                    throw new YabtSyncException(
                        $"Change manifest path '{entry.RelativePath}' lacks unique safe restore evidence.");
                }
                sorted.Add(new(path.Parent.Value, entry), path.Parent.Value + "\0" + path.Value);
                for (var parent = path.Parent; !parent.IsRoot; parent = parent.Parent)
                {
                    if (!children.Add(parent.Value)) { break; }
                    sorted.Add(new(parent.Parent.Value, ChildPath: parent.Value),
                        parent.Parent.Value + "\0" + parent.Value);
                }
            }
            string? currentFolder = null;
            long start = 0;
            foreach (var record in sorted.EnumerateOrdered(cancellationToken))
            {
                if (!string.Equals(record.FolderPath, currentFolder, StringComparison.Ordinal))
                {
                    if (currentFolder is not null)
                    {
                        _folders.Add(currentFolder, new(start, _records.LongCount - start));
                    }
                    currentFolder = record.FolderPath;
                    start = _records.LongCount;
                }
                _records.Add(record);
            }
            if (currentFolder is not null)
            {
                _folders.Add(currentFolder, new(start, _records.LongCount - start));
            }
        }
        catch (Exception)
        {
            Dispose();
            throw;
        }
    }

    internal IEnumerable<RestoreFolderRecord> GetFolder(string relativePath)
    {
        if (!_folders.TryGetValue(relativePath, out var range)) { yield break; }
        for (long offset = 0; offset < range.Count; offset++)
        {
            yield return _records.Get(range.Start + offset);
        }
    }

    internal bool ContainsFolder(string relativePath) => _folders.ContainsKey(relativePath);

    public void Dispose()
    {
        _folders.Dispose();
        _records.Dispose();
        _index.Dispose();
    }
}
