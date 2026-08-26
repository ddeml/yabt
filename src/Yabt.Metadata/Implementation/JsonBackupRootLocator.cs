using Microsoft.Extensions.Logging;

namespace Yabt.Metadata.Implementation;

internal sealed class JsonBackupRootLocator
(
    IBackupRootSerializer _serializer,
    ILogger<JsonBackupRootLocator> _logger
) : IBackupRootLocator
{
    public async Task<BackupRootLocation> LocateRootAsync
    (
        string startPath,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(LocateRootAsync));

        if (string.IsNullOrWhiteSpace(startPath))
        {
            throw new YabtMetadataException("Backup root lookup requires a start path.");
        }

        var currentPath = GetInitialDirectory(startPath);
        while (currentPath is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var descriptorPath = Path.Combine(currentPath, BackupRootFileNames.Primary);
            _logger.LogBackupRootDescriptorCheck(descriptorPath);
            if (File.Exists(descriptorPath))
            {
                _logger.LogBackupRootDescriptorRead(descriptorPath);
                await using var stream = File.OpenRead(descriptorPath);
                var document = await _serializer.ReadDocumentAsync(stream, cancellationToken);
                return new(currentPath, document);
            }

            currentPath = Directory.GetParent(currentPath)?.FullName;
        }

        throw new YabtMetadataException(
            $"Backup root JSON '{BackupRootFileNames.Primary}' could not be found in '{startPath}' or any parent folder.");
    }

    private string GetInitialDirectory(string startPath)
    {
        _logger.LogTrace(nameof(GetInitialDirectory));

        var fullPath = Path.GetFullPath(startPath);
        _logger.LogBackupRootStartPathCheck(fullPath);
        return File.Exists(fullPath) ?
            Path.GetDirectoryName(fullPath) ?? fullPath :
            fullPath;
    }
}
