using System.Globalization;
using Yabt.Common;
using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

/// <summary>
/// Owns the ordered, top-level obsolete folder prefixes and their ancestor
/// lookup. Both records and ordering remain in operation-owned scratch storage.
/// </summary>
internal sealed class BackupCleanupFolderIndex : IDisposable
{
    private readonly TemporaryIndex _index;
    private readonly DiskSet _folders;
    private readonly DiskEntryCollection<string> _orderedFolders;

    private BackupCleanupFolderIndex(TemporaryIndex index)
    {
        _index = index;
        _folders = index.CreateSet(StringComparer.Ordinal);
        _orderedFolders = index.CreateCollection<string>();
    }

    internal static BackupCleanupFolderIndex Create
    (
        IEnumerable<string> folderPaths,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = TemporaryIndex.Create();
        BackupCleanupFolderIndex? result = null;
        try
        {
            result = new(index);
            using var candidates = index.CreateCollection<string>();
            using var candidatePaths = index.CreateSet(StringComparer.Ordinal);
            foreach (var inputPath in folderPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ArchiveLayout.NormalizeObjectKey(inputPath);
                if (path.Length == 0 || !candidatePaths.Add(path)) { continue; }

                var depth = ArchivePathRules.GetPathDepth(path).ToString("D10", CultureInfo.InvariantCulture);
                candidates.Add(path, $"{depth}/{path}");
            }

            var orderedCandidates = candidates.EnumerateOrdered(cancellationToken);
            foreach (var path in orderedCandidates)
            {
                if (result.ContainsAncestor(path, cancellationToken)) { continue; }

                result._folders.Add(path);
                result._orderedFolders.Add(path);
            }

            return result;
        }
        catch (Exception)
        {
            if (result is null) { index.Dispose(); }
            else { result.Dispose(); }
            throw;
        }
    }

    internal IEnumerable<string> Enumerate(CancellationToken cancellationToken) =>
        _orderedFolders.Enumerate(cancellationToken);

    internal bool ContainsAncestor(string relativePath, CancellationToken cancellationToken) =>
        ContainsAncestor(relativePath, _folders, cancellationToken);

    internal static bool ContainsAncestor
    (
        string relativePath,
        IReadOnlySet<string> folderPaths,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ArchiveLayout.NormalizeObjectKey(relativePath);
        var separator = path.IndexOf('/');
        while (separator >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (folderPaths.Contains(path[..separator])) { return true; }

            separator = path.IndexOf('/', separator + 1);
        }

        // An exact object and the same-named descendant prefix can coexist.
        // Only a strict ancestor folder includes this object in its move.
        return false;
    }

    public void Dispose()
    {
        try { _orderedFolders.Dispose(); }
        finally
        {
            try { _folders.Dispose(); }
            finally { _index.Dispose(); }
        }
    }
}
