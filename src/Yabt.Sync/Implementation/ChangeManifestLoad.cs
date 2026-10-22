using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed record ChangeManifestLoad
(
    IReadOnlyList<string> ExistingFileNames,
    bool InvalidationMarkerExists,
    ArchiveChangeManifest? Manifest
) : IDisposable
{
    public void Dispose() => Manifest?.Dispose();

    public bool Exists => ExistingFileNames.Count != 0 || InvalidationMarkerExists;

    public bool RequiresInvalidationMarker =>
        InvalidationMarkerExists ||
        Manifest is null && ExistingFileNames.Count > 1;

    public bool NeedsRepresentationRewrite(string expectedFileName) =>
        InvalidationMarkerExists ||
        ExistingFileNames.Count != 1 ||
        !string.Equals(
            ExistingFileNames[0],
            expectedFileName,
            StringComparison.Ordinal);
}
