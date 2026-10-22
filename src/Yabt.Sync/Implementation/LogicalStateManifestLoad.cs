using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed record LogicalStateManifestLoad
(
    bool ManifestExists,
    bool InvalidationMarkerExists,
    ArchiveLogicalStateManifest? Manifest
) : IDisposable
{
    public void Dispose() => Manifest?.Dispose();
}
