using Microsoft.Extensions.Logging;
using Yabt.Core.Abstractions;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

// The executor brackets payload mutations and history completion with these steps.
// It owns the manifests and lock; failure must not trigger marker cleanup.
internal sealed class RestoreManifestTransaction
(
    ArchiveManifestStore _manifests,
    IArchiveMutableObjectStore _destinationStore,
    LogicalStateManifestLoad _manifestLoad,
    ILogger _logger
)
{
    private bool _started;
    private bool _changeManifestInvalidationMarkerActive;
    private bool _logicalStateInvalidationMarkerActive = _manifestLoad.InvalidationMarkerExists;

    public async Task BeginAsync
    (
        bool liveStateChanged,
        bool rootDescriptorNeedsWrite,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(BeginAsync));

        if (_started) { return; }

        _changeManifestInvalidationMarkerActive =
            await _manifests.PrepareRestoreChangeManifestMutationAsync(
                _destinationStore,
                liveStateChanged || rootDescriptorNeedsWrite,
                cancellationToken);
        if (liveStateChanged &&
            _manifestLoad.ManifestExists &&
            !_logicalStateInvalidationMarkerActive)
        {
            await _manifests.UploadLogicalStateManifestInvalidationMarkerAsync(
                _destinationStore,
                cancellationToken);
            _logicalStateInvalidationMarkerActive = true;
        }

        _started = true;
    }

    public async Task PublishAsync
    (
        ArchiveLogicalStateManifest nextManifest,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(PublishAsync));

        var needsWrite = _logicalStateInvalidationMarkerActive ||
            _manifestLoad.Manifest is null ||
            !string.Equals(
                _manifestLoad.Manifest.ManifestHash,
                nextManifest.ManifestHash,
                StringComparison.Ordinal);
        if (needsWrite)
        {
            if (_manifestLoad.ManifestExists &&
                !_logicalStateInvalidationMarkerActive)
            {
                await _manifests.UploadLogicalStateManifestInvalidationMarkerAsync(
                    _destinationStore,
                    cancellationToken);
                _logicalStateInvalidationMarkerActive = true;
            }

            if (_manifestLoad.ManifestExists)
            {
                await _manifests.DeleteInternalObjectAsync(
                    _destinationStore,
                    ArchiveLogicalStateManifest.FileName,
                    "Obsolete logical state manifest",
                    cancellationToken);
            }

            await _manifests.UploadLogicalStateManifestAsync(
                _destinationStore,
                nextManifest,
                cancellationToken);
        }

        if (_logicalStateInvalidationMarkerActive)
        {
            await _manifests.DeleteInternalObjectAsync(
                _destinationStore,
                ArchiveLogicalStateManifest.InvalidationMarkerFileName,
                "Logical state manifest invalidation marker",
                cancellationToken);
        }

        if (_changeManifestInvalidationMarkerActive)
        {
            await _manifests.DeleteInternalObjectAsync(
                _destinationStore,
                ArchiveChangeManifest.InvalidationMarkerFileName,
                "Change manifest invalidation marker",
                cancellationToken);
        }
    }
}
