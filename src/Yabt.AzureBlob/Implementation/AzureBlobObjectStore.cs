using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.AzureBlob.Implementation;

internal sealed class AzureBlobObjectStore
(
    IOptionsMonitor<AzureBlobObjectStoreOptions> _options,
    ILogger<AzureBlobObjectStore> _logger
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
        ArchiveObjectKey key,
        Stream content,
        string contentType,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(UploadAsync));

        try
        {
            var blob = GetBlobClient(key);
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
                $"Upload failed for Azure Blob object '{key.ToObjectPath()}'.",
                ex);
        }
    }

    public async Task<ArchiveObjectContent> OpenReadAsync
    (
        ArchiveObjectKey key,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(OpenReadAsync));

        try
        {
            var blob = GetBlobClient(key);
            var download = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken);
            var details = download.Value.Details;

            return new(
                download.Value.Content,
                string.IsNullOrWhiteSpace(details.ContentType) ?
                    "application/octet-stream" :
                    details.ContentType,
                details.Metadata.Count == 0 ?
                    null :
                    new Dictionary<string, string>(details.Metadata, StringComparer.Ordinal));
        }
        catch (Exception ex)
        {
            throw new YabtAzureBlobException(
                $"Open read failed for Azure Blob object '{key.ToObjectPath()}'.",
                ex);
        }
    }

    public async Task<bool> ExistsAsync
    (
        ArchiveObjectKey key,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ExistsAsync));

        try
        {
            var blob = GetBlobClient(key);
            var response = await blob.ExistsAsync(cancellationToken);

            return response.Value;
        }
        catch (Exception ex)
        {
            throw new YabtAzureBlobException(
                $"Azure Blob object existence check failed for '{key.ToObjectPath()}'.",
                ex);
        }
    }

    public async IAsyncEnumerable<ArchiveObjectInfo> ListAsync
    (
        ArchiveArea area,
        string? prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ListAsync));

        var container = GetContainerClient();
        var areaRoot = GetBlobName(new(area, string.Empty));
        var listPrefix = string.IsNullOrWhiteSpace(prefix) ?
            $"{areaRoot}/" :
            $"{GetBlobName(new(area, prefix))}/";

        await foreach (var blob in container.GetBlobsAsync(
                           BlobTraits.None,
                           BlobStates.None,
                           prefix: listPrefix,
                           cancellationToken: cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return new(
                ToArchiveObjectKey(area, areaRoot, blob.Name),
                blob.Properties.ContentLength,
                blob.Properties.LastModified,
                ToContentHash(blob.Properties.ContentHash));
        }
    }

    public async Task MoveAsync
    (
        ArchiveObjectKey source,
        ArchiveObjectKey destination,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(MoveAsync));

        try
        {
            var sourceBlob = GetBlobClient(source);
            var destinationBlob = GetBlobClient(destination);
            var properties = await sourceBlob.GetPropertiesAsync(cancellationToken: cancellationToken);
            var sourceConditions = new BlobRequestConditions
            {
                IfMatch = properties.Value.ETag,
            };

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
                        Conditions = new BlobRequestConditions
                        {
                            IfNoneMatch = ETag.All,
                        },
                        HttpHeaders = ToBlobHttpHeaders(properties.Value),
                        Metadata = properties.Value.Metadata.Count == 0 ?
                            null :
                            new Dictionary<string, string>(properties.Value.Metadata, StringComparer.Ordinal),
                    },
                    cancellationToken);
            }

            await sourceBlob.DeleteAsync(
                DeleteSnapshotsOption.IncludeSnapshots,
                sourceConditions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtAzureBlobException(
                $"Move failed for Azure Blob object '{source.ToObjectPath()}' to '{destination.ToObjectPath()}'.",
                ex);
        }
    }

    private BlobClient GetBlobClient(ArchiveObjectKey key)
    {
        try
        {
            return GetContainerClient().GetBlobClient(GetBlobName(key));
        }
        catch (Exception ex)
        {
            throw new YabtAzureBlobException(
                $"Azure Blob object path for '{key.ToObjectPath()}' could not be resolved.",
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

    private string GetBlobName(ArchiveObjectKey key)
    {
        return CombineBlobName(_options.CurrentValue.Prefix, key.ToObjectPath());
    }

    private static string CombineBlobName(params string?[] parts)
    {
        var normalizedParts = parts
            .Select(NormalizeBlobNamePart)
            .Where(part => !string.IsNullOrWhiteSpace(part));

        var blobName = string.Join('/', normalizedParts);
        if (string.IsNullOrWhiteSpace(blobName))
        {
            throw new YabtAzureBlobException("Azure Blob object name must not be empty.");
        }

        return blobName;
    }

    private static string NormalizeBlobNamePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace('\\', '/').Trim('/');
        var segments = normalized.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                throw new YabtAzureBlobException("Azure Blob object name contains an invalid segment.");
            }
        }

        return string.Join('/', segments);
    }

    private static ArchiveObjectKey ToArchiveObjectKey(
        ArchiveArea area,
        string areaRoot,
        string blobName)
    {
        if (string.Equals(blobName, areaRoot, StringComparison.Ordinal))
        {
            return new(area, string.Empty);
        }

        var expectedPrefix = $"{areaRoot}/";
        if (!blobName.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            throw new YabtAzureBlobException(
                $"Azure Blob object '{blobName}' did not belong to archive area '{area}'.");
        }

        return new(area, blobName[expectedPrefix.Length..]);
    }

    private static string? ToContentHash(byte[]? contentHash)
    {
        return contentHash is null || contentHash.Length == 0 ?
            null :
            Convert.ToBase64String(contentHash);
    }

    private static BlobHttpHeaders ToBlobHttpHeaders(BlobProperties properties)
    {
        return new()
        {
            CacheControl = properties.CacheControl,
            ContentDisposition = properties.ContentDisposition,
            ContentEncoding = properties.ContentEncoding,
            ContentHash = properties.ContentHash,
            ContentLanguage = properties.ContentLanguage,
            ContentType = properties.ContentType,
        };
    }
}
