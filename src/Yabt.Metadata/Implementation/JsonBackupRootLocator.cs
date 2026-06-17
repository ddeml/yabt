namespace Yabt.Metadata.Implementation;

internal sealed class JsonBackupRootLocator(IBackupRootSerializer _serializer) : IBackupRootLocator
{
    public async Task<BackupRootLocation> LocateRootAsync
    (
        string startPath,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(startPath))
        {
            throw new YabtMetadataException("Backup root lookup requires a start path.");
        }

        var currentPath = GetInitialDirectory(startPath);
        while (currentPath is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var descriptorPath = Path.Combine(currentPath, BackupRootFileNames.Primary);
            if (File.Exists(descriptorPath))
            {
                await using var stream = File.OpenRead(descriptorPath);
                var descriptor = await _serializer.ReadAsync(stream, cancellationToken);
                return new(currentPath, descriptor);
            }

            currentPath = Directory.GetParent(currentPath)?.FullName;
        }

        throw new YabtMetadataException(
            $"Backup root JSON '{BackupRootFileNames.Primary}' could not be found in '{startPath}' or any parent folder.");
    }

    private static string GetInitialDirectory(string startPath)
    {
        var fullPath = Path.GetFullPath(startPath);
        return File.Exists(fullPath) ?
            Path.GetDirectoryName(fullPath) ?? fullPath :
            fullPath;
    }
}
