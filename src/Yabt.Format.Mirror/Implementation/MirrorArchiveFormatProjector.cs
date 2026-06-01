using Microsoft.Extensions.Logging;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Format.Mirror.Implementation;

internal sealed class MirrorArchiveFormatProjector
(
    ILogger<MirrorArchiveFormatProjector> _logger
) : IArchiveFormatProjector
{
    public string FormatName => MirrorArchiveFormatName.Value;

    public async Task<ArchiveProjection> ProjectAsync
    (
        ArchiveProjectionRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ProjectAsync));

        ArgumentNullException.ThrowIfNull(request);

        await request.SourceStore.EnsureReadyAsync(cancellationToken);

        var sourcePrefix = ArchiveLayout.NormalizeObjectPrefix(request.SourcePrefix);
        var projectedObjects = new List<ArchiveProjectedObject>();
        var sourceObjects = request.SourceStore.ListAsync(
            sourcePrefix,
            cancellationToken);

        await foreach (var sourceObject in sourceObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceKey = ArchiveLayout.NormalizeObjectKey(sourceObject.Key);
            var relativePath = ArchiveLayout.RemovePrefix(sourceKey, sourcePrefix);
            if (string.IsNullOrEmpty(relativePath))
            {
                continue;
            }

            projectedObjects.Add(CreateProjectedObject(
                request.SourceStore,
                sourceObject,
                sourceKey,
                relativePath));

            _logger.LogMirrorProjectedObject(sourceKey, relativePath);
        }

        _logger.LogMirrorProjectionCompleted(projectedObjects.Count);

        return new(projectedObjects);
    }

    private static ArchiveProjectedObject CreateProjectedObject
    (
        IObjectStore sourceStore,
        ArchiveObjectInfo sourceObject,
        string sourceKey,
        string relativePath
    )
    {
        return new
        (
            relativePath,
            cancellationToken => sourceStore.OpenReadAsync(
                sourceKey,
                cancellationToken),
            sourceObject.ContentLength,
            sourceObject.LastModifiedUtc,
            sourceObject.ContentHash
        );
    }
}
