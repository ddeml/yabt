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
    private readonly SemaphoreSlim _allocationLock = new(1, 1);
    private readonly List<(string RootName, SortedSet<string> RelativePaths)> _commandRoots = [];
    private long? _nextHistoricalSequence;

    public async Task<string> CreateHistoricalKeyAsync
    (
        string relativePath,
        CancellationToken cancellationToken
    )
    {
        var normalizedRelativePath = ArchiveLayout.NormalizeObjectKey(relativePath);
        await _allocationLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureNextHistoricalSequenceAsync(cancellationToken);

            foreach (var commandRoot in _commandRoots)
            {
                if (!HasPathConflict(commandRoot.RelativePaths, normalizedRelativePath))
                {
                    commandRoot.RelativePaths.Add(normalizedRelativePath);
                    return ToHistoricalObjectKey(
                        commandRoot.RootName,
                        normalizedRelativePath);
                }
            }

            var sequence = _nextHistoricalSequence ??
                throw new YabtSyncException("A history timestamp sequence was not initialized.");
            if (sequence > int.MaxValue)
            {
                throw new YabtSyncException(
                    $"History timestamp '{_historicalTimestampSegment}' has no remaining sequence values.");
            }

            var historicalRootName = BuildHistoricalRootName(sequence);
            _nextHistoricalSequence = sequence + 1;
            _commandRoots.Add
            ((
                historicalRootName,
                new SortedSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    normalizedRelativePath,
                }
            ));
            return ToHistoricalObjectKey(historicalRootName, normalizedRelativePath);
        }
        finally
        {
            _allocationLock.Release();
        }
    }

    private async Task EnsureNextHistoricalSequenceAsync(CancellationToken cancellationToken)
    {
        if (_nextHistoricalSequence.HasValue) { return; }

        var histPrefix = ArchiveLayout.NormalizeObjectPrefix(_targetLayout.HistPrefix);
        var historyRoots = _targetStore.GetFolderItemsAsync(
            histPrefix,
            recursive: false,
            cancellationToken);
        var highestSequence = -1;
        await foreach (var historyRoot in historyRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryParseHistoricalTimestampSegment(historyRoot.Name, out var sequence))
            {
                highestSequence = Math.Max(highestSequence, sequence);
            }
        }

        if (highestSequence == int.MaxValue)
        {
            throw new YabtSyncException(
                $"History timestamp '{_historicalTimestampSegment}' has no remaining sequence values.");
        }

        _nextHistoricalSequence = highestSequence + 1;
    }

    private string ToHistoricalObjectKey(string rootName, string relativePath) =>
        _targetLayout.ToHistoryObjectKey(
            ArchiveLayout.CombinePrefixAndRelativePath(rootName, relativePath));

    private static bool HasPathConflict
    (
        SortedSet<string> existingPaths,
        string relativePath
    )
    {
        if (existingPaths.Contains(relativePath)) { return true; }

        var ancestorPath = GetParentPrefix(relativePath);
        while (!string.IsNullOrEmpty(ancestorPath))
        {
            if (existingPaths.Contains(ancestorPath)) { return true; }

            ancestorPath = GetParentPrefix(ancestorPath);
        }

        var descendantPrefix = $"{relativePath}/";
        var possibleDescendants = existingPaths.GetViewBetween(
            descendantPrefix,
            $"{relativePath}0");
        using var enumerator = possibleDescendants.GetEnumerator();
        return enumerator.MoveNext() &&
            enumerator.Current.StartsWith(
                descendantPrefix,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string GetParentPrefix(string relativePath)
    {
        var separator = relativePath.LastIndexOf('/');
        return separator < 0 ? string.Empty : relativePath[..separator];
    }

    private bool TryParseHistoricalTimestampSegment
    (
        string timestampSegment,
        out int sequence
    )
    {
        if (string.Equals(
                timestampSegment,
                _historicalTimestampSegment,
                StringComparison.OrdinalIgnoreCase))
        {
            sequence = 0;
            return true;
        }

        var numberedPrefix = $"{_historicalTimestampSegment}-";
        if (!timestampSegment.StartsWith(numberedPrefix, StringComparison.OrdinalIgnoreCase))
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

    private string BuildHistoricalRootName(long sequence)
    {
        var historicalRootName = _historicalTimestampSegment;
        if (sequence > 0)
        {
            historicalRootName += $"-{sequence.ToString(CultureInfo.InvariantCulture)}";
        }

        return historicalRootName;
    }

    private static string ToHistoricalTimestampSegment(DateTimeOffset historicalTimestamp)
    {
        var historicalTimestampUtc = historicalTimestamp.ToUniversalTime();
        return historicalTimestampUtc.ToString(
            "yyyyMMdd'T'HHmmssFFFFFFF'Z'",
            CultureInfo.InvariantCulture);
    }
}
