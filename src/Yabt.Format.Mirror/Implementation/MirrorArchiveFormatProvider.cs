using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Format.Mirror.Implementation;

internal sealed class MirrorArchiveFormatProvider
(
    ILogger<MirrorArchiveFormatProvider> _logger,
    IOptionsMonitor<MirrorArchiveFormatProviderOptions> _options,
    TimeProvider _timeProvider
) : IArchiveFormatProvider
{
    private const int DefaultBufferSize = 81_920;

    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new Dictionary<string, string>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal);

    public string FormatName => MirrorArchiveFormatName.Value;

    public async Task<ArchiveFormatOperationResult> BackupAsync
    (
        ArchiveFormatBackupRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(BackupAsync));

        ArgumentNullException.ThrowIfNull(request);

        return await MirrorAsync(
            request.SourceStore,
            request.TargetStore,
            "backup",
            cancellationToken);
    }

    public async Task<ArchiveFormatOperationResult> RestoreAsync
    (
        ArchiveFormatRestoreRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(RestoreAsync));

        ArgumentNullException.ThrowIfNull(request);

        return await MirrorAsync(
            request.SourceStore,
            request.TargetStore,
            "restore",
            cancellationToken);
    }

    public async Task<ArchiveFormatOperationResult> VerifyAsync
    (
        ArchiveFormatVerifyRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(VerifyAsync));

        ArgumentNullException.ThrowIfNull(request);

        return await VerifyMirrorAsync(
            request.SourceStore,
            request.TargetStore,
            cancellationToken);
    }

    private int GetEffectiveBufferSize()
    {
        var bufferSize = _options.CurrentValue.BufferSize ?? DefaultBufferSize;
        if (bufferSize <= 0)
        {
            throw new YabtFormatMirrorException("Mirror archive format buffer size must be greater than zero.");
        }
        return bufferSize;
    }

    private async Task<ArchiveFormatOperationResult> MirrorAsync
    (
        IObjectStore sourceStore,
        IObjectStore targetStore,
        string operationName,
        CancellationToken cancellationToken
    )
    {
        var bufferSize = GetEffectiveBufferSize();
        var historicalTimestamp = _timeProvider.GetUtcNow();
        var historyKeyAllocator = new MirrorArchiveHistoryKeyAllocator(targetStore, historicalTimestamp);
        var summary = await WalkLiveObjectPairsAsync(
            sourceStore,
            targetStore,
            operationName,
            (relativePath, sourceObject, targetObject, currentCancellationToken) =>
                HasSameContentAsync(
                        sourceStore,
                        targetStore,
                        sourceObject,
                        targetObject,
                        bufferSize,
                        currentCancellationToken),
            (_, _, _, _) => Task.CompletedTask,
            (relativePath, _, currentCancellationToken) =>
                CopyLiveObjectAsync(
                    sourceStore,
                    targetStore,
                    relativePath,
                    currentCancellationToken),
            async (relativePath, _, _, currentCancellationToken) =>
            {
                await MoveLiveObjectToHistoryAsync(
                    targetStore,
                    historyKeyAllocator,
                    relativePath,
                    currentCancellationToken);

                await CopyLiveObjectAsync(
                    sourceStore,
                    targetStore,
                    relativePath,
                    currentCancellationToken);
            },
            (relativePath, _, currentCancellationToken) =>
                MoveLiveObjectToHistoryAsync(
                    targetStore,
                    historyKeyAllocator,
                    relativePath,
                    currentCancellationToken),
            cancellationToken);

        return new(
            Completed: true,
            Message:
                $"Mirror {operationName} completed; {summary.NewCount} new object(s), " +
                $"{summary.ChangedCount} changed object(s), {summary.ExtraCount} extra object(s), " +
                $"and {summary.UnchangedCount} unchanged object(s).");
    }

    private async Task<ArchiveFormatOperationResult> VerifyMirrorAsync
    (
        IObjectStore sourceStore,
        IObjectStore targetStore,
        CancellationToken cancellationToken
    )
    {
        var bufferSize = GetEffectiveBufferSize();

        var summary = await WalkLiveObjectPairsAsync(
            sourceStore,
            targetStore,
            "verify",
            (relativePath, sourceObject, targetObject, currentCancellationToken) =>
                HasSameContentAsync(
                        sourceStore,
                        targetStore,
                        sourceObject,
                        targetObject,
                        bufferSize,
                        currentCancellationToken),
            (_, _, _, _) => Task.CompletedTask,
            (_, _, _) => Task.CompletedTask,
            (_, _, _, _) => Task.CompletedTask,
            (_, _, _) => Task.CompletedTask,
            cancellationToken);

        var completed = summary.NewCount == 0 &&
            summary.ChangedCount == 0 &&
            summary.ExtraCount == 0;

        var message = completed ?
            $"Mirror verification completed; verified {summary.UnchangedCount} unchanged object(s)." :
            $"Mirror verification found {summary.NewCount} new source object(s), " +
            $"{summary.ExtraCount} extra target object(s), and {summary.ChangedCount} changed object(s).";

        return new(completed, message);
    }

    private async Task<MirrorArchiveObjectPairSummary> WalkLiveObjectPairsAsync
    (
        IObjectStore sourceStore,
        IObjectStore targetStore,
        string operationName,
        Func<string, ArchiveObjectInfo, ArchiveObjectInfo, CancellationToken, Task<bool>> compareExistingTargetAsync,
        Func<string, ArchiveObjectInfo, ArchiveObjectInfo, CancellationToken, Task> handleUnchangedAsync,
        Func<string, ArchiveObjectInfo, CancellationToken, Task> handleNewAsync,
        Func<string, ArchiveObjectInfo, ArchiveObjectInfo, CancellationToken, Task> handleChangedAsync,
        Func<string, ArchiveObjectInfo, CancellationToken, Task> handleExtraAsync,
        CancellationToken cancellationToken
    )
    {
        await sourceStore.EnsureReadyAsync(cancellationToken);
        await targetStore.EnsureReadyAsync(cancellationToken);

        var sourceObjects = await ListLiveObjectsAsync(sourceStore, cancellationToken);
        var targetObjects = await ListLiveObjectsAsync(targetStore, cancellationToken);
        var summary = new MirrorArchiveObjectPairSummary();

        foreach (var (relativePath, sourceObject) in sourceObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (targetObjects.TryGetValue(relativePath, out var targetObject))
            {
                targetObjects.Remove(relativePath);
                if (await compareExistingTargetAsync(
                        relativePath,
                        sourceObject,
                        targetObject,
                        cancellationToken))
                {
                    await handleUnchangedAsync(
                        relativePath,
                        sourceObject,
                        targetObject,
                        cancellationToken);
                    _logger.LogMirrorObjectPairState(operationName, relativePath, "unchanged");
                    summary.AddUnchanged();
                    continue;
                }

                await handleChangedAsync(
                    relativePath,
                    sourceObject,
                    targetObject,
                    cancellationToken);
                _logger.LogMirrorObjectPairState(operationName, relativePath, "changed");
                summary.AddChanged();
                continue;
            }

            await handleNewAsync(
                relativePath,
                sourceObject,
                cancellationToken);
            _logger.LogMirrorObjectPairState(operationName, relativePath, "new");
            summary.AddNew();
        }

        foreach (var (relativePath, targetObject) in targetObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await handleExtraAsync(
                relativePath,
                targetObject,
                cancellationToken);
            _logger.LogMirrorObjectPairState(operationName, relativePath, "extra");
            summary.AddExtra();
        }

        _logger.LogMirrorObjectPairWalkCompleted(
            operationName,
            summary.NewCount,
            summary.ChangedCount,
            summary.ExtraCount,
            summary.UnchangedCount);

        return summary;
    }

    private static async Task<SortedDictionary<string, ArchiveObjectInfo>> ListLiveObjectsAsync
    (
        IObjectStore store,
        CancellationToken cancellationToken
    )
    {
        var objects = new SortedDictionary<string, ArchiveObjectInfo>(StringComparer.Ordinal);

        await foreach (var archiveObject in store.ListAsync(
                           ArchiveArea.Live,
                           prefix: null,
                           cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = NormalizeRelativePath(archiveObject.Key.RelativePath);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            objects[relativePath] = archiveObject with
            {
                Key = new(ArchiveArea.Live, relativePath),
            };
        }

        return objects;
    }

    private static async Task CopyLiveObjectAsync
    (
        IObjectStore sourceStore,
        IObjectStore targetStore,
        string relativePath,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var key = new ArchiveObjectKey(ArchiveArea.Live, relativePath);
            await using var sourceObject = await sourceStore.OpenReadAsync(key, cancellationToken);

            await targetStore.UploadAsync(
                key,
                sourceObject.Content,
                sourceObject.ContentType,
                sourceObject.Metadata ?? EmptyMetadata,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtFormatMirrorException(
                $"Mirror copy failed for live object '{relativePath}'.",
                ex);
        }
    }

    private static async Task MoveLiveObjectToHistoryAsync
    (
        IObjectStore targetStore,
        MirrorArchiveHistoryKeyAllocator historyKeyAllocator,
        string relativePath,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var sourceKey = new ArchiveObjectKey(ArchiveArea.Live, relativePath);
            var destinationKey = await historyKeyAllocator.CreateHistoricalKeyAsync(
                relativePath,
                cancellationToken);

            await targetStore.MoveAsync(sourceKey, destinationKey, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtFormatMirrorException(
                $"Mirror history move failed for live object '{relativePath}'.",
                ex);
        }
    }

    private static string BuildHistoricalRelativePath
    (
        string relativePath,
        string historicalTimestampSegment,
        int sequence
    )
    {
        if (sequence > 0)
        {
            historicalTimestampSegment += $"-{sequence.ToString(CultureInfo.InvariantCulture)}";
        }

        return $"{historicalTimestampSegment}/{relativePath}";
    }

    private static string ToHistoricalTimestampSegment(DateTimeOffset historicalTimestamp)
    {
        var historicalTimestampUtc = historicalTimestamp.ToUniversalTime();
        return historicalTimestampUtc.ToString(
            "yyyyMMdd'T'HHmmssFFFFFFF'Z'",
            CultureInfo.InvariantCulture);
    }

    private static async Task<bool> HasSameContentAsync
    (
        IObjectStore sourceStore,
        IObjectStore targetStore,
        ArchiveObjectInfo sourceObject,
        ArchiveObjectInfo targetObject,
        int bufferSize,
        CancellationToken cancellationToken
    )
    {
        var sourceLength = sourceObject.ContentLength;
        var targetLength = targetObject.ContentLength;
        if (sourceLength.HasValue &&
            targetLength.HasValue &&
            sourceLength.Value != targetLength.Value)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(sourceObject.ContentHash) &&
            !string.IsNullOrWhiteSpace(targetObject.ContentHash))
        {
            return string.Equals(
                sourceObject.ContentHash,
                targetObject.ContentHash,
                StringComparison.Ordinal);
        }

        try
        {
            await using var sourceContent = await sourceStore.OpenReadAsync(
                sourceObject.Key,
                cancellationToken);
            await using var targetContent = await targetStore.OpenReadAsync(
                targetObject.Key,
                cancellationToken);

            return await StreamsHaveSameContentAsync(
                sourceContent.Content,
                targetContent.Content,
                bufferSize,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtFormatMirrorException(
                $"Mirror content comparison failed for live object '{sourceObject.Key.RelativePath}'.",
                ex);
        }
    }

    private static async Task<bool> StreamsHaveSameContentAsync
    (
        Stream source,
        Stream target,
        int bufferSize,
        CancellationToken cancellationToken
    )
    {
        var sourceBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var targetBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            while (true)
            {
                var sourceBytesRead = await source.ReadAsync(
                    sourceBuffer.AsMemory(0, bufferSize),
                    cancellationToken);
                var targetBytesRead = await target.ReadAsync(
                    targetBuffer.AsMemory(0, bufferSize),
                    cancellationToken);

                if (sourceBytesRead != targetBytesRead)
                {
                    return false;
                }

                if (sourceBytesRead == 0)
                {
                    return true;
                }

                var sourceSpan = sourceBuffer.AsSpan(0, sourceBytesRead);
                var targetSpan = targetBuffer.AsSpan(0, targetBytesRead);
                if (!sourceSpan.SequenceEqual(targetSpan))
                {
                    return false;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sourceBuffer);
            ArrayPool<byte>.Shared.Return(targetBuffer);
        }
    }

    private static string NormalizeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace('\\', '/').Trim('/');
        var segments = normalized.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                throw new YabtFormatMirrorException("Mirror object path contains an invalid segment.");
            }
        }

        return string.Join('/', segments);
    }

    private sealed class MirrorArchiveObjectPairSummary
    {
        public int NewCount { get; private set; }

        public int ChangedCount { get; private set; }

        public int ExtraCount { get; private set; }

        public int UnchangedCount { get; private set; }

        public void AddNew()
        {
            NewCount++;
        }

        public void AddChanged()
        {
            ChangedCount++;
        }

        public void AddExtra()
        {
            ExtraCount++;
        }

        public void AddUnchanged()
        {
            UnchangedCount++;
        }
    }

    private sealed class MirrorArchiveHistoryKeyAllocator
    (
        IObjectStore _targetStore,
        DateTimeOffset _historicalTimestamp
    )
    {
        private readonly string _historicalTimestampSegment = ToHistoricalTimestampSegment(_historicalTimestamp);
        private Dictionary<string, int>? _highestHistoricalSequencesByRelativePath;

        public async Task<ArchiveObjectKey> CreateHistoricalKeyAsync
        (
            string relativePath,
            CancellationToken cancellationToken
        )
        {
            var highestHistoricalSequences = await GetHighestHistoricalSequencesAsync(cancellationToken);
            var sequence = highestHistoricalSequences.TryGetValue(relativePath, out var highestSequence) ?
                highestSequence + 1 :
                0;
            var historicalPath = BuildHistoricalRelativePath(
                relativePath,
                _historicalTimestampSegment,
                sequence);

            highestHistoricalSequences[relativePath] = sequence;

            return new(ArchiveArea.Hist, historicalPath);
        }

        private async Task<Dictionary<string, int>> GetHighestHistoricalSequencesAsync
        (
            CancellationToken cancellationToken
        )
        {
            if (_highestHistoricalSequencesByRelativePath is not null)
            {
                return _highestHistoricalSequencesByRelativePath;
            }

            var highestHistoricalSequences = new Dictionary<string, int>(StringComparer.Ordinal);
            await foreach (var archiveObject in _targetStore.ListAsync(
                               ArchiveArea.Hist,
                               prefix: null,
                               cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = NormalizeRelativePath(archiveObject.Key.RelativePath);
                if (string.IsNullOrWhiteSpace(relativePath) ||
                    !TryParseHistoricalPath(relativePath, out var historicalRelativePath, out var sequence))
                {
                    continue;
                }

                if (!highestHistoricalSequences.TryGetValue(
                        historicalRelativePath,
                        out var highestSequence) ||
                    sequence > highestSequence)
                {
                    highestHistoricalSequences[historicalRelativePath] = sequence;
                }
            }

            _highestHistoricalSequencesByRelativePath = highestHistoricalSequences;

            return highestHistoricalSequences;
        }

        private bool TryParseHistoricalPath
        (
            string historicalPath,
            out string relativePath,
            out int sequence
        )
        {
            var pathStart = historicalPath.IndexOf('/');
            if (pathStart <= 0 || pathStart == historicalPath.Length - 1)
            {
                relativePath = string.Empty;
                sequence = default;
                return false;
            }

            var timestampSegment = historicalPath[..pathStart];
            if (string.Equals(timestampSegment, _historicalTimestampSegment, StringComparison.Ordinal))
            {
                relativePath = historicalPath[(pathStart + 1)..];
                sequence = 0;
                return true;
            }

            var numberedPrefix = $"{_historicalTimestampSegment}-";
            if (!timestampSegment.StartsWith(numberedPrefix, StringComparison.Ordinal))
            {
                relativePath = string.Empty;
                sequence = default;
                return false;
            }

            var sequenceText = timestampSegment[numberedPrefix.Length..];
            if (!int.TryParse(
                    sequenceText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out sequence) ||
                sequence <= 0)
            {
                relativePath = string.Empty;
                sequence = default;
                return false;
            }

            relativePath = historicalPath[(pathStart + 1)..];
            return true;
        }
    }
}
