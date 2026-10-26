using Microsoft.Extensions.DependencyInjection;
using Yabt.Core.Abstractions;

namespace Yabt.Tests.Implementation;

internal sealed class DeployedServiceStore
(
    ServiceProvider _services,
    Func<CancellationToken, Task> _cleanup,
    IDisposable? _resource = null
) : IAsyncDisposable
{
    public IArchiveMutableObjectStore Store { get; } =
        _services.GetRequiredService<IArchiveMutableObjectStore>();

    public Task CleanupAsync(CancellationToken cancellationToken) => _cleanup(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _services.DisposeAsync();
        }
        finally
        {
            _resource?.Dispose();
        }
    }
}
