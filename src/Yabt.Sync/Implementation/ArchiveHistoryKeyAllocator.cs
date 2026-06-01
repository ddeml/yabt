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
    private Dictionary<string, int>? _highestHistoricalSequencesByRelativePath;

    public async Task<string> CreateHistoricalKeyAsync
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

        return _targetLayout.ToHistoryObjectKey(historicalPath);
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
        var histPrefix = ArchiveLayout.NormalizeObjectPrefix(_targetLayout.HistPrefix);
        var historicalObjects = _targetStore.ListAsync(
            histPrefix,
            cancellationToken);

        await foreach (var archiveObject in historicalObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var objectKey = ArchiveLayout.NormalizeObjectKey(archiveObject.Key);
            var relativePath = ArchiveLayout.RemovePrefix(objectKey, histPrefix);
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
