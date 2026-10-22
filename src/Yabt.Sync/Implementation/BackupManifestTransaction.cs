using Microsoft.Extensions.Logging;
using Yabt.Core.Abstractions;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

// The caller owns the loaded and replacement manifests and the mutation lock.
// Failure leaves recovery evidence in place; disposal never publishes or rolls back.
internal sealed class BackupManifestTransaction
(
    ArchiveManifestStore _manifests,
    IArchiveMutableObjectStore _targetStore,
    ChangeManifestLoad _manifestLoad,
    string _manifestFileName,
    ILogger _logger
)
{
    private bool _invalidated;
    private bool _invalidationMarkerActive = _manifestLoad.InvalidationMarkerExists;

    public async Task InvalidateAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogTrace(nameof(InvalidateAsync));

        if (!_manifestLoad.Exists || _invalidated) { return; }

        if (!_invalidationMarkerActive)
        {
            await _manifests.UploadChangeManifestInvalidationMarkerAsync(
                _targetStore,
                cancellationToken);
            _invalidationMarkerActive = true;
        }

        await _manifests.DeleteChangeManifestsAsync(
            _targetStore,
            _manifestLoad.ExistingFileNames,
            cancellationToken);
        _invalidated = true;
    }

    public async Task PublishAsync
    (
        ArchiveChangeManifest nextManifest,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(PublishAsync));

        var needsWrite = _invalidated ||
            _manifestLoad.NeedsRepresentationRewrite(_manifestFileName) ||
            _manifestLoad.Manifest is null ||
            !string.Equals(
                _manifestLoad.Manifest.ManifestHash,
                nextManifest.ManifestHash,
                StringComparison.Ordinal);
        if (!needsWrite)
        {
            _logger.LogControlMetadataOperation(
                "Determined unchanged",
                _manifestFileName);
            return;
        }

        await InvalidateAsync(cancellationToken);
        await _manifests.UploadChangeManifestAsync(
            _targetStore,
            nextManifest,
            _manifestFileName,
            cancellationToken);
        if (_invalidationMarkerActive)
        {
            await _manifests.DeleteInternalObjectAsync(
                _targetStore,
                ArchiveChangeManifest.InvalidationMarkerFileName,
                "Change manifest invalidation marker",
                cancellationToken);
            _invalidationMarkerActive = false;
        }
    }
}
