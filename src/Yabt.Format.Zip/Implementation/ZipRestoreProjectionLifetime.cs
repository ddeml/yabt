using Yabt.Common;
using Yabt.Core.Models;

namespace Yabt.Format.Zip.Implementation;

internal sealed class ZipRestoreProjectionLifetime
(
    IAsyncDisposable _materialization,
    ArchiveManifest _manifest,
    TemporaryIndex _index,
    TemporaryContent? _adjacentManifest
) : IAsyncDisposable
{
    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }
        _disposed = true;
        try { await _materialization.DisposeAsync(); }
        finally
        {
            try { _manifest.Dispose(); }
            finally
            {
                try { _index.Dispose(); }
                finally
                {
                    if (_adjacentManifest is not null) { await _adjacentManifest.DisposeAsync(); }
                }
            }
        }
    }
}
