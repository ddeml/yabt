using System.Collections;
using Yabt.Common;
using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

/// <summary>
/// Owns ordered history moves and provider-aware blocker lookup in scratch storage.
/// Completion uses original spelling so mutations never change pending identities.
/// </summary>
internal sealed class RestoreHistoryPlan : IReadOnlyCollection<RestoreHistoryMove>, IDisposable
{
    private readonly TemporaryIndex _index;
    private readonly DiskEntryCollection<RestoreHistoryMove> _moves;
    private readonly DiskDictionary<long> _paths;
    private readonly DiskSet _pendingPaths;

    public RestoreHistoryPlan(StringComparer pathComparer)
    {
        _index = TemporaryIndex.Create();
        try
        {
            _moves = _index.CreateCollection<RestoreHistoryMove>();
            _paths = _index.CreateDictionary<long>(comparer: pathComparer);
            _pendingPaths = _index.CreateSet();
        }
        catch (Exception)
        {
            _index.Dispose();
            throw;
        }
    }

    public int Count => _moves.Count;

    public void Add(RestoreHistoryMove move)
    {
        if (!_paths.TryAdd(move.RelativePath, _moves.LongCount))
        {
            throw new YabtSyncException(
                $"Restore history contains equivalent paths: '{move.RelativePath}'.");
        }

        _moves.Add(move);
        _pendingPaths.Add(move.RelativePath);
    }

    public IReadOnlyList<RestoreHistoryMove> GetBlockers
    (
        string fileRelativePath,
        CancellationToken cancellationToken = default
    )
    {
        List<(long Sequence, RestoreHistoryMove Move)> blockers = [];
        var filePath = ObjectPath.Parse(fileRelativePath);
        for (var candidate = filePath; !candidate.IsRoot; candidate = candidate.Parent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_paths.TryGetValue(candidate.Value, out var sequence)) { continue; }

            var move = _moves.Get(sequence);
            if ((candidate == filePath || !move.IsFolder) &&
                _pendingPaths.Contains(move.RelativePath))
            {
                blockers.Add((sequence, move));
            }
        }

        // Only blockers on this file's ancestor chain enter this small list.
        blockers.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
        return blockers.Select(item => item.Move).ToArray();
    }

    public void MarkCompleted(RestoreHistoryMove move) => _pendingPaths.Remove(move.RelativePath);

    public IEnumerable<RestoreHistoryMove> EnumeratePending
    (
        CancellationToken cancellationToken = default
    )
    {
        foreach (var move in _moves.Enumerate(cancellationToken))
        {
            if (_pendingPaths.Contains(move.RelativePath)) { yield return move; }
        }
    }

    public IEnumerator<RestoreHistoryMove> GetEnumerator() => _moves.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        try
        {
            _pendingPaths.Dispose();
            _paths.Dispose();
            _moves.Dispose();
        }
        finally
        {
            _index.Dispose();
        }
    }
}
