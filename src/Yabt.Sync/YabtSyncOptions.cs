using Yabt.Common;

namespace Yabt.Sync;

public sealed class YabtSyncOptions
{
    public const string DefaultConfigurationSectionPath = "Sync";
    public const int DefaultRestoreMaximumConcurrency = 5;

    /// <summary>
    /// Maximum number of restore files that may be downloaded and validated concurrently.
    /// Default is <c>5</c>.
    /// </summary>
    public int RestoreMaximumConcurrency { get; init; } = DefaultRestoreMaximumConcurrency;

    /// <summary>
    /// Maximum bytes consumed when reading a remote archive mutation lock document.
    /// Defaults to 64 KiB. Native filesystem locking does not deserialize a document.
    /// </summary>
    public long? LockDocumentMaximumBytes { get; init; }

    /// <summary>Retain disposable local scheduling progress after interruption. Defaults to true.</summary>
    public bool? ProgressEnabled { get; init; }

    /// <summary>Local per-user checkpoint directory, outside every operation data location.</summary>
    public string? ProgressDirectory { get; init; }

    /// <summary>Flush after this many completed units. Defaults to 32.</summary>
    public int? ProgressFlushUnitCount { get; init; }

    /// <summary>Flush at the next completed unit after this interval. Defaults to five seconds.</summary>
    public double? ProgressFlushIntervalSeconds { get; init; }

    internal string GetEffectiveProgressDirectory() => ProgressDirectory is null ?
        LocalStateDirectory.GetProgressDirectory() : Path.GetFullPath(ProgressDirectory);

    internal int GetValidatedProgressFlushUnitCount()
    {
        var count = ProgressFlushUnitCount ?? 32;
        if (count <= 0) { throw new YabtSyncException("Progress flush unit count must be greater than zero."); }
        return count;
    }

    internal TimeSpan GetValidatedProgressFlushInterval()
    {
        var seconds = ProgressFlushIntervalSeconds ?? 5;
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
        {
            throw new YabtSyncException("Progress flush interval must be a finite positive number of seconds.");
        }
        return TimeSpan.FromSeconds(seconds);
    }

    internal long GetValidatedLockDocumentMaximumBytes()
    {
        var maximumBytes = LockDocumentMaximumBytes ?? 64 * 1024;
        if (maximumBytes <= 0 || maximumBytes > Array.MaxLength)
        {
            throw new YabtSyncException
            (
                $"Lock document maximum bytes must be between 1 and {Array.MaxLength}."
            );
        }

        return maximumBytes;
    }

    internal int GetValidatedRestoreMaximumConcurrency()
    {
        if (RestoreMaximumConcurrency <= 0)
        {
            throw new YabtSyncException(
                "Restore maximum concurrency must be greater than zero.");
        }

        return RestoreMaximumConcurrency;
    }
}
