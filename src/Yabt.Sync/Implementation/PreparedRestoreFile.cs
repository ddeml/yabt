using Microsoft.Extensions.Logging;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

internal sealed class PreparedRestoreFile
(
    ArchiveProjectedObject _file,
    string? stagingToken,
    IObjectStoreStagingArea? _stagingArea,
    ILogger _logger
) : IAsyncDisposable
{
    private string? _stagingToken = stagingToken;

    public ArchiveProjectedObject File => _file;

    public string? StagingToken => _stagingToken;

    public void MarkCommitted()
    {
        _logger.LogTrace(nameof(MarkCommitted));

        _stagingToken = null;
    }

    public ValueTask DisposeAsync()
    {
        _logger.LogTrace(nameof(DisposeAsync));

        if (_stagingToken is not null)
        {
            var stagingToken = _stagingToken;
            TryDeleteRestoreTemporaryPath(
                _logger,
                stagingToken,
                () => _stagingArea!.DeleteFile(stagingToken));
            _stagingToken = null;
        }

        return ValueTask.CompletedTask;
    }

    internal static void TryDeleteRestoreTemporaryPath
    (
        ILogger logger,
        string path,
        Action delete
    )
    {
        logger.LogTrace(nameof(TryDeleteRestoreTemporaryPath));
        logger.LogControlMetadataOperation("Deleting temporary restore path", path);

        try
        {
            delete();
        }
        catch (Exception ex)
        {
            logger.LogIgnoringRestoreTemporaryPathDeleteException(ex, path);
        }
    }

}
