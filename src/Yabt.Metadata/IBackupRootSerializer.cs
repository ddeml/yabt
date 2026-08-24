using Yabt.Core.Models;

namespace Yabt.Metadata;

public interface IBackupRootSerializer
{
    Task WriteAsync
    (
        BackupRootDescriptor descriptor,
        Stream destination,
        CancellationToken cancellationToken = default
    );

    Task<BackupRootDescriptor> ReadAsync
    (
        Stream source,
        CancellationToken cancellationToken = default
    );

    Task<BackupRootDocument> ReadDocumentAsync
    (
        Stream source,
        CancellationToken cancellationToken = default
    );
}
