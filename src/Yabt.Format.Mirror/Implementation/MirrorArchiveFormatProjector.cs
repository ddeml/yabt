using System.Runtime.CompilerServices;
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

    public bool ProjectsBesideSourceFolder => false;

    public async IAsyncEnumerable<ArchiveProjectedObject> ProjectAsync
    (
        ArchiveProjectionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ProjectAsync));

        ArgumentNullException.ThrowIfNull(request);

        await request.SourceStore.EnsureReadyAsync(cancellationToken);

        var sourcePrefix = ArchiveLayout.NormalizeObjectPrefix(request.SourcePrefix);
        var projectedObjectCount = 0;
        var projectedObjects = ProjectFolderAsync(
            request.SourceStore,
            sourcePrefix,
            sourcePrefix,
            cancellationToken);

        await foreach (var projectedObject in projectedObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return projectedObject;
            projectedObjectCount++;
        }

        _logger.LogMirrorProjectionCompleted(projectedObjectCount);
    }

    private async IAsyncEnumerable<ArchiveProjectedObject> ProjectFolderAsync
    (
        IReadOnlyObjectStore sourceStore,
        string? sourcePrefix,
        string? folderPrefix,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var hasItems = false;
        var sourceItems = sourceStore.GetFolderItemsAsync(
            folderPrefix,
            recursive: false,
            cancellationToken);

        await foreach (var sourceItem in sourceItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            hasItems = true;
            if (sourceItem.IsFolder)
            {
                var childProjectedObjects = ProjectFolderAsync(
                    sourceStore,
                    sourcePrefix,
                    sourceItem.Key,
                    cancellationToken);

                await foreach (var childProjectedObject in childProjectedObjects)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    yield return childProjectedObject;
                }

                continue;
            }

            if (sourceItem.Object is null)
            {
                continue;
            }

            var sourceKey = ArchiveLayout.NormalizeObjectKey(sourceItem.Object.Key);
            var relativePath = ArchiveLayout.RemovePrefix(sourceKey, sourcePrefix);
            if (string.IsNullOrEmpty(relativePath))
            {
                continue;
            }

            yield return CreateProjectedObject(
                sourceStore,
                sourceItem.Object,
                sourceKey,
                relativePath);

            _logger.LogMirrorProjectedObject(sourceKey, relativePath);
        }

        if (!hasItems)
        {
            yield return CreateEmptyFolderMarker(sourcePrefix, folderPrefix);
        }
    }

    private static ArchiveProjectedObject CreateProjectedObject
    (
        IReadOnlyObjectStore sourceStore,
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

    private static ArchiveProjectedObject CreateEmptyFolderMarker
    (
        string? sourcePrefix,
        string? folderPrefix
    )
    {
        var folderRelativePath = ArchiveLayout.RemovePrefix(
            ArchiveLayout.NormalizeObjectKey(folderPrefix),
            sourcePrefix);
        var markerRelativePath = ArchiveLayout.CombinePrefixAndRelativePath(
            folderRelativePath,
            ArchiveFolderMarkerFileNames.EmptyFolder);

        return new
        (
            markerRelativePath,
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(new ArchiveObjectContent(
                    new MemoryStream([], writable: false)));
            },
            0
        );
    }
}
