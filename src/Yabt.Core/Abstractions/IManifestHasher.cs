using Yabt.Core.Models;

namespace Yabt.Core.Abstractions;

public interface IManifestHasher
{
    ValueTask<string> ComputeManifestHashAsync
    (
        ArchiveManifest manifest,
        CancellationToken cancellationToken = default
    );
}
