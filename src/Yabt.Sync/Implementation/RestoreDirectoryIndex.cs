using System.Collections;
using System.Globalization;
using Yabt.Common;

namespace Yabt.Sync.Implementation;

/// <summary>
/// Owns directory classification and ordered traversal without retaining a
/// directory-sized array or set in the restore plan.
/// </summary>
internal sealed class RestoreDirectoryIndex : IDisposable
{
    private readonly TemporaryIndex _index;
    private readonly DiskDictionary<bool> _membership;
    private readonly DiskEntryCollection<string> _directories;
    private readonly DiskEntryCollection<string> _emptyDirectories;

    private RestoreDirectoryIndex(StringComparer pathComparer)
    {
        _index = TemporaryIndex.Create();
        try
        {
            _membership = _index.CreateDictionary<bool>(comparer: pathComparer);
            _directories = _index.CreateCollection<string>();
            _emptyDirectories = _index.CreateCollection<string>();
        }
        catch (Exception)
        {
            _index.Dispose();
            throw;
        }
    }

    public IReadOnlyCollection<string> Directories => new OrderedPaths(_directories);

    public IReadOnlyCollection<string> EmptyDirectories => new OrderedPaths(_emptyDirectories);

    public bool IsDirectory(string relativePath) => _membership.ContainsKey(relativePath);

    public bool IsEmptyDirectory(string relativePath) =>
        _membership.TryGetValue(relativePath, out var isEmpty) && isEmpty;

    public static RestoreDirectoryIndex Create
    (
        IEnumerable<string> directoryPaths,
        IReadOnlySet<string> nonemptyDirectories,
        StringComparer pathComparer,
        CancellationToken cancellationToken = default
    )
    {
        var result = new RestoreDirectoryIndex(pathComparer);
        try
        {
            foreach (var relativePath in directoryPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var isEmpty = !nonemptyDirectories.Contains(relativePath);
                if (!result._membership.TryAdd(relativePath, isEmpty))
                {
                    throw new YabtSyncException(
                        $"Restore directories cannot be represented distinctly: '{relativePath}'.");
                }

                var depth = ArchivePathRules.GetPathDepth(relativePath)
                    .ToString("D10", CultureInfo.InvariantCulture);
                var sortKey = depth + "/" + relativePath;
                result._directories.Add(relativePath, sortKey);
                if (isEmpty) { result._emptyDirectories.Add(relativePath, sortKey); }
            }

            return result;
        }
        catch (Exception)
        {
            result.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            _membership.Dispose();
            _directories.Dispose();
            _emptyDirectories.Dispose();
        }
        finally
        {
            _index.Dispose();
        }
    }

    private sealed class OrderedPaths(DiskEntryCollection<string> _records)
        : IReadOnlyCollection<string>
    {
        public int Count => _records.Count;

        public IEnumerator<string> GetEnumerator() => _records.EnumerateOrdered().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
