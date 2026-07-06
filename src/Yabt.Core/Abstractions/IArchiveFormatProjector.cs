using Yabt.Core.Models;

namespace Yabt.Core.Abstractions;

public interface IArchiveFormatProjector
{
    string FormatName { get; }

    IAsyncEnumerable<ArchiveProjectedObject> ProjectAsync
    (
        ArchiveProjectionRequest request,
        CancellationToken cancellationToken = default
    );
}
