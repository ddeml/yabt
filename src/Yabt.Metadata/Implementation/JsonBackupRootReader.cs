using Yabt.Core.Models;

namespace Yabt.Metadata.Implementation;

internal sealed class JsonBackupRootReader(IBackupRootSerializer _serializer) : IBackupRootReader
{
    public async Task<BackupRootDescriptor> ReadRootAsync
    (
        string rootPath,
        CancellationToken cancellationToken = default
    )
    {
        var descriptorPath = Path.Combine(rootPath, BackupRootFileNames.Primary);
        await using var stream = File.OpenRead(descriptorPath);
        return await _serializer.ReadAsync(stream, cancellationToken);
    }
}
