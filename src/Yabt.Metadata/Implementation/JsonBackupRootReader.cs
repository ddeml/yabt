using Microsoft.Extensions.Logging;
using Yabt.Core.Models;

namespace Yabt.Metadata.Implementation;

internal sealed class JsonBackupRootReader
(
    IBackupRootSerializer _serializer,
    ILogger<JsonBackupRootReader> _logger
) : IBackupRootReader
{
    public async Task<BackupRootDescriptor> ReadRootAsync
    (
        string rootPath,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ReadRootAsync));

        var descriptorPath = Path.Combine(rootPath, BackupRootFileNames.Primary);
        _logger.LogBackupRootDescriptorRead(descriptorPath);
        await using var stream = File.OpenRead(descriptorPath);
        return await _serializer.ReadAsync(stream, cancellationToken);
    }
}
