using Yabt.Core.Models;

namespace Yabt.Metadata;

public interface IBackupRootReader
{
    Task<BackupRootDescriptor> ReadRootAsync
    (
        string rootPath,
        CancellationToken cancellationToken = default
    );
}
