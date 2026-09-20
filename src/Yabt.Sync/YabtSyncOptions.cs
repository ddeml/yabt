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
