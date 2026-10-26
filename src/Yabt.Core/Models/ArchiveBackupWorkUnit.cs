namespace Yabt.Core.Models;

/// <summary>
/// A fully enumerated immediate folder namespace and its pending child folders.
/// Objects and child paths are stable snapshots relative to the selected source root;
/// each must be an immediate child of RelativePath. Children have not been traversed.
/// The consumer owns this unit until its objects have finished processing.
/// </summary>
public sealed class ArchiveBackupWorkUnit
(
    string relativePath,
    IReadOnlyList<ArchiveProjectedObject> objects,
    IReadOnlyList<string> childFolderPaths,
    IAsyncDisposable? lifetime = default
) : IAsyncDisposable
{
    private IAsyncDisposable? _lifetime = lifetime;

    public string RelativePath { get; } = ObjectPath.Parse(relativePath).Value;

    public IReadOnlyList<ArchiveProjectedObject> Objects { get; } = objects;

    public IReadOnlyList<string> ChildFolderPaths { get; } = childFolderPaths;

    /// <summary>
    /// Transfers content ownership to an adapter that retains the projected objects.
    /// The recipient must dispose the returned lifetime after its consumers finish.
    /// </summary>
    public IAsyncDisposable? DetachLifetime() => Interlocked.Exchange(ref _lifetime, null);

    public async ValueTask DisposeAsync()
    {
        var lifetime = DetachLifetime();
        if (lifetime is not null) { await lifetime.DisposeAsync(); }
    }
}
