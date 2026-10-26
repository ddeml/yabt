using System.Text.Json;
using Yabt.Common;
using Yabt.Metadata;

namespace Yabt.Sync.Tests.Implementation;

/// <summary>
/// Stops inside an existing structured log callback after the selected durable
/// effect. The parent kills the process while this callback holds its stack;
/// cancellation and managed disposal must never simulate the crash.
/// </summary>
internal sealed class CrashRecoveryGate
{
    private readonly string _rootPath;
    private readonly string _scenario;
    private readonly bool _armed;
    private readonly Lock _gate = new();

    public CrashRecoveryGate(string rootPath, string scenario, bool armed = true)
    {
        if
        (
            scenario is not
                ("backup-history" or "backup-commit" or "backup-package" or "backup-finalization" or
                "restore-history" or "restore-commit" or "restore-finalization")
        )
        {
            throw new ArgumentException("Unknown crash recovery scenario.", nameof(scenario));
        }
        _rootPath = Path.GetFullPath(rootPath);
        _scenario = scenario;
        _armed = armed;
    }

    public int ProgressUnavailableCount { get; private set; }

    public int ProgressResumedCount { get; private set; }

    public int ProgressCleanupFailedCount { get; private set; }

    public void Observe<TState>(int eventId, TState state)
    {
        lock (_gate)
        {
            if (eventId == YabtEventIds.OperationProgressUnavailable) { ProgressUnavailableCount++; }
            if (eventId == YabtEventIds.OperationProgressResumed) { ProgressResumedCount++; }
            if (eventId == YabtEventIds.OperationProgressCleanupFailed) { ProgressCleanupFailedCount++; }
            if (!_armed) { return; }

            var relativePath = GetValue(state, "RelativePath");
            var path = GetValue(state, "Path");
            var payloadEvent = relativePath == "branch/item.txt";
            var matched = _scenario switch
            {
                "backup-history" => payloadEvent && eventId == YabtEventIds.BackupObjectHistorized,
                "backup-commit" => payloadEvent &&
                    eventId is YabtEventIds.BackupObjectAdded or YabtEventIds.BackupObjectChanged,
                "backup-package" => relativePath?.EndsWith(".zip", StringComparison.Ordinal) == true &&
                    eventId is YabtEventIds.BackupObjectAdded or YabtEventIds.BackupObjectChanged,
                "restore-history" => payloadEvent && eventId == YabtEventIds.RestoreItemHistorized,
                "restore-commit" => payloadEvent && eventId == YabtEventIds.RestoreItemWritten &&
                    GetValue(state, "ItemKind") == "file",
                "backup-finalization" => eventId == YabtEventIds.ControlMetadataOperation &&
                    GetValue(state, "Action") == "Wrote" &&
                    path == ArchiveChangeManifest.UncompressedFileName,
                "restore-finalization" => eventId == YabtEventIds.ControlMetadataOperation &&
                    GetValue(state, "Action") == "Wrote" && path == ArchiveLogicalStateManifest.FileName,
                _ => false,
            };
            if (!matched) { return; }

            var signal = JsonSerializer.SerializeToUtf8Bytes
            (
                new
                {
                    scenario = _scenario,
                    processId = Environment.ProcessId,
                    eventId,
                    relativePath = relativePath ?? path,
                }
            );
            var pendingPath = Path.Combine(_rootPath, "crash-ready.pending");
            using (var stream = new FileStream(pendingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(signal);
                stream.Flush(flushToDisk: true);
            }
            File.Move(pendingPath, Path.Combine(_rootPath, "crash-ready.json"));
            Thread.Sleep(Timeout.Infinite);
            throw new InvalidOperationException("The crash gate must be terminated externally, never released.");
        }
    }

    private static string? GetValue<TState>(TState state, string key)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> values) { return null; }
        foreach (var pair in values)
        {
            if (pair.Key == key) { return pair.Value as string; }
        }
        return null;
    }
}
