using System.Globalization;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

internal sealed class ArchiveHistoryKeyAllocator
(
    IObjectStore _targetStore,
    ArchiveLayout _targetLayout,
    DateTimeOffset _historicalTimestamp
)
{
    private readonly string _historicalTimestampSegment = ToHistoricalTimestampSegment(_historicalTimestamp);
    private readonly Dictionary<string, int> _highestHistoricalSequencesByRelativePath = new(StringComparer.Ordinal);

    public async Task<string> CreateHistoricalKeyAsync
    (
        string relativePath,
        CancellationToken cancellationToken
    )
    {
        var normalizedRelativePath = ArchiveLayout.NormalizeObjectKey(relativePath);
        var highestSequence = await GetHighestHistoricalSequenceAsync(
            normalizedRelativePath,
            cancellationToken);
        var sequence = highestSequence >= 0 ?
            highestSequence + 1 :
            0;
        var historicalPath = BuildHistoricalRelativePath(
            normalizedRelativePath,
            _historicalTimestampSegment,
            sequence);

        _highestHistoricalSequencesByRelativePath[normalizedRelativePath] = sequence;

        return _targetLayout.ToHistoryObjectKey(historicalPath);
    }

    private async Task<int> GetHighestHistoricalSequenceAsync
    (
        string relativePath,
        CancellationToken cancellationToken
    )
    {
        if (_highestHistoricalSequencesByRelativePath.TryGetValue(relativePath, out var cachedHighestSequence))
        {
            return cachedHighestSequence;
        }

        var objectName = GetObjectName(relativePath);
        var objectParentPrefix = GetParentPrefix(relativePath);
        var histPrefix = ArchiveLayout.NormalizeObjectPrefix(_targetLayout.HistPrefix);
        var historyRoots = _targetStore.GetFolderItemsAsync(
            histPrefix,
            recursive: false,
            cancellationToken);
        var highestSequence = -1;

        await foreach (var historyRoot in historyRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!historyRoot.IsFolder ||
                !TryParseHistoricalTimestampSegment(historyRoot.Name, out var sequence))
            {
                continue;
            }

            var historyParentPrefix = _targetLayout.ToHistoryObjectKey(
                ArchiveLayout.CombinePrefixAndRelativePath(
                    historyRoot.Name,
                    objectParentPrefix));
            var historyFolderItems = _targetStore.GetFolderItemsAsync(
                historyParentPrefix,
                recursive: false,
                cancellationToken);

            await foreach (var historyFolderItem in historyFolderItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (historyFolderItem.Object is not null &&
                    string.Equals(historyFolderItem.Name, objectName, StringComparison.Ordinal) &&
                    sequence > highestSequence)
                {
                    highestSequence = sequence;
                }
            }
        }

        _highestHistoricalSequencesByRelativePath[relativePath] = highestSequence;
        return highestSequence;
    }

    private bool TryParseHistoricalTimestampSegment
    (
        string timestampSegment,
        out int sequence
    )
    {
        if (string.Equals(timestampSegment, _historicalTimestampSegment, StringComparison.Ordinal))
        {
            sequence = 0;
            return true;
        }

        var numberedPrefix = $"{_historicalTimestampSegment}-";
        if (!timestampSegment.StartsWith(numberedPrefix, StringComparison.Ordinal))
        {
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
            sequence = default;
            return false;
        }

        return true;
    }

    private static string GetParentPrefix(string relativePath)
    {
        var normalizedRelativePath = ArchiveLayout.NormalizeObjectKey(relativePath);
        var separator = normalizedRelativePath.LastIndexOf('/');

        return separator < 0 ? string.Empty : normalizedRelativePath[..separator];
    }

    private static string GetObjectName(string relativePath)
    {
        var normalizedRelativePath = ArchiveLayout.NormalizeObjectKey(relativePath);
        var separator = normalizedRelativePath.LastIndexOf('/');

        return separator < 0 ? normalizedRelativePath : normalizedRelativePath[(separator + 1)..];
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
}
