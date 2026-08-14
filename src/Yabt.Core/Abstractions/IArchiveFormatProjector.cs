using Yabt.Core.Models;

namespace Yabt.Core.Abstractions;

public interface IArchiveFormatProjector
{
    string FormatName { get; }

    bool ProjectsBesideSourceFolder => false;

    IAsyncEnumerable<ArchiveProjectedObject> ProjectAsync
    (
        ArchiveProjectionRequest request,
        CancellationToken cancellationToken = default
    );
}
