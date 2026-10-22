using System.Buffers;
using System.IO.Hashing;
using Microsoft.Extensions.Logging;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed class ArchiveContentTransfer
(
    ILogger<ArchiveContentTransfer> _logger,
    IRestoreDestinationFactory _restoreDestinationFactory
)
{
    internal const int DefaultBufferSize = 81_920;

    internal async Task<ArchiveChangeManifestEntry> UploadProjectedObjectAsync
    (
        IObjectStore targetStore,
        ArchiveLayout targetLayout,
        ArchiveProjectedObject projectedObject,
        string relativePath,
        CancellationToken cancellationToken,
        ArchiveObjectContent? preparedContent = default
    )
    {
        _logger.LogTrace(nameof(UploadProjectedObjectAsync));

        var targetKey = targetLayout.ToLiveObjectKey(relativePath);
        ArchiveObjectContent? openedContent = null;
        try
        {
            try
            {
                var content = preparedContent;
                if (content is null)
                {
                    _logger.ReportObjectReadOperation(relativePath, "backup upload");
                    openedContent = await projectedObject.OpenContentAsync(cancellationToken);
                    content = openedContent;
                }

                using var hashingContent = new ContentHashingReadStream(content.Content);

                await targetStore.UploadAsync(
                    targetKey,
                    hashingContent,
                    content.ContentType,
                    content.Metadata ?? ArchiveManifestStore.EmptyMetadata,
                    cancellationToken);

                var endProbe = new byte[1];
                var unreadByteCount = await hashingContent.ReadAsync(
                    endProbe,
                    cancellationToken);
                if (unreadByteCount != 0)
                {
                    throw new InvalidDataException(
                        $"Target upload for '{targetKey}' completed before consuming all projected content.");
                }

                return CreateManifestEntry(
                    relativePath,
                    projectedObject,
                    hashingContent.BytesRead,
                    hashingContent.CompleteHash());
            }
            finally
            {
                if (openedContent is not null)
                {
                    await openedContent.DisposeAsync();
                }
            }
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Backup upload failed for projected object '{relativePath}' to target object '{targetKey}'.",
                ex);
        }
    }

    internal async Task<ArchiveObjectContent> PrepareProjectedObjectContentAsync
    (
        ArchiveProjectedObject projectedObject,
        string relativePath,
        string purpose,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(PrepareProjectedObjectContentAsync));

        ArchiveObjectContent? sourceContent = null;
        Stream? replayContent = null;
        try
        {
            _logger.ReportObjectReadOperation(relativePath, purpose);
            sourceContent = await projectedObject.OpenContentAsync(cancellationToken);
            replayContent = _restoreDestinationFactory.CreateComparisonReplayStream();
            await sourceContent.Content.CopyToAsync(
                replayContent,
                DefaultBufferSize,
                cancellationToken);
            await replayContent.FlushAsync(cancellationToken);
            if (projectedObject.ContentLength.HasValue &&
                projectedObject.ContentLength.Value != replayContent.Length)
            {
                throw new YabtSyncException(
                    $"Projected object '{relativePath}' reported length " +
                        $"{projectedObject.ContentLength.Value}, but its content contained " +
                        $"{replayContent.Length} bytes.");
            }

            replayContent.Position = 0;
            var preparedContent = new ArchiveObjectContent
            (
                replayContent,
                sourceContent.ContentType,
                sourceContent.Metadata
            );
            replayContent = null;
            return preparedContent;
        }
        finally
        {
            try
            {
                if (sourceContent is not null)
                {
                    await sourceContent.DisposeAsync();
                }
            }
            finally
            {
                if (replayContent is not null)
                {
                    await replayContent.DisposeAsync();
                }
            }
        }
    }

    internal static async Task<string> ComputeStoredObjectHashAsync
    (
        IObjectStore targetStore,
        string key,
        CancellationToken cancellationToken
    )
    {
        await using var content = await targetStore.OpenReadAsync(key, cancellationToken);
        using var hashingContent = new ContentHashingReadStream(content.Content);
        await hashingContent.CopyToAsync(Stream.Null, cancellationToken);
        return hashingContent.CompleteHash();
    }

    internal async Task<ProjectedObjectComparison> CompareProjectedObjectAsync
    (
        ArchiveProjectedObject projectedObject,
        IObjectStore targetStore,
        ArchiveObjectInfo targetObject,
        ArchiveChangeManifestEntry? previousManifestEntry,
        bool byteForByte,
        bool prepareChangedContent,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(CompareProjectedObjectAsync));

        var expectedArtifactLength = projectedObject.ContentLength ??
            (byteForByte ? null : previousManifestEntry?.ArtifactLength);
        if (expectedArtifactLength.HasValue &&
            targetObject.ContentLength.HasValue &&
            expectedArtifactLength.Value != targetObject.ContentLength.Value)
        {
            var preparedContent = prepareChangedContent ?
                await PrepareProjectedObjectContentAsync(
                    projectedObject,
                    projectedObject.RelativePath,
                    "backup changed-content preparation",
                    cancellationToken) :
                null;
            return new(false, null, preparedContent);
        }

        if (!byteForByte &&
            TryCreateFastManifestEntry(
                projectedObject,
                targetObject,
                previousManifestEntry,
                out var fastManifestEntry))
        {
            return new(true, fastManifestEntry, null);
        }

        ArchiveObjectContent? sourceContent = null;
        Stream? replayContent = null;
        try
        {
            try
            {
                _logger.ReportObjectReadOperation(
                    projectedObject.RelativePath,
                    "backup source comparison");
                sourceContent = await projectedObject.OpenContentAsync(cancellationToken);
                if (prepareChangedContent)
                {
                    replayContent = _restoreDestinationFactory.CreateComparisonReplayStream();
                }

                StreamComparison streamComparison;
                _logger.ReportObjectReadOperation(
                    projectedObject.RelativePath,
                    "backup target comparison");
                await using (var targetContent = await targetStore.OpenReadAsync(
                    targetObject.Key,
                    cancellationToken))
                {
                    streamComparison = await CompareStreamsAsync(
                        sourceContent.Content,
                        targetContent.Content,
                        DefaultBufferSize,
                        replayContent,
                        cancellationToken);
                }

                if (!streamComparison.Same)
                {
                    if (replayContent is null)
                    {
                        return new(false, null, null);
                    }

                    await sourceContent.Content.CopyToAsync(
                        replayContent,
                        DefaultBufferSize,
                        cancellationToken);
                    await replayContent.FlushAsync(cancellationToken);
                    if (projectedObject.ContentLength.HasValue &&
                        projectedObject.ContentLength.Value != replayContent.Length)
                    {
                        throw new YabtSyncException(
                            $"Projected object '{projectedObject.RelativePath}' reported length " +
                            $"{projectedObject.ContentLength.Value}, but its content contained " +
                            $"{replayContent.Length} bytes.");
                    }

                    replayContent.Position = 0;

                    var preparedContentType = sourceContent.ContentType;
                    var preparedContentMetadata = sourceContent.Metadata;
                    var completedSourceContent = sourceContent;
                    sourceContent = null;
                    await completedSourceContent.DisposeAsync();

                    var preparedContent = new ArchiveObjectContent
                    (
                        replayContent,
                        preparedContentType,
                        preparedContentMetadata
                    );
                    replayContent = null;

                    return new(false, null, preparedContent);
                }

                return new
                (
                    true,
                    CreateManifestEntry(
                        ArchiveLayout.NormalizeObjectKey(projectedObject.RelativePath),
                        projectedObject,
                        streamComparison.SourceLength,
                        streamComparison.SourceContentHash ??
                            throw new YabtSyncException(
                                "Successful byte comparison did not produce a content hash.")),
                    null
                );
            }
            finally
            {
                try
                {
                    if (sourceContent is not null)
                    {
                        await sourceContent.DisposeAsync();
                    }
                }
                finally
                {
                    if (replayContent is not null)
                    {
                        await replayContent.DisposeAsync();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Backup content comparison failed for target object '{targetObject.Key}'.",
                ex);
        }
    }

    private static bool TryCreateFastManifestEntry
    (
        ArchiveProjectedObject projectedObject,
        ArchiveObjectInfo targetObject,
        ArchiveChangeManifestEntry? previousManifestEntry,
        out ArchiveChangeManifestEntry? manifestEntry
    )
    {
        // A matching metadata fingerprint is a quick presumption, not byte-level verification.
        // --byte-for-byte bypasses this method, and incomplete target evidence falls back to streams.
        manifestEntry = null;
        if (previousManifestEntry is null ||
            string.IsNullOrWhiteSpace(projectedObject.ChangeFingerprint) ||
            string.IsNullOrWhiteSpace(previousManifestEntry.ContentHash) ||
            !ArchiveHash.IsValid(previousManifestEntry.ContentHash) ||
            previousManifestEntry.Projection != projectedObject.Projection ||
            !string.Equals(
                projectedObject.ChangeFingerprint,
                previousManifestEntry.ChangeFingerprint,
                StringComparison.Ordinal))
        {
            return false;
        }

        var expectedArtifactLength = projectedObject.ContentLength ??
            previousManifestEntry.ArtifactLength;
        if (!expectedArtifactLength.HasValue)
        {
            return false;
        }

        if (!targetObject.ContentLength.HasValue)
        {
            if (!ArchivePathRules.IsEmptyFolderMarker(projectedObject.RelativePath))
            {
                return false;
            }
        }
        else if (targetObject.ContentLength.Value != expectedArtifactLength.Value)
        {
            return false;
        }

        if (HaveSameHashAlgorithm(
                targetObject.ContentHash,
                previousManifestEntry.ContentHash) &&
            !string.Equals(
                targetObject.ContentHash,
                previousManifestEntry.ContentHash,
                StringComparison.Ordinal))
        {
            return false;
        }

        manifestEntry = CreateManifestEntry(
            previousManifestEntry.RelativePath,
            projectedObject,
            expectedArtifactLength.Value,
            previousManifestEntry.ContentHash);
        return true;
    }

    private static ArchiveChangeManifestEntry CreateManifestEntry
    (
        string relativePath,
        ArchiveProjectedObject projectedObject,
        long contentLength,
        string contentHash
    )
    {
        if (projectedObject.ContentLength.HasValue &&
            projectedObject.ContentLength.Value != contentLength)
        {
            throw new YabtSyncException(
                $"Projected object '{relativePath}' reported length " +
                $"{projectedObject.ContentLength.Value}, but its content contained {contentLength} bytes.");
        }

        var changeFingerprint = string.IsNullOrWhiteSpace(projectedObject.ChangeFingerprint) ?
            contentHash :
            projectedObject.ChangeFingerprint;
        return new
        (
            ArchiveLayout.NormalizeObjectKey(relativePath),
            changeFingerprint,
            ArtifactLength: projectedObject.ContentLength.HasValue ? null : contentLength,
            ContentHash: contentHash,
            Projection: projectedObject.Projection
        );
    }

    private static bool HaveSameHashAlgorithm
    (
        string? firstHash,
        string? secondHash
    )
    {
        if (string.IsNullOrWhiteSpace(firstHash) ||
            string.IsNullOrWhiteSpace(secondHash))
        {
            return false;
        }

        var firstSeparator = firstHash.IndexOf(':', StringComparison.Ordinal);
        var secondSeparator = secondHash.IndexOf(':', StringComparison.Ordinal);
        return firstSeparator > 0 &&
            secondSeparator == firstSeparator &&
            firstHash.AsSpan(0, firstSeparator).SequenceEqual(secondHash.AsSpan(0, secondSeparator));
    }

    private static async Task<StreamComparison> CompareStreamsAsync
    (
        Stream source,
        Stream target,
        int bufferSize,
        Stream? sourceReplay,
        CancellationToken cancellationToken
    )
    {
        var sourceBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var targetBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var sourceHash = new XxHash128();
        long sourceLength = 0;

        try
        {
            while (true)
            {
                var sourceBytesRead = await FillBufferAsync(
                    source,
                    sourceBuffer.AsMemory(0, bufferSize),
                    cancellationToken);
                var targetBytesRead = await FillBufferAsync(
                    target,
                    targetBuffer.AsMemory(0, bufferSize),
                    cancellationToken);

                sourceHash.Append(sourceBuffer.AsSpan(0, sourceBytesRead));
                sourceLength += sourceBytesRead;
                if (sourceReplay is not null && sourceBytesRead != 0)
                {
                    await sourceReplay.WriteAsync(
                        sourceBuffer.AsMemory(0, sourceBytesRead),
                        cancellationToken);
                }

                if (sourceBytesRead != targetBytesRead)
                {
                    return new(false, sourceLength, null);
                }

                if (sourceBytesRead == 0)
                {
                    var hash = sourceHash.GetHashAndReset();
                    return new
                    (
                        true,
                        sourceLength,
                        ArchiveHash.Format(hash)
                    );
                }

                var sourceSpan = sourceBuffer.AsSpan(0, sourceBytesRead);
                var targetSpan = targetBuffer.AsSpan(0, targetBytesRead);
                if (!sourceSpan.SequenceEqual(targetSpan))
                {
                    return new(false, sourceLength, null);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sourceBuffer);
            ArrayPool<byte>.Shared.Return(targetBuffer);
        }
    }

    private static async Task<int> FillBufferAsync
    (
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        var totalBytesRead = 0;
        while (totalBytesRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer[totalBytesRead..], cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytesRead += bytesRead;
        }

        return totalBytesRead;
    }
}
