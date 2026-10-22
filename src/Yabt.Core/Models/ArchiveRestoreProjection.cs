namespace Yabt.Core.Models;

/// <summary>
/// Owns a restore representation and its replayable object/directory sequences.
/// Sequences must remain stable and available until this projection is disposed;
/// they may read temporary indexes and are not eagerly copied into memory.
/// </summary>
public sealed class ArchiveRestoreProjection(
    IEnumerable<ArchiveProjectedObject>? objects = default,
    IEnumerable<string>? directories = default,
    IAsyncDisposable? lifetime = default
    ) : IAsyncDisposable
{
    private IAsyncDisposable? _lifetime = lifetime;

    public IReadOnlyCollection<ArchiveProjectedObject> Objects { get; } =
        new Implementation.ArchiveProjectionCollection<ArchiveProjectedObject>(objects ?? []);

    public IReadOnlyCollection<string> Directories { get; } =
        new Implementation.ArchiveProjectionCollection<string>(directories ?? []);

    public async ValueTask DisposeAsync()
    {
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        if (lifetime is not null)
        {
            await lifetime.DisposeAsync();
        }
    }
}
