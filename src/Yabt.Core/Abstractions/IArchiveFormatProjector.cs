using Yabt.Core.Models;

namespace Yabt.Core.Abstractions;

public interface IArchiveFormatProjector
{
    string FormatName { get; }

    Task<ArchiveProjection> ProjectAsync
    (
        ArchiveProjectionRequest request,
        CancellationToken cancellationToken = default
    );
}
