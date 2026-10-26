namespace Yabt.Core.Models;

/// <summary>
/// Owns a restore representation and its replayable object/directory sequences.
/// Sequences must remain stable and available until this projection is disposed;
/// they may read temporary indexes and are not eagerly copied into memory.
/// </summary>
public sealed class ArchiveRestoreProjection
(
    IEnumerable<ArchiveProjectedObject>? objects = default,
    IEnumerable<string>? directories = default,
    IAsyncDisposable? lifetime = default,
    Func<CancellationToken, Task>? validate = default
) : IAsyncDisposable
{
    private IAsyncDisposable? _lifetime = lifetime;
    private bool _disposed;

    public IReadOnlyCollection<ArchiveProjectedObject> Objects { get; } =
        new Implementation.ArchiveProjectionCollection<ArchiveProjectedObject>(objects ?? []);

    public IReadOnlyCollection<string> Directories { get; } =
        new Implementation.ArchiveProjectionCollection<string>(directories ?? []);

    /// <summary>Validates projection evidence before a directory-only mutation.</summary>
    public Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return validate?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        if (lifetime is not null)
        {
            await lifetime.DisposeAsync();
        }
    }
}
