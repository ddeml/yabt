namespace Yabt.Core.Models;

public sealed class ArchiveRestoreProjection(
    IEnumerable<ArchiveProjectedObject>? objects = default,
    IEnumerable<string>? directories = default,
    IAsyncDisposable? lifetime = default
    ) : IAsyncDisposable
{
    private IAsyncDisposable? _lifetime = lifetime;

    public IReadOnlyList<ArchiveProjectedObject> Objects { get; } = objects?.ToArray() ?? [];

    public IReadOnlyList<string> Directories { get; } = directories?.ToArray() ?? [];

    public async ValueTask DisposeAsync()
    {
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        if (lifetime is not null)
        {
            await lifetime.DisposeAsync();
        }
    }
}
