using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Yabt.Common;

namespace Yabt.Profiling.Implementation;

/// <summary>
/// Observes completed payload writes and provider invocation events, not bytes.
/// Its bounded-fixture path sets and log writes are measurement overhead.
/// </summary>
internal sealed class ResumeProfilingObserver : IDisposable
{
    private readonly Lock _gate = new();
    private readonly ResumeProfilingScenario _scenario;
    private readonly CancellationTokenSource _operationCancellation;
    private readonly int _abortAfter;
    private readonly StreamWriter _commitLog;
    private readonly HashSet<string> _priorCommits = new(StringComparer.Ordinal);
    private readonly HashSet<string> _currentCommits = new(StringComparer.Ordinal);
    private readonly Stopwatch _clock = new();
    private long _providerReads;
    private long _providerLists;
    private long _sourceReads;
    private long _archiveReads;
    private long _destinationReads;
    private long _zipEntryReads;
    private long _zipSourceReads;
    private long _progressUnavailable;

    public ResumeProfilingObserver
    (
        ResumeProfilingScenario scenario,
        string commitLogPath,
        string? priorCommitLogPath,
        int abortAfter,
        CancellationTokenSource operationCancellation
    )
    {
        _scenario = scenario;
        _operationCancellation = operationCancellation;
        _abortAfter = abortAfter;
        if (priorCommitLogPath is not null)
        {
            foreach (var line in File.ReadLines(priorCommitLogPath))
            {
                _priorCommits.Add
                (
                    JsonSerializer.Deserialize<string>(line) ??
                        throw new InvalidDataException("A recorded committed path was null.")
                );
            }
        }
        _commitLog = new
        (
            new FileStream(commitLogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false)
        );
    }

    public double? FirstPayloadCommitMilliseconds { get; private set; }

    public double? FirstNewPayloadCommitMilliseconds { get; private set; }

    public ResumeProfilingIoCounts? CallsBeforeFirstNewCommit { get; private set; }

    public int PayloadCommitCount => _currentCommits.Count;

    public int PreviouslyCommittedCount => _priorCommits.Count;

    public int RecommittedPayloadCount { get; private set; }

    public bool RequestedInterruption { get; private set; }

    public void Start() => _clock.Start();

    public ResumeProfilingIoCounts Snapshot()
    {
        lock (_gate)
        {
            return new
            (
                _providerReads, _providerLists, _sourceReads, _archiveReads,
                _destinationReads, _zipEntryReads, _zipSourceReads, _progressUnavailable
            );
        }
    }

    public void Observe<TState>(int eventId, TState state)
    {
        if (!_clock.IsRunning) { return; }
        var cancel = false;
        lock (_gate)
        {
            if (eventId == YabtEventIds.OperationProgressUnavailable) { _progressUnavailable++; }
            if (eventId == YabtEventIds.FileSystemFolderList) { _providerLists++; }
            if (eventId == YabtEventIds.ZipEntryRead) { _zipEntryReads++; }
            if (eventId == YabtEventIds.ZipSourceObjectRead) { _zipSourceReads++; }
            if (eventId == YabtEventIds.FileSystemObjectRead)
            {
                _providerReads++;
                var nativePath = GetValue(state, "Path");
                if (IsUnder(nativePath, _scenario.Fixture.SourceRoot)) { _sourceReads++; }
                else if (IsUnder(nativePath, _scenario.Fixture.ArchiveRoot)) { _archiveReads++; }
                else if (IsUnder(nativePath, _scenario.DestinationRoot)) { _destinationReads++; }
            }

            if
            (
                eventId is not
                    (YabtEventIds.BackupObjectAdded or YabtEventIds.BackupObjectChanged or YabtEventIds.RestoreItemWritten)
            )
            {
                return;
            }
            if (eventId == YabtEventIds.RestoreItemWritten && GetValue(state, "ItemKind") != "file") { return; }
            var relativePath = GetValue(state, "RelativePath");
            if (relativePath is null || !_currentCommits.Add(relativePath)) { return; }
            var elapsed = _clock.Elapsed.TotalMilliseconds;
            FirstPayloadCommitMilliseconds ??= elapsed;
            if (_priorCommits.Contains(relativePath)) { RecommittedPayloadCount++; }
            else if (FirstNewPayloadCommitMilliseconds is null)
            {
                FirstNewPayloadCommitMilliseconds = elapsed;
                CallsBeforeFirstNewCommit = Snapshot();
            }
            _commitLog.WriteLine(JsonSerializer.Serialize(relativePath));
            if (_abortAfter > 0 && _currentCommits.Count >= _abortAfter && !RequestedInterruption)
            {
                RequestedInterruption = true;
                cancel = true;
            }
        }
        if (cancel) { _operationCancellation.Cancel(); }
    }

    public void Dispose()
    {
        _clock.Stop();
        _commitLog.Dispose();
    }

    private static string? GetValue<TState>(TState state, string key)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> values) { return null; }
        foreach (var value in values)
        {
            if (string.Equals(value.Key, key, StringComparison.Ordinal)) { return value.Value as string; }
        }
        return null;
    }

    private static bool IsUnder(string? path, string root) => path is not null &&
        path.StartsWith
        (
            root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
        );
}
