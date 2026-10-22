using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

internal sealed class RestorePlan
(
    IReadOnlyList<ArchiveProjectedObject> _files,
    IReadOnlyList<string> _directories,
    IReadOnlyList<string> _emptyDirectories,
    IReadOnlyList<ArchiveRestoreProjection> _projections,
    IReadOnlyDictionary<string, IReadOnlyList<ArchiveRestoreProjection>>
        _fileProjectionDependencies,
    IObjectStoreNamespace _namespace
) : IAsyncDisposable
{
    public IObjectStoreNamespace Namespace => _namespace;

    public IReadOnlyList<ArchiveProjectedObject> Files => _files;

    public IReadOnlyList<string> Directories => _directories;

    public IReadOnlyList<string> EmptyDirectories => _emptyDirectories;

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
        foreach (var projection in _projections)
        {
            await projection.DisposeAsync();
        }
    }
}
