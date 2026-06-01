using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.AzureBlob.Implementation;

internal sealed class AzureBlobObjectStore
(
    IOptionsMonitor<AzureBlobObjectStoreOptions> _options,
    ILogger<AzureBlobObjectStore> _logger,
    TimeProvider _timeProvider
) : IObjectStore
{
    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogTrace(nameof(EnsureReadyAsync));

        try
        {
            var container = GetContainerClient();
            await container.CreateIfNotExistsAsync(
                PublicAccessType.None,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtAzureBlobException(
                "Azure Blob object store could not ensure the configured container exists.",
                ex);
        }
    }

    public async Task UploadAsync
    (
        string key,
        Stream content,
        string contentType,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(UploadAsync));

        var normalizedKey = NormalizeObjectKey(key);
        try
        {
            var blob = GetBlobClient(normalizedKey);
            var uploadOptions = new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions
                {
                    IfNoneMatch = ETag.All,
                },
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = string.IsNullOrWhiteSpace(contentType) ? null : contentType,
                },
                Metadata = metadata.Count == 0 ?
                    null :
                    new Dictionary<string, string>(metadata, StringComparer.Ordinal),
            };

            await blob.UploadAsync(content, uploadOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtAzureBlobException(
                $"Upload failed for Azure Blob object '{normalizedKey}'.",
                ex);
        }
    }

    public async Task<ArchiveObjectContent> OpenReadAsync
    (
        string key,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(OpenReadAsync));

        var normalizedKey = NormalizeObjectKey(key);
        try
        {
            var blob = GetBlobClient(normalizedKey);
            var download = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken);
            var details = download.Value.Details;

            return new
            (
                download.Value.Content,
                string.IsNullOrWhiteSpace(details.ContentType) ?
                    "application/octet-stream" :
                    details.ContentType,
                details.Metadata.Count == 0 ?
                    null :
                    new Dictionary<string, string>(details.Metadata, StringComparer.Ordinal)
            );
        }
        catch (Exception ex)
        {
            throw new YabtAzureBlobException(
                $"Open read failed for Azure Blob object '{normalizedKey}'.",
                ex);
        }
    }

    public async Task<bool> ExistsAsync
    (
        string key,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ExistsAsync));

        var normalizedKey = NormalizeObjectKey(key);
        try
        {
            var blob = GetBlobClient(normalizedKey);
            var response = await blob.ExistsAsync(cancellationToken);

            return response.Value;
        }
        catch (Exception ex)
        {
            throw new YabtAzureBlobException(
                $"Azure Blob object existence check failed for '{normalizedKey}'.",
                ex);
        }
    }

    public async IAsyncEnumerable<ArchiveObjectInfo> ListAsync
    (
        string? prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ListAsync));

        var container = GetContainerClient();
        var objectStorePrefix = NormalizeObjectPrefix(_options.CurrentValue.Prefix);
        var requestedPrefix = NormalizeObjectPrefix(prefix);
        var blobPrefix = CombineBlobNameParts(objectStorePrefix, requestedPrefix);
        var blobs = container.GetBlobsAsync
        (
            BlobTraits.None,
            BlobStates.None,
            prefix: string.IsNullOrEmpty(blobPrefix) ? null : blobPrefix,
            cancellationToken: cancellationToken
        );

        await foreach (var blob in blobs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return new
            (
                ToObjectStoreKey(objectStorePrefix, blob.Name),
                blob.Properties.ContentLength,
                blob.Properties.LastModified,
                ToContentHash(blob.Properties.ContentHash)
            );
        }
    }

    public async Task MoveAsync
    (
        string source,
        string destination,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(MoveAsync));

        var normalizedSource = NormalizeObjectKey(source);
        var normalizedDestination = NormalizeObjectKey(destination);
        try
        {
            var sourceBlob = GetBlobClient(normalizedSource);
            var destinationBlob = GetBlobClient(normalizedDestination);
            var properties = await sourceBlob.GetPropertiesAsync(cancellationToken: cancellationToken);
            var sourceConditions = new BlobRequestConditions
            {
                IfMatch = properties.Value.ETag,
            };

            if (await TryCopyAndDeleteAsync(
                    sourceBlob,
                    destinationBlob,
                    properties.Value,
                    sourceConditions,
                    cancellationToken))
            {
                return;
            }

            await MoveByDownloadUploadDeleteAsync(
                sourceBlob,
                destinationBlob,
                properties.Value,
                sourceConditions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtAzureBlobException(
                $"Move failed for Azure Blob object '{normalizedSource}' to '{normalizedDestination}'.",
                ex);
        }
    }

    private async Task<bool> TryCopyAndDeleteAsync
    (
        BlobClient sourceBlob,
        BlobClient destinationBlob,
        BlobProperties properties,
        BlobRequestConditions sourceConditions,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var sourceUri = GetCopySourceUri(sourceBlob);
            await destinationBlob.SyncCopyFromUriAsync(
                sourceUri,
                new BlobCopyFromUriOptions
                {
                    Metadata = ToMetadataDictionary(properties),
                    SourceConditions = sourceConditions,
                    DestinationConditions = CreateDestinationCreateConditions(),
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested) { throw; }
            _logger.LogFallingBackToDownloadedAzureBlobMove(ex, sourceBlob.Name, destinationBlob.Name);
            return false;
        }

        await sourceBlob.DeleteAsync(
            DeleteSnapshotsOption.IncludeSnapshots,
            sourceConditions,
            cancellationToken);

        return true;
    }

    private static async Task MoveByDownloadUploadDeleteAsync
    (
        BlobClient sourceBlob,
        BlobClient destinationBlob,
        BlobProperties properties,
        BlobRequestConditions sourceConditions,
        CancellationToken cancellationToken
    )
    {
        var destinationConditions = CreateDestinationCreateConditions();
        var download = await sourceBlob.DownloadStreamingAsync(
            new BlobDownloadOptions
            {
                Conditions = sourceConditions,
            },
            cancellationToken);

        await using (download.Value.Content)
        {
            await destinationBlob.UploadAsync(
                download.Value.Content,
                new BlobUploadOptions
                {
                    Conditions = destinationConditions,
                    HttpHeaders = ToBlobHttpHeaders(properties),
                    Metadata = ToMetadataDictionary(properties),
                },
                cancellationToken);
        }

        await sourceBlob.DeleteAsync(
            DeleteSnapshotsOption.IncludeSnapshots,
            sourceConditions,
            cancellationToken);
    }

    private Uri GetCopySourceUri(BlobClient sourceBlob)
    {
        return sourceBlob.CanGenerateSasUri ?
            sourceBlob.GenerateSasUri(BlobSasPermissions.Read, _timeProvider.GetUtcNow().AddMinutes(10)) :
            sourceBlob.Uri;
    }

    private static BlobRequestConditions CreateDestinationCreateConditions()
    {
        return new()
        {
            IfNoneMatch = ETag.All,
        };
    }

    private static Dictionary<string, string>? ToMetadataDictionary(BlobProperties properties)
    {
        return properties.Metadata.Count == 0 ?
            null :
            new Dictionary<string, string>(properties.Metadata, StringComparer.Ordinal);
    }

    private BlobClient GetBlobClient(string key)
    {
        try
        {
            return GetContainerClient().GetBlobClient(GetBlobName(key));
        }
        catch (Exception ex)
        {
            throw new YabtAzureBlobException(
                $"Azure Blob object path for '{key}' could not be resolved.",
                ex);
        }
    }

    private BlobContainerClient GetContainerClient()
    {
        var options = _options.CurrentValue;
        var containerName = options.GetEffectiveContainerName();
        if (string.IsNullOrWhiteSpace(containerName))
        {
            throw new YabtAzureBlobException("Azure Blob object store requires a container name.");
        }

        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new BlobContainerClient(options.ConnectionString, containerName);
        }

        if (options.ServiceUri is not null)
        {
            return new BlobServiceClient(options.ServiceUri).GetBlobContainerClient(containerName);
        }

        throw new YabtAzureBlobException(
            "Azure Blob object store requires either a connection string or service URI.");
    }

    private string GetBlobName(string key)
    {
        return CombineBlobNameParts(_options.CurrentValue.Prefix, key);
    }

    private static string CombineBlobNameParts(params IEnumerable<string?> parts)
    {
        var normalizedParts = parts
            .Select(NormalizeObjectPrefix)
            .Where(part => !string.IsNullOrWhiteSpace(part));

        return string.Join('/', normalizedParts);
    }

    private static string NormalizeObjectKey(string? value)
    {
        var normalized = NormalizeObjectPrefix(value);
        if (string.IsNullOrEmpty(normalized))
        {
            throw new YabtAzureBlobException("Azure Blob object name must not be empty.");
        }

        return normalized;
    }

    private static string NormalizeObjectPrefix(string? value)
    {
        return ArchiveLayout.NormalizeObjectKey(value);
    }

    private static string ToObjectStoreKey
    (
        string objectStorePrefix,
        string blobName
    )
    {
        var normalizedBlobName = NormalizeObjectKey(blobName);
        if (string.IsNullOrEmpty(objectStorePrefix))
        {
            return normalizedBlobName;
        }

        return ArchiveLayout.RemovePrefix(normalizedBlobName, objectStorePrefix);
    }

    private static string? ToContentHash(byte[]? contentHash)
    {
        return contentHash is null || contentHash.Length == 0 ?
            null :
            Convert.ToBase64String(contentHash);
    }

    private static BlobHttpHeaders ToBlobHttpHeaders(BlobProperties properties) =>
        new()
        {
            CacheControl = properties.CacheControl,
            ContentDisposition = properties.ContentDisposition,
            ContentEncoding = properties.ContentEncoding,
            ContentHash = properties.ContentHash,
            ContentLanguage = properties.ContentLanguage,
            ContentType = properties.ContentType,
        };
}
