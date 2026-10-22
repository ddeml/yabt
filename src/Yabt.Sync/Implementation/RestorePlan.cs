using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

internal sealed class RestorePlan
(
    IReadOnlyList<ArchiveProjectedObject> _files,
    RestoreDirectoryIndex _directories,
    IReadOnlyList<ArchiveRestoreProjection> _projections,
    IReadOnlyDictionary<string, IReadOnlyList<ArchiveRestoreProjection>>
        _fileProjectionDependencies,
    IObjectStoreNamespace _namespace
) : IAsyncDisposable
{
    public IObjectStoreNamespace Namespace => _namespace;

    public IReadOnlyList<ArchiveProjectedObject> Files => _files;

    public IReadOnlyCollection<string> Directories => _directories.Directories;

    public IReadOnlyCollection<string> EmptyDirectories => _directories.EmptyDirectories;

    public bool IsDirectory(string relativePath) => _directories.IsDirectory(relativePath);

    public bool IsEmptyDirectory(string relativePath) => _directories.IsEmptyDirectory(relativePath);

    public IReadOnlyList<ArchiveRestoreProjection> Projections => _projections;

    public IReadOnlyList<ArchiveRestoreProjection> GetProjectionDependencies
    (
        string relativePath
    ) => _fileProjectionDependencies.TryGetValue(relativePath, out var dependencies) ?
        dependencies :
        throw new YabtSyncException(
            $"Restore path '{relativePath}' has no projection lifetime evidence.");

    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var projection in _projections)
            {
                await projection.DisposeAsync();
            }
        }
        finally
        {
            _directories.Dispose();
        }
    }
}
