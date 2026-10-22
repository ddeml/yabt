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
