using Yabt.Core.Models;

namespace Yabt.Core.Abstractions;

public interface IChangeDetector
{
    IAsyncEnumerable<FileSystemChange> FindChangesAsync
    (
        ChangeDetectionRequest request,
        CancellationToken cancellationToken = default
    );
}
