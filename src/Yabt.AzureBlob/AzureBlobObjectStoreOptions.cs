using Azure.Core;
using Azure.Storage;
using Azure.Storage.Blobs;

namespace Yabt.AzureBlob;

public sealed class AzureBlobObjectStoreOptions
{
    public const string DefaultConfigurationSectionPath = "ObjectStores:AzureBlob";
    public const int DefaultUploadMaximumConcurrency = 5;

    public string? ConnectionString { get; init; }

    public Uri? ServiceUri { get; init; }

    /// <summary>
    /// Default is <c>archive</c>.
    /// </summary>
    public string? ContainerName { get; init; }

    public string? Prefix { get; init; }

    /// <summary>
    /// Maximum number of Azure Blob upload subtransfers that may run in parallel.
    /// Default is <c>5</c>.
    /// </summary>
    public int UploadMaximumConcurrency { get; init; } = DefaultUploadMaximumConcurrency;

    /// <summary>
    /// Retry policy used by every Azure Blob request, including resumable restore reads.
    /// </summary>
    public AzureBlobRetryOptions Retry { get; init; } = new();

    public string GetEffectiveContainerName() => ContainerName ?? "archive";

    internal StorageTransferOptions CreateUploadTransferOptions()
    {
        if (UploadMaximumConcurrency <= 0)
        {
            throw new YabtAzureBlobException(
                "Azure Blob upload maximum concurrency must be greater than zero.");
        }

        return new()
        {
            MaximumConcurrency = UploadMaximumConcurrency,
        };
    }

    internal BlobClientOptions CreateClientOptions()
    {
        if (Retry is null)
        {
            throw new YabtAzureBlobException(
                "Azure Blob retry configuration must not be null.");
        }

        Retry.Validate();
        return new BlobClientOptions
        {
            Retry =
            {
                Mode = RetryMode.Exponential,
                MaxRetries = Retry.MaximumRetries,
                Delay = Retry.Delay,
                MaxDelay = Retry.MaximumDelay,
                NetworkTimeout = Retry.NetworkTimeout,
            },
        };
    }
}

public sealed class AzureBlobRetryOptions
{
    public const int DefaultMaximumRetries = 8;

    public int MaximumRetries { get; init; } = DefaultMaximumRetries;

    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaximumDelay { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan NetworkTimeout { get; init; } = TimeSpan.FromSeconds(100);

    internal void Validate()
    {
        if (MaximumRetries < 0)
        {
            throw new YabtAzureBlobException(
                "Azure Blob retry maximum retries must not be negative.");
        }

        if (Delay <= TimeSpan.Zero)
        {
            throw new YabtAzureBlobException(
                "Azure Blob retry delay must be greater than zero.");
        }

        if (MaximumDelay <= TimeSpan.Zero)
        {
            throw new YabtAzureBlobException(
                "Azure Blob retry maximum delay must be greater than zero.");
        }

        if (NetworkTimeout <= TimeSpan.Zero)
        {
            throw new YabtAzureBlobException(
                "Azure Blob retry network timeout must be greater than zero.");
        }
    }
}
